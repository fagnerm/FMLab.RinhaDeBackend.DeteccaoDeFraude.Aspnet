namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

public class VectorStoreLoader(VectorStore store, ILogger<VectorStoreLoader> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await store.LoadAsync(ct);
            logger.LogInformation("VectorStore loaded. Ready to serve requests.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "VectorStore failed to load. /ready will remain unavailable.");
        }
    }
}
