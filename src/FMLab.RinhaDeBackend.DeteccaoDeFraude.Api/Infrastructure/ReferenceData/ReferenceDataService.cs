using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.ReferenceData;

public class ReferenceDataService
{
    public IReadOnlyDictionary<string, float> MccRisk { get; }
    public NormalizationParams Normalization { get; }

    public ReferenceDataService()
    {
        var basePath = AppContext.BaseDirectory;
        MccRisk = Load<Dictionary<string, float>>("mcc_risk.json", basePath);
        Normalization = Load("normalization.json", basePath, AppJsonContext.Default.NormalizationParams);
    }

    static T Load<T>(string fileName, string basePath)
    {
        var path = Path.Combine(basePath, "App_Data", fileName);
        using var stream = File.OpenRead(path);
        return (T)JsonSerializer.Deserialize(stream, typeof(T), AppJsonContext.Default)!;
    }

    static T Load<T>(string fileName, string basePath, JsonTypeInfo<T> typeInfo)
    {
        var path = Path.Combine(basePath, "App_Data", fileName);
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, typeInfo)!;
    }
}

public record NormalizationParams
{
    [JsonPropertyName("max_amount")] public float MaxAmount { get; init; }
    [JsonPropertyName("max_installments")] public float MaxInstallments { get; init; }
    [JsonPropertyName("amount_vs_avg_ratio")] public float AmountVsAvgRatio { get; init; }
    [JsonPropertyName("max_minutes")] public float MaxMinutes { get; init; }
    [JsonPropertyName("max_km")] public float MaxKm { get; init; }
    [JsonPropertyName("max_tx_count_24h")] public float MaxTxCount24h { get; init; }
    [JsonPropertyName("max_merchant_avg_amount")] public float MaxMerchantAvgAmount { get; init; }
}
