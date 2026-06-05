using System.Text;

/// <summary>
/// Respostas pré-codificadas em UTF-8, indexadas pela contagem de fraudes (0..5)
/// entre os 5 vizinhos. fraud_score = n/5 ; approved = n &lt; 3 (threshold 0.6).
/// Escrever um byte[] pronto evita serialização/formatação no hot path.
/// </summary>
internal static class Responses
{
    public static readonly byte[][] ByFraudCount =
    {
        Enc("{\"approved\":true,\"fraud_score\":0.0}"),  // 0/5
        Enc("{\"approved\":true,\"fraud_score\":0.2}"),  // 1/5
        Enc("{\"approved\":true,\"fraud_score\":0.4}"),  // 2/5
        Enc("{\"approved\":false,\"fraud_score\":0.6}"), // 3/5
        Enc("{\"approved\":false,\"fraud_score\":0.8}"), // 4/5
        Enc("{\"approved\":false,\"fraud_score\":1.0}"), // 5/5
    };

    private static byte[] Enc(string s) => Encoding.UTF8.GetBytes(s);
}
