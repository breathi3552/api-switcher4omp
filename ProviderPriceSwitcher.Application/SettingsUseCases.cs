using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Application;

public sealed class SiteManagementUseCase(ISettingsRepository settingsRepository, IPricingSnapshotRepository snapshotRepository)
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
                snapshotRepository.Delete(originalProviderId);
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
