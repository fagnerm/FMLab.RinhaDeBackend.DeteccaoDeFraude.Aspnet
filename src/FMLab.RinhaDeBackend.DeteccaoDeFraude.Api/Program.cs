using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.ReferenceData;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

// Run offline index build: dotnet run -- --build-index
if (args.Contains("--build-index"))
{
    var appData = Path.Combine(AppContext.BaseDirectory, "App_Data");
    await IndexBuilder.BuildAndSaveAsync(appData);

    return;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();

var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");
if (!string.IsNullOrEmpty(socketPath))
{
    if (File.Exists(socketPath)) File.Delete(socketPath);
    builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socketPath));
}

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

var referenceData = new ReferenceDataService();
var vectorStore   = new VectorStore();
var responseTable = new FraudResponseTable();
var handler       = new FraudDetectionHandler(referenceData, vectorStore, responseTable);

await vectorStore.LoadAsync();

var app = builder.Build();

if (!string.IsNullOrEmpty(socketPath))
{
    app.Lifetime.ApplicationStarted.Register(() =>
        File.SetUnixFileMode(socketPath,
            UnixFileMode.UserRead  | UnixFileMode.UserWrite  |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
}

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
    if (ctx.Request.Path == "/ping")
    {
        ctx.Response.StatusCode = 200;
        return;
    }
    if (ctx.Request.Path == "/ready")
    {
        if (vectorStore.IsReady)
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("Ready");
            return;
        }

        ctx.Response.StatusCode = 204;
        return;
    }
    await next(ctx);
});

app.Run();
