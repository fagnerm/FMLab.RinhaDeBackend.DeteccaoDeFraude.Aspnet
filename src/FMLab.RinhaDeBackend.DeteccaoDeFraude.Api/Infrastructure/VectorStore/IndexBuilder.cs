using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

/// <summary>
/// Constrói e persiste o índice IVF (Inverted File Index) baseado em K-means offline.
/// Deve ser executado na máquina do desenvolvedor (sem limites de CPU).
///
/// Formato binário RIF3 (little-endian):
///   4 bytes         — magic "RIF3"
///   4 bytes         — NCC (int32)
///   4 bytes         — count (int32)
///   NCC × 16 bytes  — centroides quantizados (byte)
///   count × 2 bytes — atribuições de cluster (ushort, suporta NCC até 65535)
///   count × 16 bytes— vetores quantizados (byte) ← elimina leitura do JSON no runtime
///   count × 1 byte  — rótulos binários (byte: 0=legítimo, 1=fraude)
/// </summary>
public static class IndexBuilder
{
    private const int Dims   = 14;
    private const int Stride = 16;
    private const int NCC    = 500;
    private const int Iters  = 10;

    // v3: inclui vetores e rótulos no arquivo, eliminando JSON no runtime.
    private static readonly byte[] Magic = "RIF3"u8.ToArray();

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

        byte[]   centroids   = RunKMeans(vecs, count);
        ushort[] assignments = Assign(vecs, count, centroids);

        Console.Error.WriteLine("Saving index...");
        var idxPath = Path.Combine(appDataDir, "references.idx");
        await SaveAsync(idxPath, centroids, assignments, vecs, lbls, count);
        Console.Error.WriteLine($"Saved to {idxPath}");
    }

    // ── K-means ─────────────────────────────────────────────────────────────────

    static byte[] RunKMeans(byte[] vecs, int count)
    {
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
            ushort[] assignments = Assign(vecs, count, centroids);
            UpdateCentroids(vecs, count, assignments, centroids);
            Console.Error.WriteLine($"  Iteration {iter + 1}/{Iters} done.");
        }

        return centroids;
    }

    static ushort[] Assign(byte[] vecs, int count, byte[] centroids)
    {
        var assignments = new ushort[count];
        Parallel.For(0, count, i =>
            assignments[i] = (ushort)VectorStore.NearestCentroidPublic(vecs, i * Stride, centroids, NCC));
        return assignments;
    }

    static void UpdateCentroids(byte[] vecs, int count, ushort[] assignments, byte[] centroids)
    {
        var sums   = new float[NCC * Dims];
        var counts = new int[NCC];

        for (int i = 0; i < count; i++)
        {
            int c    = assignments[i];
            int off  = i * Stride;
            int cOff = c * Dims;
            for (int d = 0; d < Dims; d++) sums[cOff + d] += vecs[off + d];
            counts[c]++;
        }

        for (int c = 0; c < NCC; c++)
        {
            int cnt = counts[c];
            if (cnt == 0) continue;
            int cOff  = c * Dims;
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

    /// <summary>
    /// Formato RIF3: cabeçalho + centroides + assignments (ushort) + vetores + rótulos.
    /// Vetores e rótulos embutidos eliminam a necessidade de carregar o JSON no runtime.
    /// </summary>
    static async Task SaveAsync(string path, byte[] centroids, ushort[] assignments,
                                byte[] vecs, byte[] lbls, int count)
    {
        await using var fs = File.Create(path);

        await fs.WriteAsync(Magic);                                          // "RIF3"
        await fs.WriteAsync(BitConverter.GetBytes(NCC));                     // NCC
        await fs.WriteAsync(BitConverter.GetBytes(count));                   // count
        await fs.WriteAsync(centroids.AsMemory(0, NCC * Stride));            // centroides
        fs.Write(MemoryMarshal.AsBytes(assignments.AsSpan(0, count)));       // assignments (ushort)
        await fs.WriteAsync(vecs.AsMemory(0, count * Stride));               // vetores quantizados
        await fs.WriteAsync(lbls.AsMemory(0, count));                        // rótulos binários
    }
}
