using System.Text.Json.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Models
{
    public class LastTransaction
    {
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; init; }

        [JsonPropertyName("km_from_current")]       
        public decimal KmFromCurrent { get; init; }
    }
}