using System.Numerics;
using System.Text;
using System.Text.Json;

/// <summary>
/// Store vetorial + classificador k-NN EXATO que reproduz o gabarito oficial.
///
/// O gabarito do teste é gerado rodando exatamente: k=5, votação por maioria,
/// distância euclidiana sobre vetores de 14 dimensões com 4 casas decimais
/// (REGRAS_DE_DETECCAO.md / DATASET.md). Para zerar FP/FN é preciso reproduzir
/// isso bit a bit. Por isso:
///   - quantizamos tudo para inteiro na escala 10000 (4 casas) => distância
///     inteira EXATA, sem ruído de ponto flutuante;
///   - k=5 e limiar 0.6 são fixos (NÃO se afina nada: afinar afastaria do gabarito);
///   - empate de distância é resolvido pelo MENOR índice (ordem do dataset),
///     que é o comportamento estável do gerador;
///   - int16 (short) também corta a banda de memória pela metade => ~2x vazão.
/// </summary>
internal sealed class FraudStore
{
    private const int K = 5;
    private const int Scale = 10000;

    private short[] _dims = Array.Empty<short>();   // SoA quantizada: _dims[d*N + i]
    private byte[] _labels = Array.Empty<byte>();   // 1 = fraud, 0 = legit
    private int _n;

    private byte[][] _responses = Array.Empty<byte[]>();
    private volatile bool _ready;

    public bool Ready => _ready;
    public byte[][] Responses => _responses;

    public void Load(string jsonPath)
    {
        byte[] json = File.ReadAllBytes(jsonPath);
        Index(json);
        BuildResponses();
        _ready = true;
        Console.Error.WriteLine($"READY: n={_n} k=5 limiar=0.6 (int16 escala {Scale}, euclidiana exata)");
    }

    // 4 casas decimais -> inteiro. Math.Round em ToEven (padrão) casa com o
    // printf("%.4f") usado para serializar as referências.
    private static short Q(double v) => (short)Math.Round(v * Scale, MidpointRounding.ToEven);

    private void Index(ReadOnlySpan<byte> json)
    {
        int cap = 100_000;
        short[] aos = new short[cap * 14];
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
                        if (d < 14) aos[baseI + d] = Q(r.GetDouble());
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

        // AoS -> SoA (layout que o SIMD percorre por dimensão).
        short[] soa = new short[n * 14];
        for (int i = 0; i < n; i++)
        {
            int bi = i * 14;
            for (int d = 0; d < 14; d++) soa[d * n + i] = aos[bi + d];
        }

        _dims = soa;
        _labels = labs;
        _n = n;
    }

    private void BuildResponses()
    {
        // k=5 fixo: fraud_score = c/5; approved = fraud_score < 0.6 (c <= 2).
        string[] scores = { "0.0", "0.2", "0.4", "0.6", "0.8", "1.0" };
        var arr = new byte[6][];
        for (int c = 0; c <= 5; c++)
        {
            bool approved = (c / 5.0) < 0.6;
            string json = "{\"approved\":" + (approved ? "true" : "false") + ",\"fraud_score\":" + scores[c] + "}";
            arr[c] = Encoding.UTF8.GetBytes(json);
        }
        _responses = arr;
    }

    /// <summary>Conta fraudes entre os 5 vizinhos mais próximos (0..5).</summary>
    public int CountTop5(ReadOnlySpan<double> v)
    {
        Span<short> q = stackalloc short[14];
        for (int d = 0; d < 14; d++) q[d] = Q(v[d]);

        Span<int> bd = stackalloc int[K];
        Span<byte> bl = stackalloc byte[K];
        for (int j = 0; j < K; j++) { bd[j] = int.MaxValue; bl[j] = 0; }
        int worst = int.MaxValue, filled = 0;

        var dims = _dims; var labs = _labels; int N = _n;
        if (N == 0) return 0;

        int n0 = 0, n1 = N, n2 = 2 * N, n3 = 3 * N, n4 = 4 * N, n5 = 5 * N, n6 = 6 * N,
            n7 = 7 * N, n8 = 8 * N, n9 = 9 * N, n10 = 10 * N, n11 = 11 * N, n12 = 12 * N, n13 = 13 * N;

        int b = 0;
        int Ws = Vector<short>.Count;   // 16 com AVX2
        int Wi = Vector<int>.Count;     // 8 com AVX2  (= Ws/2)

        if (Vector.IsHardwareAccelerated && N >= Ws)
        {
            Vector<short> q0 = new(q[0]), q1 = new(q[1]), q2 = new(q[2]), q3 = new(q[3]),
                          q4 = new(q[4]), q5 = new(q[5]), q6 = new(q[6]), q7 = new(q[7]),
                          q8 = new(q[8]), q9 = new(q[9]), q10 = new(q[10]), q11 = new(q[11]),
                          q12 = new(q[12]), q13 = new(q[13]);

            for (; b + Ws <= N; b += Ws)
            {
                Vector<short> sv, diff;
                Vector<int> lo, hi, accLo = Vector<int>.Zero, accHi = Vector<int>.Zero;

                sv = new Vector<short>(dims, n0 + b); diff = sv - q0; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n1 + b); diff = sv - q1; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n2 + b); diff = sv - q2; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n3 + b); diff = sv - q3; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n4 + b); diff = sv - q4; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n5 + b); diff = sv - q5; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n6 + b); diff = sv - q6; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n7 + b); diff = sv - q7; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n8 + b); diff = sv - q8; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n9 + b); diff = sv - q9; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n10 + b); diff = sv - q10; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n11 + b); diff = sv - q11; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n12 + b); diff = sv - q12; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;
                sv = new Vector<short>(dims, n13 + b); diff = sv - q13; Vector.Widen(diff, out lo, out hi); accLo += lo * lo; accHi += hi * hi;

                // accLo -> refs b..b+Wi-1 ; accHi -> refs b+Wi..b+2Wi-1 (ordem de índice)
                for (int l = 0; l < Wi; l++) { int dd = accLo[l]; if (dd < worst) Insert(bd, bl, ref worst, ref filled, dd, labs[b + l]); }
                for (int l = 0; l < Wi; l++) { int dd = accHi[l]; if (dd < worst) Insert(bd, bl, ref worst, ref filled, dd, labs[b + Wi + l]); }
            }
        }

        // cauda escalar (e caminho completo se não houver SIMD)
        for (; b < N; b++)
        {
            int s = 0;
            s += Sq(dims[n0 + b] - q[0]); s += Sq(dims[n1 + b] - q[1]); s += Sq(dims[n2 + b] - q[2]);
            s += Sq(dims[n3 + b] - q[3]); s += Sq(dims[n4 + b] - q[4]); s += Sq(dims[n5 + b] - q[5]);
            s += Sq(dims[n6 + b] - q[6]); s += Sq(dims[n7 + b] - q[7]); s += Sq(dims[n8 + b] - q[8]);
            s += Sq(dims[n9 + b] - q[9]); s += Sq(dims[n10 + b] - q[10]); s += Sq(dims[n11 + b] - q[11]);
            s += Sq(dims[n12 + b] - q[12]); s += Sq(dims[n13 + b] - q[13]);
            if (s < worst) Insert(bd, bl, ref worst, ref filled, s, labs[b]);
        }

        int c = 0;
        for (int i = 0; i < filled; i++) c += bl[i];
        return c;
    }

    private static int Sq(int x) => x * x;

    // Mantém os K menores. Empate (dd == worst) NÃO substitui => o de menor
    // índice (visto antes) permanece, reproduzindo o desempate do gerador.
    private static void Insert(Span<int> bd, Span<byte> bl, ref int worst, ref int filled, int dist, byte lab)
    {
        if (filled < K)
        {
            bd[filled] = dist; bl[filled] = lab; filled++;
            if (filled == K)
            {
                int w = bd[0];
                for (int j = 1; j < K; j++) if (bd[j] > w) w = bd[j];
                worst = w;
            }
            return;
        }
        int wj = 0, wv = bd[0];
        for (int j = 1; j < K; j++) if (bd[j] > wv) { wv = bd[j]; wj = j; }
        bd[wj] = dist; bl[wj] = lab;
        int nw = bd[0];
        for (int j = 1; j < K; j++) if (bd[j] > nw) nw = bd[j];
        worst = nw;
    }
}
