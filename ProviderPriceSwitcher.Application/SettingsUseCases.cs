using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Application;

public sealed class SiteManagementUseCase(
    ISettingsRepository settingsRepository,
    IPricingSnapshotRepository snapshotRepository,
    ISiteAccessCredentialStore? siteCredentialStore = null,
    IInferenceApiKeyStore? inferenceApiKeyStore = null)
{
    public LocalAppSettings SaveSite(LocalAppSettings settings, SiteConfiguration site, string? originalProviderId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(site);
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
                snapshotRepository.Delete(originalProviderId);
                siteCredentialStore?.ClearCredential(originalProviderId);
                inferenceApiKeyStore?.Clear(originalProviderId);
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

    public LocalAppSettings DeleteSite(LocalAppSettings settings, string providerId)
    {
        var sites = settings.Sites.Where(x => !string.Equals(x.ProviderId, providerId, StringComparison.Ordinal)).ToList();
        if (sites.Count == settings.Sites.Count) throw new InvalidOperationException($"站点 '{providerId}' 不存在。");
        var updated = settings with { Sites = sites.ToArray() };
        settingsRepository.Save(updated);
        snapshotRepository.Delete(providerId);
        siteCredentialStore?.ClearCredential(providerId);
        inferenceApiKeyStore?.Clear(providerId);
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

public sealed class InferenceApiKeyUseCase(IInferenceApiKeyStore keyStore, ISettingsRepository? settingsRepository = null)
{
    public InferenceApiKeySummary? GetSummary(string providerId) => keyStore.GetSummary(providerId);
    public InferenceApiKeySummary Save(string providerId, string apiKey, string boundGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundGroup);
        var normalizedProviderId = providerId.Trim();
        if (settingsRepository is not null && !settingsRepository.Load().Sites.Any(site => string.Equals(site.ProviderId, normalizedProviderId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"供应商 '{normalizedProviderId}' 不存在。");
        var record = new InferenceApiKeyRecord { ProviderId = normalizedProviderId, ApiKey = apiKey.Trim(), BoundGroup = boundGroup.Trim(), KeyHandle = Guid.NewGuid().ToString("N") };
        keyStore.Save(record);
        return keyStore.GetSummary(record.ProviderId) ?? throw new InvalidOperationException("推理 API key 保存后不可读取。");
    }
    public void Delete(string providerId) => keyStore.Clear(providerId);
}
