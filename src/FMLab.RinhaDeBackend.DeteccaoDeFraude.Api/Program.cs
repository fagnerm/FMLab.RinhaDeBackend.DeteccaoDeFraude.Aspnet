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

// Pre-warm thread pool to avoid starvation during initial load ramp-up.
// Without this, the runtime adds threads slowly (one per 500ms) under sudden load.
ThreadPool.SetMinThreads(32, 32);

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

builder.Services.AddSingleton<ReferenceDataService>();
builder.Services.AddSingleton<VectorStore>();
builder.Services.AddHostedService<VectorStoreLoader>();
builder.Services.AddSingleton<FraudDetectionHandler>();

var app = builder.Build();

app.MapGet("/ready", (VectorStore store) => store.IsReady ? Results.Ok("Ready") : Results.NoContent());
app.MapGet("/time", () => Results.Ok("Time"));

app.MapFraudDetectionEndpoints();

app.Run();
