using System.Text.Json.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Models;

public class Merchant
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("mcc")]
    public required string MerchantCategoryCode { get; init; }

    [JsonPropertyName("avg_amount")]    
    public required decimal AverageAmount { get; init; }
}