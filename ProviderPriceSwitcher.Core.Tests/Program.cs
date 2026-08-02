using ProviderPriceSwitcher.Core;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var calculator = new PricingCalculator();
var highCache = new UsageProfile { ProfileId = "u", UncachedInputTokens = 1_000_000, CachedInputTokens = 9_000_000, OutputTokens = 2_000_000 };
var prices = new TokenPrices { InputPerMillion = 1m, CachedInputPerMillion = 0.1m, OutputPerMillion = 2m };
Assert(PricingCalculator.Calculate(highCache, prices) == 5.9m, "high cache cost");
try { PricingCalculator.Calculate(highCache with { OutputTokens = -1 }, prices); throw new InvalidOperationException("negative tokens accepted"); } catch (ArgumentOutOfRangeException) { }
try { PricingCalculator.Calculate(highCache, prices with { InputPerMillion = -1 }); throw new InvalidOperationException("negative price accepted"); } catch (ArgumentOutOfRangeException) { }

var now = DateTimeOffset.UtcNow;
var usage = new UsageProfile { ProfileId = "u", UncachedInputTokens = 1_000_000, CachedInputTokens = 0, OutputTokens = 0 };
SiteConfiguration Site(string id, string key = "k", string model = "m", string group = "g") => new() { ProviderId = id, ConfigurationKey = key, BaseUrl = new Uri($"https://{id}.example"), Model = model, CurrentGroup = group };
PricingSnapshot Snapshot(SiteConfiguration s, DateTimeOffset at, decimal input = 1m) => new() { ProviderId = s.ProviderId, ConfigurationKey = s.ConfigurationKey, Model = s.Model, CurrentGroup = s.CurrentGroup, Prices = new TokenPrices { InputPerMillion = input, CachedInputPerMillion = 0, OutputPerMillion = 0 }, RefreshedAt = at };
RecommendationDecision Decide(IReadOnlyList<SiteConfiguration> sites, IReadOnlyDictionary<string, PricingSnapshot> snaps, SiteRefreshResult refresh, string? current = null, UsageProfile? selectedUsage = null) => new RecommendationService().Decide(sites, snaps, selectedUsage ?? usage, refresh, current, now);

var oldSite = Site("old");
var old = Decide([oldSite], new Dictionary<string, PricingSnapshot> { ["old"] = Snapshot(oldSite, now.AddYears(-1)) }, new SiteRefreshResult { States = [new SiteRefreshState { ProviderId = "old", Status = SiteRefreshStatus.Succeeded }] });
Assert(old.Selected?.Site.ProviderId == "old", "old matching snapshot refreshed successfully this round must remain eligible");
var a = Site("a"); var b = Site("b");
var success = new SiteRefreshResult { States = [new SiteRefreshState { ProviderId = "a", Status = SiteRefreshStatus.Succeeded }, new SiteRefreshState { ProviderId = "b", Status = SiteRefreshStatus.Succeeded }] };
var equal = Decide([a, b], new Dictionary<string, PricingSnapshot> { ["a"] = Snapshot(a, now), ["b"] = Snapshot(b, now) }, success, "b");
Assert(equal.Selected?.Site.ProviderId == "b", "current provider tie break");
var stable = Decide([b, a], new Dictionary<string, PricingSnapshot> { ["a"] = Snapshot(a, now), ["b"] = Snapshot(b, now) }, success);
Assert(stable.Selected?.Site.ProviderId == "a", "ordinal tie break");
var failed = Decide([a, b], new Dictionary<string, PricingSnapshot> { ["a"] = Snapshot(a, now, input: 1), ["b"] = Snapshot(b, now, input: 0.1m) }, new SiteRefreshResult { States = [new SiteRefreshState { ProviderId = "a", Status = SiteRefreshStatus.Succeeded }, new SiteRefreshState { ProviderId = "b", Status = SiteRefreshStatus.Failed, FailureReason = "offline" }] });
Assert(failed.Selected?.Site.ProviderId == "a" && failed.ManualSelectionCandidates.Any(x => x.Site.ProviderId == "b"), "refresh success filter");
var changed = Decide([Site("a", key: "changed")], new Dictionary<string, PricingSnapshot> { ["a"] = Snapshot(a, now) }, success);
Assert(!changed.IsRecommended && changed.ManualSelectionCandidates.Count == 1, "configuration invalidation");
var modelChanged = Decide([Site("a", model: "new")], new Dictionary<string, PricingSnapshot> { ["a"] = Snapshot(a, now) }, success);
Assert(!modelChanged.IsRecommended, "model invalidation");
var groupChanged = Decide([Site("a", group: "new")], new Dictionary<string, PricingSnapshot> { ["a"] = Snapshot(a, now) }, success);
Assert(!groupChanged.IsRecommended, "group invalidation");
var changedUsage = usage with { ProfileId = "other", CachedInputTokens = 1_000_000 };
var recalculated = Decide([a], new Dictionary<string, PricingSnapshot> { ["a"] = Snapshot(a, now, input: 2m) }, success, selectedUsage: changedUsage);
Assert(recalculated.IsRecommended && recalculated.Selected?.EstimatedCost == 2m, "usage profile change must reuse price snapshot and recalculate locally");
var ratioSnapshot = Snapshot(a, now, input: 2m) with
{
    BasePrices = new TokenPrices { InputPerMillion = 2m, CachedInputPerMillion = 0.2m, OutputPerMillion = 4m },
    CurrentGroupRatio = 1m
};
var overridden = ratioSnapshot.WithCurrentRatio(0.5m, "手动");
Assert(overridden.Prices.InputPerMillion == 1m && overridden.Prices.CachedInputPerMillion == 0.1m && overridden.Prices.OutputPerMillion == 2m && overridden.GroupRatioSource == "手动", "manual ratio scales all prices");
Console.WriteLine("Core contract tests passed.");
Assert(InferenceApiKeySummary.Mask("short-key") == "********", "short inference keys must use fixed masking");
Assert(InferenceApiKeySummary.Mask("1234567890123456") == "1234…3456", "long inference keys must expose only four-character ends");
Console.WriteLine("Core inference credential contract tests passed.");
