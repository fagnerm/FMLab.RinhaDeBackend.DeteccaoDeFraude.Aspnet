using FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.Fraud_Detection;
public static class FraudDetectionEndpoints
{
    public static void MapFraudDetectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/fraud-score", async (FraudDetectionRequest request) =>
        {
            return await Task.FromResult(Results.Ok(new FraudDetectionResponse()
            {
                IsFraud = true,
                FraudScore = 0.6f
            }));
        });
    }
}

