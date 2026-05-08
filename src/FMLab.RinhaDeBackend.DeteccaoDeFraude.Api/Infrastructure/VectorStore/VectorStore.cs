// ════════════════════════════════════════════════════════════════════════════
// ANN via IVF (Inverted File Index)
//
// Problema: comparar a query com todos os 3 000 000 vetores a cada requisição
// levaria ~18M operações/segundo (inviável).
//
// Solução IVF:
//   BUILD  – K-means agrupa os 3M vetores em NCC=500 clusters.
//            Cada vetor é atribuído ao centroide mais próximo e armazenado
//            na lista invertida (inverted list) desse cluster.
//   SEARCH – Quantizamos a query, achamos os SearchClusters=5 centroides
//            mais próximos e varremos apenas os vetores nesses clusters.
//            Com NCC=500 e 3M vetores, cada cluster tem ~6 000 vetores.
//            Custo: 500 (centroide scan) + 5×6000 (vetores) = 30 500 comparações.
//            Speedup ~100× versus KNN exato, com pequena perda de recall.
//
// Quantização: float [0,1] → byte [0,254] via round(v × 254).
//              Valor negativo → byte 255 (Sentinel) — fora do range esperado.
//
// SIMD:
//   Build (byte×byte): SSE2  – UnpackLow/High para expandir byte→int16,
//                              PMADDWD para soma dos quadrados.
//   Search (short×byte): AVX2 – VPMOVZXBW (1 instrução byte→int16 via 256-bit),
//                               VPMADDWD, subtração vetorial.
//
// Memória pinned: arrays alocados com GC.AllocateArray(..., pinned:true)
// e ponteiros guardados como nuint — evita fixar (pin) via `fixed` a cada
// query, eliminando barreira GC no hot path.
// ════════════════════════════════════════════════════════════════════════════

using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Text.Json.Serialization;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

// DTO de desserialização do references.json / references.json.gz
internal record VectorRow(
    [property: JsonPropertyName("vector")] float[] Vector,   // 14 features normalizadas [0,1]
    [property: JsonPropertyName("label")]  string  Label     // "fraud" ou "legit"
);

public class VectorStore
{
    // ── Constantes do modelo ─────────────────────────────────────────────────────

    // Número de features do vetor de entrada (14 dimensões do modelo de risco)
    private const int Dims = 14;

    // Largura de cada vetor em bytes no array contíguo.
    // Stride=16 (>Dims=14) garante alinhamento de 16 bytes para SIMD 128-bit.
    // Os 2 bytes extras (posições 14 e 15) são zero-padding e ignorados na distância.
    private const int Stride = 16;

    // K do KNN final: votamos nos K=5 vizinhos mais próximos encontrados nos clusters.
    // Resultado: fraudCount = quantos dos 5 têm label=fraud.
    private const int K = 5;

    // Valor sentinel: representa feature fora do range [0,1] (ex.: negativa).
    // Como byte sem sinal, 255 é o maior valor possível — coloca o ponto "longe" dos demais.
    private const byte Sentinel = 255;

    // Número de Centroides de Cluster (NCC): o K-means gera 500 clusters.
    // Clusters de fraude e de legítimo são criados em proporção à frequência de cada classe.
    private const int NCC = 500;

    // Iterações de K-means no fallback em runtime (sem .idx pré-construído).
    // Menos iterações = build mais rápido no startup; qualidade ligeiramente inferior ao IndexBuilder (10 iters).
    private const int KMeansIterations = 5;

    // Quantos centroides varremos na busca ANN.
    // SearchClusters=5 → varremos os 5 clusters mais próximos da query.
    // Tradeoff: mais clusters = maior recall, maior latência.
    private const int SearchClusters = 5;

    // ── Arrays de dados ──────────────────────────────────────────────────────────

    // Centroides dos NCC clusters, armazenados contiguamente: [NCC × Stride] bytes.
    // Alocado como pinned para que o ponteiro _centroidsPtr nunca invalide.
    private byte[] _centroids = [];

    // Lista invertida de vetores: _clusterVecs[c] contém todos os vetores do cluster c,
    // concatenados como [n × Stride] bytes (também pinned para acesso direto por ponteiro).
    private byte[][] _clusterVecs = [];

    // Labels correspondentes: _clusterLabels[c][k] = 0 (legit) ou 1 (fraud) do k-ésimo vetor do cluster c.
    private byte[][] _clusterLabels = [];

    // ── Cache de ponteiros (evita pin/unpin GC por query) ────────────────────────

    // Ponteiro fixo para _centroids[0]. nuint = UIntPtr, compatível com AOT/unsafe sem `fixed`.
    private nuint _centroidsPtr;

    // _clusterVecPtrs[c] = ponteiro para _clusterVecs[c][0]. Cacheado uma vez após o build.
    private nuint[] _clusterVecPtrs = [];

    // _clusterLabelPtrs[c] = ponteiro para _clusterLabels[c][0].
    private nuint[] _clusterLabelPtrs = [];

    // _clusterSizes[c] = número de vetores no cluster c (pré-computado para evitar .Length no hot path).
    private int[] _clusterSizes = [];

    // Número total de vetores carregados
    private int _count;

    // Flag volatile: true após build e cache de ponteiros; lida sem lock na rota crítica.
    private volatile bool _ready;

    public bool IsReady => _ready;

    // ── API pública ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Carrega o índice IVF do disco (references.idx) ou o constrói a partir do JSON.
    /// Chamado uma única vez pelo VectorStoreLoader (IHostedService) no startup.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var basePath = AppContext.BaseDirectory;

        // Caminho do índice pré-construído (gerado pelo IndexBuilder — mais iterações, maior qualidade)
        var idxPath = Path.Combine(basePath, "App_Data", "references.idx");

        if (File.Exists(idxPath))
        {
            try
            {
                // Carrega centroides, listas invertidas e reconstrói os arrays de cluster
                BuildIvfIndexFromFile(idxPath);

                // Cacheia ponteiros para os arrays pinned — única operação unsafe necessária
                CachePointers();

                _ready = true;
                return;
            }
            catch (InvalidDataException)
            {
                // Arquivo corrompido ou NCC diferente — recai no build a partir do JSON
            }
        }

        // Fallback: sem .idx, carrega o JSON e constrói o índice em runtime
        var gzPath   = Path.Combine(basePath, "App_Data", "references.json.gz");
        var jsonPath = Path.Combine(basePath, "App_Data", "references.json");

        // Prefere .gz (menor I/O de disco) se disponível
        Stream fileStream = File.Exists(gzPath)
            ? new GZipStream(File.OpenRead(gzPath), CompressionMode.Decompress)
            : File.OpenRead(jsonPath);

        byte[] tmpVec;   // vetores quantizados: [count × Stride] bytes
        byte[] tmpLbl;   // labels: [count] bytes, 0=legit / 1=fraud
        int    count;    // número de linhas lidas do JSON
        await using (fileStream)
            (tmpVec, tmpLbl, count) = await LoadRawAsync(fileStream, ct);

        _count = count;

        // Constrói K-means + listas invertidas em memória
        BuildIvfIndex(tmpVec, tmpLbl, count);
        CachePointers();
        _ready = true;
    }

    /// <summary>
    /// Busca ANN: retorna quantos dos K vizinhos mais próximos são fraude (0..K).
    /// Caminho quente — chamado a cada requisição POST /fraud-score.
    /// </summary>
    public unsafe int Search(ReadOnlySpan<float> query)
    {
        // Índice ainda não pronto (startup) → assume legítimo
        if (!_ready) return 0;

        // AVX2 não suportado → usa path SSE2/scalar
        if (!Avx2.IsSupported) return SearchFallback(query);

        // ── Etapa 1: quantizar a query de float para short ───────────────────────
        // Usamos short (16-bit) em vez de byte para combinar com DistSq_Ptr,
        // que faz subtração signed (byte poderia underflow).
        Span<short> qShorts = stackalloc short[Stride]; // 16 shorts na stack — sem heap alloc
        for (int d = 0; d < Dims; d++)
            qShorts[d] = Quantize(query[d]);             // float [0,1] → short [0,254] ou 255

        // ── Etapa 2: achar os SearchClusters centroides mais próximos ────────────
        // Mantemos um heap máximo de tamanho SearchClusters:
        //   topCentDist[i] = distância ao i-ésimo centroide selecionado
        //   topCentIdx[i]  = índice do i-ésimo centroide selecionado
        //   worstCentDist  = maior distância entre os selecionados (limiar de substituição)
        //   worstCentPos   = posição do pior no array topCentDist
        Span<int> topCentDist = stackalloc int[SearchClusters];
        Span<int> topCentIdx  = stackalloc int[SearchClusters];
        topCentDist.Fill(int.MaxValue);          // inicializa com "infinito"
        int worstCentDist = int.MaxValue;        // maior distância no heap
        int worstCentPos  = 0;                   // posição do pior no heap

        // Resultado final: K vizinhos mais próximos (heap máximo de tamanho K)
        Span<int>  topDist   = stackalloc int[K];
        Span<byte> topLabels = stackalloc byte[K];
        topDist.Fill(int.MaxValue);              // inicializa com "infinito"
        int maxDist = int.MaxValue;              // maior distância entre os K selecionados
        int maxIdx  = 0;                         // posição do pior no heap de K vizinhos

        fixed (short* qp = qShorts)
        {
            // Ponteiro para o array contíguo de centroides (já pinned — sem overhead de GC)
            byte* centPtr = (byte*)_centroidsPtr;

            // Varre todos os NCC=500 centroides para achar os SearchClusters mais próximos
            for (int c = 0; c < NCC; c++)
            {
                // Distância ao quadrado entre a query (short) e o centroide c (byte) via AVX2
                int d = DistSq_Ptr(qp, centPtr + c * Stride);

                // Atualiza o heap máximo: substitui o pior se encontramos um centroide melhor
                if (d < worstCentDist)
                {
                    topCentDist[worstCentPos] = d;   // substitui o pior pelo novo
                    topCentIdx[worstCentPos]  = c;

                    // Re-localiza o novo pior (O(SearchClusters) = O(5) — custo fixo)
                    worstCentDist = topCentDist[0];
                    worstCentPos  = 0;
                    for (int j = 1; j < SearchClusters; j++)
                        if (topCentDist[j] > worstCentDist)
                        {
                            worstCentDist = topCentDist[j];
                            worstCentPos  = j;
                        }
                }
            }

            // Ordena os centroides selecionados por distância crescente (insertion sort, n=5)
            // Isso faz com que varremos primeiro os clusters mais próximos,
            // preenchendo o heap de K vizinhos mais rapidamente.
            for (int i = 1; i < SearchClusters; i++)
            {
                int di = topCentDist[i]; // distância do elemento atual
                int ii = topCentIdx[i];  // índice do centroide atual
                int j  = i - 1;

                // Desloca elementos maiores para a direita
                while (j >= 0 && topCentDist[j] > di)
                {
                    topCentDist[j + 1] = topCentDist[j];
                    topCentIdx[j + 1]  = topCentIdx[j];
                    j--;
                }

                // Insere na posição correta
                topCentDist[j + 1] = di;
                topCentIdx[j + 1]  = ii;
            }

            // ── Etapa 3: varrer os vetores nos SearchClusters clusters selecionados ──
            // Copia referências para variáveis locais — evita indireção extra via `this` no loop
            nuint[] vecPtrs   = _clusterVecPtrs;
            nuint[] labelPtrs = _clusterLabelPtrs;
            int[]   sizes     = _clusterSizes;

            for (int ci = 0; ci < SearchClusters; ci++)
            {
                int   cIdx = topCentIdx[ci];             // índice global do cluster
                byte* sp   = (byte*)vecPtrs[cIdx];       // ponteiro para vetores do cluster
                byte* lp   = (byte*)labelPtrs[cIdx];     // ponteiro para labels do cluster
                int   n    = sizes[cIdx];                 // número de vetores no cluster

                for (int k = 0; k < n; k++)
                {
                    // Distância AVX2 entre query (short) e vetor k do cluster (byte)
                    int dist = DistSq_Ptr(qp, sp + k * Stride);

                    // Atualiza o heap máximo de K vizinhos mais próximos
                    if (dist < maxDist)
                    {
                        topDist[maxIdx]   = dist;    // substitui o mais distante
                        topLabels[maxIdx] = lp[k];   // label correspondente

                        // Re-localiza o novo pior no heap (O(K) = O(5))
                        maxDist = topDist[0];
                        maxIdx  = 0;
                        for (int j = 1; j < K; j++)
                            if (topDist[j] > maxDist) { maxDist = topDist[j]; maxIdx = j; }
                    }
                }
            }
        }

        // ── Etapa 4: votação por maioria entre os K vizinhos ─────────────────────
        // Conta quantos dos K vizinhos têm label=fraud (1).
        // O chamador usa fraudCount como score: 0..K.
        int fraudCount = 0;
        for (int i = 0; i < K; i++)
            if (topLabels[i] == 1) fraudCount++;
        return fraudCount;
    }

    // Expõe NearestCentroidIdx para o IndexBuilder usar (fase de assign do .idx)
    public static int NearestCentroidPublic(byte[] vecs, int vOff, byte[] cents, int ncc)
        => NearestCentroidIdx(vecs, vOff, cents, ncc);

    // ── Cache de ponteiros ───────────────────────────────────────────────────────

    /// <summary>
    /// Guarda ponteiros brutos para os arrays pinned.
    /// Chamado uma vez após o build — elimina o custo de `fixed` por query.
    /// </summary>
    unsafe void CachePointers()
    {
        // Ponteiro para o início do array de centroides
        _centroidsPtr = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_centroids));

        _clusterVecPtrs   = new nuint[NCC];
        _clusterLabelPtrs = new nuint[NCC];
        _clusterSizes     = new int[NCC];

        for (int c = 0; c < NCC; c++)
        {
            // Ponteiro para o primeiro byte do array de vetores do cluster c
            _clusterVecPtrs[c]   = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_clusterVecs[c]));

            // Ponteiro para o primeiro byte do array de labels do cluster c
            _clusterLabelPtrs[c] = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_clusterLabels[c]));

            // Tamanho em número de vetores (não bytes)
            _clusterSizes[c] = _clusterLabels[c].Length;
        }
    }

    // ── Fallback (sem AVX2) ──────────────────────────────────────────────────────

    /// <summary>
    /// Mesma lógica de Search, mas usando SSE2 ou scalar para distância.
    /// Chamado automaticamente quando AVX2 não está disponível.
    /// </summary>
    int SearchFallback(ReadOnlySpan<float> query)
    {
        // Quantiza query para short (mesma lógica do path AVX2)
        Span<short> qShorts = stackalloc short[Stride];
        for (int d = 0; d < Dims; d++) qShorts[d] = Quantize(query[d]);

        // Heap de centroides mais próximos
        Span<int> topCentDist = stackalloc int[SearchClusters];
        Span<int> topCentIdx  = stackalloc int[SearchClusters];
        topCentDist.Fill(int.MaxValue);
        int worstCentDist = int.MaxValue, worstCentPos = 0;

        // Varre centroides via SSE2/scalar
        for (int c = 0; c < NCC; c++)
        {
            int d = DistSq_SB(_centroids, c * Stride, qShorts);
            if (d < worstCentDist)
            {
                topCentDist[worstCentPos] = d;
                topCentIdx[worstCentPos]  = c;
                worstCentDist = topCentDist[0];
                worstCentPos  = 0;
                for (int j = 1; j < SearchClusters; j++)
                    if (topCentDist[j] > worstCentDist) { worstCentDist = topCentDist[j]; worstCentPos = j; }
            }
        }

        // Heap de K vizinhos
        Span<int>  topDist   = stackalloc int[K];
        Span<byte> topLabels = stackalloc byte[K];
        topDist.Fill(int.MaxValue);
        int maxDist = int.MaxValue, maxIdx = 0;

        // Varre vetores dos clusters selecionados
        for (int ci = 0; ci < SearchClusters; ci++)
        {
            int    cIdx   = topCentIdx[ci];
            byte[] cvecs  = _clusterVecs[cIdx];    // array de vetores do cluster
            byte[] clbls  = _clusterLabels[cIdx];  // labels do cluster
            int    n      = clbls.Length;

            for (int k = 0; k < n; k++)
            {
                int dist = DistSq_SB(cvecs, k * Stride, qShorts);
                if (dist < maxDist)
                {
                    topDist[maxIdx]   = dist;
                    topLabels[maxIdx] = clbls[k];
                    maxDist = topDist[0]; maxIdx = 0;
                    for (int j = 1; j < K; j++)
                        if (topDist[j] > maxDist) { maxDist = topDist[j]; maxIdx = j; }
                }
            }
        }

        // Votação
        int fraudCount = 0;
        for (int i = 0; i < K; i++) if (topLabels[i] == 1) fraudCount++;
        return fraudCount;
    }

    // ── Leitura do JSON ──────────────────────────────────────────────────────────

    /// <summary>
    /// Lê references.json (ou .gz) e preenche dois arrays paralelos:
    ///   vecs[i*Stride .. i*Stride+Dims] = vetor quantizado do item i
    ///   lbls[i]                         = label do item i (0 ou 1)
    /// </summary>
    static async Task<(byte[] vecs, byte[] lbls, int count)> LoadRawAsync(
        Stream stream, CancellationToken ct)
    {
        // Pré-aloca para o máximo esperado (3M linhas × 16 bytes) — evita realloc
        var vecs  = new byte[3_000_000 * Stride];
        var lbls  = new byte[3_000_000];
        int count = 0; // cursor: próxima posição livre

        // Desserializa linha a linha (streaming) para não carregar tudo na RAM de uma vez
        await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable(
                           stream, AppJsonContext.Default.VectorRow, ct))
        {
            if (row is null) continue;

            int off = count * Stride; // offset em bytes para o vetor atual

            // Quantiza cada dimensão de float para byte
            for (int d = 0; d < Dims; d++)
                vecs[off + d] = Quantize(row.Vector[d]);

            // Label: "fraud" → 1, qualquer outro (="legit") → 0
            lbls[count] = (byte)(row.Label == "fraud" ? 1 : 0);
            count++;
        }

        return (vecs, lbls, count);
    }

    // ── Build do índice IVF em runtime (fallback sem .idx) ───────────────────────

    /// <summary>
    /// Constrói o índice IVF a partir do arquivo .idx pré-construído (IndexBuilder).
    /// Lança InvalidDataException se o arquivo for incompatível (magic errado ou NCC diferente).
    /// </summary>
    void BuildIvfIndexFromFile(string idxPath)
    {
        using var fs = File.OpenRead(idxPath);

        // Header: 4 bytes magic + 4 bytes NCC + 4 bytes count = 12 bytes
        Span<byte> header = stackalloc byte[12];
        fs.ReadExactly(header);

        // Valida o magic "RIF3" (Rinha Inverted File v3)
        if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != '3')
            throw new InvalidDataException("Magic inválido. Esperado RIF3.");

        int ncc   = BitConverter.ToInt32(header[4..8]);   // NCC salvo no arquivo
        int count = BitConverter.ToInt32(header[8..12]);  // número de vetores salvos

        // Verifica que o NCC do arquivo bate com o NCC compilado — se diferente, o índice está errado
        if (ncc != NCC)
            throw new InvalidDataException($"NCC incompatível: arquivo={ncc}, esperado={NCC}. Apague references.idx e reconstrua.");

        _count = count;

        // Lê os NCC centroides (pinned para cache de ponteiro)
        _centroids = GC.AllocateArray<byte>(NCC * Stride, pinned: true);
        fs.ReadExactly(_centroids);

        // Lê os assignments: cada vetor foi atribuído ao centroide assignments[i] durante o build
        var assignments = new ushort[count];  // ushort: suficiente para NCC ≤ 65535
        fs.ReadExactly(MemoryMarshal.AsBytes(assignments.AsSpan()));

        // Lê os vetores quantizados
        var vecs = new byte[count * Stride];
        fs.ReadExactly(vecs);

        // Lê as labels
        var lbls = new byte[count];
        fs.ReadExactly(lbls);

        // Constrói as listas invertidas a partir dos assignments salvos
        BuildClusterArrays(vecs, lbls, count, assignments);
    }

    /// <summary>
    /// Constrói o índice IVF em runtime via K-means (fallback — menos iterações que o IndexBuilder).
    /// </summary>
    void BuildIvfIndex(byte[] vecs, byte[] lbls, int count)
    {
        // Separa índices de vetores por classe para K-means class-separated
        var fraudIdx = new List<int>(count / 2); // índices de vetores fraud
        var legitIdx = new List<int>(count / 2); // índices de vetores legit
        for (int i = 0; i < count; i++)
            (lbls[i] == 1 ? fraudIdx : legitIdx).Add(i);

        // Clusters proporcionais à frequência de cada classe
        int fraudK = Math.Max(1, (int)Math.Round(NCC * (double)fraudIdx.Count / count));
        int legitK = NCC - fraudK;

        _centroids = GC.AllocateArray<byte>(NCC * Stride, pinned: true);

        // K-means independente por classe — evita que a maioria (legit) "domine" os centroides
        var fraudCentroids = KMeansClass(vecs, fraudIdx, fraudK);
        var legitCentroids = KMeansClass(vecs, legitIdx, legitK);

        // Concatena: primeiros fraudK centroides = fraud; restantes = legit
        Buffer.BlockCopy(fraudCentroids, 0, _centroids, 0,               fraudK * Stride);
        Buffer.BlockCopy(legitCentroids, 0, _centroids, fraudK * Stride, legitK * Stride);

        // Atribui cada vetor ao centroide mais próximo para montar as listas invertidas
        var assignments = new ushort[count];
        for (int i = 0; i < count; i++)
            assignments[i] = (ushort)NearestCentroidIdx(vecs, i * Stride, _centroids, NCC);

        BuildClusterArrays(vecs, lbls, count, assignments);
    }

    /// <summary>
    /// K-means para um subconjunto de vetores (`indices`) agrupados em `k` clusters.
    /// Inicialização: evenly-spaced sobre o primeiro eixo (mais variável).
    /// Medoid replacement: substitui o centroide médio pelo vetor real mais próximo.
    /// </summary>
    static byte[] KMeansClass(byte[] vecs, List<int> indices, int k)
    {
        int n = indices.Count; // número de vetores nesse subconjunto de classe

        // Inicialização k-means++: ordena por dim[0] e escolhe k pontos espaçados uniformemente
        int[] sortedIdx = [.. indices.OrderBy(i => vecs[i * Stride])];

        var centroids = new byte[k * Stride]; // buffer dos k centroides (k×16 bytes)
        for (int c = 0; c < k; c++)
        {
            // Índice global do vetor de inicialização do centroide c
            int src = sortedIdx[(long)c * (n - 1) / Math.Max(k - 1, 1)];
            Buffer.BlockCopy(vecs, src * Stride, centroids, c * Stride, Stride);
        }

        var assignments = new int[n];      // assignments[i] = cluster do vetor i (índice local)
        var sums        = new float[k * Dims]; // somas acumuladas para recalcular centroides (M-step)
        var counts      = new int[k];      // número de vetores por cluster (para calcular média)
        var rng         = new Random(42);  // seed fixo para determinismo entre plataformas

        for (int iter = 0; iter < KMeansIterations; iter++)
        {
            // ── E-step: atribui cada vetor ao centroide mais próximo ──────────────
            for (int i = 0; i < n; i++)
                assignments[i] = NearestCentroidIdx(vecs, indices[i] * Stride, centroids, k);

            // Na última iteração, não recalculamos centroides — queremos os assignments finais
            if (iter == KMeansIterations - 1) break;

            // ── M-step: recalcula centroides como média dos vetores atribuídos ────
            Array.Clear(sums);
            Array.Clear(counts);

            // Acumula somas por cluster
            for (int i = 0; i < n; i++)
            {
                int c    = assignments[i];          // cluster do vetor i
                int vOff = indices[i] * Stride;     // offset do vetor em `vecs`
                int cOff = c * Dims;                // offset do cluster em `sums`
                for (int d = 0; d < Dims; d++) sums[cOff + d] += vecs[vOff + d];
                counts[c]++;
            }

            // Atualiza centroides: média aritmética, requantizada para byte
            for (int c = 0; c < k; c++)
            {
                int cnt = counts[c];
                if (cnt == 0)
                {
                    // Cluster vazio: reinicializa com um vetor aleatório do subconjunto
                    int src = indices[rng.Next(n)];
                    Buffer.BlockCopy(vecs, src * Stride, centroids, c * Stride, Stride);
                    continue;
                }

                int cOff  = c * Dims;   // offset de c em `sums`
                int cbOff = c * Stride; // offset de c em `centroids`
                for (int d = 0; d < Dims; d++)
                    centroids[cbOff + d] = (byte)Math.Round(sums[cOff + d] / cnt);
            }
        }

        // ── Medoid replacement: substitui centroide médio pelo vetor real mais próximo ──
        // Melhora a qualidade do índice: o centroide se torna um ponto existente no dataset,
        // reduzindo a distância média centroide→vizinhos do cluster.
        var bestDist   = new int[k]; // menor distância ao centroide médio por cluster
        var bestMedoid = new int[k]; // índice global do vetor mais próximo do centroide
        Array.Fill(bestDist, int.MaxValue);
        Array.Fill(bestMedoid, -1);

        for (int i = 0; i < n; i++)
        {
            int c    = assignments[i];
            int vOff = indices[i] * Stride;
            int dist = DistSq_BB_Sse2(vecs, vOff, centroids, c * Stride);
            if (dist < bestDist[c]) { bestDist[c] = dist; bestMedoid[c] = indices[i]; }
        }

        for (int c = 0; c < k; c++)
            if (bestMedoid[c] >= 0) // cluster não-vazio
                Buffer.BlockCopy(vecs, bestMedoid[c] * Stride, centroids, c * Stride, Stride);

        return centroids;
    }

    /// <summary>
    /// Distribui os `count` vetores nas listas invertidas (_clusterVecs, _clusterLabels)
    /// usando os `assignments` salvos/calculados. Arrays alocados como pinned.
    /// </summary>
    void BuildClusterArrays(byte[] vecs, byte[] lbls, int count, ushort[] assignments)
    {
        // Conta vetores por cluster para pre-alocar os arrays com tamanho exato
        var clusterSizes = new int[NCC];
        for (int i = 0; i < count; i++) clusterSizes[assignments[i]]++;

        _clusterVecs   = new byte[NCC][];
        _clusterLabels = new byte[NCC][];

        for (int c = 0; c < NCC; c++)
        {
            // Pinned: o GC não vai mover esses arrays — ponteiros em _clusterVecPtrs são estáveis
            _clusterVecs[c]   = GC.AllocateArray<byte>(clusterSizes[c] * Stride, pinned: true);
            _clusterLabels[c] = GC.AllocateArray<byte>(clusterSizes[c],          pinned: true);
        }

        var fill = new int[NCC]; // fill[c] = próxima posição livre no cluster c

        for (int i = 0; i < count; i++)
        {
            int c   = assignments[i]; // cluster deste vetor
            int pos = fill[c]++;       // posição dentro do cluster

            // Copia os Stride bytes do vetor i para a posição correta no cluster
            Buffer.BlockCopy(vecs, i * Stride, _clusterVecs[c], pos * Stride, Stride);

            // Copia a label do vetor i
            _clusterLabels[c][pos] = lbls[i];
        }
    }

    // ── Distância ao quadrado ────────────────────────────────────────────────────

    /// <summary>
    /// AVX2: distância L2² entre query (short*, 16 elementos) e vetor do store (byte*, 16 elementos).
    /// VPMOVZXBW expande os 16 bytes do store para 16 int16 em um único ciclo (via zmm256).
    /// VPMADDWD multiplica pares adjacentes e soma: (a²+b²) por par → 8 int32.
    /// Redução horizontal: soma as 8 lanes para produzir um único int.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe int DistSq_Ptr(short* q, byte* s)
    {
        // Carrega 16 bytes do store e expande para 16 int16 (zero-extension)
        var sExt = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(s));

        // Carrega 16 shorts da query (já no formato int16)
        var qVec = Avx.LoadVector256(q);

        // diff[i] = q[i] - s[i] (int16, sem overflow: ambos em [0,255])
        var diff = Avx2.Subtract(qVec, sExt);

        // sq[i] = diff[2i]² + diff[2i+1]² (VPMADDWD: multiplica e soma pares adjacentes → int32)
        // Resultado: Vector256<int> com 8 elementos, cada um = soma de dois quadrados
        var sq = Avx2.MultiplyAddAdjacent(diff, diff);

        // Redução: soma upper lane (128 bits superiores) na lower lane
        var sum = Sse2.Add(sq.GetLower(), sq.GetUpper()); // 4 × int32

        // Soma pares: [0+2, 1+3, 0+2, 1+3]
        sum = Sse2.Add(sum, Sse2.Shuffle(sum, 0b_01_00_11_10));

        // Soma os dois últimos: [0+1, ...]
        sum = Sse2.Add(sum, Sse2.Shuffle(sum, 0b_00_00_00_01));

        // Retorna o escalar final no lane 0
        return sum.GetElement(0);
    }

    /// <summary>
    /// SSE2: distância L2² entre query (short[]) e vetor do store (byte[], offset off).
    /// Despacha para SSE2 vetorial ou scalar conforme suporte em runtime.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int DistSq_SB(byte[] store, int off, ReadOnlySpan<short> q)
    {
        if (Sse2.IsSupported) return DistSq_SB_Sse2(store, off, q);
        return DistSq_SB_Scalar(store, off, q);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe int DistSq_SB_Sse2(byte[] store, int off, ReadOnlySpan<short> q)
    {
        fixed (short* qp = q)
        fixed (byte*  sp = &store[off])
        {
            // Carrega 16 bytes e separa em low/high halves (8 bytes cada)
            var sb   = Sse2.LoadVector128(sp);
            var zero = Vector128<byte>.Zero;
            var sL   = Sse2.UnpackLow (sb, zero).AsInt16(); // bytes 0..7  → int16
            var sH   = Sse2.UnpackHigh(sb, zero).AsInt16(); // bytes 8..15 → int16

            // Carrega as duas metades da query (128 bits cada)
            var qL = Sse2.LoadVector128(qp);     // shorts 0..7
            var qH = Sse2.LoadVector128(qp + 8); // shorts 8..15

            // diff = q - s para cada metade
            var dL = Sse2.Subtract(qL, sL);
            var dH = Sse2.Subtract(qH, sH);

            // PMADDWD: soma de quadrados por pares adjacentes → 4 int32 por metade
            var s = Sse2.Add(Sse2.MultiplyAddAdjacent(dL, dL),
                             Sse2.MultiplyAddAdjacent(dH, dH)); // 4 × int32

            // Redução horizontal (2 shuffles + add)
            s = Sse2.Add(s, Sse2.Shuffle(s, 0b_01_00_11_10));
            s = Sse2.Add(s, Sse2.Shuffle(s, 0b_00_00_00_01));
            return s.GetElement(0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int DistSq_SB_Scalar(byte[] store, int off, ReadOnlySpan<short> q)
    {
        int acc = 0;
        for (int d = 0; d < Dims; d++)
        {
            int diff = q[d] - store[off + d]; // short - byte = int (sem overflow)
            acc += diff * diff;
        }
        return acc;
    }

    /// <summary>
    /// SSE2: distância L2² entre dois vetores byte[] com offsets.
    /// Usada no K-means (fase de build) e no NearestCentroidIdx.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe int DistSq_BB_Sse2(byte[] a, int aO, byte[] b, int bO)
    {
        fixed (byte* ap = &a[aO], bp = &b[bO])
        {
            var zero = Vector128<byte>.Zero;
            // Expande byte→int16 separando low/high halves de cada vetor
            var aL = Sse2.UnpackLow (Sse2.LoadVector128(ap), zero).AsInt16();
            var aH = Sse2.UnpackHigh(Sse2.LoadVector128(ap), zero).AsInt16();
            var bL = Sse2.UnpackLow (Sse2.LoadVector128(bp), zero).AsInt16();
            var bH = Sse2.UnpackHigh(Sse2.LoadVector128(bp), zero).AsInt16();
            var dL = Sse2.Subtract(aL, bL);
            var dH = Sse2.Subtract(aH, bH);
            var s  = Sse2.Add(Sse2.MultiplyAddAdjacent(dL, dL),
                              Sse2.MultiplyAddAdjacent(dH, dH));
            s = Sse2.Add(s, Sse2.Shuffle(s, 0b_01_00_11_10));
            s = Sse2.Add(s, Sse2.Shuffle(s, 0b_00_00_00_01));
            return s.GetElement(0);
        }
    }

    static int DistSq_BB_Scalar(byte[] a, int aO, byte[] b, int bO)
    {
        int acc = 0;
        for (int d = 0; d < Dims; d++) { int diff = a[aO + d] - b[bO + d]; acc += diff * diff; }
        return acc;
    }

    /// <summary>
    /// Retorna o índice do centroide mais próximo do vetor em vecs[vOff..vOff+Stride].
    /// Usado no K-means (E-step) e na atribuição final dos vetores.
    /// </summary>
    static int NearestCentroidIdx(byte[] vecs, int vOff, byte[] cents, int ncc)
    {
        int best = 0, bestDist = int.MaxValue;

        if (Sse2.IsSupported)
        {
            for (int c = 0; c < ncc; c++)
            {
                int d = DistSq_BB_Sse2(vecs, vOff, cents, c * Stride);
                if (d < bestDist) { bestDist = d; best = c; }
            }
        }
        else
        {
            for (int c = 0; c < ncc; c++)
            {
                int d = DistSq_BB_Scalar(vecs, vOff, cents, c * Stride);
                if (d < bestDist) { bestDist = d; best = c; }
            }
        }

        return best;
    }

    /// <summary>
    /// Quantiza um float [0,1] para byte [0,254].
    /// Valor negativo → Sentinel=255 (ponto "distante" de todos os clusters normais).
    /// Escala 254 (não 255) para reservar 255 como sentinel.
    /// </summary>
    static byte Quantize(float value)
    {
        if (value < 0f) return Sentinel;                                  // feature ausente/inválida
        return (byte)MathF.Round(MathF.Min(value, 1f) * 254f);           // clamp + quantização linear
    }
}
