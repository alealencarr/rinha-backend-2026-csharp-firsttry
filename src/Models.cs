using System.Text.Json.Serialization;

namespace FraudApi;

// ---- Request payload (POST /fraud-score) ----

public sealed class FraudRequest
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("transaction")] public TransactionDto Transaction { get; set; } = default!;
    [JsonPropertyName("customer")] public CustomerDto Customer { get; set; } = default!;
    [JsonPropertyName("merchant")] public MerchantDto Merchant { get; set; } = default!;
    [JsonPropertyName("terminal")] public TerminalDto Terminal { get; set; } = default!;
    [JsonPropertyName("last_transaction")] public LastTransactionDto? LastTransaction { get; set; }
}

public sealed class TransactionDto
{
    [JsonPropertyName("amount")] public double Amount { get; set; }
    [JsonPropertyName("installments")] public int Installments { get; set; }
    [JsonPropertyName("requested_at")] public string RequestedAt { get; set; } = default!;
}

public sealed class CustomerDto
{
    [JsonPropertyName("avg_amount")] public double AvgAmount { get; set; }
    [JsonPropertyName("tx_count_24h")] public int TxCount24h { get; set; }
    [JsonPropertyName("known_merchants")] public string[] KnownMerchants { get; set; } = [];
}

public sealed class MerchantDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = default!;
    [JsonPropertyName("mcc")] public string Mcc { get; set; } = default!;
    [JsonPropertyName("avg_amount")] public double AvgAmount { get; set; }
}

public sealed class TerminalDto
{
    [JsonPropertyName("is_online")] public bool IsOnline { get; set; }
    [JsonPropertyName("card_present")] public bool CardPresent { get; set; }
    [JsonPropertyName("km_from_home")] public double KmFromHome { get; set; }
}

public sealed class LastTransactionDto
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = default!;
    [JsonPropertyName("km_from_current")] public double KmFromCurrent { get; set; }
}

// ---- Response ----

public sealed class FraudResponse
{
    [JsonPropertyName("approved")] public bool Approved { get; set; }
    [JsonPropertyName("fraud_score")] public double FraudScore { get; set; }
}

[JsonSerializable(typeof(FraudRequest))]
[JsonSerializable(typeof(FraudResponse))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
public partial class AppJsonContext : JsonSerializerContext { }
