using FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.Fraud_Detection;

public static class FraudDetectionEndpoints
{
    public static void MapFraudDetectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/fraud-score", (FraudDetectionRequest request, FraudDetectionHandler handler) =>
            Results.Ok(handler.Handle(request)));
    }
}
