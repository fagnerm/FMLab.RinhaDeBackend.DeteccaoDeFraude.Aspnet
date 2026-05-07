using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Text.Json.Serialization;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

/// <summary>
/// Representa uma linha do JSON de referências: vetor de features + rótulo textual.
/// </summary>
internal record VectorRow(
    [property: JsonPropertyName("vector")] float[] Vector,
    [property: JsonPropertyName("label")] string Label
);

/// <summary>
/// Armazena vetores de transações de referência e classifica novas transações
/// usando KNN (K vizinhos mais próximos) sobre um índice IVF (Inverted File Index).
///
/// Pipeline de busca:
///   1. Quantiza o vetor de consulta de float para int16.
///   2. Varre os NCC centroides para encontrar o(s) cluster(s) mais próximo(s) — O(NCC).
///   3. Faz scan sequencial apenas dentro do(s) cluster(s) selecionado(s) — sub-linear no total.
///   4. Vota entre os K vizinhos encontrados: proporção de "fraude" → FraudScore.
///
/// Todo o cálculo de distância usa intrínsecos SSE2 quando disponíveis (fallback escalar).
/// Os vetores são armazenados quantizados em byte para máxima densidade de cache.
/// </summary>
public class VectorStore
{
    // Número de dimensões reais de cada vetor de features.
    private const int Dims            = 14;

    // Stride em bytes por vetor: 14 dims + 2 bytes de zero-padding → alinha a 16 bytes (um load SSE2).
    private const int Stride          = 16;

    // Número de vizinhos mais próximos a considerar na votação final.
    private const int K               = 5;

    // Proporção mínima de vizinhos rotulados como "fraude" para reprovar a transação.
    private const float FraudThreshold = 0.6f;

    // Valor sentinela para features ausentes/negativas após quantização.
    private const byte Sentinel       = 255;

    // Número de centroides do índice IVF (coarse quantizer).
    private const int NCC             = 500;

    // Iterações do K-means ao construir o índice em tempo de execução (sem arquivo .idx).
    private const int KMeansIterations = 5;

    // Quantos clusters vizinhos são varridos na busca (tradeoff recall × latência).
    private const int SearchClusters  = 10;

    // Centroides do índice IVF em forma quantizada (NCC × Stride bytes).
    private byte[] _centroids = [];

    // Vetores de cada cluster em armazenamento contíguo — scan sequencial é cache-friendly.
    private byte[][] _clusterVecs   = [];

    // Rótulo (0/1) de cada vetor dentro do respectivo cluster.
    private byte[][] _clusterLabels = [];

    // Total de vetores carregados.
    private int _count;

    // Flag volatile: garante visibilidade imediata entre threads após o carregamento.
    private volatile bool _ready;

    /// <summary>Retorna true quando o índice está totalmente carregado e pronto para buscas.</summary>
    public bool IsReady => _ready;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Carrega o arquivo de referências (JSON ou JSON.gz), constrói o índice IVF
    /// (usando o arquivo .idx pré-computado quando disponível) e sinaliza prontidão.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var basePath = AppContext.BaseDirectory;
        var idxPath  = Path.Combine(basePath, "App_Data", "references.idx");

        if (File.Exists(idxPath))
        {
            // Caminho rápido: lê tudo do binário pré-computado — sem JSON, sem GZ, sem parsing.
            BuildIvfIndexFromFile(idxPath);
        }
        else
        {
            // Fallback: carrega JSON e roda K-means localmente (sem arquivo .idx).
            var gzPath   = Path.Combine(basePath, "App_Data", "references.json.gz");
            var jsonPath  = Path.Combine(basePath, "App_Data", "references.json");

            Stream fileStream = File.Exists(gzPath)
                ? new GZipStream(File.OpenRead(gzPath), CompressionMode.Decompress)
                : File.OpenRead(jsonPath);

            byte[] tmpVec;
            byte[] tmpLbl;
            int    count;
            await using (fileStream)
                (tmpVec, tmpLbl, count) = await LoadRawAsync(fileStream, ct);

            _count = count;
            BuildIvfIndex(tmpVec, tmpLbl, count);
        }

        // Escrita volatile garante visibilidade imediata entre threads.
        _ready = true;
    }

    /// <summary>
    /// Classifica uma transação como aprovada ou fraudulenta usando KNN sobre o índice IVF.
    /// Thread-safe após _ready ser true.
    /// </summary>
    public (bool Approved, float FraudScore) Search(ReadOnlySpan<float> query)
    {
        // Antes do índice estar pronto retorna aprovação (score 0) —
        // peso de falso positivo (1) < peso de falso negativo (3), então aprovar é o menor risco.
        if (!_ready) return (true, 0f);

        // Quantiza o vetor de consulta para int16 na stack — evita alocação no heap.
        Span<short> qShorts = stackalloc short[Stride];
        for (int d = 0; d < Dims; d++)
            qShorts[d] = Quantize(query[d]);

        // ── Passo 1: encontrar os SearchClusters centroides mais próximos ──────────
        // Arrays na stack para os top-K candidatos (8 KB de centroides cabem em L1).
        Span<int> topCentDist = stackalloc int[SearchClusters];
        Span<int> topCentIdx  = stackalloc int[SearchClusters];
        topCentDist.Fill(int.MaxValue); // inicializa com infinito

        int worstCentDist = int.MaxValue; // distância do pior candidato atual
        int worstCentPos  = 0;            // posição do pior candidato no array

        // Varre todos os centroides procurando os mais próximos.
        for (int c = 0; c < NCC; c++)
        {
            // Calcula distância quadrada entre o vetor de consulta (int16) e o centroide (byte).
            int d = DistSq_SB(qShorts, _centroids, c * Stride);

            if (d < worstCentDist)
            {
                // Substitui o pior candidato pelo centroide atual.
                topCentDist[worstCentPos] = d;
                topCentIdx[worstCentPos]  = c;

                // Atualiza qual é o novo pior candidato entre os selecionados.
                worstCentDist = topCentDist[0];
                worstCentPos  = 0;
                for (int j = 1; j < SearchClusters; j++)
                {
                    if (topCentDist[j] > worstCentDist)
                    {
                        worstCentDist = topCentDist[j];
                        worstCentPos  = j;
                    }
                }
            }
        }

        // ── Passo 2: KNN dentro dos clusters selecionados (scan sequencial) ────────
        Span<int>  topDist   = stackalloc int[K];   // distâncias dos K vizinhos mais próximos
        Span<byte> topLabels = stackalloc byte[K];  // rótulos dos K vizinhos mais próximos
        topDist.Fill(int.MaxValue);
        int maxDist = int.MaxValue; // distância máxima atual entre os K selecionados
        int maxIdx  = 0;            // posição do vizinho mais distante

        // Para cada cluster selecionado no passo 1, varre todos os seus vetores.
        for (int ci = 0; ci < SearchClusters; ci++)
        {
            byte[] cvecs   = _clusterVecs[topCentIdx[ci]];   // vetores do cluster
            byte[] clabels = _clusterLabels[topCentIdx[ci]]; // rótulos do cluster
            int    n       = clabels.Length;                  // quantidade de vetores no cluster

            for (int k = 0; k < n; k++)
            {
                // Distância quadrada entre consulta (int16) e vetor do cluster (byte).
                int dist = DistSq_SB(qShorts, cvecs, k * Stride);

                if (dist < maxDist)
                {
                    // Substitui o vizinho mais distante pelo candidato atual.
                    topDist[maxIdx]   = dist;
                    topLabels[maxIdx] = clabels[k];

                    // Recalcula quem é agora o mais distante entre os K selecionados.
                    maxDist = topDist[0];
                    maxIdx  = 0;
                    for (int j = 1; j < K; j++)
                        if (topDist[j] > maxDist) { maxDist = topDist[j]; maxIdx = j; }
                }
            }
        }

        // ── Votação: proporção de vizinhos rotulados como fraude ─────────────────
        int fraudCount = 0;
        for (int i = 0; i < K; i++)
            if (topLabels[i] == 1) fraudCount++; // rótulo 1 = fraude

        // FraudScore = fração de vizinhos que são fraude; acima do limiar → reprovar.
        float fraudScore = (float)fraudCount / K;
        return (fraudScore < FraudThreshold, fraudScore);
    }

    // ── Load ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lê o arquivo JSON em modo streaming, quantiza cada vetor de float para byte
    /// e devolve os buffers brutos necessários para construir o índice.
    /// </summary>
    static async Task<(byte[] vecs, byte[] lbls, int count)> LoadRawAsync(
        Stream stream, CancellationToken ct)
    {
        // Pré-aloca para 3 M de vetores (pior caso) — evita realocações durante a leitura.
        var vecs  = new byte[3_000_000 * Stride];
        var lbls  = new byte[3_000_000];
        int count = 0;

        // Streaming JSON: desserializa uma entrada por vez sem carregar o JSON inteiro na memória.
        await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable(
                           stream, AppJsonContext.Default.VectorRow, ct))
        {
            if (row is null) continue; // ignora entradas malformadas

            int off = count * Stride; // offset de escrita no buffer de vetores

            // Quantiza cada dimensão de float [0,1] → byte [0,254].
            for (int d = 0; d < Dims; d++)
                vecs[off + d] = Quantize(row.Vector[d]);

            // Rótulo binário: 1 para fraude, 0 para legítimo.
            lbls[count] = (byte)(row.Label == "fraud" ? 1 : 0);
            count++;
        }

        return (vecs, lbls, count);
    }

    // ── IVF / K-means build ─────────────────────────────────────────────────────

    /// <summary>
    /// Carrega tudo do arquivo RIF3: centroides, assignments, vetores e rótulos.
    /// Não requer leitura de JSON — startup reduzido a leitura sequencial binária.
    /// </summary>
    void BuildIvfIndexFromFile(string idxPath)
    {
        using var fs = File.OpenRead(idxPath);

        // Cabeçalho: magic (4) + NCC (4) + count (4) = 12 bytes.
        Span<byte> header = stackalloc byte[12];
        fs.ReadExactly(header);

        if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != '3')
            throw new InvalidDataException("Invalid index file magic. Expected RIF3.");

        int ncc   = BitConverter.ToInt32(header[4..8]);
        int count = BitConverter.ToInt32(header[8..12]);
        if (ncc != NCC)
            throw new InvalidDataException($"Index NCC mismatch: file={ncc}, expected={NCC}.");

        _count = count;

        // Centroides quantizados em byte.
        _centroids = new byte[NCC * Stride];
        fs.ReadExactly(_centroids);

        // Atribuições de cluster em ushort (suporta NCC até 65535).
        var assignments = new ushort[count];
        fs.ReadExactly(MemoryMarshal.AsBytes(assignments.AsSpan()));

        // Vetores quantizados — leitura sequencial binária, sem JSON nem GZ.
        var vecs = new byte[count * Stride];
        fs.ReadExactly(vecs);

        // Rótulos binários (0=legítimo, 1=fraude).
        var lbls = new byte[count];
        fs.ReadExactly(lbls);

        BuildClusterArrays(vecs, lbls, count, assignments);
    }

    /// <summary>
    /// Constrói o índice IVF em tempo de execução rodando K-means localmente.
    /// Usado apenas quando o arquivo .idx pré-computado não está disponível.
    /// </summary>
    void BuildIvfIndex(byte[] vecs, byte[] lbls, int count)
    {
        var rng = new Random(42); // semente fixa para reprodutibilidade

        // Inicialização por frequência igual na dimensão 0 — melhor cobertura que amostragem aleatória.
        int[] sortedIdx = Enumerable.Range(0, count)
            .OrderBy(i => vecs[i * Stride])
            .ToArray();

        // Aloca e inicializa os centroides a partir dos vetores igualmente espaçados no ranking.
        _centroids = new byte[NCC * Stride];
        for (int c = 0; c < NCC; c++)
        {
            // Seleciona o vetor de referência mapeado linearmente ao longo do ranking.
            int srcIdx = sortedIdx[(long)c * (count - 1) / (NCC - 1)];
            Buffer.BlockCopy(vecs, srcIdx * Stride, _centroids, c * Stride, Stride);
        }

        var assignments   = new int[count];      // atribuição de cluster por vetor
        var clusterSums   = new float[NCC * Dims]; // somas acumuladas para recálculo dos centroides
        var clusterCounts = new int[NCC];          // contagem de vetores por cluster

        // Loop de refinamento do K-means.
        for (int iter = 0; iter < KMeansIterations; iter++)
        {
            // Passo E: atribui cada vetor ao centroide mais próximo.
            for (int i = 0; i < count; i++)
                assignments[i] = NearestCentroidIdx(vecs, i * Stride, _centroids, NCC);

            // Na última iteração, pula o recálculo — as atribuições finais já estão prontas.
            if (iter == KMeansIterations - 1) break;

            // Passo M: recalcula os centroides como média dos vetores do cluster.
            Array.Clear(clusterSums);
            Array.Clear(clusterCounts);

            // Acumula as somas por cluster.
            for (int i = 0; i < count; i++)
            {
                int c   = assignments[i]; // cluster deste vetor
                int off  = i * Stride;    // offset do vetor no buffer global
                int cOff = c * Dims;      // offset do cluster no array de somas

                for (int d = 0; d < Dims; d++) clusterSums[cOff + d] += vecs[off + d];
                clusterCounts[c]++;
            }

            // Recalcula cada centroide como média quantizada para byte.
            for (int c = 0; c < NCC; c++)
            {
                int cnt = clusterCounts[c];
                if (cnt == 0) continue; // cluster vazio: mantém centroide anterior

                int cOff  = c * Dims;
                int cbOff = c * Stride;

                for (int d = 0; d < Dims; d++)
                    _centroids[cbOff + d] = (byte)Math.Round(clusterSums[cOff + d] / cnt);
            }
        }

        // Converte as atribuições de int para ushort (suporta NCC até 65535).
        var ushortAssignments = new ushort[count];
        for (int i = 0; i < count; i++) ushortAssignments[i] = (ushort)assignments[i];

        // Organiza os vetores por cluster.
        BuildClusterArrays(vecs, lbls, count, ushortAssignments);
    }

    /// <summary>
    /// Reorganiza os vetores e rótulos em arrays contíguos por cluster,
    /// habilitando scan sequencial cache-friendly durante a busca.
    /// </summary>
    void BuildClusterArrays(byte[] vecs, byte[] lbls, int count, ushort[] assignments)
    {
        // Conta quantos vetores cada cluster terá para pré-alocar os arrays exatos.
        var clusterSizes = new int[NCC];
        for (int i = 0; i < count; i++) clusterSizes[assignments[i]]++;

        // Aloca um buffer contíguo por cluster tanto para vetores quanto para rótulos.
        _clusterVecs   = new byte[NCC][];
        _clusterLabels = new byte[NCC][];
        for (int c = 0; c < NCC; c++)
        {
            _clusterVecs[c]   = new byte[clusterSizes[c] * Stride];
            _clusterLabels[c] = new byte[clusterSizes[c]];
        }

        // Copia cada vetor e seu rótulo para a posição correta dentro do cluster.
        var fill = new int[NCC];
        for (int i = 0; i < count; i++)
        {
            int c   = assignments[i]; // ushort promovido a int automaticamente
            int pos = fill[c]++;

            // Copia o bloco de Stride bytes do buffer global para o buffer do cluster.
            Buffer.BlockCopy(vecs, i * Stride, _clusterVecs[c], pos * Stride, Stride);
            _clusterLabels[c][pos] = lbls[i];
        }
    }

    /// <summary>
    /// Expõe NearestCentroidIdx para o IndexBuilder (mesmo processo, sem restrição de acesso).
    /// </summary>
    public static int NearestCentroidPublic(byte[] vecs, int vOff, byte[] cents, int ncc)
        => NearestCentroidIdx(vecs, vOff, cents, ncc);

    // ── Distance helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Distância quadrada entre vetor de consulta int16 (q) e vetor armazenado byte (store[off..]).
    /// Caminho quente da busca — despacha para AVX2, SSE2 ou scalar conforme suporte da CPU.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int DistSq_SB(ReadOnlySpan<short> q, byte[] store, int off)
    {
        if (Avx2.IsSupported) return DistSq_SB_Avx2(q, store, off);
        if (Sse2.IsSupported) return DistSq_SB_Sse2(q, store, off);
        return DistSq_SB_Scalar(q, store, off);
    }

    /// <summary>
    /// Versão SSE2 de DistSq_SB: carrega 16 bytes do store, expande para int16,
    /// subtrai do vetor de consulta e acumula os quadrados usando PMADDWD.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe int DistSq_SB_Sse2(ReadOnlySpan<short> q, byte[] store, int off)
    {
        fixed (short* qp = q)
        fixed (byte*  sp = &store[off])
        {
            // Carrega 16 bytes do store em um registrador de 128 bits.
            var sb   = Sse2.LoadVector128(sp);
            var zero = Vector128<byte>.Zero;

            // Expande bytes para int16: metade baixa e metade alta separadas.
            var sL   = Sse2.UnpackLow (sb, zero).AsInt16(); // bytes 0-7 → int16
            var sH   = Sse2.UnpackHigh(sb, zero).AsInt16(); // bytes 8-15 → int16

            // Carrega as duas metades do vetor de consulta (já em int16).
            var qL   = Sse2.LoadVector128(qp);       // elementos 0-7
            var qH   = Sse2.LoadVector128(qp + 8);   // elementos 8-15

            // Calcula as diferenças (q - s) para cada metade.
            var dL   = Sse2.Subtract(qL, sL);
            var dH   = Sse2.Subtract(qH, sH);

            // PMADDWD: multiplica cada int16 por si mesmo e soma pares adjacentes → int32.
            // Resultado: 4 int32 por metade, depois somados entre si.
            var s    = Sse2.Add(Sse2.MultiplyAddAdjacent(dL, dL),
                                Sse2.MultiplyAddAdjacent(dH, dH));

            // Horizontal sum das 4 lanes: shuffle + add duas vezes.
            s = Sse2.Add(s, Sse2.Shuffle(s, 0b_01_00_11_10)); // swap pares de lanes
            s = Sse2.Add(s, Sse2.Shuffle(s, 0b_00_00_00_01)); // soma lane 1 na lane 0
            return s.GetElement(0); // resultado final na lane 0
        }
    }

    /// <summary>
    /// Versão AVX2 de DistSq_SB: expande 16 bytes para int16 via UnpackLow/High,
    /// combina em Vector256 e usa uma única VPMADDWD de 256 bits — ~2x menos instruções que SSE2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe int DistSq_SB_Avx2(ReadOnlySpan<short> q, byte[] store, int off)
    {
        fixed (short* qp = q)
        fixed (byte*  sp = &store[off])
        {
            var sb   = Sse2.LoadVector128(sp);
            var zero = Vector128<byte>.Zero;

            // Expande 16 bytes para 16 × int16 via dois UnpackLow/High de 128 bits.
            var sL   = Sse2.UnpackLow (sb, zero).AsInt16();
            var sH   = Sse2.UnpackHigh(sb, zero).AsInt16();
            var sExt = Vector256.Create(sL, sH);              // 16 × int16 em 256 bits

            // Carrega os 16 shorts da query de uma vez (256 bits).
            var qVec = Avx.LoadVector256(qp);

            // Subtrai, eleva ao quadrado e soma pares — tudo em 256 bits (1 instrução VPMADDWD).
            var diff = Avx2.Subtract(qVec, sExt);
            var sq   = Avx2.MultiplyAddAdjacent(diff, diff);  // 8 × int32

            // Horizontal sum: reduz 8 int32 (256 bits) para escalar.
            var lo  = sq.GetLower();
            var hi  = sq.GetUpper();
            var sum = Sse2.Add(lo, hi);
            sum = Sse2.Add(sum, Sse2.Shuffle(sum, 0b_01_00_11_10));
            sum = Sse2.Add(sum, Sse2.Shuffle(sum, 0b_00_00_00_01));
            return sum.GetElement(0);
        }
    }

    /// <summary>
    /// Fallback escalar de DistSq_SB para CPUs sem SSE2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int DistSq_SB_Scalar(ReadOnlySpan<short> q, byte[] store, int off)
    {
        int acc = 0;
        // Soma os quadrados das diferenças dimensão a dimensão.
        for (int d = 0; d < Dims; d++) { int diff = q[d] - store[off + d]; acc += diff * diff; }
        return acc;
    }

    /// <summary>
    /// Distância quadrada byte×byte via SSE2 — usada apenas durante o K-means (build),
    /// não no caminho quente de busca.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe int DistSq_BB_Sse2(byte[] a, int aO, byte[] b, int bO)
    {
        fixed (byte* ap = &a[aO], bp = &b[bO])
        {
            var zero = Vector128<byte>.Zero;

            // Expande ambos os vetores byte para int16 (metade baixa e alta).
            var aL = Sse2.UnpackLow (Sse2.LoadVector128(ap), zero).AsInt16();
            var aH = Sse2.UnpackHigh(Sse2.LoadVector128(ap), zero).AsInt16();
            var bL = Sse2.UnpackLow (Sse2.LoadVector128(bp), zero).AsInt16();
            var bH = Sse2.UnpackHigh(Sse2.LoadVector128(bp), zero).AsInt16();

            // Diferenças e produto interno acumulado via PMADDWD.
            var dL = Sse2.Subtract(aL, bL);
            var dH = Sse2.Subtract(aH, bH);
            var s  = Sse2.Add(Sse2.MultiplyAddAdjacent(dL, dL),
                              Sse2.MultiplyAddAdjacent(dH, dH));

            // Horizontal sum.
            s = Sse2.Add(s, Sse2.Shuffle(s, 0b_01_00_11_10));
            s = Sse2.Add(s, Sse2.Shuffle(s, 0b_00_00_00_01));
            return s.GetElement(0);
        }
    }

    /// <summary>
    /// Encontra o índice do centroide mais próximo de um vetor dado,
    /// usando SSE2 quando disponível.
    /// </summary>
    static int NearestCentroidIdx(byte[] vecs, int vOff, byte[] cents, int ncc)
    {
        int best = 0, bestDist = int.MaxValue;

        if (Sse2.IsSupported)
        {
            // Caminho SSE2: calcula todas as distâncias usando vetorização.
            for (int c = 0; c < ncc; c++)
            {
                int d = DistSq_BB_Sse2(vecs, vOff, cents, c * Stride);
                if (d < bestDist) { bestDist = d; best = c; }
            }
        }
        else
        {
            // Fallback escalar para CPUs sem SSE2.
            for (int c = 0; c < ncc; c++)
            {
                int d = DistSq_BB_Scalar(vecs, vOff, cents, c * Stride);
                if (d < bestDist) { bestDist = d; best = c; }
            }
        }
        return best;
    }

    /// <summary>
    /// Distância quadrada byte×byte escalar — fallback para CPUs sem SSE2.
    /// </summary>
    static int DistSq_BB_Scalar(byte[] a, int aO, byte[] b, int bO)
    {
        int acc = 0;
        for (int d = 0; d < Dims; d++) { int diff = a[aO + d] - b[bO + d]; acc += diff * diff; }
        return acc;
    }

    /// <summary>
    /// Quantiza um valor float para byte: negativos → sentinel 255; [0,1] → [0,254].
    /// O sentinel preserva a semântica de "feature ausente" sem colidir com valores válidos.
    /// </summary>
    static byte Quantize(float value)
    {
        if (value < 0f) return Sentinel;              // feature ausente/negativa
        return (byte)MathF.Round(MathF.Min(value, 1f) * 254f); // escala linear para [0,254]
    }
}
