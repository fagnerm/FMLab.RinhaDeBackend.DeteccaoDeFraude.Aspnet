using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.ReferenceData;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.Fraud_Detection;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSingleton<ReferenceDataService>();
builder.Services.AddSingleton<VectorStore>();
builder.Services.AddHostedService<VectorStoreLoader>();

var app = builder.Build();

app.MapGet("/ready", (VectorStore store) =>
    store.IsReady ? Results.Ok("Ready") : Results.NoContent());

app.MapFraudDetectionEndpoints();

app.Run();
