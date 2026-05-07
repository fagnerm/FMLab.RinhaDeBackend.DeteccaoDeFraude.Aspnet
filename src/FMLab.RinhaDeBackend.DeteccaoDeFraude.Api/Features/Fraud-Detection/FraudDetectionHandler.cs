using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.ReferenceData;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

public class FraudDetectionHandler(ReferenceDataService referenceData, VectorStore vectorStore)
{
    public FraudDetectionResponse Handle(FraudDetectionRequest request)
    {
        Span<float> vector = stackalloc float[14];
        Vectorize(request, vector);
        var (approved, fraudScore) = vectorStore.Search(vector);
        return new FraudDetectionResponse { Approved = approved, FraudScore = fraudScore };
    }

    void Vectorize(FraudDetectionRequest r, Span<float> v)
    {
        var p = referenceData.Normalization;
        var requestedAt = r.Transaction.RequestedAt;
        var minutesSinceLast = r.LastTransaction is null ? -1f : (float)(requestedAt - r.LastTransaction.Timestamp).TotalMinutes;
        var kmFromLast = r.LastTransaction is null ? -1f : Clamp((float)r.LastTransaction.KmFromCurrent / p.MaxKm);

        // Cast decimal → float before arithmetic to avoid slow decimal division.
        var amount     = (float)r.Transaction.Amount;
        var avgAmount  = (float)r.Customer.AverageAmount;
        var merchantAvg = (float)r.Merchant.AverageAmount;

        // ToRound() removed: Quantize() in VectorStore already rounds float→byte,
        // so rounding to 4 decimal places here is a no-op that costs 12 Math.Round calls/request.
        v[0]  = Clamp(amount / p.MaxAmount);
        v[1]  = Clamp((float)r.Transaction.Installments / p.MaxInstallments);
        v[2]  = Clamp(amount / (avgAmount * p.AmountVsAvgRatio));
        v[3]  = requestedAt.Hour / 23f;
        v[4]  = ((int)requestedAt.DayOfWeek + 6) % 7 / 6f;
        v[5]  = minutesSinceLast < 0 ? -1f : Clamp(minutesSinceLast / p.MaxMinutes);
        v[6]  = kmFromLast;
        v[7]  = Clamp((float)r.Terminal.KmFromHome / p.MaxKm);
        v[8]  = Clamp((float)r.Customer.TransactionsLast24h / p.MaxTxCount24h);
        v[9]  = r.Terminal.IsOnline ? 1f : 0f;
        v[10] = r.Terminal.CardPresent ? 1f : 0f;
        v[11] = r.Customer.KnownMerchants.Contains(r.Merchant.Id) ? 0f : 1f;
        v[12] = referenceData.MccRisk.GetValueOrDefault(r.Merchant.MerchantCategoryCode, 0.50f);
        v[13] = Clamp(merchantAvg / p.MaxMerchantAvgAmount);
    }

    static float Clamp(float value) => Math.Clamp(value, 0f, 1f);
}
