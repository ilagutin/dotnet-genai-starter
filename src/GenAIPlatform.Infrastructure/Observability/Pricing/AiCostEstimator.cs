using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Observability;

namespace GenAIPlatform.Infrastructure.Observability.Pricing;

public sealed class AiCostEstimator(IPricingRepository pricingRepository)
{
    public async Task<CostEstimate?> EstimateAsync(
        string provider,
        string model,
        AiModelUsage? usage,
        int? embeddingTokens,
        string? embeddingProvider,
        string? embeddingModel,
        DateTimeOffset usedAtUtc,
        CancellationToken cancellationToken)
    {
        if (usage is null && embeddingTokens is null)
        {
            return null;
        }

        var modelPricing = usage is null
            ? null
            : await pricingRepository.GetEffectivePricingAsync(
                provider,
                model,
                usedAtUtc,
                cancellationToken);
        var embeddingPricing = embeddingTokens is null ||
                               string.IsNullOrWhiteSpace(embeddingProvider) ||
                               string.IsNullOrWhiteSpace(embeddingModel)
            ? null
            : await pricingRepository.GetEffectivePricingAsync(
                embeddingProvider,
                embeddingModel,
                usedAtUtc,
                cancellationToken);

        if (modelPricing is null && embeddingPricing is null)
        {
            return null;
        }

        if (modelPricing is not null &&
            embeddingPricing is not null &&
            !string.Equals(modelPricing.Currency, embeddingPricing.Currency, StringComparison.Ordinal))
        {
            return null;
        }

        var inputCost = PriceTokens(usage?.InputTokens, modelPricing?.InputTokenPricePerMillion ?? 0);
        var outputCost = PriceTokens(usage?.OutputTokens, modelPricing?.OutputTokenPricePerMillion ?? 0);
        var embeddingCost = PriceTokens(embeddingTokens, embeddingPricing?.EmbeddingTokenPricePerMillion ?? 0);
        var currency = modelPricing?.Currency ?? embeddingPricing!.Currency;

        return new CostEstimate(
            decimal.Round(inputCost + outputCost + embeddingCost, 8, MidpointRounding.AwayFromZero),
            currency,
            modelPricing?.Id ?? embeddingPricing!.Id);
    }

    private static decimal PriceTokens(int? tokens, decimal pricePerMillion)
    {
        if (tokens is null or <= 0 || pricePerMillion <= 0)
        {
            return 0;
        }

        return tokens.Value / 1_000_000m * pricePerMillion;
    }
}
