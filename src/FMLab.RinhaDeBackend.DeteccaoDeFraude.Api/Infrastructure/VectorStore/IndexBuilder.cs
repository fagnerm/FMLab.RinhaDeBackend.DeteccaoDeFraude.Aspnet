using System.IO.Compression;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

/// <summary>
/// Constrói e persiste o índice IVF (Inverted File Index) baseado em K-means offline.
/// Deve ser executado na máquina do desenvolvedor (sem limites de CPU) para pré-computar
/// os centroides e atribuições de cluster, gerando o arquivo App_Data/references.idx
/// que é carregado em tempo de execução pelo VectorStore — evitando rodar K-means no servidor.
///
/// Formato binário do arquivo .idx (little-endian):
///   4 bytes  — magic "RIFV" (identifica o formato do arquivo)
///   4 bytes  — NCC como int32 (número de centroides)
///   4 bytes  — count como int32 (total de vetores)
///   NCC × Stride bytes — centroides quantizados em byte
///   count bytes        — atribuição de cluster por vetor (byte, valor 0..NCC-1)
/// </summary>
public static class IndexBuilder
{
    // Número de dimensões de cada vetor de features (deve bater com VectorStore).
    private const int Dims   = 14;

    // Stride em bytes por vetor: 14 dims + 2 bytes de padding para alinhar a 16 bytes (um load SSE2).
    private const int Stride = 16;

    // Número de centroides do K-means (NCC = Number of Coarse Centroids).
    private const int NCC    = 200;

    // Número de iterações do K-means — pode ser maior offline pois não há limite de tempo.
    private const int Iters  = 10;

    // Assinatura de 4 bytes que identifica o arquivo como um índice IVF válido.
    private static readonly byte[] Magic = "RIFV"u8.ToArray();

    /// <summary>
    /// Ponto de entrada principal: carrega os vetores brutos, executa K-means,
    /// atribui cada vetor ao centroide mais próximo e salva o índice em disco.
    /// </summary>
    public static async Task BuildAndSaveAsync(string appDataDir)
    {
        // Informa se SSE2 está disponível na CPU atual (acelera o cálculo de distâncias).
        Console.Error.WriteLine($"SSE2 supported: {Sse2.IsSupported}");
        Console.Error.WriteLine("Loading vectors...");

        // Caminhos candidatos para o arquivo de referências (gzip tem prioridade por ser menor).
        var gzPath   = Path.Combine(appDataDir, "references.json.gz");
        var jsonPath  = Path.Combine(appDataDir, "references.json");

        // Abre o stream descomprimindo on-the-fly se necessário.
        Stream src = File.Exists(gzPath)
            ? new GZipStream(File.OpenRead(gzPath), CompressionMode.Decompress)
            : File.OpenRead(jsonPath);

        byte[] vecs;   // buffer contíguo com todos os vetores quantizados
        byte[] lbls;   // rótulo por vetor (1 = fraude, 0 = legítimo)
        int    count;  // quantidade real de vetores lidos

        // Lê e fecha o stream antes de processar para liberar o arquivo.
        await using (src)
            (vecs, lbls, count) = await LoadRawAsync(src);

        Console.Error.WriteLine($"Loaded {count:N0} vectors. Running K-means (NCC={NCC}, iterations={Iters})...");

        // Executa K-means e obtém os centroides finais quantizados.
        byte[]   centroids  = RunKMeans(vecs, count);

        // Atribui cada vetor ao centroide mais próximo após a convergência.
        byte[]   assignments = Assign(vecs, count, centroids);

        Console.Error.WriteLine("Saving index...");

        // Destino final do índice binário.
        var idxPath = Path.Combine(appDataDir, "references.idx");

        // Persiste o índice no formato binário definido acima.
        await SaveAsync(idxPath, centroids, assignments, count);
        Console.Error.WriteLine($"Saved to {idxPath}");
    }

    // ── K-means ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executa K-means com inicialização por frequência igual e retorna os centroides
    /// quantizados em byte ao final das iterações.
    /// </summary>
    static byte[] RunKMeans(byte[] vecs, int count)
    {
        var rng = new Random(42); // semente fixa para reprodutibilidade

        // Inicialização por frequência igual na dimensão 0:
        // ordena os índices dos vetores pelo valor da dim-0 e espaça os centroides
        // uniformemente ao longo do ranking — garante cobertura melhor que amostragem aleatória pura.
        int[] sortedIdx = Enumerable.Range(0, count)
            .OrderBy(i => vecs[i * Stride])
            .ToArray();

        // Aloca os centroides (NCC centros × Stride bytes cada).
        var centroids = new byte[NCC * Stride];

        // Copia cada centroide inicial do vetor de referência mais espaçado.
        for (int c = 0; c < NCC; c++)
        {
            // Mapeia linearmente o índice do centroide para uma posição no ranking ordenado.
            int srcIdx = sortedIdx[(long)c * (count - 1) / (NCC - 1)];
            Buffer.BlockCopy(vecs, srcIdx * Stride, centroids, c * Stride, Stride);
        }

        // Loop de refinamento do K-means.
        for (int iter = 0; iter < Iters; iter++)
        {
            // Atribui cada vetor ao centroide mais próximo (passo E do EM).
            byte[] assignments = Assign(vecs, count, centroids);

            // Recalcula os centroides como média dos vetores de cada cluster (passo M do EM).
            UpdateCentroids(vecs, count, assignments, centroids);
            Console.Error.WriteLine($"  Iteration {iter + 1}/{Iters} done.");
        }

        return centroids;
    }

    /// <summary>
    /// Para cada vetor, encontra o índice do centroide mais próximo e retorna
    /// o array de atribuições em paralelo para aproveitar todos os núcleos da máquina.
    /// </summary>
    static byte[] Assign(byte[] vecs, int count, byte[] centroids)
    {
        // Array de atribuições: assignments[i] = índice do centroide mais próximo do vetor i.
        var assignments = new byte[count];

        // Paraleliza o cálculo por vetor — seguro porque cada índice i é escrito exatamente uma vez.
        Parallel.For(0, count, i =>
            assignments[i] = (byte)VectorStore.NearestCentroidPublic(vecs, i * Stride, centroids, NCC));

        return assignments;
    }

    /// <summary>
    /// Recalcula os centroides como a média (em ponto flutuante) dos vetores
    /// de cada cluster e re-quantiza para byte.
    /// </summary>
    static void UpdateCentroids(byte[] vecs, int count, byte[] assignments, byte[] centroids)
    {
        // Acumuladores em float para evitar overflow durante a soma.
        var sums   = new float[NCC * Dims];
        var counts = new int[NCC];  // contagem de vetores por cluster

        // Acumula a soma de cada dimensão para cada cluster.
        for (int i = 0; i < count; i++)
        {
            int c   = assignments[i];  // cluster deste vetor
            int off  = i * Stride;     // offset do vetor no buffer global
            int cOff = c * Dims;       // offset do cluster no array de somas

            // Soma cada dimensão ao acumulador do cluster.
            for (int d = 0; d < Dims; d++) sums[cOff + d] += vecs[off + d];
            counts[c]++;
        }

        // Calcula a média e re-quantiza para byte (arredondamento para mínimo de erro).
        for (int c = 0; c < NCC; c++)
        {
            int cnt  = counts[c];
            if (cnt == 0) continue; // cluster vazio: mantém centroide anterior

            int cOff  = c * Dims;
            int cbOff = c * Stride; // offset no buffer de centroides

            // Divide a soma acumulada pelo número de vetores e arredonda para byte.
            for (int d = 0; d < Dims; d++)
                centroids[cbOff + d] = (byte)Math.Round(sums[cOff + d] / cnt);
        }
    }

    // ── I/O ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lê o JSON (ou JSON.gz) de referências de forma assíncrona usando streaming,
    /// quantiza cada float para byte e devolve os buffers brutos prontos para o K-means.
    /// </summary>
    static async Task<(byte[], byte[], int)> LoadRawAsync(Stream stream)
    {
        // Valor sentinela para features negativas — indica ausência/missing value.
        const byte Sentinel = 255;

        // Pré-aloca para o pior caso (3 M de vetores) para evitar realocações.
        var vecs  = new byte[3_000_000 * Stride];
        var lbls  = new byte[3_000_000];
        int count = 0;

        // Lê o JSON em modo streaming (DeserializeAsyncEnumerable) para baixo uso de memória.
        await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable(
                           stream, AppJsonContext.Default.VectorRow))
        {
            if (row is null) continue; // ignora entradas nulas por segurança

            int off = count * Stride; // posição de escrita no buffer de vetores

            // Quantiza cada dimensão de float [0,1] para byte [0,254]; negativos → 255.
            for (int d = 0; d < Dims; d++)
            {
                float v = row.Vector[d];
                vecs[off + d] = v < 0f ? Sentinel : (byte)MathF.Round(MathF.Min(v, 1f) * 254f);
            }

            // Converte o rótulo de string para byte binário (1 = fraude, 0 = legítimo).
            lbls[count] = (byte)(row.Label == "fraud" ? 1 : 0);
            count++;
        }

        return (vecs, lbls, count);
    }

    /// <summary>
    /// Persiste o índice binário no formato RIFV:
    /// magic (4 B) + NCC (4 B) + count (4 B) + centroides + atribuições.
    /// </summary>
    static async Task SaveAsync(string path, byte[] centroids, byte[] assignments, int count)
    {
        await using var fs = File.Create(path); // cria ou sobrescreve o arquivo de índice

        await fs.WriteAsync(Magic);                                // 4 bytes: "RIFV"
        await fs.WriteAsync(BitConverter.GetBytes(NCC));           // 4 bytes: número de centroides
        await fs.WriteAsync(BitConverter.GetBytes(count));         // 4 bytes: total de vetores
        await fs.WriteAsync(centroids.AsMemory(0, NCC * Stride));  // centroides quantizados
        await fs.WriteAsync(assignments.AsMemory(0, count));       // atribuição de cluster por vetor
    }
}
