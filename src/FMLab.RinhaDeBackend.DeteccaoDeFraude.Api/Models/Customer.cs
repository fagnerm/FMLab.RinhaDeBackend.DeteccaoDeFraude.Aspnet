using System.Text.Json.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Models;

public class Customer
{
    [JsonPropertyName("avg_amount")]
    public decimal AverageAmoungt { get; init; }
    
    [JsonPropertyName("tx_count_24h")]
    public int TransactionsLast24h { get; init; }

    [JsonPropertyName("known_merchants")]
    public IReadOnlyList<string> KnownMerchants { get; init; } = Array.Empty<string>();
}