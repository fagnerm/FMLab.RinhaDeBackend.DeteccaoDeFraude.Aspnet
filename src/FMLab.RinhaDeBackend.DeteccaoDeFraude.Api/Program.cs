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

    var projectRoot = Path.Combine(Directory.GetParent(AppContext.BaseDirectory)!.Parent!.Parent!.Parent!.FullName, "App_Data");
    File.Copy(Path.Combine(appData, "references.idx"), Path.Combine(projectRoot, "references.idx"), overwrite: true);

    Console.WriteLine("Copying references.idx to APP_Data project root folder: " + projectRoot);
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



builder.Services.AddSingleton<ReferenceDataService>();
builder.Services.AddSingleton<VectorStore>();
builder.Services.AddHostedService<VectorStoreLoader>();
builder.Services.AddSingleton<FraudDetectionHandler>();

var app = builder.Build();

if (!string.IsNullOrEmpty(socketPath))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        if (File.Exists(socketPath))
            File.SetUnixFileMode(socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
    });
}

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/fraud-score")
    {
        var handler = ctx.RequestServices.GetRequiredService<FraudDetectionHandler>();
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
    await next(ctx);
});
app.MapGet("/ready", (VectorStore store) => store.IsReady ? Results.Ok("Ready") : Results.NoContent());

app.Run();
