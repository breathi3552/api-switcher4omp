using ProviderPriceSwitcher.Adapters;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Infrastructure;

public enum PricingRefreshSiteStatus
{
    Succeeded,
    Failed,
    Disabled
}

public enum PricingRefreshFailureKind
{
    UnknownSiteType,
    Timeout,
    Authentication,
    Adapter,
    Unexpected
}

public sealed record PricingRefreshSiteResult
{
    public required string ProviderId { get; init; }
    public required PricingRefreshSiteStatus Status { get; init; }
    public SitePricingResult? PricingResult { get; init; }
    public PricingSnapshot? PreviousSnapshot { get; init; }
    public bool PreviousSnapshotAvailable => PreviousSnapshot is not null;
    public PricingRefreshFailureKind? FailureKind { get; init; }
    public string? FailureMessage { get; init; }

    public PricingSnapshot? Snapshot => PricingResult?.Snapshot;
}

public sealed record PricingRefreshResult
{
    public required IReadOnlyList<PricingRefreshSiteResult> Sites { get; init; }
    public IReadOnlyList<PricingRefreshSiteResult> SiteResults => Sites;
    public IReadOnlyList<PricingRefreshSiteResult> States => Sites;
    public required IReadOnlyDictionary<string, SitePricingResult> SuccessfulResults { get; init; }
    public required IReadOnlyDictionary<string, PricingSnapshot> LatestSnapshots { get; init; }
    public required RecommendationDecision Recommendation { get; init; }
    public RecommendationDecision RecommendationDecision => Recommendation;
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public DateTimeOffset StartTime => StartedAt;
    public DateTimeOffset CompletionTime => CompletedAt;
}

public sealed class PricingRefreshService
{
    private readonly Dictionary<string, IPricingAdapter> _adapters;
    private readonly JsonPricingSnapshotRepository _snapshots;
    private readonly RecommendationService _recommendation;

    public PricingRefreshService(
        IEnumerable<IPricingAdapter> adapters,
        JsonPricingSnapshotRepository snapshots,
        RecommendationService? recommendationService = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(snapshots);
        var map = new Dictionary<string, IPricingAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (string.IsNullOrWhiteSpace(adapter.SiteType))
                throw new ArgumentException("Pricing adapters must declare a site type.", nameof(adapters));
            if (!map.TryAdd(adapter.SiteType, adapter))
                throw new ArgumentException($"Duplicate pricing adapter site type '{adapter.SiteType}'.", nameof(adapters));
        }
        _adapters = map;
        _snapshots = snapshots;
        _recommendation = recommendationService ?? new RecommendationService();
    }

    public Task<PricingRefreshResult> RefreshAsync(
        LocalAppSettings settings,
        UsageProfile usage,
        string? currentProviderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return RefreshAsync(settings.Sites, usage, currentProviderId, settings.RequestTimeoutSeconds, cancellationToken);
    }

    public Task<PricingRefreshResult> RefreshAsync(
        IReadOnlyList<SiteConfiguration> sites,
        UsageProfile usage,
        string? currentProviderId,
        int requestTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentOutOfRangeException.ThrowIfNegative(requestTimeoutSeconds);
        return RefreshCoreAsync(sites, usage, currentProviderId, requestTimeoutSeconds, cancellationToken);
    }
    public IReadOnlyDictionary<string, PricingSnapshot> LoadSnapshots() => _snapshots.LoadAll();

    public void DeleteSnapshot(string providerId) => _snapshots.Delete(providerId);
    public void SaveSnapshot(PricingSnapshot snapshot) => _snapshots.Save(snapshot);

    public async Task<SitePricingResult> ProbeAsync(SiteConfiguration site, int requestTimeoutSeconds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        if (!_adapters.TryGetValue(site.SiteType, out var adapter))
            throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, $"没有为站点类型 '{site.SiteType}' 注册价格适配器。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(requestTimeoutSeconds));
        return await adapter.FetchAsync(site, timeout.Token).ConfigureAwait(false);
    }

    private async Task<PricingRefreshResult> RefreshCoreAsync(
        IReadOnlyList<SiteConfiguration> sites,
        UsageProfile usage,
        string? currentProviderId,
        int requestTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var previous = _snapshots.LoadAll();
        using var roundCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = sites.Select(site => RefreshSiteAsync(site, previous.GetValueOrDefault(site.ProviderId), requestTimeoutSeconds, roundCancellation.Token)).ToArray();

        IReadOnlyList<PricingRefreshSiteResult> results;
        try
        {
            results = await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            roundCancellation.Cancel();
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var successful = results
            .Where(x => x.Status == PricingRefreshSiteStatus.Succeeded && x.PricingResult is not null)
            .ToDictionary(x => x.ProviderId, x => x.PricingResult!, StringComparer.Ordinal);
        var latest = new Dictionary<string, PricingSnapshot>(previous, StringComparer.Ordinal);
        foreach (var result in successful.Values)
            latest[result.Snapshot.ProviderId] = result.Snapshot;
        if (successful.Count > 0)
            _snapshots.SaveAll(latest.Values);

        var refreshStates = results
            .Where(x => x.Status != PricingRefreshSiteStatus.Disabled)
            .Select(x => new SiteRefreshState
            {
                ProviderId = x.ProviderId,
                Status = x.Status == PricingRefreshSiteStatus.Succeeded
                    ? SiteRefreshStatus.Succeeded
                    : x.FailureKind == PricingRefreshFailureKind.Authentication
                        ? SiteRefreshStatus.AuthenticationRequired
                        : SiteRefreshStatus.Failed,
                FailureReason = x.FailureMessage
            }).ToArray();
        var decision = _recommendation.Decide(sites, latest, usage, new SiteRefreshResult { States = refreshStates }, currentProviderId, DateTimeOffset.UtcNow);
        return new PricingRefreshResult
        {
            Sites = results,
            SuccessfulResults = successful,
            LatestSnapshots = latest,
            Recommendation = decision,
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<PricingRefreshSiteResult> RefreshSiteAsync(
        SiteConfiguration site,
        PricingSnapshot? previous,
        int requestTimeoutSeconds,
        CancellationToken roundCancellation)
    {
        if (!site.Enabled)
            return new PricingRefreshSiteResult { ProviderId = site.ProviderId, Status = PricingRefreshSiteStatus.Disabled, PreviousSnapshot = previous };

        if (!_adapters.TryGetValue(site.SiteType, out var adapter))
            return Failed(site, previous, PricingRefreshFailureKind.UnknownSiteType, $"No pricing adapter is registered for site type '{site.SiteType}'.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(roundCancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(requestTimeoutSeconds));
        try
        {
            var pricing = await adapter.FetchAsync(site, timeout.Token).ConfigureAwait(false);
            return new PricingRefreshSiteResult
            {
                ProviderId = site.ProviderId,
                Status = PricingRefreshSiteStatus.Succeeded,
                PricingResult = pricing,
                PreviousSnapshot = previous
            };
        }
        catch (OperationCanceledException) when (!roundCancellation.IsCancellationRequested)
        {
            return Failed(site, previous, PricingRefreshFailureKind.Timeout, $"Pricing request timed out after {requestTimeoutSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PricingAdapterException ex)
        {
            var kind = ex.Message.Contains("绑定", StringComparison.Ordinal) || ex.Message.Contains("令牌", StringComparison.Ordinal) || ex.Message.Contains("认证", StringComparison.Ordinal)
                ? PricingRefreshFailureKind.Authentication
                : PricingRefreshFailureKind.Adapter;
            return Failed(site, previous, kind, ex.Message);
        }
        catch (Exception ex)
        {
            return Failed(site, previous, PricingRefreshFailureKind.Unexpected, ex.Message);
        }
    }

    private static PricingRefreshSiteResult Failed(
        SiteConfiguration site,
        PricingSnapshot? previous,
        PricingRefreshFailureKind kind,
        string message) => new()
        {
            ProviderId = site.ProviderId,
            Status = PricingRefreshSiteStatus.Failed,
            PreviousSnapshot = previous,
            FailureKind = kind,
            FailureMessage = message
        };
}
