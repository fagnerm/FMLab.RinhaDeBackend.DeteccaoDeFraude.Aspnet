using System.Text.Json.Serialization;

public record FraudDetectionResponse
{
    [JsonPropertyName("approved")] public bool IsFraud { get; init; }
    [JsonPropertyName("fraud_score")] public float FraudScore { get; init; }
}