using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.ReferenceData;
using FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.Infrastructure.VectorStore;

namespace FMLab.RinhaDeBackend.DeteccaoDeFraude.Features.FraudDetection;

public class FraudDetectionHandler(ReferenceDataService referenceData, VectorStore vectorStore)
{
    public FraudDetectionResponse Handle(FraudDetectionRequest request)
    {
        var vector = Vectorize(request);
        var (approved, fraudScore) = vectorStore.Search(vector);
        return new FraudDetectionResponse { Approved = approved, FraudScore = fraudScore };
    }

    float[] Vectorize(FraudDetectionRequest r)
    {
        var p = referenceData.Normalization;
        var requestedAt = r.Transaction.RequestedAt;
        var minutesSinceLast = r.LastTransaction is null ? -1f : (float)(requestedAt - r.LastTransaction.Timestamp).TotalMinutes;
        var kmFromLast = r.LastTransaction is null ? -1f : Clamp((float)r.LastTransaction.KmFromCurrent / p.MaxKm);

        return [
            Clamp((float)r.Transaction.Amount / p.MaxAmount).ToRound(),                                                // [0]  amount
            Clamp((float)r.Transaction.Installments / p.MaxInstallments).ToRound(),                                    // [1]  installments
            Clamp((float)(r.Transaction.Amount / r.Customer.AverageAmount) / p.AmountVsAvgRatio).ToRound(),            // [2]  amount_vs_avg
            (requestedAt.Hour / 23f).ToRound(),                                                                        // [3]  hour_of_day
            (((int)requestedAt.DayOfWeek + 6) % 7 / 6f).ToRound(),                                                    // [4]  day_of_week (seg=0)
            minutesSinceLast < 0 ? -1f : Clamp(minutesSinceLast / p.MaxMinutes).ToRound(),                            // [5]  minutes_since_last_tx
            kmFromLast < 0 ? -1f : kmFromLast.ToRound(),                                                              // [6]  km_from_last_tx
            Clamp((float)r.Terminal.KmFromHome / p.MaxKm).ToRound(),                                                   // [7]  km_from_home
            Clamp((float)r.Customer.TransactionsLast24h / p.MaxTxCount24h).ToRound(),                                  // [8]  tx_count_24h
            r.Terminal.IsOnline ? 1f : 0f,                                                                             // [9]  is_online
            r.Terminal.CardPresent ? 1f : 0f,                                                                          // [10] card_present
            r.Customer.KnownMerchants.Contains(r.Merchant.Id) ? 0f : 1f,                                              // [11] unknown_merchant
            referenceData.MccRisk.GetValueOrDefault(r.Merchant.MerchantCategoryCode, 0.50f).ToRound(),                // [12] mcc_risk
            Clamp((float)r.Merchant.AverageAmount / p.MaxMerchantAvgAmount).ToRound(),                                 // [13] merchant_avg_amount
        ];
    }

    static float Clamp(float value) => Math.Clamp(value, 0f, 1f);
}

public static class FloatExtensions
{
    public static float ToRound(this float value, int decimals = 4)
        => (float)Math.Round(value, decimals);
}
