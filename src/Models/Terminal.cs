using System.Text.Json.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Models;
public class Terminal
{
    [JsonPropertyName("is_online")]
    public bool IsOnline { get; init; }

    [JsonPropertyName("card_present")]
    public bool CardPresent { get; init; }

    [JsonPropertyName("km_from_home")]
    public decimal KmFromHome { get; init; }
}