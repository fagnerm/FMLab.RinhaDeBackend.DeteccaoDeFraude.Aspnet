using System.Text.Json.Serialization;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.ReferenceData;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;

[JsonSerializable(typeof(FraudDetectionRequest))]
[JsonSerializable(typeof(FraudDetectionResponse))]
[JsonSerializable(typeof(Transaction))]
[JsonSerializable(typeof(Customer))]
[JsonSerializable(typeof(Merchant))]
[JsonSerializable(typeof(Terminal))]
[JsonSerializable(typeof(LastTransaction))]
[JsonSerializable(typeof(NormalizationParams))]
[JsonSerializable(typeof(Dictionary<string, float>))]
[JsonSerializable(typeof(VectorRow))]
[JsonSerializable(typeof(float[]))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class AppJsonContext : JsonSerializerContext { }
