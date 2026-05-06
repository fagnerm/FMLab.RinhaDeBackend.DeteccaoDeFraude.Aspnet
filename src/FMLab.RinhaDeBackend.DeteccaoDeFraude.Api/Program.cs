using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.ReferenceData;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.Fraud_Detection;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

// Run offline index build: dotnet run -- --build-index
if (args.Contains("--build-index"))
{
    var appData = Path.Combine(AppContext.BaseDirectory, "App_Data");
    await IndexBuilder.BuildAndSaveAsync(appData);
    return;
}

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

builder.Services.AddSingleton<ReferenceDataService>();
builder.Services.AddSingleton<VectorStore>();
builder.Services.AddHostedService<VectorStoreLoader>();
builder.Services.AddSingleton<FraudDetectionHandler>();

var app = builder.Build();

app.MapGet("/ready", (VectorStore store) =>
    store.IsReady ? Results.Ok("Ready") : Results.NoContent());

app.MapFraudDetectionEndpoints();

app.Run();
