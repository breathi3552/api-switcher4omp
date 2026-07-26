using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Infrastructure;

static void Assert(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static SiteConfiguration Site(string id, string type = "fake", bool enabled = true) => new()
{
    ProviderId = id,
    ConfigurationKey = "key",
    BaseUrl = new Uri("https://example.test"),
    SiteType = type,
    Enabled = enabled,
    Model = "model",
    CurrentGroup = "standard"
};

static PricingSnapshot Snapshot(string id, decimal price) => new()
{
    ProviderId = id,
    ConfigurationKey = "key",
    Model = "model",
    CurrentGroup = "standard",
    Prices = new TokenPrices { InputPerMillion = price, CachedInputPerMillion = 0, OutputPerMillion = 0 },
    RefreshedAt = DateTimeOffset.UtcNow
};

static SitePricingResult Pricing(string id, decimal price) => new()
{
    Snapshot = Snapshot(id, price),
    ValidGroups = new HashSet<string>(["standard"]),
    MinimumValidGroup = "standard",
    MinimumGroupRatio = 1,
    Warnings = []
};

var root = Path.Combine(Path.GetTempPath(), "ProviderPriceSwitcher.Refresh.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var usage = new UsageProfile { ProfileId = "test", UncachedInputTokens = 1, CachedInputTokens = 1, OutputTokens = 1 };
    var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var adapter = new FakeAdapter("fake", async (site, ct) =>
    {
        started.TrySetResult(true);
        await release.Task.WaitAsync(ct);
        return Pricing(site.ProviderId, site.ProviderId == "one" ? 1 : 2);
    });
    var service = new PricingRefreshService(new PricingAdapterRegistry([adapter]), Microsoft.Extensions.Logging.Abstractions.NullLogger<PricingRefreshService>.Instance, new RecommendationService());
    var run = service.RefreshAsync([Site("one"), Site("two")], usage, null, 5);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(adapter.Calls == 2, "enabled sites must fetch concurrently");
    release.TrySetResult(true);
    var result = await run;
    Assert(result.SuccessfulResults.Count == 2 && result.Recommendation.EligibleCandidates.Count == 2, "success and recommendation");

    var fallbackRoot = Path.Combine(root, "fallback");
    var fallbackRepo = new JsonPricingSnapshotRepository(fallbackRoot);
    fallbackRepo.Save(Snapshot("old", 9));
    var fallbackService = new PricingRefreshService(new PricingAdapterRegistry([new FakeAdapter("fake", (site, ct) => throw new PricingAdapterException(PricingAdapterFailure.Request, "offline"))]), Microsoft.Extensions.Logging.Abstractions.NullLogger<PricingRefreshService>.Instance);
    var fallback = await fallbackService.RefreshAsync([Site("old")], usage, null, 5, fallbackRepo.LoadAll());
    Assert(fallback.LatestSnapshots["old"].Prices.InputPerMillion == 9, "failure preserves old snapshot");
    Assert(fallback.Recommendation.EligibleCandidates.Count == 0 && fallback.Recommendation.ManualSelectionCandidates.Count == 1, "failed old site is manual only");

    var disabled = await service.RefreshAsync([Site("disabled", enabled: false), Site("unknown", "missing")], usage, null, 5);
    Assert(disabled.Sites.Single(x => x.ProviderId == "disabled").Status == PricingRefreshSiteStatus.Disabled && adapter.Calls == 2, "disabled site must not request");
    Assert(disabled.Sites.Single(x => x.ProviderId == "unknown").FailureKind == PricingRefreshFailureKind.UnknownSiteType, "unknown type fails");

    var timeoutService = new PricingRefreshService(new PricingAdapterRegistry([new FakeAdapter("fake", async (site, ct) =>
    {
        if (site.ProviderId == "slow") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return Pricing(site.ProviderId, 3);
    })]), Microsoft.Extensions.Logging.Abstractions.NullLogger<PricingRefreshService>.Instance);
    var timeout = await timeoutService.RefreshAsync([Site("slow"), Site("fast")], usage, null, 1);
    Assert(timeout.Sites.Single(x => x.ProviderId == "slow").FailureKind == PricingRefreshFailureKind.Timeout && timeout.Sites.Single(x => x.ProviderId == "fast").Status == PricingRefreshSiteStatus.Succeeded, "single timeout isolation");
    var authRoot = Path.Combine(root, "auth");
    var authRepo = new JsonPricingSnapshotRepository(authRoot);
    authRepo.Save(Snapshot("auth", 7));
    var authService = new PricingRefreshService(new PricingAdapterRegistry([new FakeAdapter("fake", (site, ct) => throw new PricingAdapterException(PricingAdapterFailure.Authentication, "访问被拒绝"))]), Microsoft.Extensions.Logging.Abstractions.NullLogger<PricingRefreshService>.Instance);
    var auth = await authService.RefreshAsync([Site("auth")], usage, null, 5, authRepo.LoadAll());
    Assert(auth.Sites.Single().FailureKind == PricingRefreshFailureKind.Authentication, "structured auth failure kind");
    Assert(auth.Recommendation.ManualSelectionCandidates.Count == 1, "auth site with old snapshot stays manual");

    var misleadingService = new PricingRefreshService(new PricingAdapterRegistry([new FakeAdapter("fake", (site, ct) => throw new PricingAdapterException(PricingAdapterFailure.Request, "认证服务网络故障"))]), Microsoft.Extensions.Logging.Abstractions.NullLogger<PricingRefreshService>.Instance);
    var misleading = await misleadingService.RefreshAsync([Site("misleading")], usage, null, 5);
    Assert(misleading.Sites.Single().FailureKind == PricingRefreshFailureKind.Adapter, "message must not control failure kind");

    var adapterTimeoutService = new PricingRefreshService(new PricingAdapterRegistry([new FakeAdapter("fake", (site, ct) => throw new PricingAdapterException(PricingAdapterFailure.Timeout, "adapter deadline"))]), Microsoft.Extensions.Logging.Abstractions.NullLogger<PricingRefreshService>.Instance);
    var adapterTimeout = await adapterTimeoutService.RefreshAsync([Site("adapter-timeout")], usage, null, 5);
    Assert(adapterTimeout.Sites.Single().FailureKind == PricingRefreshFailureKind.Timeout, "structured adapter timeout kind");
    const string syntheticFailure = "synthetic-token-DO-NOT-LOG https://example.invalid/prices?api_key=synthetic-query-secret&token=synthetic-token&cookie=synthetic-cookie C:\\Users\\Private\\Documents\\secret";
    var unexpectedService = new PricingRefreshService(new PricingAdapterRegistry([new FakeAdapter("fake", (_, _) => throw new InvalidOperationException(syntheticFailure))]), Microsoft.Extensions.Logging.Abstractions.NullLogger<PricingRefreshService>.Instance);
    var unexpected = await unexpectedService.RefreshAsync([Site("unexpected")], usage, null, 5);
    var unexpectedSite = unexpected.Sites.Single();
    Assert(unexpectedSite.FailureKind == PricingRefreshFailureKind.Unexpected && !unexpectedSite.ToString().Contains(syntheticFailure, StringComparison.Ordinal), "unexpected failures must be structured and non-leaking");
    Assert(unexpected.Recommendation.EligibleCandidates.Count == 0, "unexpected failure must not participate in automatic recommendation");

    var cancelService = new PricingRefreshService(new PricingAdapterRegistry([new FakeAdapter("fake", async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Pricing("cancel", 1); })]), Microsoft.Extensions.Logging.Abstractions.NullLogger<PricingRefreshService>.Instance);
    using var userCancel = new CancellationTokenSource();
    var canceledRun = cancelService.RefreshAsync([Site("cancel")], usage, null, 30, cancellationToken: userCancel.Token);
    userCancel.Cancel();
    try { await canceledRun; throw new InvalidOperationException("user cancellation was swallowed"); } catch (OperationCanceledException) { }

    var duplicateThrown = false;
    try { _ = new PricingAdapterRegistry([new FakeAdapter("fake", (_, _) => Task.FromResult(Pricing("x", 1))), new FakeAdapter("fake", (_, _) => Task.FromResult(Pricing("y", 1)))]); }
    catch (ArgumentException) { duplicateThrown = true; }
    Assert(duplicateThrown, "duplicate site types rejected");
    Console.WriteLine("Pricing refresh contract tests passed.");
}
finally { Directory.Delete(root, true); }

sealed class FakeAdapter(string siteType, Func<SiteConfiguration, CancellationToken, Task<SitePricingResult>> fetch) : IPricingAdapter
{
    private int _calls;
    public PricingAdapterDescriptor Descriptor { get; } = new(siteType, siteType, false, ["无需认证"]);
    public int Calls => Volatile.Read(ref _calls);
    public Task<SitePricingResult> FetchAsync(SiteConfiguration site, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return fetch(site, cancellationToken);
    }
}
