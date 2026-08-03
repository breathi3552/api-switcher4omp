using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Application;

public sealed record PricingCheckOutcome(LocalAppSettings Settings, PricingRefreshResult RefreshResult);

public sealed class PricingCheckUseCase(PricingRefreshService refreshService, ISettingsRepository settingsRepository, IPricingSnapshotRepository snapshotRepository)
{
    public async Task<PricingCheckOutcome> ExecuteAsync(
        LocalAppSettings settings,
        string? currentProviderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var previous = snapshotRepository.LoadAll();
        var result = await refreshService.RefreshAsync(settings, LocalAppSettings.DefaultUsageProfile, currentProviderId, previous, cancellationToken).ConfigureAwait(false);
        if (result.SuccessfulResults.Count > 0) snapshotRepository.SaveAll(result.LatestSnapshots.Values);

        var changed = false;
        var sites = settings.Sites.Select(site =>
        {
            if (!result.SuccessfulResults.TryGetValue(site.ProviderId, out var pricing)
                || pricing.Snapshot.CurrentGroupRatio is not > 0)
                return site;

            var ratio = pricing.Snapshot.CurrentGroupRatio.Value;
            if (site.CurrentGroupRatio == ratio && string.Equals(site.GroupRatioSource, "自动", StringComparison.Ordinal))
                return site;

            changed = true;
            return site with { CurrentGroupRatio = ratio, GroupRatioSource = "自动" };
        }).ToList();

        if (!changed)
        {
            var latest = settingsRepository.Load();
            return new PricingCheckOutcome(settings with { ActiveProviderId = latest.ActiveProviderId }, result);
        }

        var updated = settingsRepository.Update(current => settings with { Sites = sites.ToArray(), ActiveProviderId = current.ActiveProviderId });
        return new PricingCheckOutcome(updated, result);
    }
}

public sealed class PricingProbeUseCase(IPricingAdapterRegistry adapterRegistry)
{
    public async Task<SitePricingResult> ExecuteAsync(
        SiteConfiguration site,
        int requestTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentOutOfRangeException.ThrowIfNegative(requestTimeoutSeconds);
        if (!adapterRegistry.TryGet(site.SiteType, out var adapter))
            throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, $"没有为站点类型 '{site.SiteType}' 注册价格适配器。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(requestTimeoutSeconds));
        try
        {
            return await adapter.FetchAsync(site, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PricingAdapterException(PricingAdapterFailure.Timeout, $"价格查询在 {requestTimeoutSeconds} 秒后超时。");
        }
    }
}
