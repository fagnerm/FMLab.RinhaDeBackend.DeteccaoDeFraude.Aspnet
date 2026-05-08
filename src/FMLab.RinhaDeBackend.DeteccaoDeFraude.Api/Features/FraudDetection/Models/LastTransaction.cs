using System.Text.Json.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection
{
    public class LastTransaction
    {
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; init; }

        [JsonPropertyName("km_from_current")]       
        public double KmFromCurrent { get; init; }
    }
}