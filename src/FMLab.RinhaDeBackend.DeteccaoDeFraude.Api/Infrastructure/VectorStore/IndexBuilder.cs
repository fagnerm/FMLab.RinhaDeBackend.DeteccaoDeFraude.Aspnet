// ════════════════════════════════════════════════════════════════════════════
// IndexBuilder — Construção offline do índice IVF (Inverted File Index)
//
// Propósito: gerar o arquivo references.idx antes de subir os containers,
// no mesmo ambiente Linux/x64 que vai executar a aplicação. Isso garante
// que o índice é construído com mais iterações (Iters=10) e na mesma
// plataforma, evitando divergências entre Windows (build) e Linux (runtime).
//
// Fluxo:
//   1. Carrega references.json.gz → quantiza float→byte → arrays (vecs, lbls)
//   2. Separa vetores por classe (fraud / legit)
//   3. K-means class-separated: fraudK centroides para fraude, legitK para legít.
//      Inicialização por pontos espaçados uniformemente (evenly-spaced seed).
//      10 iterações E-step/M-step + medoid replacement no final.
//   4. Atribui cada vetor ao centroide mais próximo (fase de assign).
//   5. Salva references.idx com o formato binário RIF3.
//
// Formato do arquivo .idx:
//   [4 bytes] magic "RIF3"
//   [4 bytes] int32 NCC         — número de centroides
//   [4 bytes] int32 count       — número de vetores
//   [NCC × Stride bytes]        — centroides
//   [count × 2 bytes]           — assignments (ushort: índice do centroide de cada vetor)
//   [count × Stride bytes]      — vetores quantizados
//   [count × 1 byte]            — labels (0=legit, 1=fraud)
// ════════════════════════════════════════════════════════════════════════════

using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

public static class IndexBuilder
{
    // Número de features do vetor (14 dimensões do modelo de risco)
    private const int Dims = 14;

    // Stride em bytes por vetor: 16 ≥ 14 para alinhamento SIMD de 128 bits.
    // Os 2 bytes de padding são zerados e ignorados no cálculo de distância.
    private const int Stride = 16;

    // Número de Centroides de Cluster: 500 clusters divididos entre fraude e legít
    // em proporção à frequência de cada classe no dataset.
    private const int NCC = 500;

    // Iterações do K-means: 10 é suficiente para convergência na escala de 3M vetores.
    // Mais iterações melhoram a qualidade mas aumentam o tempo de build.
    private const int Iters = 10;

    // Magic do arquivo .idx: identifica o formato "RIF3" (Rinha Inverted File v3).
    // Verificado no load para detectar arquivos corrompidos ou de versão anterior.
    private static readonly byte[] Magic = "RIF3"u8.ToArray();

    /// <summary>
    /// Ponto de entrada: carrega o dataset, constrói o índice IVF e salva em disco.
    /// Invocado via `dotnet run -- --build-index` (ver Program.cs).
    /// </summary>
    public static async Task BuildAndSaveAsync(string appDataDir)
    {
        Console.Error.WriteLine($"SSE2 suportado: {Sse2.IsSupported}");
        Console.Error.WriteLine("Carregando vetores...");

        // Prefere o .gz (menor I/O); cai no .json se não existir
        var gzPath   = Path.Combine(appDataDir, "references.json.gz");
        var jsonPath = Path.Combine(appDataDir, "references.json");

        Stream src = File.Exists(gzPath)
            ? new GZipStream(File.OpenRead(gzPath), CompressionMode.Decompress)
            : File.OpenRead(jsonPath);

        byte[] vecs;  // vetores quantizados: [count × Stride] bytes
        byte[] lbls;  // labels paralelas: lbls[i] = 0 (legit) ou 1 (fraud)
        int    count; // número de linhas lidas
        await using (src)
            (vecs, lbls, count) = await LoadRawAsync(src);

        Console.Error.WriteLine($"Carregados {count:N0} vetores. K-means class-separated (NCC={NCC}, iterações={Iters})...");

        // Fase 1: K-means → NCC centroides
        byte[] centroids = RunKMeans(vecs, lbls, count);

        // Fase 2: atribuição final — cada vetor recebe o centroide mais próximo
        ushort[] assignments = Assign(vecs, count, centroids);

        Console.Error.WriteLine("Salvando índice...");
        var idxPath = Path.Combine(appDataDir, "references.idx");
        await SaveAsync(idxPath, centroids, assignments, vecs, lbls, count);
        Console.Error.WriteLine($"Salvo em {idxPath}");
    }

    // ── K-means class-separated ──────────────────────────────────────────────────

    /// <summary>
    /// Executa K-means separado por classe e concatena os centroides.
    /// Separação por classe garante que fraude (minoritária) tenha centroides próprios
    /// e não seja absorvida pelos clusters de legítimo (majoritária).
    /// </summary>
    static byte[] RunKMeans(byte[] vecs, byte[] lbls, int count)
    {
        // Separa índices por classe
        var fraudIdx = new List<int>(count / 2); // índices dos vetores fraud
        var legitIdx = new List<int>(count / 2); // índices dos vetores legit
        for (int i = 0; i < count; i++)
            (lbls[i] == 1 ? fraudIdx : legitIdx).Add(i);

        // Calcula k de cada classe proporcional à sua frequência no dataset
        int fraudK = Math.Max(1, (int)Math.Round(NCC * (double)fraudIdx.Count / count));
        int legitK = NCC - fraudK; // complemento para totalizar NCC

        Console.Error.WriteLine($"  Fraude: {fraudIdx.Count:N0} vetores → {fraudK} clusters");
        Console.Error.WriteLine($"  Legít:  {legitIdx.Count:N0} vetores → {legitK} clusters");

        Console.Error.WriteLine("  Clusterizando fraude...");
        var fraudCentroids = RunKMeansClass(vecs, fraudIdx, fraudK);

        Console.Error.WriteLine("  Clusterizando legítimo...");
        var legitCentroids = RunKMeansClass(vecs, legitIdx, legitK);

        // Buffer final: fraud centroides primeiro, depois legit
        var centroids = new byte[NCC * Stride];
        Buffer.BlockCopy(fraudCentroids, 0, centroids, 0,               fraudK * Stride);
        Buffer.BlockCopy(legitCentroids, 0, centroids, fraudK * Stride, legitK * Stride);
        return centroids;
    }

    /// <summary>
    /// K-means para um subconjunto de vetores identificado por `indices`.
    ///
    /// Algoritmo:
    ///   Init  – k pontos espaçados uniformemente sobre o eixo de maior variância (dim[0]).
    ///   Loop  – Iters vezes:
    ///     E-step: assign cada vetor ao centroide mais próximo (Parallel.For).
    ///     M-step: recalcula centroides como média dos vetores atribuídos.
    ///   Final – Medoid replacement: substitui cada centroide médio pelo vetor real
    ///           do dataset mais próximo dele (melhora a qualidade do índice).
    /// </summary>
    static byte[] RunKMeansClass(byte[] vecs, List<int> indices, int k)
    {
        int n = indices.Count; // número de vetores nesta classe

        // ── Inicialização: evenly-spaced seed ────────────────────────────────────
        // Ordena vetores pelo valor da primeira dimensão e escolhe k pontos igualmente espaçados.
        // Mais barato que k-means++ mas produz boa cobertura do espaço linear de dim[0].
        int[] sortedIdx = [.. indices.OrderBy(i => vecs[i * Stride])];

        var centroids = new byte[k * Stride]; // buffer dos k centroides
        for (int c = 0; c < k; c++)
        {
            // Índice global no array sortedIdx do c-ésimo centroide inicial
            int src = sortedIdx[(long)c * (n - 1) / Math.Max(k - 1, 1)];
            // Copia o vetor selecionado como centroide inicial c
            Buffer.BlockCopy(vecs, src * Stride, centroids, c * Stride, Stride);
        }

        var assignments = new int[n];      // assignments[i] = cluster do i-ésimo vetor local
        var sums        = new float[k * Dims]; // somas acumuladas por cluster (M-step)
        var counts      = new int[k];      // vetores por cluster (para calcular média)
        var rng         = new Random(42);  // seed fixo → resultados reprodutíveis entre plataformas

        for (int iter = 0; iter < Iters; iter++)
        {
            // ── E-step (paralelo): cada vetor → centroide mais próximo ─────────────
            // Parallel.For é seguro aqui: assignments[i] é escrito apenas pela thread i,
            // e `centroids` é somente lido (não há data race).
            Parallel.For(0, n, i =>
            {
                int vOff     = indices[i] * Stride; // offset do vetor i no array global `vecs`
                int best     = 0;
                int bestDist = int.MaxValue;

                for (int c = 0; c < k; c++)
                {
                    int d = DistSq(vecs, vOff, centroids, c * Stride);
                    if (d < bestDist) { bestDist = d; best = c; }
                }

                assignments[i] = best; // atribui ao centroide mais próximo
            });

            // Última iteração: não precisamos recalcular centroides, só queremos os assignments
            if (iter == Iters - 1) break;

            // ── M-step (sequencial): recalcula centroides como média ──────────────
            Array.Clear(sums);   // zera acumuladores
            Array.Clear(counts); // zera contadores

            // Acumula soma de cada dimensão por cluster
            for (int i = 0; i < n; i++)
            {
                int c    = assignments[i];       // cluster do vetor i
                int vOff = indices[i] * Stride;  // offset do vetor i em `vecs`
                int cOff = c * Dims;             // offset do cluster c em `sums`

                for (int d = 0; d < Dims; d++)
                    sums[cOff + d] += vecs[vOff + d]; // acumula byte como float para precisão

                counts[c]++;
            }

            // Calcula nova posição de cada centroide (média → requantiza para byte)
            for (int c = 0; c < k; c++)
            {
                int cnt = counts[c]; // número de vetores atribuídos ao cluster c

                if (cnt == 0)
                {
                    // Cluster vazio: reinicializa com vetor aleatório para evitar centroide "morto"
                    int src = indices[rng.Next(n)];
                    Buffer.BlockCopy(vecs, src * Stride, centroids, c * Stride, Stride);
                    continue;
                }

                int cOff  = c * Dims;   // offset em `sums`
                int cbOff = c * Stride; // offset em `centroids`

                for (int d = 0; d < Dims; d++)
                    // Divide acumulado pelo número de membros e re-quantiza para byte
                    centroids[cbOff + d] = (byte)Math.Round(sums[cOff + d] / cnt);
            }
        }

        // ── Medoid replacement ────────────────────────────────────────────────────
        // O centroide médio está fora do dataset. Substituímos pelo vetor real mais próximo
        // (medoid), que é um ponto existente. Isso melhora o recall do índice pois o centroide
        // representa um ponto com vizinhança real conhecida.
        var bestMedoidDist = new int[k]; // menor distância ao centroide médio encontrada até agora
        var bestMedoid     = new int[k]; // índice global do melhor medoid por cluster
        Array.Fill(bestMedoidDist, int.MaxValue);
        Array.Fill(bestMedoid,     -1);

        for (int i = 0; i < n; i++)
        {
            int c    = assignments[i];       // cluster do vetor i
            int vOff = indices[i] * Stride;  // offset do vetor i

            // Distância entre o vetor i e o centroide atual do cluster c
            int dist = DistSq(vecs, vOff, centroids, c * Stride);

            // Atualiza o melhor medoid se este vetor está mais próximo do centroide
            if (dist < bestMedoidDist[c])
            {
                bestMedoidDist[c] = dist;
                bestMedoid[c]     = indices[i]; // guarda índice global (não local)
            }
        }

        // Substitui cada centroide médio pelo vetor medoid correspondente
        for (int c = 0; c < k; c++)
            if (bestMedoid[c] >= 0) // -1 = cluster vazio (não deve acontecer após M-step)
                Buffer.BlockCopy(vecs, bestMedoid[c] * Stride, centroids, c * Stride, Stride);

        return centroids;
    }

    /// <summary>
    /// Fase de assign final: atribui cada vetor ao centroide mais próximo nos NCC centroides.
    /// Paralelo para processar 3M vetores rapidamente.
    /// O resultado é salvo no .idx para ser lido pelo VectorStore sem recalcular.
    /// </summary>
    static ushort[] Assign(byte[] vecs, int count, byte[] centroids)
    {
        var assignments = new ushort[count]; // ushort: NCC ≤ 500 cabe em ushort (max 65535)

        Parallel.For(0, count, i =>
            // NearestCentroidPublic é thread-safe: lê arrays imutáveis, escreve em índice exclusivo
            assignments[i] = (ushort)VectorStore.NearestCentroidPublic(vecs, i * Stride, centroids, NCC));

        return assignments;
    }

    // ── Distância ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Distância L2² entre vetor em a[aO..aO+Stride] e b[bO..bO+Stride].
    /// SSE2 path: expande byte→int16 via UnpackLow/High, PMADDWD para soma de quadrados.
    /// Scalar fallback: loop simples.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe int DistSq(byte[] a, int aO, byte[] b, int bO)
    {
        if (!Sse2.IsSupported)
        {
            // Fallback scalar: (a[d] - b[d])² somado para todas as dimensões
            int acc = 0;
            for (int d = 0; d < Dims; d++) { int diff = a[aO + d] - b[bO + d]; acc += diff * diff; }
            return acc;
        }

        fixed (byte* ap = &a[aO], bp = &b[bO])
        {
            // Carrega 16 bytes de cada vetor em registradores de 128 bits
            var raw_a = Sse2.LoadVector128(ap);
            var raw_b = Sse2.LoadVector128(bp);
            var zero  = Vector128<byte>.Zero;

            // Expande bytes 0..7 de cada vetor para int16 (zero-extension)
            var aL = Sse2.UnpackLow (raw_a, zero).AsInt16();
            var bL = Sse2.UnpackLow (raw_b, zero).AsInt16();

            // Expande bytes 8..15 de cada vetor para int16
            var aH = Sse2.UnpackHigh(raw_a, zero).AsInt16();
            var bH = Sse2.UnpackHigh(raw_b, zero).AsInt16();

            // diff = a - b para cada metade (int16 — sem overflow: valores em [0,255])
            var dL = Sse2.Subtract(aL, bL);
            var dH = Sse2.Subtract(aH, bH);

            // PMADDWD: multiplica pares adjacentes e soma → 4 × int32 por metade
            // Soma low e high → vetor de 4 int32 com soma acumulada de 8 pares cada
            var s = Sse2.Add(Sse2.MultiplyAddAdjacent(dL, dL),
                             Sse2.MultiplyAddAdjacent(dH, dH));

            // Redução horizontal: soma pares → [s0+s2, s1+s3, s0+s2, s1+s3]
            s = Sse2.Add(s, Sse2.Shuffle(s, 0b_01_00_11_10));

            // Soma os dois elementos restantes → escalar no lane 0
            s = Sse2.Add(s, Sse2.Shuffle(s, 0b_00_00_00_01));

            return s.GetElement(0); // distância L2² total
        }
    }

    // ── I/O ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lê o JSON de referências e preenche os arrays paralelos vecs e lbls.
    /// </summary>
    static async Task<(byte[], byte[], int)> LoadRawAsync(Stream stream)
    {
        // Sentinel: valor de quantização para features negativas/ausentes
        const byte Sentinel = 255;

        // Pré-aloca para 3M entradas (máximo esperado); evita realloc durante leitura
        var vecs  = new byte[3_000_000 * Stride];
        var lbls  = new byte[3_000_000];
        int count = 0; // posição atual (número de vetores lidos)

        // Streaming JSON: não carrega o arquivo inteiro na RAM
        await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable(
                           stream, AppJsonContext.Default.VectorRow))
        {
            if (row is null) continue;

            int off = count * Stride; // offset em bytes para o vetor atual em `vecs`

            for (int d = 0; d < Dims; d++)
            {
                float v = row.Vector[d];

                // Quantiza: negativo → Sentinel=255; [0,1] → [0,254] via round(v×254)
                vecs[off + d] = v < 0f
                    ? Sentinel
                    : (byte)MathF.Round(MathF.Min(v, 1f) * 254f);
            }

            // "fraud" → 1, qualquer outro (="legit") → 0
            lbls[count] = (byte)(row.Label == "fraud" ? 1 : 0);
            count++;
        }

        return (vecs, lbls, count);
    }

    /// <summary>
    /// Salva o índice no formato binário RIF3.
    /// Estrutura: magic(4) + NCC(4) + count(4) + centroids(NCC×Stride)
    ///            + assignments(count×2) + vecs(count×Stride) + lbls(count×1)
    /// </summary>
    static async Task SaveAsync(string path, byte[] centroids, ushort[] assignments,
                                byte[] vecs, byte[] lbls, int count)
    {
        await using var fs = File.Create(path);

        // Cabeçalho: identifica o arquivo e permite validação no load
        await fs.WriteAsync(Magic);                              // "RIF3" — 4 bytes
        await fs.WriteAsync(BitConverter.GetBytes(NCC));         // número de centroides — 4 bytes
        await fs.WriteAsync(BitConverter.GetBytes(count));       // número de vetores — 4 bytes

        // Centroides: NCC × Stride bytes
        await fs.WriteAsync(centroids.AsMemory(0, NCC * Stride));

        // Assignments: count × 2 bytes (ushort, little-endian)
        // MemoryMarshal.AsBytes reinterpreta o span de ushort como span de byte — zero cópias
        fs.Write(MemoryMarshal.AsBytes(assignments.AsSpan(0, count)));

        // Vetores quantizados: count × Stride bytes
        await fs.WriteAsync(vecs.AsMemory(0, count * Stride));

        // Labels: count × 1 byte
        await fs.WriteAsync(lbls.AsMemory(0, count));
    }
}
