using FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.Fraud_Detection;
public static class FraudDetectionEndpoints
{
    public static void MapFraudDetectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/fraud-score", async (FraudDetectionRequest request) =>
        {
            var handler = new FraudDetectionHandler();
            var response = handler.Handle(request);

            return await Task.FromResult(Results.Ok(response));
        });
    }
}

