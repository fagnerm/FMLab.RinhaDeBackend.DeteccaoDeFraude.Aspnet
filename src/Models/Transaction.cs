using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Models;
public class Transaction
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("installments")]
    public int Installments { get; set; }

    [JsonPropertyName("requested_at")]
    public DateTime RequestedAt { get; set; }
}