using System.IO.Compression;
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
    private const int Dims = 14;
    private const int K = 5;
    private const float FraudThreshold = 0.6f;
    // Sentinel: -1 float → byte 255 (outside valid [0,254] range)
    private const byte Sentinel = 255;

    private byte[] _vectors = [];
    private byte[] _labels = [];
    private int _count;
    private volatile bool _ready;

    public bool IsReady => _ready;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var basePath = AppContext.BaseDirectory;
        var gzPath = Path.Combine(basePath, "App_Data", "references.json.gz");
        var jsonPath = Path.Combine(basePath, "App_Data", "references.json");

        Stream fileStream = File.Exists(gzPath)
            ? new GZipStream(File.OpenRead(gzPath), CompressionMode.Decompress)
            : File.OpenRead(jsonPath);

        await using (fileStream)
            await FillFromStreamAsync(fileStream, ct);

        _ready = true;
    }

    private async Task FillFromStreamAsync(Stream stream, CancellationToken ct)
    {
        _vectors = new byte[3_000_000 * Dims];
        _labels = new byte[3_000_000];

        int count = 0;

        await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable(
                           stream, AppJsonContext.Default.VectorRow, ct))
        {
            if (row is null) continue;

            int offset = count * Dims;
            for (int d = 0; d < Dims; d++)
                _vectors[offset + d] = Quantize(row.Vector[d]);

            _labels[count] = (byte)(row.Label == "fraud" ? 1 : 0);
            count++;
        }

        _count = count;
    }

    // Thread-safe after _ready is set — arrays are only written during LoadAsync.
    public (bool Approved, float FraudScore) Search(float[] query)
    {
        Span<byte> qBytes = stackalloc byte[Dims];
        for (int d = 0; d < Dims; d++)
            qBytes[d] = Quantize(query[d]);

        Span<int> topDist = stackalloc int[K];
        Span<byte> topLabels = stackalloc byte[K];
        topDist.Fill(int.MaxValue);

        var vectors = _vectors;
        var labels = _labels;
        int count = _count;

        for (int i = 0; i < count; i++)
        {
            int dist = DistanceSquared(qBytes, vectors, i * Dims);

            int maxIdx = 0;
            for (int j = 1; j < K; j++)
                if (topDist[j] > topDist[maxIdx]) maxIdx = j;

            if (dist < topDist[maxIdx])
            {
                topDist[maxIdx] = dist;
                topLabels[maxIdx] = labels[i];
            }
        }

        int fraudCount = 0;
        for (int i = 0; i < K; i++)
            if (topLabels[i] == 1) fraudCount++;

        float fraudScore = (float)fraudCount / K;
        return (fraudScore < FraudThreshold, fraudScore);
    }

    static int DistanceSquared(ReadOnlySpan<byte> query, byte[] store, int offset)
    {
        int sum = 0;
        for (int d = 0; d < Dims; d++)
        {
            int diff = query[d] - store[offset + d];
            sum += diff * diff;
        }
        return sum;
    }

    static byte Quantize(float value)
    {
        if (value < 0f) return Sentinel;
        return (byte)MathF.Round(MathF.Min(value, 1f) * 254f);
    }
}
