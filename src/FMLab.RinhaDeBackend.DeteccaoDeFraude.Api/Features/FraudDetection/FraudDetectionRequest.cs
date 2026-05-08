using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

public record FraudDetectionRequest
{
    [JsonPropertyName("id")] 
    public required string Id { get; init; }

    [JsonPropertyName("transaction")]
    public required Transaction Transaction { get; init; }

    [JsonPropertyName("customer")] 
    public required Customer Customer { get; init; }
    
    [JsonPropertyName("merchant")] 
    public required Merchant Merchant { get; init; }
    
    [JsonPropertyName("terminal")] 
    public required Terminal Terminal { get; init; }

    [JsonPropertyName("last_transaction")]
    public LastTransaction? LastTransaction { get; init; }
}