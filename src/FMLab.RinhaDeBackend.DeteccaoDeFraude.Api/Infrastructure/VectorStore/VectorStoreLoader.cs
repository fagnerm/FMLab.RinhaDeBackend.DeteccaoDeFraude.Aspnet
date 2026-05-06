namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

public class VectorStoreLoader(VectorStore store) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) => store.LoadAsync(ct);
}
