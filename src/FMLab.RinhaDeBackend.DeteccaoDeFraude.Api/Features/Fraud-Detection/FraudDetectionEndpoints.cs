using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.ReferenceData;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.Fraud_Detection;

public static class FraudDetectionEndpoints
{
    public static void MapFraudDetectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/fraud-score", (FraudDetectionRequest request, ReferenceDataService referenceData, VectorStore vectorStore) =>
        {
            var handler = new FraudDetectionHandler(referenceData, vectorStore);
            var response = handler.Handle(request);
            return Results.Ok(response);
        });
    }
}
