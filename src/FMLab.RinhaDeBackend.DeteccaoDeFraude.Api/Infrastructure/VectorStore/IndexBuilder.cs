using System.IO.Compression;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

/// <summary>
/// Run on developer machine (no CPU limits) to pre-compute the K-means IVF index.
/// Output: App_Data/references.idx  — loaded at runtime instead of running K-means.
/// </summary>
public static class IndexBuilder
{
    // Must match VectorStore constants.
    private const int Dims   = 14;
    private const int Stride = 16;
    private const int NCC    = 200;
    private const int Iters  = 10;   // more iterations when running offline

    // Binary format (little-endian):
    //   4 bytes  magic "RIFV"
    //   4 bytes  NCC (int32)
    //   4 bytes  count (int32)
    //   NCC × Stride bytes   centroids
    //   count bytes          cluster assignments (byte, 0..NCC-1)
    private static readonly byte[] Magic = "RIFV"u8.ToArray();

    public static async Task BuildAndSaveAsync(string appDataDir)
    {
        Console.Error.WriteLine($"SSE2 supported: {Sse2.IsSupported}");
        Console.Error.WriteLine("Loading vectors...");

        var gzPath   = Path.Combine(appDataDir, "references.json.gz");
        var jsonPath  = Path.Combine(appDataDir, "references.json");

        Stream src = File.Exists(gzPath)
            ? new GZipStream(File.OpenRead(gzPath), CompressionMode.Decompress)
            : File.OpenRead(jsonPath);

        byte[] vecs;
        byte[] lbls;
        int    count;
        await using (src)
            (vecs, lbls, count) = await LoadRawAsync(src);

        Console.Error.WriteLine($"Loaded {count:N0} vectors. Running K-means (NCC={NCC}, iterations={Iters})...");

        byte[]   centroids  = RunKMeans(vecs, count);
        byte[]   assignments = Assign(vecs, count, centroids);

        Console.Error.WriteLine("Saving index...");
        var idxPath = Path.Combine(appDataDir, "references.idx");
        await SaveAsync(idxPath, centroids, assignments, count);
        Console.Error.WriteLine($"Saved to {idxPath}");
    }

    // ── K-means ─────────────────────────────────────────────────────────────────

    static byte[] RunKMeans(byte[] vecs, int count)
    {
        var rng = new Random(42);

        // Equal-frequency init: sort by dim-0, pick evenly-spaced centroids.
        // Gives far better coverage than purely random sampling.
        int[] sortedIdx = Enumerable.Range(0, count)
            .OrderBy(i => vecs[i * Stride])
            .ToArray();

        var centroids = new byte[NCC * Stride];
        for (int c = 0; c < NCC; c++)
        {
            int srcIdx = sortedIdx[(long)c * (count - 1) / (NCC - 1)];
            Buffer.BlockCopy(vecs, srcIdx * Stride, centroids, c * Stride, Stride);
        }

        for (int iter = 0; iter < Iters; iter++)
        {
            byte[] assignments = Assign(vecs, count, centroids);
            UpdateCentroids(vecs, count, assignments, centroids);
            Console.Error.WriteLine($"  Iteration {iter + 1}/{Iters} done.");
        }

        return centroids;
    }

    static byte[] Assign(byte[] vecs, int count, byte[] centroids)
    {
        var assignments = new byte[count];
        Parallel.For(0, count, i =>
            assignments[i] = (byte)VectorStore.NearestCentroidPublic(vecs, i * Stride, centroids, NCC));
        return assignments;
    }

    static void UpdateCentroids(byte[] vecs, int count, byte[] assignments, byte[] centroids)
    {
        var sums   = new float[NCC * Dims];
        var counts = new int[NCC];
        for (int i = 0; i < count; i++)
        {
            int c   = assignments[i];
            int off = i * Stride;
            int cOff = c * Dims;
            for (int d = 0; d < Dims; d++) sums[cOff + d] += vecs[off + d];
            counts[c]++;
        }
        for (int c = 0; c < NCC; c++)
        {
            int cnt  = counts[c];
            if (cnt == 0) continue;
            int cOff = c * Dims;
            int cbOff = c * Stride;
            for (int d = 0; d < Dims; d++)
                centroids[cbOff + d] = (byte)Math.Round(sums[cOff + d] / cnt);
        }
    }

    // ── I/O ─────────────────────────────────────────────────────────────────────

    static async Task<(byte[], byte[], int)> LoadRawAsync(Stream stream)
    {
        const byte Sentinel = 255;
        var vecs  = new byte[3_000_000 * Stride];
        var lbls  = new byte[3_000_000];
        int count = 0;

        await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable(
                           stream, AppJsonContext.Default.VectorRow))
        {
            if (row is null) continue;
            int off = count * Stride;
            for (int d = 0; d < Dims; d++)
            {
                float v = row.Vector[d];
                vecs[off + d] = v < 0f ? Sentinel : (byte)MathF.Round(MathF.Min(v, 1f) * 254f);
            }
            lbls[count] = (byte)(row.Label == "fraud" ? 1 : 0);
            count++;
        }
        return (vecs, lbls, count);
    }

    static async Task SaveAsync(string path, byte[] centroids, byte[] assignments, int count)
    {
        await using var fs = File.Create(path);
        await fs.WriteAsync(Magic);
        await fs.WriteAsync(BitConverter.GetBytes(NCC));
        await fs.WriteAsync(BitConverter.GetBytes(count));
        await fs.WriteAsync(centroids.AsMemory(0, NCC * Stride));
        await fs.WriteAsync(assignments.AsMemory(0, count));
    }
}
