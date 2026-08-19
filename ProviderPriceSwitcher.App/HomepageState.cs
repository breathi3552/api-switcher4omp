using System.Globalization;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.App;

public sealed record OmpConfigurationTargetChoice(string ProviderId)
{
    public string Display => ProviderId == OmpConfigurationReplacementTargets.LocalProviderId
        ? "provider-price-switcher（本地）"
        : "openai-codex（官方 OAuth）";
}

public sealed record ProviderChoice(string ProviderId)
{
    public string Display => ProviderId;
}

public sealed class PriceRow
{
    public PriceRow(
        string providerId,
        string status,
        string group,
        decimal? ratio,
        TokenPrices? prices,
        UsageProfile usage,
        string cacheHitRate,
        string ratioSource,
        DateTimeOffset? checkedAt,
        string issue,
        bool isStale,
        bool hasWarning,
        bool isSiteFirstRow = false,
        Uri? baseUrl = null,
        string? configurationApiAddress = null)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ProviderId = providerId;
        Status = status;
        Group = group;
        Ratio = ratio?.ToString("0.####", CultureInfo.InvariantCulture) ?? "—";
        InputPrice = prices?.InputPerMillion.ToString("0.####", CultureInfo.InvariantCulture) ?? "—";
        CachedPrice = prices?.CachedInputPerMillion.ToString("0.####", CultureInfo.InvariantCulture) ?? "—";
        OutputPrice = prices?.OutputPerMillion.ToString("0.####", CultureInfo.InvariantCulture) ?? "—";
        EstimatedCost = prices is null ? "—" : PricingCalculator.Calculate(usage, prices).ToString("0.####", CultureInfo.InvariantCulture);
        CacheHitRate = cacheHitRate;
        RatioSource = string.IsNullOrWhiteSpace(ratioSource) ? "—" : ratioSource;
        UpdatedAt = checkedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";
        Issue = issue;
        IsStale = isStale;
        HasWarning = hasWarning;
        IsSiteFirstRow = isSiteFirstRow;
        KeysUri = isSiteFirstRow ? MainWindow.BuildKeysUri(baseUrl, configurationApiAddress) : null;
    }

    public string ProviderId { get; }
    public string Status { get; }
    public string Group { get; }
    public string Ratio { get; }
    public string InputPrice { get; }
    public string CachedPrice { get; }
    public string OutputPrice { get; }
    public string EstimatedCost { get; }
    public string CacheHitRate { get; }
    public string RatioSource { get; }
    public string UpdatedAt { get; }
    public string Issue { get; }
    public bool IsStale { get; }
    public bool HasWarning { get; }
    public bool IsSiteFirstRow { get; }
    public Uri? KeysUri { get; }
}

public sealed record HomepageState(
    IReadOnlyList<PriceRow> Rows,
    string CurrentProvider,
    string RecommendedProvider,
    string LastCheckedText,
    string OmpConfigurationStatus,
    string GatewayPortStatus,
    string GatewayStatusText,
    string ActiveRouteStatusText,
    string StatusText,
    string SelectionHint,
    IReadOnlyList<ProviderChoice> ProviderChoices,
    ProviderChoice? SelectedProvider,
    IReadOnlyList<OmpConfigurationTargetChoice> OmpConfigurationTargetChoices,
    OmpConfigurationTargetChoice? SelectedOmpConfigurationTarget,
    IReadOnlyList<string> OmpWorkingDirectoryChoices,
    string? SelectedOmpWorkingDirectory,
    bool IsCheckingPrices,
    bool IsApplyingRoute,
    bool IsStartingOmp,
    bool IsReplacingOmpGptProvider)
{
    public const string DefaultSelectionHint = "仅当前分组可应用；最低价分组只读比较。";

    public string ApplyRouteButtonText => IsApplyingRoute ? "应用中…" : "应用供应商";
    public string ReplaceOmpGptProviderButtonText => IsReplacingOmpGptProvider ? "替换中…" : "替换 OMP GPT";
    public string StartOmpButtonText => IsStartingOmp ? "启动中…" : "启动 OMP";
}

public static class HomepageStateProjector
{
    public static HomepageState ProjectInitial(
        LocalAppSettings settings,
        string? activeProviderId,
        SidecarStatus? gatewayStatus,
        IReadOnlyDictionary<string, PricingSnapshot>? persistedSnapshots,
        bool snapshotReadSuccess = true,
        string? preferredOmpTarget = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var currentProviderText = activeProviderId ?? "未应用";
        var ompTargetChoices = BuildOmpConfigurationTargets();
        var selectedOmpTarget = ompTargetChoices.FirstOrDefault(
            t => string.Equals(t.ProviderId, preferredOmpTarget, StringComparison.Ordinal))
            ?? (ompTargetChoices.Count > 0 ? ompTargetChoices[0] : null);

        var workingDirectories = BuildWorkingDirectories(settings);
        var selectedWorkingDirectory = workingDirectories.FirstOrDefault(
            path => string.Equals(path, settings.LastOmpWorkingDirectory, StringComparison.OrdinalIgnoreCase))
            ?? (workingDirectories.Count > 0 ? workingDirectories[0] : null);

        var providerChoices = BuildProviderChoices(settings.Sites);
        var selectedProvider = ResolveSelectedProvider(providerChoices, currentProviderText, currentProviderText);

        var portStatus = FormatGatewayPortStatus(settings.CurrentGatewayPort, settings.GatewayPort);
        var gatewayText = FormatGatewayStatusText(gatewayStatus);
        var activeRouteText = FormatActiveRouteStatusText(gatewayStatus, activeProviderId);

        IReadOnlyList<PriceRow> rows;
        string lastCheckedText;
        string statusText;
        string recommendedProvider;

        if (!snapshotReadSuccess)
        {
            rows = ProjectPriceRows(settings.Sites, null, LocalAppSettings.DefaultUsageProfile);
            lastCheckedText = "尚未检查";
            recommendedProvider = "等待手动检查";
            statusText = "无法读取上次价格记录，请检查本地数据文件。";
        }
        else
        {
            var snapshots = persistedSnapshots ?? new Dictionary<string, PricingSnapshot>(StringComparer.Ordinal);
            rows = ProjectPriceRows(settings.Sites, snapshots, LocalAppSettings.DefaultUsageProfile);
            lastCheckedText = FormatLastCheckedText(snapshots.Values);
            recommendedProvider = "等待手动检查";
            statusText = snapshots.Count > 0
                ? "已加载上次检查结果；点击“检查价格”更新。"
                : "尚无价格记录；点击“检查价格”。";
        }

        return new HomepageState(
            Rows: rows,
            CurrentProvider: currentProviderText,
            RecommendedProvider: recommendedProvider,
            LastCheckedText: lastCheckedText,
            OmpConfigurationStatus: "可手动替换",
            GatewayPortStatus: portStatus,
            GatewayStatusText: gatewayText,
            ActiveRouteStatusText: activeRouteText,
            StatusText: statusText,
            SelectionHint: HomepageState.DefaultSelectionHint,
            ProviderChoices: providerChoices,
            SelectedProvider: selectedProvider,
            OmpConfigurationTargetChoices: ompTargetChoices,
            SelectedOmpConfigurationTarget: selectedOmpTarget,
            OmpWorkingDirectoryChoices: workingDirectories,
            SelectedOmpWorkingDirectory: selectedWorkingDirectory,
            IsCheckingPrices: false,
            IsApplyingRoute: false,
            IsStartingOmp: false,
            IsReplacingOmpGptProvider: false);
    }

    public static HomepageState ProjectPersistedSnapshots(
        HomepageState current,
        LocalAppSettings settings,
        IReadOnlyDictionary<string, PricingSnapshot>? snapshots,
        bool snapshotReadSuccess = true,
        string? preferredProvider = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(settings);

        var providerChoices = BuildProviderChoices(settings.Sites);
        var selectedProvider = ResolveSelectedProvider(
            providerChoices,
            preferredProvider ?? current.SelectedProvider?.ProviderId,
            current.CurrentProvider);

        if (!snapshotReadSuccess)
        {
            var emptyRows = ProjectPriceRows(settings.Sites, null, LocalAppSettings.DefaultUsageProfile);
            return current with
            {
                Rows = emptyRows,
                ProviderChoices = providerChoices,
                SelectedProvider = selectedProvider,
                StatusText = "无法读取上次价格记录，请检查本地数据文件。"
            };
        }

        var map = snapshots ?? new Dictionary<string, PricingSnapshot>(StringComparer.Ordinal);
        var rows = ProjectPriceRows(settings.Sites, map, LocalAppSettings.DefaultUsageProfile);
        var lastCheckedText = FormatLastCheckedText(map.Values);
        var statusText = map.Count > 0
            ? "已加载上次检查结果；点击“检查价格”更新。"
            : "尚无价格记录；点击“检查价格”。";

        return current with
        {
            Rows = rows,
            LastCheckedText = lastCheckedText,
            RecommendedProvider = "等待手动检查",
            ProviderChoices = providerChoices,
            SelectedProvider = selectedProvider,
            StatusText = statusText
        };
    }

    public static HomepageState ProjectPriceCheckStarted(HomepageState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            IsCheckingPrices = true,
            StatusText = "正在检查已启用站点的价格…"
        };
    }

    public static HomepageState ProjectPriceCheckCompleted(
        HomepageState current,
        LocalAppSettings settings,
        PricingRefreshResult result,
        string? activeProviderId,
        UsageProfile? usage = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(result);

        var effectiveUsage = usage ?? LocalAppSettings.DefaultUsageProfile;
        var rows = ProjectPriceRowsFromRefreshResult(settings.Sites, result, effectiveUsage);
        var recommended = result.Recommendation.Selected?.Site.ProviderId;
        var recommendedText = recommended ?? "无可自动推荐项";

        var providerChoices = BuildProviderChoices(settings.Sites);
        var previousSelection = current.SelectedProvider?.ProviderId;
        var selectedProvider = ResolveSelectedProvider(
            providerChoices,
            recommended ?? previousSelection,
            activeProviderId ?? current.CurrentProvider);

        var lastCheckedText = result.CompletedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        return current with
        {
            Rows = rows,
            RecommendedProvider = recommendedText,
            LastCheckedText = lastCheckedText,
            ProviderChoices = providerChoices,
            SelectedProvider = selectedProvider,
            IsCheckingPrices = false,
            StatusText = "检查完成。"
        };
    }

    public static HomepageState ProjectPriceCheckCanceled(HomepageState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            IsCheckingPrices = false,
            StatusText = "已取消检查。"
        };
    }

    public static HomepageState ProjectGatewayStatus(
        HomepageState current,
        SidecarStatus? gatewayStatus,
        string? activeProviderId,
        int currentGatewayPort,
        int targetGatewayPort)
    {
        ArgumentNullException.ThrowIfNull(current);

        return current with
        {
            GatewayPortStatus = FormatGatewayPortStatus(currentGatewayPort, targetGatewayPort),
            GatewayStatusText = FormatGatewayStatusText(gatewayStatus),
            ActiveRouteStatusText = FormatActiveRouteStatusText(gatewayStatus, activeProviderId)
        };
    }

    public static HomepageState ProjectActiveRoute(
        HomepageState current,
        string? activeProviderId,
        SidecarStatus? gatewayStatus)
    {
        ArgumentNullException.ThrowIfNull(current);

        var currentProviderText = activeProviderId ?? "未应用";
        return current with
        {
            CurrentProvider = currentProviderText,
            ActiveRouteStatusText = FormatActiveRouteStatusText(gatewayStatus, activeProviderId)
        };
    }

    public static HomepageState ProjectApplyingRouteStarted(HomepageState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with { IsApplyingRoute = true };
    }

    public static HomepageState ProjectApplyingRouteCompleted(
        HomepageState current,
        ApplyActiveRouteOutcome outcome,
        string? activeProviderId,
        SidecarStatus? gatewayStatus)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(outcome);

        var currentProviderText = activeProviderId ?? "未应用";
        return current with
        {
            IsApplyingRoute = false,
            CurrentProvider = currentProviderText,
            ActiveRouteStatusText = FormatActiveRouteStatusText(gatewayStatus, activeProviderId),
            StatusText = UserErrorMessages.ForApplyRouteStatus(outcome.Status)
        };
    }

    public static HomepageState ProjectStartingOmpStarted(HomepageState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with { IsStartingOmp = true };
    }

    public static HomepageState ProjectStartingOmpCompleted(
        HomepageState current,
        OmpLaunchOutcome outcome,
        LocalAppSettings outcomeSettings)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(outcomeSettings);

        var portStatus = FormatGatewayPortStatus(outcomeSettings.CurrentGatewayPort, outcomeSettings.GatewayPort);
        return current with
        {
            IsStartingOmp = false,
            GatewayPortStatus = portStatus,
            StatusText = UserErrorMessages.ForOmpLaunchStatus(outcome)
        };
    }

    public static HomepageState ProjectReplacingOmpStarted(HomepageState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with { IsReplacingOmpGptProvider = true };
    }

    public static HomepageState ProjectReplacingOmpPreview(
        HomepageState current,
        OmpConfigurationReplacementPreview preview,
        bool confirmed,
        string? customStatusText = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(preview);

        if (!preview.Succeeded)
        {
            return current with
            {
                IsReplacingOmpGptProvider = false,
                OmpConfigurationStatus = "配置替换失败",
                StatusText = preview.ErrorMessage ?? "无法预览 OMP 配置替换。"
            };
        }

        if (!preview.HasChanges)
        {
            return current with
            {
                IsReplacingOmpGptProvider = false,
                OmpConfigurationStatus = "无可变更 GPT",
                StatusText = "没有需要替换的 GPT 路由，未写入文件。"
            };
        }

        if (!confirmed)
        {
            return current with
            {
                IsReplacingOmpGptProvider = false,
                StatusText = customStatusText ?? "已取消 OMP 配置替换，未写入文件。"
            };
        }

        return current;
    }

    public static HomepageState ProjectReplacingOmpCompleted(
        HomepageState current,
        OmpConfigurationReplacementResult result)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Succeeded)
        {
            return current with
            {
                IsReplacingOmpGptProvider = false,
                OmpConfigurationStatus = "配置替换失败",
                StatusText = result.ErrorMessage ?? "OMP 配置替换失败，未报告成功。"
            };
        }

        var statusText = !result.BackupRetentionSucceeded
            ? "OMP 配置已替换，但旧备份清理失败；请检查备份数量后再手动重启 OMP。"
            : result.Preview.IsNoOp
                ? "没有需要替换的 GPT 路由，未写入文件。"
                : "OMP 配置已替换；已运行的 OMP 需要手动重启后生效。";

        return current with
        {
            IsReplacingOmpGptProvider = false,
            OmpConfigurationStatus = "配置已替换",
            StatusText = statusText
        };
    }

    public static HomepageState ProjectSettingsUpdated(
        HomepageState current,
        LocalAppSettings newSettings,
        string? activeProviderId,
        IReadOnlyDictionary<string, PricingSnapshot>? persistedSnapshots,
        bool snapshotReadSuccess = true,
        string? preferredProvider = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(newSettings);

        var workingDirectories = BuildWorkingDirectories(newSettings);
        var selectedWorkingDirectory = workingDirectories.FirstOrDefault(
            path => string.Equals(path, newSettings.LastOmpWorkingDirectory, StringComparison.OrdinalIgnoreCase))
            ?? (workingDirectories.Count > 0 ? workingDirectories[0] : null);

        var portStatus = FormatGatewayPortStatus(newSettings.CurrentGatewayPort, newSettings.GatewayPort);
        var providerChoices = BuildProviderChoices(newSettings.Sites);
        var currentProviderText = activeProviderId ?? "未应用";
        var selectedProvider = ResolveSelectedProvider(
            providerChoices,
            preferredProvider ?? current.SelectedProvider?.ProviderId,
            currentProviderText);

        var intermediate = current with
        {
            GatewayPortStatus = portStatus,
            OmpWorkingDirectoryChoices = workingDirectories,
            SelectedOmpWorkingDirectory = selectedWorkingDirectory,
            ProviderChoices = providerChoices,
            SelectedProvider = selectedProvider
        };

        return ProjectPersistedSnapshots(
            intermediate,
            newSettings,
            persistedSnapshots,
            snapshotReadSuccess,
            preferredProvider ?? selectedProvider?.ProviderId);
    }

    public static HomepageState ProjectSelectedProvider(HomepageState current, ProviderChoice? choice)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with { SelectedProvider = choice };
    }

    public static HomepageState ProjectSelectedOmpTarget(HomepageState current, OmpConfigurationTargetChoice? choice)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with { SelectedOmpConfigurationTarget = choice };
    }

    public static HomepageState ProjectSelectedWorkingDirectory(HomepageState current, string? workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with { SelectedOmpWorkingDirectory = workingDirectory };
    }

    public static IReadOnlyList<PriceRow> ProjectPriceRows(
        IReadOnlyList<SiteConfiguration> sites,
        IReadOnlyDictionary<string, PricingSnapshot>? snapshots,
        UsageProfile usage)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(usage);

        var rows = new List<PriceRow>();
        foreach (var site in sites)
        {
            var snapshot = snapshots?.GetValueOrDefault(site.ProviderId);
            AddPriceRowsForSite(rows, site, snapshot, usage, null, null);
        }
        return rows;
    }

    private static IReadOnlyList<PriceRow> ProjectPriceRowsFromRefreshResult(
        IReadOnlyList<SiteConfiguration> sites,
        PricingRefreshResult result,
        UsageProfile usage)
    {
        var rows = new List<PriceRow>();
        foreach (var site in sites)
        {
            var state = result.Sites.FirstOrDefault(x => string.Equals(x.ProviderId, site.ProviderId, StringComparison.Ordinal));
            var pricing = state?.PricingResult;
            var snapshot = pricing?.Snapshot ?? state?.PreviousSnapshot ?? result.LatestSnapshots.GetValueOrDefault(site.ProviderId);
            AddPriceRowsForSite(rows, site, snapshot, usage, state, pricing);
        }
        return rows;
    }

    public static void AddPriceRowsForSite(
        List<PriceRow> rows,
        SiteConfiguration site,
        PricingSnapshot? snapshot,
        UsageProfile usage,
        PricingRefreshSiteResult? state,
        SitePricingResult? pricing)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(usage);

        var unavailable = snapshot is not null && !snapshot.Matches(site);
        var failed = state is not null && state.Status == PricingRefreshSiteStatus.Failed;
        var stale = unavailable || failed;
        var warning = pricing?.Warnings.Count > 0;
        var status = state is null
            ? site.Enabled ? snapshot is null ? "未检查" : unavailable ? "不可用" : "已保存" : "已禁用"
            : state.Status == PricingRefreshSiteStatus.Succeeded ? warning ? "成功（警告）" : "成功"
            : state.Status == PricingRefreshSiteStatus.Disabled ? "已禁用"
            : state.FailureKind == PricingRefreshFailureKind.Authentication ? "需认证"
            : "失败";

        var issue = state?.Status == PricingRefreshSiteStatus.Failed
            ? UserErrorMessages.ForPricingFailure(state.FailureKind)
            : warning ? "价格数据包含提示，请谨慎核对。" : string.Empty;

        if (snapshot is null)
        {
            rows.Add(new PriceRow(
                site.ProviderId,
                status,
                site.CurrentGroup + " [当前]",
                site.CurrentGroupRatio,
                null,
                usage,
                "—",
                site.GroupRatioSource,
                null,
                issue,
                stale,
                warning,
                isSiteFirstRow: true,
                site.BaseUrl,
                site.ConfigurationApiAddress));
            return;
        }

        var effective = site.CurrentGroupRatio is > 0
            ? snapshot.WithCurrentRatio(site.CurrentGroupRatio.Value, site.GroupRatioSource)
            : snapshot;

        var minimumSame = string.Equals(effective.MinimumGroup, site.CurrentGroup, StringComparison.Ordinal);
        var currentLabel = site.CurrentGroup + (minimumSame ? " [当前][最低]" : " [当前]");

        rows.Add(new PriceRow(
            site.ProviderId,
            status,
            currentLabel,
            effective.CurrentGroupRatio ?? site.CurrentGroupRatio,
            effective.Prices,
            usage,
            "—",
            effective.GroupRatioSource,
            effective.RefreshedAt,
            issue,
            stale,
            warning,
            isSiteFirstRow: true,
            site.BaseUrl,
            site.ConfigurationApiAddress));

        if (!minimumSame && !string.IsNullOrWhiteSpace(effective.MinimumGroup) && effective.MinimumGroupPrices is not null)
        {
            var minimumIssue = state?.FailureKind == PricingRefreshFailureKind.Authentication
                ? "需先绑定或更新凭据"
                : "仅供手动选择，未参与自动推荐";

            rows.Add(new PriceRow(
                site.ProviderId,
                status,
                effective.MinimumGroup + " [最低]",
                effective.MinimumGroupRatio,
                effective.MinimumGroupPrices,
                usage,
                "—",
                "自动",
                effective.RefreshedAt,
                minimumIssue,
                stale,
                warning));
        }
    }

    public static string FormatGatewayPortStatus(int currentPort, int targetPort)
    {
        return currentPort == targetPort
            ? $"网关端口：127.0.0.1:{currentPort}"
            : $"网关端口：127.0.0.1:{currentPort}；下次启动：{targetPort}";
    }

    public static string FormatGatewayStatusText(SidecarStatus? gatewayStatus)
    {
        return gatewayStatus?.Status switch
        {
            SidecarConnectionStatus.Ready => "网关运行中",
            SidecarConnectionStatus.Starting => "网关启动中",
            SidecarConnectionStatus.Disconnected => "网关连接断开",
            SidecarConnectionStatus.Faulted => "网关故障",
            SidecarConnectionStatus.Stopped => "网关已停止",
            _ => "网关状态未知"
        };
    }

    public static string FormatActiveRouteStatusText(SidecarStatus? gatewayStatus, string? activeProviderId)
    {
        return gatewayStatus?.Status switch
        {
            SidecarConnectionStatus.Ready => activeProviderId is null ? "无活动路由" : "活动路由已应用",
            SidecarConnectionStatus.Starting => "网关恢复中",
            SidecarConnectionStatus.Disconnected or SidecarConnectionStatus.Faulted => "网关不可用",
            SidecarConnectionStatus.Stopped => "网关已停止",
            _ => activeProviderId is null ? "无活动路由" : "网关状态未知"
        };
    }

    public static IReadOnlyList<OmpConfigurationTargetChoice> BuildOmpConfigurationTargets()
    {
        return OmpConfigurationReplacementTargets.All
            .Select(providerId => new OmpConfigurationTargetChoice(providerId))
            .ToArray();
    }

    public static IReadOnlyList<string> BuildWorkingDirectories(LocalAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var list = settings.OmpWorkingDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0 && !string.IsNullOrWhiteSpace(settings.OmpRootDirectory))
            list.Add(settings.OmpRootDirectory);

        return list;
    }

    public static IReadOnlyList<ProviderChoice> BuildProviderChoices(IReadOnlyList<SiteConfiguration> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);

        return sites
            .Where(x => x.Enabled)
            .Select(x => x.ProviderId)
            .Distinct(StringComparer.Ordinal)
            .Select(id => new ProviderChoice(id))
            .ToArray();
    }

    public static ProviderChoice? ResolveSelectedProvider(
        IReadOnlyList<ProviderChoice> choices,
        string? preferredProvider,
        string? fallbackProvider)
    {
        ArgumentNullException.ThrowIfNull(choices);

        if (choices.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredProvider))
        {
            var match = choices.FirstOrDefault(x => string.Equals(x.ProviderId, preferredProvider, StringComparison.Ordinal));
            if (match is not null) return match;
        }

        if (!string.IsNullOrWhiteSpace(fallbackProvider))
        {
            var match = choices.FirstOrDefault(x => string.Equals(x.ProviderId, fallbackProvider, StringComparison.Ordinal));
            if (match is not null) return match;
        }

        return choices.Count > 0 ? choices[0] : null;
    }

    public static string FormatLastCheckedText(IEnumerable<PricingSnapshot> snapshots)
    {
        DateTimeOffset? latest = null;
        foreach (var s in snapshots)
        {
            if (latest is null || s.RefreshedAt > latest) latest = s.RefreshedAt;
        }
        return latest?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "尚未检查";
    }
}
