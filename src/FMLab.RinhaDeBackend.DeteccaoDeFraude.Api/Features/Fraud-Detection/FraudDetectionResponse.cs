using System.Text.Json.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

public record FraudDetectionResponse
{
    [JsonPropertyName("approved")] public bool Approved { get; init; }
    [JsonPropertyName("fraud_score")] public float FraudScore { get; init; }
}
