using System.Text.Json.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

public class Customer
{
    [JsonPropertyName("avg_amount")]
    public double AverageAmount { get; init; }
    
    [JsonPropertyName("tx_count_24h")]
    public int TransactionsLast24h { get; init; }

    [JsonPropertyName("known_merchants")]
    public string[] KnownMerchants { get; init; } = [];
}