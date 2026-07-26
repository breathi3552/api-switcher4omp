using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Adapters;

public interface IPricingAdapter
{
    string SiteType { get; }
    Task<SitePricingResult> FetchAsync(SiteConfiguration site, CancellationToken cancellationToken = default);
}

public enum PricingAdapterFailure
{
    Request,
    InvalidResponse,
    ModelNotFound,
    InvalidQuotaType,
    InvalidPrice,
    InvalidGroups,
    UnsupportedBilling
}

public sealed class PricingAdapterException : Exception
{
    public PricingAdapterFailure Failure { get; }
    public PricingAdapterException(PricingAdapterFailure failure, string message) : base(message) => Failure = failure;
    public PricingAdapterException(PricingAdapterFailure failure, string message, Exception inner) : base(message, inner) => Failure = failure;
}

public sealed record SitePricingResult
{
    public required PricingSnapshot Snapshot { get; init; }
    public required IReadOnlySet<string> ValidGroups { get; init; }
    public required string MinimumValidGroup { get; init; }
    public required decimal MinimumGroupRatio { get; init; }
    public string? BillingExpression { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public DateTimeOffset FetchedAt => Snapshot.RefreshedAt;
    public TokenPrices Prices => Snapshot.Prices;
}
