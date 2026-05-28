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

// 0 = carregando, 1 = pronto para servir tráfego.
// /ready devolve 503 enquanto = 0; flip para 1 só após load + warmup.
var ready = 0;

var app = builder.Build();

if (!string.IsNullOrEmpty(socketPath))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        // Kestrel cria o socket com a umask do processo (0644 no Alpine).
        // nginx roda como UID 101 e precisa de write — sem 0777 o connect retorna EACCES.
        for (int i = 0; i < 50 && !File.Exists(socketPath); i++) Thread.Sleep(20);
        if (File.Exists(socketPath))
            File.SetUnixFileMode(socketPath,
                UnixFileMode.UserRead  | UnixFileMode.UserWrite  | UnixFileMode.UserExecute  |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
    });
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
    await next(ctx);
});

app.MapGet("/ready", () =>
    Volatile.Read(ref ready) == 1
        ? Results.Ok("Ready")
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

// Inicia load do .idx + warmup só depois que Kestrel já está listening (socket bindado, chmod aplicado).
// O endpoint /ready devolve 503 durante esse período — runner/health check espera o 200.
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        await vectorStore.LoadAsync();

        // Warmup: aquece cache/branch predictors da CPU executando o caminho quente.
        // Sem isso a primeira centena de requests paga o custo de cold cache.
        var dummy = new float[14] { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f,
                                    0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f };
        for (int i = 0; i < 200; i++) vectorStore.Search(dummy);

        Volatile.Write(ref ready, 1);
    });
});

app.Run();
