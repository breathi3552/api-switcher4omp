using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Application;

public sealed class SiteManagementUseCase(
    ISettingsRepository settingsRepository,
    IPricingSnapshotRepository snapshotRepository,
    ISiteAccessCredentialStore? siteCredentialStore = null,
    IInferenceApiKeyStore? inferenceApiKeyStore = null,
    IActiveRouteController? activeRoute = null,
    IRouteController? routeController = null,
    InferenceApiKeyResolverBridge? keyResolver = null)
{
    public async Task<LocalAppSettings> SaveSiteAsync(LocalAppSettings settings, SiteConfiguration site, string? originalProviderId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentException.ThrowIfNullOrWhiteSpace(site.ProviderId);
        using var routeLease = activeRoute is null ? null : await activeRoute.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (settings.Sites.Any(existing => !string.Equals(existing.ProviderId, originalProviderId, StringComparison.Ordinal) && string.Equals(existing.ProviderId, site.ProviderId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"供应商 '{site.ProviderId}' 已存在。");
        var sites = settings.Sites.ToList();
        var renamesActiveProvider = false;
        if (originalProviderId is null)
        {
            sites.Add(site);
        }
        else
        {
            var index = sites.FindIndex(x => string.Equals(x.ProviderId, originalProviderId, StringComparison.Ordinal));
            if (index < 0) throw new InvalidOperationException($"站点 '{originalProviderId}' 不存在。");
            sites[index] = site;
            renamesActiveProvider = string.Equals(settings.ActiveProviderId, originalProviderId, StringComparison.Ordinal)
                || string.Equals(activeRoute?.CurrentProviderId, originalProviderId, StringComparison.Ordinal);
            if (!string.Equals(originalProviderId, site.ProviderId, StringComparison.Ordinal))
            {
                if (renamesActiveProvider && routeController is not null)
                    await routeController.ClearAsync(cancellationToken).ConfigureAwait(false);
                snapshotRepository.Delete(originalProviderId);
                siteCredentialStore?.ClearCredential(originalProviderId);
                inferenceApiKeyStore?.Clear(originalProviderId);
                keyResolver?.Remove(originalProviderId);
                activeRoute?.ClearIfProvider(originalProviderId);
            }
        }

        var clearsRenamedActiveRoute = renamesActiveProvider && !string.Equals(originalProviderId, site.ProviderId, StringComparison.Ordinal);
        var requested = settings with { Sites = sites.ToArray(), ActiveProviderId = clearsRenamedActiveRoute ? null : settings.ActiveProviderId };
        var updated = settingsRepository.Update(current => requested with { ActiveProviderId = clearsRenamedActiveRoute ? null : current.ActiveProviderId });
        var snapshot = snapshotRepository.Load(site.ProviderId);
        if (snapshot is not null && snapshot.Matches(site) && site.CurrentGroupRatio is > 0)
            snapshotRepository.Save(snapshot.WithCurrentRatio(site.CurrentGroupRatio.Value, site.GroupRatioSource));
        return updated;
    }

    public LocalAppSettings SetEnabled(LocalAppSettings settings, string providerId, bool enabled)
    {
        var index = settings.Sites.ToList().FindIndex(x => string.Equals(x.ProviderId, providerId, StringComparison.Ordinal));
        if (index < 0) throw new InvalidOperationException($"站点 '{providerId}' 不存在。");
        var sites = settings.Sites.ToList();
        sites[index] = sites[index] with { Enabled = enabled };
        var requested = settings with { Sites = sites.ToArray() };
        return settingsRepository.Update(current => requested with { ActiveProviderId = current.ActiveProviderId });
    }

    public async Task<LocalAppSettings> DeleteSiteAsync(LocalAppSettings settings, string providerId, CancellationToken cancellationToken = default)
    {
        using var routeLease = activeRoute is null ? null : await activeRoute.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var sites = settings.Sites.Where(x => !string.Equals(x.ProviderId, providerId, StringComparison.Ordinal)).ToList();
        if (sites.Count == settings.Sites.Count) throw new InvalidOperationException($"站点 '{providerId}' 不存在。");
        var clearsActiveRoute = string.Equals(activeRoute?.CurrentProviderId, providerId, StringComparison.Ordinal)
            || string.Equals(settings.ActiveProviderId, providerId, StringComparison.Ordinal);
        if (clearsActiveRoute && routeController is not null)
            await routeController.ClearAsync(cancellationToken).ConfigureAwait(false);
        var requested = settings with { Sites = sites.ToArray(), ActiveProviderId = clearsActiveRoute ? null : settings.ActiveProviderId };
        var updated = settingsRepository.Update(current => requested with { ActiveProviderId = clearsActiveRoute ? null : current.ActiveProviderId });
        snapshotRepository.Delete(providerId);
        siteCredentialStore?.ClearCredential(providerId);
        inferenceApiKeyStore?.Clear(providerId);
        keyResolver?.Remove(providerId);
        activeRoute?.ClearIfProvider(providerId);
        return updated;
    }
}

public sealed class SettingsUseCase(ISettingsRepository settingsRepository)
{
    public LocalAppSettings Save(LocalAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.GatewayPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(settings), "Gateway port must be between 1 and 65535.");

        var directories = settings.OmpWorkingDirectories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var defaultDirectory = directories.FirstOrDefault(x => string.Equals(x, settings.LastOmpWorkingDirectory, StringComparison.OrdinalIgnoreCase))
            ?? directories.FirstOrDefault();
        var normalized = settings with { OmpWorkingDirectories = directories, LastOmpWorkingDirectory = defaultDirectory };
        return settingsRepository.Update(current => current with
        {
            RequestTimeoutSeconds = normalized.RequestTimeoutSeconds,
            OmpRootDirectory = normalized.OmpRootDirectory,
            OmpWorkingDirectories = normalized.OmpWorkingDirectories,
            LastOmpWorkingDirectory = normalized.LastOmpWorkingDirectory,
            GatewayPort = normalized.GatewayPort
        });
    }
}

public interface IInferenceApiKeyUseCase
{
    InferenceApiKeySummary? GetSummary(string providerId);
    InferenceApiKeySummary Save(string providerId, string apiKey, string boundGroup);
    Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}

public sealed class InferenceApiKeyUseCase(
    IInferenceApiKeyStore keyStore,
    IInferenceBindingStore bindingStore,
    IActiveRouteController? activeRoute = null,
    IRouteController? routeController = null,
    InferenceApiKeyResolverBridge? keyResolver = null,
    ISettingsRepository? settingsRepository = null) : IInferenceApiKeyUseCase
{
    public InferenceApiKeySummary? GetSummary(string providerId) => keyStore.GetSummary(providerId);

    public InferenceApiKeySummary Save(string providerId, string apiKey, string boundGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundGroup);
        var normalizedProviderId = providerId.Trim();
        var normalizedGroup = boundGroup.Trim();
        var summary = bindingStore.Save(normalizedProviderId, apiKey.Trim(), normalizedGroup);
        var registered = keyStore.Load(normalizedProviderId);
        if (registered is not null) keyResolver?.Register(registered);
        return summary;
    }
    public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        using var routeLease = activeRoute is null ? null : await activeRoute.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var settings = settingsRepository?.Load();
        var clearsActiveRoute = (activeRoute?.CurrentProviderId is { } active && string.Equals(active, providerId, StringComparison.Ordinal))
            || (settings?.ActiveProviderId is { } persisted && string.Equals(persisted, providerId, StringComparison.Ordinal));
        if (clearsActiveRoute && routeController is not null)
            await routeController.ClearAsync(cancellationToken).ConfigureAwait(false);
        keyStore.Clear(providerId);
        keyResolver?.Remove(providerId);
        activeRoute?.ClearIfProvider(providerId);
        if (settings is not null)
            settingsRepository!.Update(current => string.Equals(current.ActiveProviderId, providerId, StringComparison.Ordinal) ? current with { ActiveProviderId = null } : current);
    }
}
