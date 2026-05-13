using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.ReferenceData;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

public class FraudDetectionHandler(
    ReferenceDataService referenceData,
    VectorStore vectorStore,
    FraudResponseTable responseTable)
{
    private readonly ReadOnlyMemory<byte>[] _responses = responseTable.Responses;

    public ReadOnlyMemory<byte> Handle(FraudDetectionRequest request)
    {
        Span<float> vector = stackalloc float[14];
        Vectorize(request, vector);
        return _responses[vectorStore.Search(vector)];
    }

    void Vectorize(FraudDetectionRequest r, Span<float> v)
    {
        var p = referenceData.Normalization;
        var requestedAt = r.Transaction.RequestedAt;
        var minutesSinceLast = r.LastTransaction is null ? -1f : (float)(requestedAt - r.LastTransaction.Timestamp).TotalMinutes;
        var kmFromLast = r.LastTransaction is null ? -1f : Clamp((float)(r.LastTransaction.KmFromCurrent / p.MaxKm));

        var amount      = (float)r.Transaction.Amount;
        var avgAmount   = (float)r.Customer.AverageAmount;
        var merchantAvg = (float)r.Merchant.AverageAmount;

        v[0]  = Clamp(amount / p.MaxAmount);
        v[1]  = Clamp(r.Transaction.Installments / p.MaxInstallments);
        v[2]  = Clamp(amount / (avgAmount * p.AmountVsAvgRatio));
        v[3]  = requestedAt.Hour / 23f;
        v[4]  = ((int)requestedAt.DayOfWeek + 6) % 7 / 6f;
        v[5]  = minutesSinceLast < 0 ? -1f : Clamp(minutesSinceLast / p.MaxMinutes);
        v[6]  = kmFromLast;
        v[7]  = Clamp((float)(r.Terminal.KmFromHome / p.MaxKm));
        v[8]  = Clamp(r.Customer.TransactionsLast24h / p.MaxTxCount24h);
        v[9]  = r.Terminal.IsOnline ? 1f : 0f;
        v[10] = r.Terminal.CardPresent ? 1f : 0f;
        v[11] = r.Customer.KnownMerchants.Contains(r.Merchant.Id) ? 0f : 1f;
        v[12] = referenceData.MccRisk.GetValueOrDefault(r.Merchant.MerchantCategoryCode, 0.50f);
        v[13] = Clamp(merchantAvg / p.MaxMerchantAvgAmount);
    }

    static float Clamp(float value) => Math.Clamp(value, 0f, 1f);
}
