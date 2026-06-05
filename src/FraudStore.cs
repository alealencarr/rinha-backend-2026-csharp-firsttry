using System.Numerics;
using System.Text.Json;

/// <summary>
/// Armazena os 100k vetores de referência em layout SoA (struct-of-arrays):
/// _dims[d * N + i] = dimensão d do vetor i. Isso permite que o brute-force kNN
/// processe Vector&lt;float&gt;.Count referências por instrução SIMD, com acesso
/// sequencial perfeito por dimensão e sem sobra (tail) quando N % Count == 0.
///
/// A busca é EXATA (distância euclidiana ao quadrado, mesma ordenação de vizinhos
/// da euclidiana). Nada de ANN/HNSW: a pontuação de detecção satura em +3000
/// apenas com E=0, então qualquer aproximação que erre 1 vizinho já custa pontos.
/// </summary>
internal sealed class FraudStore
{
    private float[] _dims = Array.Empty<float>();
    private byte[] _labels = Array.Empty<byte>(); // 1 = fraud, 0 = legit
    private int _n;
    private volatile bool _ready;

    public bool Ready => _ready;

    public void Load(string jsonPath)
    {
        byte[] json = File.ReadAllBytes(jsonPath);
        Index(json);
        _ready = true;
    }

    private void Index(ReadOnlySpan<byte> json)
    {
        int cap = 100_000;
        float[] aos = new float[cap * 14]; // temporário row-major durante o parse
        byte[] labs = new byte[cap];
        int n = 0;

        var r = new Utf8JsonReader(json);
        if (!r.Read() || r.TokenType != JsonTokenType.StartArray)
            throw new InvalidDataException("references.json deve ser um array");

        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (n >= cap)
            {
                cap *= 2;
                Array.Resize(ref aos, cap * 14);
                Array.Resize(ref labs, cap);
            }

            int baseI = n * 14;
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.ValueTextEquals("vector"u8))
                {
                    r.Read(); // StartArray
                    int d = 0;
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        if (d < 14) aos[baseI + d] = (float)r.GetDouble();
                        d++;
                    }
                }
                else if (r.ValueTextEquals("label"u8))
                {
                    r.Read();
                    labs[n] = r.ValueTextEquals("fraud"u8) ? (byte)1 : (byte)0;
                }
                else { r.Read(); r.Skip(); }
            }
            n++;
        }

        // Transpõe AoS -> SoA com stride = n (sem buracos).
        float[] soa = new float[n * 14];
        for (int i = 0; i < n; i++)
        {
            int bi = i * 14;
            for (int d = 0; d < 14; d++) soa[d * n + i] = aos[bi + d];
        }

        _dims = soa;
        _labels = labs;
        _n = n;
    }

    /// <summary>
    /// Retorna quantos dos 5 vizinhos mais próximos são fraude (0..5).
    /// fraud_score = retorno / 5 ; approved = retorno &lt; 3 (threshold 0.6).
    /// </summary>
    public int FraudCountTop5(ReadOnlySpan<float> q)
    {
        var dims = _dims;
        var labs = _labels;
        int N = _n;
        if (N == 0) return 0;

        // Broadcast de cada dimensão do query (hoisted para fora do laço).
        Vector<float> q0 = new(q[0]), q1 = new(q[1]), q2 = new(q[2]), q3 = new(q[3]),
                      q4 = new(q[4]), q5 = new(q[5]), q6 = new(q[6]), q7 = new(q[7]),
                      q8 = new(q[8]), q9 = new(q[9]), q10 = new(q[10]), q11 = new(q[11]),
                      q12 = new(q[12]), q13 = new(q[13]);

        Span<float> bd = stackalloc float[5] { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
        Span<int> bi = stackalloc int[5] { -1, -1, -1, -1, -1 };
        float worst = float.MaxValue;

        int W = Vector<float>.Count;
        int n0 = 0, n1 = N, n2 = 2 * N, n3 = 3 * N, n4 = 4 * N, n5 = 5 * N, n6 = 6 * N,
            n7 = 7 * N, n8 = 8 * N, n9 = 9 * N, n10 = 10 * N, n11 = 11 * N, n12 = 12 * N, n13 = 13 * N;

        int limit = N - W;
        int b = 0;
        for (; b <= limit; b += W)
        {
            Vector<float> d, acc;
            d = new Vector<float>(dims, n0 + b) - q0; acc = d * d;
            d = new Vector<float>(dims, n1 + b) - q1; acc += d * d;
            d = new Vector<float>(dims, n2 + b) - q2; acc += d * d;
            d = new Vector<float>(dims, n3 + b) - q3; acc += d * d;
            d = new Vector<float>(dims, n4 + b) - q4; acc += d * d;
            d = new Vector<float>(dims, n5 + b) - q5; acc += d * d;
            d = new Vector<float>(dims, n6 + b) - q6; acc += d * d;
            d = new Vector<float>(dims, n7 + b) - q7; acc += d * d;
            d = new Vector<float>(dims, n8 + b) - q8; acc += d * d;
            d = new Vector<float>(dims, n9 + b) - q9; acc += d * d;
            d = new Vector<float>(dims, n10 + b) - q10; acc += d * d;
            d = new Vector<float>(dims, n11 + b) - q11; acc += d * d;
            d = new Vector<float>(dims, n12 + b) - q12; acc += d * d;
            d = new Vector<float>(dims, n13 + b) - q13; acc += d * d;

            if (Vector.LessThanAny(acc, new Vector<float>(worst)))
            {
                for (int l = 0; l < W; l++)
                {
                    float dist = acc[l];
                    if (dist < worst) Insert(bd, bi, ref worst, dist, b + l);
                }
            }
        }

        // Tail escalar (só executa se N % W != 0; com N=100000 e W in {4,8} não roda).
        for (; b < N; b++)
        {
            float s = 0f;
            s += Sq(dims[n0 + b] - q[0]); s += Sq(dims[n1 + b] - q[1]); s += Sq(dims[n2 + b] - q[2]);
            s += Sq(dims[n3 + b] - q[3]); s += Sq(dims[n4 + b] - q[4]); s += Sq(dims[n5 + b] - q[5]);
            s += Sq(dims[n6 + b] - q[6]); s += Sq(dims[n7 + b] - q[7]); s += Sq(dims[n8 + b] - q[8]);
            s += Sq(dims[n9 + b] - q[9]); s += Sq(dims[n10 + b] - q[10]); s += Sq(dims[n11 + b] - q[11]);
            s += Sq(dims[n12 + b] - q[12]); s += Sq(dims[n13 + b] - q[13]);
            if (s < worst) Insert(bd, bi, ref worst, s, b);
        }

        int fraud = 0;
        for (int j = 0; j < 5; j++)
        {
            int ix = bi[j];
            if (ix >= 0 && labs[ix] != 0) fraud++;
        }
        return fraud;
    }

    private static float Sq(float x) => x * x;

    // Mantém os 5 menores. Empate desfeito por menor índice (ordem do dataset).
    private static void Insert(Span<float> bd, Span<int> bi, ref float worst, float dist, int idx)
    {
        int wj = 0;
        float wv = bd[0];
        for (int j = 1; j < 5; j++) { if (bd[j] > wv) { wv = bd[j]; wj = j; } }
        bd[wj] = dist;
        bi[wj] = idx;

        float nw = bd[0];
        for (int j = 1; j < 5; j++) { if (bd[j] > nw) nw = bd[j]; }
        worst = nw;
    }
}
