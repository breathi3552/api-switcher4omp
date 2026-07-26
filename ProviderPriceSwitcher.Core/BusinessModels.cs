namespace ProviderPriceSwitcher.Core;

public sealed record SiteConfiguration
{
    public required string ProviderId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public required string ConfigurationKey { get; init; }
    public required Uri BaseUrl { get; init; }
    public string SiteType { get; init; } = "new-api";
    public bool Enabled { get; init; } = true;
    public required string Model { get; init; }
    public required string CurrentGroup { get; init; }
    public decimal? CurrentGroupRatio { get; init; }
    public string GroupRatioSource { get; init; } = "未设置";
    public string AuthenticationMode { get; init; } = "无需认证";
    public string Currency { get; init; } = string.Empty;
    public decimal? CnyConversionRate { get; init; }
}

public enum SiteCredentialStatus
{
    NotConfigured,
    Available,
    Expired,
    Invalid
}

public sealed record SiteCredentialRecord
{
    public required string ProviderId { get; init; }
    public required string SiteType { get; init; }
    public required string AuthorizationScheme { get; init; }
    public required string AccessToken { get; init; }
    public string? CookieHeader { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SiteCredentialSummary
{
    public required string ProviderId { get; init; }
    public required SiteCredentialStatus Status { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public interface ISiteCredentialStore
{
    SiteCredentialRecord? LoadCredential(string providerId);
    void SaveCredential(SiteCredentialRecord credential);
    void ClearCredential(string providerId);
    SiteCredentialSummary GetSummary(string providerId);
}

public sealed record UsageProfile
{
    public required string ProfileId { get; init; }
    public required long UncachedInputTokens { get; init; }
    public required long CachedInputTokens { get; init; }
    public required long OutputTokens { get; init; }
}

public sealed record TokenPrices
{
    public required decimal InputPerMillion { get; init; }
    public required decimal CachedInputPerMillion { get; init; }
    public required decimal OutputPerMillion { get; init; }

    public void Validate()
    {
        if (InputPerMillion < 0 || CachedInputPerMillion < 0 || OutputPerMillion < 0)
            throw new ArgumentOutOfRangeException(nameof(TokenPrices), "Prices cannot be negative.");
    }
}

public sealed record PricingSnapshot
{
    public required string ProviderId { get; init; }
    public required string ConfigurationKey { get; init; }
    public required string Model { get; init; }
    public required string CurrentGroup { get; init; }
    public required TokenPrices Prices { get; init; }
    public TokenPrices? BasePrices { get; init; }
    public decimal? CurrentGroupRatio { get; init; }
    public string GroupRatioSource { get; init; } = "自动";
    public string? MinimumGroup { get; init; }
    public decimal? MinimumGroupRatio { get; init; }
    public TokenPrices? MinimumGroupPrices { get; init; }
    public required DateTimeOffset RefreshedAt { get; init; }


    public bool Matches(SiteConfiguration site) =>
        string.Equals(ProviderId, site.ProviderId, StringComparison.Ordinal) &&
        string.Equals(ConfigurationKey, site.ConfigurationKey, StringComparison.Ordinal) &&
        string.Equals(Model, site.Model, StringComparison.Ordinal) &&
        string.Equals(CurrentGroup, site.CurrentGroup, StringComparison.Ordinal);

    public PricingSnapshot WithCurrentRatio(decimal ratio, string source)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratio);
        var basis = BasePrices ?? (CurrentGroupRatio is > 0 ? Scale(Prices, 1m / CurrentGroupRatio.Value) : null);
        if (basis is null) return this;
        return this with { BasePrices = basis, CurrentGroupRatio = ratio, GroupRatioSource = source, Prices = Scale(basis, ratio) };
    }

    private static TokenPrices Scale(TokenPrices prices, decimal multiplier) => new()
    {
        InputPerMillion = prices.InputPerMillion * multiplier,
        CachedInputPerMillion = prices.CachedInputPerMillion * multiplier,
        OutputPerMillion = prices.OutputPerMillion * multiplier
    };
}

public enum SiteRefreshStatus
{
    Succeeded,
    Failed,
    AuthenticationRequired
}

public sealed record SiteRefreshState
{
    public required string ProviderId { get; init; }
    public required SiteRefreshStatus Status { get; init; }
    public string? FailureReason { get; init; }
}

public sealed record SiteRefreshResult
{
    public required IReadOnlyList<SiteRefreshState> States { get; init; }
    public bool Succeeded(string providerId) => States.Any(x =>
        string.Equals(x.ProviderId, providerId, StringComparison.Ordinal) && x.Status == SiteRefreshStatus.Succeeded);
}

public sealed record RecommendationCandidate
{
    public required SiteConfiguration Site { get; init; }
    public required PricingSnapshot Snapshot { get; init; }
    public required decimal EstimatedCost { get; init; }
}

public sealed record RecommendationDecision
{
    public RecommendationCandidate? Selected { get; init; }
    public required IReadOnlyList<RecommendationCandidate> EligibleCandidates { get; init; }
    public required IReadOnlyList<RecommendationCandidate> ManualSelectionCandidates { get; init; }
    public required IReadOnlyDictionary<string, string> ExcludedReasons { get; init; }
    public bool IsRecommended => Selected is not null;
}
