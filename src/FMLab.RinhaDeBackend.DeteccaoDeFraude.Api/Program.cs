using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.ReferenceData;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

// Run offline index build: dotnet run -- --build-index
if (args.Contains("--build-index"))
{
    var appData = Path.Combine(AppContext.BaseDirectory, "App_Data");
    await IndexBuilder.BuildAndSaveAsync(appData);

    return;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

var referenceData = new ReferenceDataService();
var vectorStore   = new VectorStore();
var responseTable = new FraudResponseTable();
var handler       = new FraudDetectionHandler(referenceData, vectorStore, responseTable);

await vectorStore.LoadAsync();

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/fraud-score")
    {
        var request = await ctx.Request.ReadFromJsonAsync(AppJsonContext.Default.FraudDetectionRequest);
        var bytes = handler.Handle(request!);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.Body.WriteAsync(bytes);
        return;
    }
    await next(ctx);
});

app.MapGet("/ready", () => Results.Ok("Ready"));

app.Run();
