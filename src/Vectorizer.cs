using System.Globalization;

namespace FraudApi;

/// <summary>
/// Turns a transaction payload into the 14-dimensional fraud vector (see REGRAS_DE_DETECCAO.md),
/// then quantizes it into the same int16 space used by the reference index.
///
/// Layout: 14 real dims + 2 zero-padding dims = 16 shorts (one AVX2 register).
/// [0,1] maps to [0, SCALE]; the "-1" sentinel (no previous transaction) maps to -SCALE.
/// </summary>
public static class Vectorizer
{
    public const int Dims = 14;
    public const int Pad = 16;
    public const short Scale = 8000;
    public const short Sentinel = -8000;

    // normalization.json (fixed for the whole test)
    private const double MaxAmount = 10000;
    private const double MaxInstallments = 12;
    private const double AmountVsAvgRatio = 10;
    private const double MaxMinutes = 1440;
    private const double MaxKm = 1000;
    private const double MaxTxCount24h = 20;
    private const double MaxMerchantAvg = 10000;

    private static double Clamp(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);

    private static short Q(double x)
    {
        double c = Clamp(x);
        return (short)Math.Round(c * Scale, MidpointRounding.AwayFromZero);
    }

    // mcc_risk.json (fixed); default 0.5 for unknown MCC
    private static double MccRisk(string mcc) => mcc switch
    {
        "5411" => 0.15,
        "5812" => 0.30,
        "5912" => 0.20,
        "5944" => 0.45,
        "7801" => 0.80,
        "7802" => 0.75,
        "7995" => 0.85,
        "4511" => 0.35,
        "5311" => 0.25,
        "5999" => 0.50,
        _ => 0.5,
    };

    private static DateTimeOffset ParseUtc(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>Writes the 16-short quantized query vector into <paramref name="dst"/>.</summary>
    public static void Quantize(FraudRequest r, Span<short> dst)
    {
        var t = r.Transaction;
        var c = r.Customer;
        var m = r.Merchant;
        var term = r.Terminal;

        var cur = ParseUtc(t.RequestedAt);
        int hour = cur.UtcDateTime.Hour;                 // 0..23
        int dow = ((int)cur.UtcDateTime.DayOfWeek + 6) % 7; // Mon=0 .. Sun=6

        dst[0] = Q(t.Amount / MaxAmount);
        dst[1] = Q(t.Installments / MaxInstallments);
        dst[2] = Q((t.Amount / c.AvgAmount) / AmountVsAvgRatio);
        dst[3] = Q(hour / 23.0);
        dst[4] = Q(dow / 6.0);

        if (r.LastTransaction is null)
        {
            dst[5] = Sentinel;
            dst[6] = Sentinel;
        }
        else
        {
            var last = ParseUtc(r.LastTransaction.Timestamp);
            double mins = (cur - last).TotalMinutes;
            dst[5] = Q(mins / MaxMinutes);
            dst[6] = Q(r.LastTransaction.KmFromCurrent / MaxKm);
        }

        dst[7] = Q(term.KmFromHome / MaxKm);
        dst[8] = Q(c.TxCount24h / MaxTxCount24h);
        dst[9] = term.IsOnline ? Scale : (short)0;
        dst[10] = term.CardPresent ? Scale : (short)0;
        dst[11] = IsKnown(m.Id, c.KnownMerchants) ? (short)0 : Scale; // 1 = unknown
        dst[12] = Q(MccRisk(m.Mcc));
        dst[13] = Q(m.AvgAmount / MaxMerchantAvg);

        dst[14] = 0; // padding
        dst[15] = 0;
    }

    private static bool IsKnown(string id, string[] known)
    {
        for (int i = 0; i < known.Length; i++)
            if (string.Equals(known[i], id, StringComparison.Ordinal)) return true;
        return false;
    }
}
