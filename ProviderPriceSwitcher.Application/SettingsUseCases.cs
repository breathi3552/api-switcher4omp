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
        if (settings.Sites.Any(existing => !string.Equals(existing.ProviderId, originalProviderId, StringComparison.Ordinal) && string.Equals(existing.ProviderId, site.ProviderId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"供应商 '{site.ProviderId}' 已存在。");
        var sites = settings.Sites.ToList();
        if (originalProviderId is null)
        {
            sites.Add(site);
        }
        else
        {
            var index = sites.FindIndex(x => string.Equals(x.ProviderId, originalProviderId, StringComparison.Ordinal));
            if (index < 0) throw new InvalidOperationException($"站点 '{originalProviderId}' 不存在。");
            sites[index] = site;
            if (!string.Equals(originalProviderId, site.ProviderId, StringComparison.Ordinal))
            {
                if (activeRoute?.CurrentProviderId is { } active && string.Equals(active, originalProviderId, StringComparison.Ordinal) && routeController is not null)
                    await routeController.ClearAsync(cancellationToken).ConfigureAwait(false);
                snapshotRepository.Delete(originalProviderId);
                siteCredentialStore?.ClearCredential(originalProviderId);
                inferenceApiKeyStore?.Clear(originalProviderId);
                keyResolver?.Remove(originalProviderId);
                activeRoute?.ClearIfProvider(originalProviderId);
            }
        }

        var updated = settings with { Sites = sites.ToArray() };
        var snapshot = snapshotRepository.Load(site.ProviderId);
        if (snapshot is not null && snapshot.Matches(site) && site.CurrentGroupRatio is > 0)
            snapshotRepository.Save(snapshot.WithCurrentRatio(site.CurrentGroupRatio.Value, site.GroupRatioSource));
        settingsRepository.Save(updated);
        return updated;
    }

    public LocalAppSettings SetEnabled(LocalAppSettings settings, string providerId, bool enabled)
    {
        var index = settings.Sites.ToList().FindIndex(x => string.Equals(x.ProviderId, providerId, StringComparison.Ordinal));
        if (index < 0) throw new InvalidOperationException($"站点 '{providerId}' 不存在。");
        var sites = settings.Sites.ToList();
        sites[index] = sites[index] with { Enabled = enabled };
        var updated = settings with { Sites = sites.ToArray() };
        settingsRepository.Save(updated);
        return updated;
    }

    public async Task<LocalAppSettings> DeleteSiteAsync(LocalAppSettings settings, string providerId, CancellationToken cancellationToken = default)
    {
        var sites = settings.Sites.Where(x => !string.Equals(x.ProviderId, providerId, StringComparison.Ordinal)).ToList();
        if (sites.Count == settings.Sites.Count) throw new InvalidOperationException($"站点 '{providerId}' 不存在。");
        if (activeRoute?.CurrentProviderId is { } active && string.Equals(active, providerId, StringComparison.Ordinal) && routeController is not null)
            await routeController.ClearAsync(cancellationToken).ConfigureAwait(false);
        var updated = settings with { Sites = sites.ToArray() };
        settingsRepository.Save(updated);
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
        var directories = settings.OmpWorkingDirectories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var defaultDirectory = directories.FirstOrDefault(x => string.Equals(x, settings.LastOmpWorkingDirectory, StringComparison.OrdinalIgnoreCase))
            ?? directories.FirstOrDefault();
        var normalized = settings with { OmpWorkingDirectories = directories, LastOmpWorkingDirectory = defaultDirectory };
        settingsRepository.Save(normalized);
        return normalized;
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
    InferenceApiKeyResolverBridge? keyResolver = null) : IInferenceApiKeyUseCase
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
        if (activeRoute?.CurrentProviderId is { } active && string.Equals(active, providerId, StringComparison.Ordinal) && routeController is not null)
            await routeController.ClearAsync(cancellationToken).ConfigureAwait(false);
        keyStore.Clear(providerId);
        keyResolver?.Remove(providerId);
        activeRoute?.ClearIfProvider(providerId);
    }
}
