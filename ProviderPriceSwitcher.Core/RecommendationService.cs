namespace ProviderPriceSwitcher.Core;

public sealed class RecommendationService
{
    private readonly object _instanceState = new();

    public RecommendationDecision Decide(
        IReadOnlyList<SiteConfiguration> sites,
        IReadOnlyDictionary<string, PricingSnapshot> snapshots,
        UsageProfile usage,
        SiteRefreshResult refresh,
        string? currentProviderId,
        DateTimeOffset now)
    {
        _ = _instanceState;
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(refresh);
        var eligible = new List<RecommendationCandidate>();
        var manual = new List<RecommendationCandidate>();
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var site in sites)
        {
            if (!snapshots.TryGetValue(site.ProviderId, out var snapshot))
            {
                excluded[site.ProviderId] = "No pricing snapshot.";
                continue;
            }
            var candidate = new RecommendationCandidate { Site = site, Snapshot = snapshot, EstimatedCost = PricingCalculator.Calculate(usage, snapshot.Prices) };
            if (!snapshot.Matches(site))
            {
                manual.Add(candidate);
                excluded[site.ProviderId] = "Snapshot does not match current configuration, model, or group.";
                continue;

            }
            if (!refresh.Succeeded(site.ProviderId))
            {
                manual.Add(candidate);
                excluded[site.ProviderId] = "Site did not refresh successfully this round.";
                continue;
            }
            eligible.Add(candidate);
        }
        RecommendationCandidate? selected = null;
        if (eligible.Count > 0)
        {
            var minimum = eligible.Min(x => x.EstimatedCost);
            var tied = eligible.Where(x => x.EstimatedCost == minimum).ToList();
            selected = tied.FirstOrDefault(x => string.Equals(x.Site.ProviderId, currentProviderId, StringComparison.Ordinal))
                    ?? tied.OrderBy(x => x.Site.ProviderId, StringComparer.Ordinal).First();
        }
        return new RecommendationDecision { Selected = selected, EligibleCandidates = eligible, ManualSelectionCandidates = manual, ExcludedReasons = excluded };
    }
}
