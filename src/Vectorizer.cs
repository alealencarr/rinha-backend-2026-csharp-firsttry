using System.Globalization;
using System.Text.Json;

/// <summary>
/// Transforma o payload JSON da transação no vetor de 14 dimensões definido em
/// REGRAS_DE_DETECCAO.md. Parsing direto com Utf8JsonReader (sem desserializar
/// para objetos), comparando nomes de campo em UTF-8. Zero alocação no heap.
///
/// O cálculo é feito em DOUBLE para que o arredondamento para 4 casas (feito na
/// quantização do FraudStore) bata com o do gerador oficial. Computar em float
/// introduziria erro na 4ª casa em alguns casos e desalinharia vizinhos.
/// </summary>
internal static class Vectorizer
{
    // Constantes de normalization.json (estáveis durante o teste).
    private const double MaxAmount = 10000.0;
    private const double MaxInstallments = 12.0;
    private const double AmountVsAvgRatio = 10.0;
    private const double MaxMinutes = 1440.0;
    private const double MaxKm = 1000.0;
    private const double MaxTxCount24h = 20.0;
    private const double MaxMerchantAvg = 10000.0;

    public static void BuildVector(ReadOnlySpan<byte> json, Span<double> v)
    {
        double amount = 0, custAvg = 0, merchAvg = 0, kmHome = 0, lastKm = 0;
        int installments = 0, txCount = 0, mcc = -1;
        bool isOnline = false, cardPresent = false, hasLast = false;
        DateTime reqUtc = DateTime.UnixEpoch, lastUtc = DateTime.UnixEpoch;

        // known_merchants e merchant.id guardados como UTF-8 cru (sem alocar string).
        Span<byte> kmBuf = stackalloc byte[512];
        Span<int> kmStart = stackalloc int[32];
        Span<int> kmLen = stackalloc int[32];
        int kmCount = 0, kmPos = 0;
        Span<byte> midBuf = stackalloc byte[64];
        int midLen = 0;

        var r = new Utf8JsonReader(json);
        if (r.Read() && r.TokenType == JsonTokenType.StartObject)
        {
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) { r.Skip(); continue; }

                if (r.ValueTextEquals("transaction"u8))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.StartObject)
                    {
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.ValueTextEquals("amount"u8)) { r.Read(); amount = r.GetDouble(); }
                            else if (r.ValueTextEquals("installments"u8)) { r.Read(); installments = r.GetInt32(); }
                            else if (r.ValueTextEquals("requested_at"u8)) { r.Read(); reqUtc = ParseUtc(r.ValueSpan); }
                            else { r.Read(); r.Skip(); }
                        }
                    }
                }
                else if (r.ValueTextEquals("customer"u8))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.StartObject)
                    {
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.ValueTextEquals("avg_amount"u8)) { r.Read(); custAvg = r.GetDouble(); }
                            else if (r.ValueTextEquals("tx_count_24h"u8)) { r.Read(); txCount = r.GetInt32(); }
                            else if (r.ValueTextEquals("known_merchants"u8))
                            {
                                r.Read();
                                if (r.TokenType == JsonTokenType.StartArray)
                                {
                                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                                    {
                                        if (r.TokenType != JsonTokenType.String) continue;
                                        var vs = r.ValueSpan;
                                        if (kmCount < 32 && kmPos + vs.Length <= 512)
                                        {
                                            vs.CopyTo(kmBuf[kmPos..]);
                                            kmStart[kmCount] = kmPos;
                                            kmLen[kmCount] = vs.Length;
                                            kmPos += vs.Length;
                                            kmCount++;
                                        }
                                    }
                                }
                            }
                            else { r.Read(); r.Skip(); }
                        }
                    }
                }
                else if (r.ValueTextEquals("merchant"u8))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.StartObject)
                    {
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.ValueTextEquals("id"u8))
                            {
                                r.Read();
                                var vs = r.ValueSpan;
                                midLen = Math.Min(vs.Length, 64);
                                vs[..midLen].CopyTo(midBuf);
                            }
                            else if (r.ValueTextEquals("mcc"u8)) { r.Read(); mcc = ParseMcc(r.ValueSpan); }
                            else if (r.ValueTextEquals("avg_amount"u8)) { r.Read(); merchAvg = r.GetDouble(); }
                            else { r.Read(); r.Skip(); }
                        }
                    }
                }
                else if (r.ValueTextEquals("terminal"u8))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.StartObject)
                    {
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.ValueTextEquals("is_online"u8)) { r.Read(); isOnline = r.TokenType == JsonTokenType.True; }
                            else if (r.ValueTextEquals("card_present"u8)) { r.Read(); cardPresent = r.TokenType == JsonTokenType.True; }
                            else if (r.ValueTextEquals("km_from_home"u8)) { r.Read(); kmHome = r.GetDouble(); }
                            else { r.Read(); r.Skip(); }
                        }
                    }
                }
                else if (r.ValueTextEquals("last_transaction"u8))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.StartObject)
                    {
                        hasLast = true;
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.ValueTextEquals("timestamp"u8)) { r.Read(); lastUtc = ParseUtc(r.ValueSpan); }
                            else if (r.ValueTextEquals("km_from_current"u8)) { r.Read(); lastKm = r.GetDouble(); }
                            else { r.Read(); r.Skip(); }
                        }
                    }
                    // null -> hasLast permanece false (sentinela -1 nas dims 5 e 6)
                }
                else { r.Read(); r.Skip(); }
            }
        }

        // ----- montagem das 14 dimensões (ordem e fórmulas do doc oficial) -----
        v[0] = Clamp01(amount / MaxAmount);
        v[1] = Clamp01(installments / MaxInstallments);
        v[2] = Clamp01((amount / custAvg) / AmountVsAvgRatio);

        int hour = reqUtc.Hour;                       // 0-23 UTC
        int dow = ((int)reqUtc.DayOfWeek + 6) % 7;    // seg=0 ... dom=6
        v[3] = hour / 23.0;                            // sem clamp (doc não limita)
        v[4] = dow / 6.0;

        if (hasLast)
        {
            double mins = (reqUtc - lastUtc).TotalMinutes;
            v[5] = Clamp01(mins / MaxMinutes);
            v[6] = Clamp01(lastKm / MaxKm);
        }
        else
        {
            v[5] = -1.0;
            v[6] = -1.0;
        }

        v[7] = Clamp01(kmHome / MaxKm);
        v[8] = Clamp01(txCount / MaxTxCount24h);
        v[9] = isOnline ? 1.0 : 0.0;
        v[10] = cardPresent ? 1.0 : 0.0;
        v[11] = IsUnknown(midBuf, midLen, kmBuf, kmStart, kmLen, kmCount) ? 1.0 : 0.0;
        v[12] = MccRisk(mcc);
        v[13] = Clamp01(merchAvg / MaxMerchantAvg);
    }

    private static double Clamp01(double x)
    {
        if (double.IsNaN(x)) return 0.0;   // ex.: 0/0 em amount_vs_avg
        if (x < 0.0) return 0.0;
        if (x > 1.0) return 1.0;
        return x;
    }

    private static int ParseMcc(ReadOnlySpan<byte> s)
    {
        if (s.Length == 0 || s.Length > 9) return -1;
        int x = 0;
        for (int i = 0; i < s.Length; i++)
        {
            byte c = s[i];
            if (c < (byte)'0' || c > (byte)'9') return -1;
            x = x * 10 + (c - '0');
        }
        return x;
    }

    // mcc_risk.json (default 0.5 quando não mapeado).
    private static double MccRisk(int mcc) => mcc switch
    {
        5411 => 0.15,
        5812 => 0.30,
        5912 => 0.20,
        5944 => 0.45,
        7801 => 0.80,
        7802 => 0.75,
        7995 => 0.85,
        4511 => 0.35,
        5311 => 0.25,
        5999 => 0.50,
        _ => 0.5,
    };

    private static bool IsUnknown(ReadOnlySpan<byte> mid, int midLen, ReadOnlySpan<byte> kmBuf,
                                  ReadOnlySpan<int> start, ReadOnlySpan<int> len, int count)
    {
        var m = mid[..midLen];
        for (int i = 0; i < count; i++)
        {
            if (kmBuf.Slice(start[i], len[i]).SequenceEqual(m)) return false; // conhecido
        }
        return true; // não está em known_merchants -> 1
    }

    private static DateTime ParseUtc(ReadOnlySpan<byte> s)
    {
        Span<char> c = stackalloc char[40];
        int n = Math.Min(s.Length, 40);
        for (int i = 0; i < n; i++) c[i] = (char)s[i]; // timestamps são ASCII
        if (DateTimeOffset.TryParse(c[..n], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            return dto.UtcDateTime;
        }
        return DateTime.UnixEpoch;
    }
}
