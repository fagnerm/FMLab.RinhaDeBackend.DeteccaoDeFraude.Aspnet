using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.Serialization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

public sealed class FraudResponseTable
{
    public readonly ReadOnlyMemory<byte>[] Responses;

    public FraudResponseTable()
    {
        Responses = new ReadOnlyMemory<byte>[6];
        for (int i = 0; i < 6; i++)
        {
            Responses[i] = JsonSerializer.SerializeToUtf8Bytes(
                new FraudDetectionResponse { Approved = i < 3, FraudScore = (float)i / 5 },
                AppJsonContext.Default.FraudDetectionResponse);
        }
    }
}
