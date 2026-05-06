namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

/// <summary>
/// Serviço de background responsável por inicializar o VectorStore na subida da aplicação.
/// Executa em paralelo com o restante do startup — o endpoint /ready fica indisponível
/// até que o carregamento seja concluído com sucesso.
/// Em caso de falha, registra um log crítico mas não derruba o processo,
/// permitindo que outros endpoints continuem respondendo.
/// </summary>
public class VectorStoreLoader(VectorStore store, ILogger<VectorStoreLoader> logger) : BackgroundService
{
    /// <summary>
    /// Ponto de entrada do BackgroundService: dispara o carregamento assíncrono do índice.
    /// O CancellationToken é cancelado pelo host na parada da aplicação.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // Carrega os vetores e constrói o índice IVF (pode levar alguns segundos).
            await store.LoadAsync(ct);

            // Após retornar, VectorStore.IsReady == true e o endpoint /ready passa a responder 200.
            logger.LogInformation("VectorStore loaded. Ready to serve requests.");
        }
        catch (Exception ex)
        {
            // Falha crítica: o store não estará disponível, mas a API continua em pé.
            // O endpoint /ready permanecerá indisponível enquanto IsReady == false.
            logger.LogCritical(ex, "VectorStore failed to load. /ready will remain unavailable.");
        }
    }
}
