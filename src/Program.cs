using FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.Fraud_Detection;

var builder = WebApplication.CreateSlimBuilder(args);

var app = builder.Build();

app.MapGet("/ready", () => { return Results.Ok("Ready"); });
app.MapFraudDetectionEndpoints();

app.Run();