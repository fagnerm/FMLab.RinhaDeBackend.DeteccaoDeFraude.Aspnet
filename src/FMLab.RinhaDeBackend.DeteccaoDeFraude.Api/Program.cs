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

// Pre-warm thread pool to avoid starvation during initial load ramp-up.
// Without this, the runtime adds threads slowly (one per 500ms) under sudden load.
ThreadPool.SetMinThreads(32, 32);

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxConcurrentConnections = null;
    options.Limits.MaxRequestBodySize = 8_192;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);

    var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");
    if (!string.IsNullOrEmpty(socketPath))
        options.ListenUnixSocket(socketPath);
    else
        options.ListenAnyIP(8080);
});

builder.Services.Configure<SocketTransportOptions>(options =>
{
    options.NoDelay = true;
    options.IOQueueCount = Environment.ProcessorCount;
    options.WaitForDataBeforeAllocatingBuffer = true;
});

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

builder.Services.AddSingleton<ReferenceDataService>();
builder.Services.AddSingleton<VectorStore>();
builder.Services.AddHostedService<VectorStoreLoader>();
builder.Services.AddSingleton<FraudDetectionHandler>();

var app = builder.Build();

var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");
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
