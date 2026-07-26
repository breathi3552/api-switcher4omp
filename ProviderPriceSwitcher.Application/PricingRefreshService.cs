using Microsoft.Extensions.Logging;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Application;

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

    public PricingSnapshot? Snapshot => PricingResult?.Snapshot;
}

public sealed record PricingRefreshResult
{
    public required IReadOnlyList<PricingRefreshSiteResult> Sites { get; init; }
    public required IReadOnlyDictionary<string, SitePricingResult> SuccessfulResults { get; init; }
    public required IReadOnlyDictionary<string, PricingSnapshot> LatestSnapshots { get; init; }
    public required RecommendationDecision Recommendation { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}

public sealed class PricingRefreshService
{
    private readonly IPricingAdapterRegistry _adapterRegistry;
    private readonly RecommendationService _recommendation;
    private readonly ILogger<PricingRefreshService> _logger;
    private static readonly Action<ILogger, string, string, Exception?> LogSiteFailure = LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(10, "PricingSiteFailure"), "Pricing refresh failed for {ProviderId}: {FailureKind}");
    private static readonly Action<ILogger, int, long, Exception?> LogRefreshCompleted = LoggerMessage.Define<int, long>(LogLevel.Information, new EventId(11, "PricingRefreshCompleted"), "Pricing refresh completed with {SiteCount} sites in {ElapsedMilliseconds} ms");

    public PricingRefreshService(
        IPricingAdapterRegistry adapterRegistry,
        ILogger<PricingRefreshService> logger,
        RecommendationService? recommendationService = null)
    {
        ArgumentNullException.ThrowIfNull(adapterRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        _adapterRegistry = adapterRegistry;
        _logger = logger;
        _recommendation = recommendationService ?? new RecommendationService();
    }

    public Task<PricingRefreshResult> RefreshAsync(
        LocalAppSettings settings,
        UsageProfile usage,
        string? currentProviderId,
        IReadOnlyDictionary<string, PricingSnapshot>? previousSnapshots = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return RefreshAsync(settings.Sites, usage, currentProviderId, settings.RequestTimeoutSeconds, previousSnapshots, cancellationToken);
    }

    public Task<PricingRefreshResult> RefreshAsync(
        IReadOnlyList<SiteConfiguration> sites,
        UsageProfile usage,
        string? currentProviderId,
        int requestTimeoutSeconds,
        IReadOnlyDictionary<string, PricingSnapshot>? previousSnapshots = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentOutOfRangeException.ThrowIfNegative(requestTimeoutSeconds);
        return RefreshCoreAsync(sites, usage, currentProviderId, requestTimeoutSeconds, previousSnapshots ?? new Dictionary<string, PricingSnapshot>(StringComparer.Ordinal), cancellationToken);
    }

    private async Task<PricingRefreshResult> RefreshCoreAsync(
        IReadOnlyList<SiteConfiguration> sites,
        UsageProfile usage,
        string? currentProviderId,
        int requestTimeoutSeconds,
        IReadOnlyDictionary<string, PricingSnapshot> previous,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        using var roundCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = sites.Select(site => RefreshSiteAsync(site, previous.GetValueOrDefault(site.ProviderId), requestTimeoutSeconds, roundCancellation.Token)).ToArray();

        IReadOnlyList<PricingRefreshSiteResult> results;
        try { results = await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { roundCancellation.Cancel(); throw; }
        cancellationToken.ThrowIfCancellationRequested();
        var successful = results.Where(x => x.Status == PricingRefreshSiteStatus.Succeeded && x.PricingResult is not null).ToDictionary(x => x.ProviderId, x => x.PricingResult!, StringComparer.Ordinal);
        var latest = new Dictionary<string, PricingSnapshot>(previous, StringComparer.Ordinal);
        foreach (var result in successful.Values) latest[result.Snapshot.ProviderId] = result.Snapshot;
        var refreshStates = results.Where(x => x.Status != PricingRefreshSiteStatus.Disabled).Select(x => new SiteRefreshState
        {
            ProviderId = x.ProviderId,
            Status = x.Status == PricingRefreshSiteStatus.Succeeded ? SiteRefreshStatus.Succeeded : x.FailureKind == PricingRefreshFailureKind.Authentication ? SiteRefreshStatus.AuthenticationRequired : SiteRefreshStatus.Failed,
            FailureReason = x.FailureKind?.ToString()
        }).ToArray();
        var decision = _recommendation.Decide(sites, latest, usage, new SiteRefreshResult { States = refreshStates }, currentProviderId, DateTimeOffset.UtcNow);
        foreach (var failed in results.Where(x => x.Status == PricingRefreshSiteStatus.Failed)) LogSiteFailure(_logger, failed.ProviderId, failed.FailureKind?.ToString() ?? "Unknown", null);
        var completed = DateTimeOffset.UtcNow;
        LogRefreshCompleted(_logger, results.Count, (long)(completed - started).TotalMilliseconds, null);
        return new PricingRefreshResult { Sites = results, SuccessfulResults = successful, LatestSnapshots = latest, Recommendation = decision, StartedAt = started, CompletedAt = completed };
    }

    private async Task<PricingRefreshSiteResult> RefreshSiteAsync(
        SiteConfiguration site,
        PricingSnapshot? previous,
        int requestTimeoutSeconds,
        CancellationToken roundCancellation)
    {
        if (!site.Enabled)
            return new PricingRefreshSiteResult { ProviderId = site.ProviderId, Status = PricingRefreshSiteStatus.Disabled, PreviousSnapshot = previous };

        if (!_adapterRegistry.TryGet(site.SiteType, out var adapter))
            return Failed(site, previous, PricingRefreshFailureKind.UnknownSiteType);

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
            return Failed(site, previous, PricingRefreshFailureKind.Timeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PricingAdapterException ex)
        {
            var kind = ex.Failure switch
            {
                PricingAdapterFailure.Authentication => PricingRefreshFailureKind.Authentication,
                PricingAdapterFailure.Timeout => PricingRefreshFailureKind.Timeout,
                _ => PricingRefreshFailureKind.Adapter
            };
            return Failed(site, previous, kind);
        }
        catch (Exception)
        {
            return Failed(site, previous, PricingRefreshFailureKind.Unexpected);
        }
    }

    private static PricingRefreshSiteResult Failed(
        SiteConfiguration site,
        PricingSnapshot? previous,
        PricingRefreshFailureKind kind) => new()
        {
            ProviderId = site.ProviderId,
            Status = PricingRefreshSiteStatus.Failed,
            PreviousSnapshot = previous,
            FailureKind = kind
        };
}
