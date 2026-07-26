namespace ProviderPriceSwitcher.Core;

public sealed class PricingCalculator
{
    public static decimal Calculate(UsageProfile usage, TokenPrices prices)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(prices);
        if (usage.UncachedInputTokens < 0 || usage.CachedInputTokens < 0 || usage.OutputTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(usage), "Token counts cannot be negative.");
        prices.Validate();
        const decimal perMillion = 1_000_000m;
        return usage.UncachedInputTokens * prices.InputPerMillion / perMillion
             + usage.CachedInputTokens * prices.CachedInputPerMillion / perMillion
             + usage.OutputTokens * prices.OutputPerMillion / perMillion;
    }
}
