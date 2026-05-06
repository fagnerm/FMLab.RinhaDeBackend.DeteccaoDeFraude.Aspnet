using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Text.Json.Serialization;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

internal record VectorRow(
    [property: JsonPropertyName("vector")] float[] Vector,
    [property: JsonPropertyName("label")] string Label
);

public class VectorStore
{
    private const int Dims            = 14;
    private const int Stride          = 16;     // 14 dims + 2 zero-pad → one SSE2 load
    private const int K               = 5;
    private const float FraudThreshold = 0.6f;
    private const byte Sentinel       = 255;

    // IVF: K-means with NCC centroids, search top SearchClusters nearest.
    private const int NCC             = 200;
    private const int KMeansIterations = 5;
    private const int SearchClusters  = 1;

    // Centroids in byte-quantized form (NCC × Stride).
    private byte[] _centroids = [];

    // Per-cluster contiguous storage — sequential scan at search time (cache-friendly).
    private byte[][] _clusterVecs   = [];
    private byte[][] _clusterLabels = [];

    private int _count;
    private volatile bool _ready;
    public bool IsReady => _ready;

    // ── Public API ─────────────────────────────────────────────────────────────

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var basePath = AppContext.BaseDirectory;
        var gzPath   = Path.Combine(basePath, "App_Data", "references.json.gz");
        var jsonPath  = Path.Combine(basePath, "App_Data", "references.json");
        var idxPath  = Path.Combine(basePath, "App_Data", "references.idx");

        Stream fileStream = File.Exists(gzPath)
            ? new GZipStream(File.OpenRead(gzPath), CompressionMode.Decompress)
            : File.OpenRead(jsonPath);

        byte[] tmpVec;
        byte[] tmpLbl;
        int    count;
        await using (fileStream)
            (tmpVec, tmpLbl, count) = await LoadRawAsync(fileStream, ct);

        _count = count;

        if (File.Exists(idxPath))
            BuildIvfIndexFromFile(tmpVec, tmpLbl, count, idxPath);
        else
            BuildIvfIndex(tmpVec, tmpLbl, count);

        _ready = true;
    }

    // Thread-safe after _ready is set.
    public (bool Approved, float FraudScore) Search(float[] query)
    {
        // Before index is ready return a safe default (approved, score=0 → FP weight 1 < FN weight 3).
        if (!_ready) return (true, 0f);

        Span<short> qShorts = stackalloc short[Stride];
        for (int d = 0; d < Dims; d++)
            qShorts[d] = Quantize(query[d]);

        // ── Step 1: find top-SearchClusters nearest centroids (8 KB data, fits in L1) ──
        Span<int> topCentDist = stackalloc int[SearchClusters];
        Span<int> topCentIdx  = stackalloc int[SearchClusters];
        topCentDist.Fill(int.MaxValue);
        int worstCentDist = int.MaxValue;
        int worstCentPos  = 0;

        for (int c = 0; c < NCC; c++)
        {
            int d = DistSq_SB(qShorts, _centroids, c * Stride);
            if (d < worstCentDist)
            {
                topCentDist[worstCentPos] = d;
                topCentIdx[worstCentPos]  = c;
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

        // ── Step 2: KNN within selected clusters (sequential scan per cluster) ──
        Span<int>  topDist   = stackalloc int[K];
        Span<byte> topLabels = stackalloc byte[K];
        topDist.Fill(int.MaxValue);
        int maxDist = int.MaxValue;
        int maxIdx  = 0;

        for (int ci = 0; ci < SearchClusters; ci++)
        {
            byte[] cvecs   = _clusterVecs[topCentIdx[ci]];
            byte[] clabels = _clusterLabels[topCentIdx[ci]];
            int    n       = clabels.Length;

            for (int k = 0; k < n; k++)
            {
                int dist = DistSq_SB(qShorts, cvecs, k * Stride);
                if (dist < maxDist)
                {
                    topDist[maxIdx]   = dist;
                    topLabels[maxIdx] = clabels[k];
                    maxDist = topDist[0];
                    maxIdx  = 0;
                    for (int j = 1; j < K; j++)
                        if (topDist[j] > maxDist) { maxDist = topDist[j]; maxIdx = j; }
                }
            }
        }

        int fraudCount = 0;
        for (int i = 0; i < K; i++)
            if (topLabels[i] == 1) fraudCount++;

        float fraudScore = (float)fraudCount / K;
        return (fraudScore < FraudThreshold, fraudScore);
    }

    // ── Load ────────────────────────────────────────────────────────────────────

    static async Task<(byte[] vecs, byte[] lbls, int count)> LoadRawAsync(
        Stream stream, CancellationToken ct)
    {
        var vecs  = new byte[3_000_000 * Stride];
        var lbls  = new byte[3_000_000];
        int count = 0;

        await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable(
                           stream, AppJsonContext.Default.VectorRow, ct))
        {
            if (row is null) continue;
            int off = count * Stride;
            for (int d = 0; d < Dims; d++)
                vecs[off + d] = Quantize(row.Vector[d]);
            lbls[count] = (byte)(row.Label == "fraud" ? 1 : 0);
            count++;
        }

        return (vecs, lbls, count);
    }

    // ── IVF / K-means build ─────────────────────────────────────────────────────

    // Load pre-computed centroid assignments from binary index file.
    void BuildIvfIndexFromFile(byte[] vecs, byte[] lbls, int count, string idxPath)
    {
        using var fs = File.OpenRead(idxPath);
        Span<byte> header = stackalloc byte[12];
        fs.ReadExactly(header);
        // Validate magic "RIFV"
        if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'V')
            throw new InvalidDataException("Invalid index file magic.");
        int ncc   = BitConverter.ToInt32(header[4..8]);
        int cnt   = BitConverter.ToInt32(header[8..12]);
        if (ncc != NCC || cnt != count)
            throw new InvalidDataException($"Index mismatch: ncc={ncc}/{NCC}, count={cnt}/{count}.");

        _centroids = new byte[NCC * Stride];
        fs.ReadExactly(_centroids);

        var assignments = new byte[count];
        fs.ReadExactly(assignments);

        BuildClusterArrays(vecs, lbls, count, assignments);
    }

    void BuildIvfIndex(byte[] vecs, byte[] lbls, int count)
    {
        var rng = new Random(42);

        // Equal-frequency init on dim-0 for better coverage than random sampling.
        int[] sortedIdx = Enumerable.Range(0, count)
            .OrderBy(i => vecs[i * Stride])
            .ToArray();

        _centroids = new byte[NCC * Stride];
        for (int c = 0; c < NCC; c++)
        {
            int srcIdx = sortedIdx[(long)c * (count - 1) / (NCC - 1)];
            Buffer.BlockCopy(vecs, srcIdx * Stride, _centroids, c * Stride, Stride);
        }

        var assignments  = new int[count];
        var clusterSums  = new float[NCC * Dims];
        var clusterCounts = new int[NCC];

        for (int iter = 0; iter < KMeansIterations; iter++)
        {
            for (int i = 0; i < count; i++)
                assignments[i] = NearestCentroidIdx(vecs, i * Stride, _centroids, NCC);

            if (iter == KMeansIterations - 1) break;

            Array.Clear(clusterSums);
            Array.Clear(clusterCounts);
            for (int i = 0; i < count; i++)
            {
                int c = assignments[i];
                int off = i * Stride;
                int cOff = c * Dims;
                for (int d = 0; d < Dims; d++) clusterSums[cOff + d] += vecs[off + d];
                clusterCounts[c]++;
            }
            for (int c = 0; c < NCC; c++)
            {
                int cnt = clusterCounts[c];
                if (cnt == 0) continue;
                int cOff = c * Dims, cbOff = c * Stride;
                for (int d = 0; d < Dims; d++)
                    _centroids[cbOff + d] = (byte)Math.Round(clusterSums[cOff + d] / cnt);
            }
        }

        var byteAssignments = new byte[count];
        for (int i = 0; i < count; i++) byteAssignments[i] = (byte)assignments[i];
        BuildClusterArrays(vecs, lbls, count, byteAssignments);
    }

    void BuildClusterArrays(byte[] vecs, byte[] lbls, int count, byte[] assignments)
    {
        var clusterSizes = new int[NCC];
        for (int i = 0; i < count; i++) clusterSizes[assignments[i]]++;

        _clusterVecs   = new byte[NCC][];
        _clusterLabels = new byte[NCC][];
        for (int c = 0; c < NCC; c++)
        {
            _clusterVecs[c]   = new byte[clusterSizes[c] * Stride];
            _clusterLabels[c] = new byte[clusterSizes[c]];
        }

        var fill = new int[NCC];
        for (int i = 0; i < count; i++)
        {
            int c   = assignments[i];
            int pos = fill[c]++;
            Buffer.BlockCopy(vecs, i * Stride, _clusterVecs[c], pos * Stride, Stride);
            _clusterLabels[c][pos] = lbls[i];
        }
    }

    // Exposed for IndexBuilder (same process, no access restrictions needed).
    public static int NearestCentroidPublic(byte[] vecs, int vOff, byte[] cents, int ncc)
        => NearestCentroidIdx(vecs, vOff, cents, ncc);

    // ── Distance helpers ────────────────────────────────────────────────────────

    // int16 query vs byte store (hot search path).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int DistSq_SB(ReadOnlySpan<short> q, byte[] store, int off)
    {
        if (Sse2.IsSupported) return DistSq_SB_Sse2(q, store, off);
        return DistSq_SB_Scalar(q, store, off);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe int DistSq_SB_Sse2(ReadOnlySpan<short> q, byte[] store, int off)
    {
        fixed (short* qp = q)
        fixed (byte*  sp = &store[off])
        {
            var sb   = Sse2.LoadVector128(sp);
            var zero = Vector128<byte>.Zero;
            var sL   = Sse2.UnpackLow (sb, zero).AsInt16();
            var sH   = Sse2.UnpackHigh(sb, zero).AsInt16();
            var qL   = Sse2.LoadVector128(qp);
            var qH   = Sse2.LoadVector128(qp + 8);
            var dL   = Sse2.Subtract(qL, sL);
            var dH   = Sse2.Subtract(qH, sH);
            var s    = Sse2.Add(Sse2.MultiplyAddAdjacent(dL, dL),
                                Sse2.MultiplyAddAdjacent(dH, dH));
            s = Sse2.Add(s, Sse2.Shuffle(s, 0b_01_00_11_10));
            s = Sse2.Add(s, Sse2.Shuffle(s, 0b_00_00_00_01));
            return s.GetElement(0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int DistSq_SB_Scalar(ReadOnlySpan<short> q, byte[] store, int off)
    {
        int acc = 0;
        for (int d = 0; d < Dims; d++) { int diff = q[d] - store[off + d]; acc += diff * diff; }
        return acc;
    }

    // byte vs byte (used only during K-means build, not on hot search path).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe int DistSq_BB_Sse2(byte[] a, int aO, byte[] b, int bO)
    {
        fixed (byte* ap = &a[aO], bp = &b[bO])
        {
            var zero = Vector128<byte>.Zero;
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

    static int DistSq_BB_Scalar(byte[] a, int aO, byte[] b, int bO)
    {
        int acc = 0;
        for (int d = 0; d < Dims; d++) { int diff = a[aO + d] - b[bO + d]; acc += diff * diff; }
        return acc;
    }

    static byte Quantize(float value)
    {
        if (value < 0f) return Sentinel;
        return (byte)MathF.Round(MathF.Min(value, 1f) * 254f);
    }
}
