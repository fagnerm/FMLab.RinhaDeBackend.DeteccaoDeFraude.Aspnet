using System.Text.Json.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Models;

public class Customer
{
    [JsonPropertyName("avg_amount")]
    public double AverageAmount { get; init; }
    
    [JsonPropertyName("tx_count_24h")]
    public int TransactionsLast24h { get; init; }

    [JsonPropertyName("known_merchants")]
    public HashSet<string> KnownMerchants { get; init; } = [];
}