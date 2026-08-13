using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Infrastructure;

namespace ProviderPriceSwitcher.App;

public interface IExternalUriLauncher
{
    void Launch(Uri uri);
}

public sealed class ShellUriLauncher : IExternalUriLauncher
{
    public void Launch(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

public partial class MainWindow : Window
{
    private readonly IExternalUriLauncher _uriLauncher;

    public MainWindow(MainViewModel viewModel, IExternalUriLauncher? uriLauncher = null)
    {
        InitializeComponent();
        _uriLauncher = uriLauncher ?? new ShellUriLauncher();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.InitializeCommand.Execute(null);
    }

    private void PriceGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid)
            TryLaunchPriceRow(grid, e.OriginalSource, e.ChangedButton);
    }

    internal bool TryLaunchPriceRow(System.Windows.Controls.DataGrid grid, object? originalSource, System.Windows.Input.MouseButton button)
    {
        if (button != System.Windows.Input.MouseButton.Left || !TryGetHitPriceRow(grid, originalSource, out var row) || row is not { IsSiteFirstRow: true, KeysUri: not null })
            return false;

        _uriLauncher.Launch(row.KeysUri);
        return true;
    }

    internal static bool TryGetHitPriceRow(System.Windows.Controls.DataGrid grid, object? originalSource, out PriceRow? row)
    {
        row = null;
        if (originalSource is not System.Windows.DependencyObject source)
            return false;

        var hitRow = System.Windows.Controls.ItemsControl.ContainerFromElement(grid, source) as System.Windows.Controls.DataGridRow;
        row = hitRow?.DataContext as PriceRow;
        return row is not null;
    }

    public static Uri? BuildKeysUri(Uri? baseUrl, string? configurationApiAddress)
    {
        if (baseUrl is null || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps)) return null;
        var address = string.IsNullOrWhiteSpace(configurationApiAddress) ? "/keys" : configurationApiAddress;
        if (!address.StartsWith('/') || address.StartsWith("//", StringComparison.Ordinal) || address.Contains('?') || address.Contains('#')) return null;
        var builder = new UriBuilder(baseUrl) { Query = string.Empty, Fragment = string.Empty };
        builder.Path = builder.Path.TrimEnd('/') + address;
        return builder.Uri;
    }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly IPricingSnapshotQuery _snapshotQuery;
    private readonly IActiveRouteController _activeRoute;
    private readonly PricingCheckUseCase _pricingCheck;
    private readonly SettingsUseCase _settingsUseCase;
    private readonly ApplyActiveRouteUseCase _applyActiveRoute;
    private readonly OmpLaunchUseCase _ompLaunch;
    private readonly OmpConfigurationReplacementUseCase? _ompReplacement;
    private readonly IUserNotificationService _notifications;
    private readonly ISitesDialogFactory _sitesDialogFactory;
    private readonly ILogger<MainViewModel> _logger;
    private static readonly Action<ILogger, string, Exception?> LogUiFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(200, "UiFailure"), "UI operation failed: {FailureKind}");
    private LocalAppSettings _settings;
    private readonly CancellationToken _lifetimeCancellationToken;
    private readonly ISidecarStatus? _sidecarStatus;
    private SidecarStatus? _gatewayStatus;
    private CancellationTokenSource? _checkCancellation;
    private string _statusText = "准备就绪";
    private string _currentProvider = "未应用";
    private string _recommendedProvider = "等待检查";
    private string _lastCheckedText = "尚未检查";
    private string _ompConfigurationStatus = "可手动替换";
    private OmpConfigurationTargetChoice? _selectedOmpConfigurationTarget;
    private ProviderChoice? _selectedProvider;
    private bool _isApplyingRoute;
    private bool _isStartingOmp;
    private bool _isReplacingOmpGptProvider;
    private string? _selectedOmpWorkingDirectory;
    private PricingRefreshResult? _lastResult;
    public MainViewModel(
        PricingCheckUseCase pricingCheck,
        SettingsUseCase settingsUseCase,
        ApplyActiveRouteUseCase applyActiveRoute,
        OmpLaunchUseCase ompLaunch,
        IActiveRouteController activeRoute,
        IPricingSnapshotQuery snapshotQuery,
        LocalAppSettings settings,
        ISitesDialogFactory sitesDialogFactory,
        IUserNotificationService notifications,
        ILogger<MainViewModel> logger,
        ISidecarStatus? sidecarStatus = null,
        OmpConfigurationReplacementUseCase? ompReplacement = null,
        CancellationToken lifetimeCancellationToken = default)
    {
        _pricingCheck = pricingCheck;
        _settingsUseCase = settingsUseCase;
        _applyActiveRoute = applyActiveRoute;
        _ompLaunch = ompLaunch;
        _ompReplacement = ompReplacement;
        _activeRoute = activeRoute;
        _snapshotQuery = snapshotQuery;
        _settings = settings;
        _sitesDialogFactory = sitesDialogFactory;
        _notifications = notifications;
        _logger = logger;
        _lifetimeCancellationToken = lifetimeCancellationToken;
        _sidecarStatus = sidecarStatus;
        _gatewayStatus = sidecarStatus?.Current;
        if (_sidecarStatus is not null)
            _sidecarStatus.Changed += HandleSidecarStatusChanged;
        _activeRoute.Changed += HandleActiveRouteChanged;
        InitializeCommand = new AsyncCommand(InitializeAsync, HandleCommandError);
        CheckCommand = new AsyncCommand(CheckAsync, HandleCommandError, () => _checkCancellation is null);
        CancelCommand = new RelayCommand(() => _checkCancellation?.Cancel(), () => _checkCancellation is not null);
        ApplyRouteCommand = new AsyncCommand(ApplyRouteAsync, HandleCommandError, () => SelectedProvider is not null);
        ReplaceOmpGptProviderCommand = new AsyncCommand(ReplaceOmpGptProviderAsync, HandleCommandError, () => _ompReplacement is not null && SelectedOmpConfigurationTarget is not null);
        StartOmpCommand = new AsyncCommand(StartOmpAsync, HandleCommandError);
        ManageSitesCommand = new RelayCommand(ManageSites);
        SettingsCommand = new AsyncCommand(EditSettingsAsync, HandleCommandError);
        foreach (var providerId in OmpConfigurationReplacementTargets.All)
            OmpConfigurationTargetChoices.Add(new OmpConfigurationTargetChoice(providerId));
        SelectedOmpConfigurationTarget = OmpConfigurationTargetChoices.FirstOrDefault();
        LoadWorkingDirectories(settings);
    }

    public ObservableCollection<PriceRow> Rows { get; } = [];
    public ObservableCollection<ProviderChoice> ProviderChoices { get; } = [];
    public ObservableCollection<OmpConfigurationTargetChoice> OmpConfigurationTargetChoices { get; } = [];
    public ObservableCollection<string> OmpWorkingDirectoryChoices { get; } = [];
    public AsyncCommand InitializeCommand { get; }
    public AsyncCommand CheckCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand ApplyRouteCommand { get; }
    public AsyncCommand ReplaceOmpGptProviderCommand { get; }
    public AsyncCommand StartOmpCommand { get; }
    public RelayCommand ManageSitesCommand { get; }
    public AsyncCommand SettingsCommand { get; }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string CurrentProvider { get => _currentProvider; private set => SetProperty(ref _currentProvider, value); }
    public string RecommendedProvider { get => _recommendedProvider; private set => SetProperty(ref _recommendedProvider, value); }
    public string LastCheckedText { get => _lastCheckedText; private set => SetProperty(ref _lastCheckedText, value); }
    public string OmpConfigurationStatus { get => _ompConfigurationStatus; private set => SetProperty(ref _ompConfigurationStatus, value); }
    public Brush OmpConfigurationStatusBrush => OmpConfigurationStatus switch
    {
        "配置已替换" => Brushes.SeaGreen,
        "配置替换失败" => Brushes.IndianRed,
        _ => Brushes.DarkOrange
    };
    public string GatewayPortStatus => _settings.GatewayPort == _settings.CurrentGatewayPort
        ? $"网关端口：127.0.0.1:{_settings.CurrentGatewayPort}"
        : $"网关端口：127.0.0.1:{_settings.CurrentGatewayPort}；下次启动：{_settings.GatewayPort}";
    public string GatewayStatusText => _gatewayStatus?.Status switch
    {
        SidecarConnectionStatus.Ready => "网关运行中",
        SidecarConnectionStatus.Starting => "网关启动中",
        SidecarConnectionStatus.Disconnected => "网关连接断开",
        SidecarConnectionStatus.Faulted => "网关故障",
        SidecarConnectionStatus.Stopped => "网关已停止",
        _ => "网关状态未知"
    };
    public string ActiveRouteStatusText => _gatewayStatus?.Status switch
    {
        SidecarConnectionStatus.Ready => _activeRoute.CurrentProviderId is null ? "无活动路由" : "活动路由已应用",
        SidecarConnectionStatus.Starting => "网关恢复中",
        SidecarConnectionStatus.Disconnected or SidecarConnectionStatus.Faulted => "网关不可用",
        SidecarConnectionStatus.Stopped => "网关已停止",
        _ => _activeRoute.CurrentProviderId is null ? "无活动路由" : "网关状态未知"
    };
    public Brush GatewayStatusBrush => _gatewayStatus?.Status switch
    {
        SidecarConnectionStatus.Ready => Brushes.SeaGreen,
        SidecarConnectionStatus.Starting => Brushes.Goldenrod,
        SidecarConnectionStatus.Disconnected or SidecarConnectionStatus.Faulted => Brushes.IndianRed,
        SidecarConnectionStatus.Stopped => Brushes.Gray,
        _ => _settings.CurrentGatewayPort is >= 1 and <= 65535 ? Brushes.SeaGreen : Brushes.IndianRed
    };
    public Brush ActiveRouteStatusBrush => _gatewayStatus?.Status switch
    {
        SidecarConnectionStatus.Ready when _activeRoute.CurrentProviderId is not null => Brushes.SeaGreen,
        SidecarConnectionStatus.Ready => Brushes.DarkOrange,
        SidecarConnectionStatus.Starting => Brushes.Goldenrod,
        SidecarConnectionStatus.Disconnected or SidecarConnectionStatus.Faulted => Brushes.IndianRed,
        _ => Brushes.Gray
    };
    public OmpConfigurationTargetChoice? SelectedOmpConfigurationTarget
    {
        get => _selectedOmpConfigurationTarget;
        set
        {
            if (SetProperty(ref _selectedOmpConfigurationTarget, value))
                ReplaceOmpGptProviderCommand.RaiseCanExecuteChanged();
        }
    }
    public ProviderChoice? SelectedProvider { get => _selectedProvider; set { if (SetProperty(ref _selectedProvider, value)) { OnPropertyChanged(nameof(SelectionHint)); ApplyRouteCommand.RaiseCanExecuteChanged(); } } }
    public string? SelectedOmpWorkingDirectory { get => _selectedOmpWorkingDirectory; set => SetProperty(ref _selectedOmpWorkingDirectory, value); }
    public bool IsApplyingRoute { get => _isApplyingRoute; private set { if (SetProperty(ref _isApplyingRoute, value)) OnPropertyChanged(nameof(ApplyRouteButtonText)); } }
    public bool IsStartingOmp { get => _isStartingOmp; private set { if (SetProperty(ref _isStartingOmp, value)) OnPropertyChanged(nameof(StartOmpButtonText)); } }
    public bool IsReplacingOmpGptProvider { get => _isReplacingOmpGptProvider; private set { if (SetProperty(ref _isReplacingOmpGptProvider, value)) OnPropertyChanged(nameof(ReplaceOmpGptProviderButtonText)); } }
    public string ApplyRouteButtonText => IsApplyingRoute ? "应用中…" : "应用供应商";
    public string ReplaceOmpGptProviderButtonText => IsReplacingOmpGptProvider ? "替换中…" : "替换 OMP GPT";
    public string StartOmpButtonText => IsStartingOmp ? "启动中…" : "启动 OMP";
    public static string SelectionHint => "仅当前绑定分组可应用；最低价分组只读比较。";

    public async Task InitializeAsync()
    {
        var restored = await _applyActiveRoute.RestoreAsync(_lifetimeCancellationToken);
        _settings = restored.Settings with { CurrentGatewayPort = _settings.CurrentGatewayPort };
        LoadWorkingDirectories(_settings);
        OnPropertyChanged(nameof(GatewayPortStatus));
        OnPropertyChanged(nameof(GatewayStatusBrush));
        OnPropertyChanged(nameof(GatewayStatusText));
        OnPropertyChanged(nameof(ActiveRouteStatusText));
        OnPropertyChanged(nameof(ActiveRouteStatusBrush));
        CurrentProvider = _activeRoute.CurrentProviderId ?? "未应用";
        LoadPersistedPrices();
        if (!restored.Succeeded)
            StatusText = UserErrorMessages.ForApplyRouteStatus(restored.Status);
    }
    private void LoadWorkingDirectories(LocalAppSettings settings)
    {
        OmpWorkingDirectoryChoices.Clear();
        foreach (var directory in settings.OmpWorkingDirectories
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            OmpWorkingDirectoryChoices.Add(directory);
        if (OmpWorkingDirectoryChoices.Count == 0 && !string.IsNullOrWhiteSpace(settings.OmpRootDirectory))
            OmpWorkingDirectoryChoices.Add(settings.OmpRootDirectory);
        SelectedOmpWorkingDirectory =
            OmpWorkingDirectoryChoices.FirstOrDefault(path => string.Equals(path, settings.LastOmpWorkingDirectory, StringComparison.OrdinalIgnoreCase))
            ?? OmpWorkingDirectoryChoices.FirstOrDefault();
    }
    private void HandleSidecarStatusChanged(SidecarStatus status)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SetGatewayStatus(status);
            return;
        }
        _ = dispatcher.BeginInvoke(() => SetGatewayStatus(status));
    }

    private void HandleActiveRouteChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SetActiveRouteStatus();
            return;
        }
        _ = dispatcher.BeginInvoke(SetActiveRouteStatus);
    }

    private void SetActiveRouteStatus()
    {
        CurrentProvider = _activeRoute.CurrentProviderId ?? "未应用";
        OnPropertyChanged(nameof(ActiveRouteStatusText));
        OnPropertyChanged(nameof(ActiveRouteStatusBrush));
    }

    private void SetGatewayStatus(SidecarStatus status)
    {
        if (_gatewayStatus == status) return;
        _gatewayStatus = status;
        OnPropertyChanged(nameof(GatewayStatusText));
        OnPropertyChanged(nameof(GatewayStatusBrush));
        OnPropertyChanged(nameof(ActiveRouteStatusText));
        OnPropertyChanged(nameof(ActiveRouteStatusBrush));
    }


    private async Task StartOmpAsync()
    {
        if (IsStartingOmp) return;
        IsStartingOmp = true;
        try
        {
            _lifetimeCancellationToken.ThrowIfCancellationRequested();
            var workingDirectory = SelectedOmpWorkingDirectory ?? _settings.OmpRootDirectory;
            var outcome = await _ompLaunch.LaunchAsync(_settings, workingDirectory, _lifetimeCancellationToken);
            _settings = outcome.Settings;
            StatusText = UserErrorMessages.ForOmpLaunchStatus(outcome);
            OnPropertyChanged(nameof(GatewayPortStatus));
            OnPropertyChanged(nameof(GatewayStatusBrush));
        }
        finally
        {
            IsStartingOmp = false;
        }
    }

    private async Task ReplaceOmpGptProviderAsync()
    {
        if (_ompReplacement is null || SelectedOmpConfigurationTarget is null || IsReplacingOmpGptProvider)
            return;

        IsReplacingOmpGptProvider = true;
        try
        {
            _lifetimeCancellationToken.ThrowIfCancellationRequested();
            var target = SelectedOmpConfigurationTarget.ProviderId;
            var preview = await _ompReplacement.PreviewAsync(_settings, target, _lifetimeCancellationToken);
            if (!preview.Succeeded)
            {
                OmpConfigurationStatus = "配置替换失败";
                OnPropertyChanged(nameof(OmpConfigurationStatusBrush));
                StatusText = preview.ErrorMessage ?? "无法预览 OMP 配置替换。";
                return;
            }

            if (!preview.HasChanges)
            {
                OmpConfigurationStatus = "无可变更 GPT";
                OnPropertyChanged(nameof(OmpConfigurationStatusBrush));
                StatusText = "没有需要替换的 GPT 路由，未写入文件。";
                return;
            }

            var rows = string.Join(
                Environment.NewLine,
                preview.Changes.Select(change =>
                    $"{change.RolePath}: {change.OriginalProvider} -> {change.TargetProvider}，ModelId={change.ModelId}"));
            var modelsText = preview.ModelsChangeKind switch
            {
                OmpModelsProviderChangeKind.Added => Environment.NewLine + "同时新增本地 provider-price-switcher 定义。",
                OmpModelsProviderChangeKind.Updated => Environment.NewLine + "同时更新本地 provider-price-switcher 定义。",
                _ => string.Empty
            };
            var confirmed = _notifications.Confirm(
                $"将替换以下 OMP GPT 路由到 {target}：{Environment.NewLine}{rows}{modelsText}{Environment.NewLine}{Environment.NewLine}确认写入 OMP 配置吗？",
                "预览 OMP GPT 供应商替换");
            if (!confirmed)
            {
                StatusText = "已取消 OMP 配置替换，未写入文件。";
                return;
            }

            var result = await _ompReplacement.ExecuteAsync(_settings, preview, _lifetimeCancellationToken);
            if (!result.Succeeded)
            {
                OmpConfigurationStatus = "配置替换失败";
                OnPropertyChanged(nameof(OmpConfigurationStatusBrush));
                StatusText = result.ErrorMessage ?? "OMP 配置替换失败，未报告成功。";
                return;
            }

            OmpConfigurationStatus = "配置已替换";
            OnPropertyChanged(nameof(OmpConfigurationStatusBrush));
            StatusText = !result.BackupRetentionSucceeded
                ? "OMP 配置已替换，但旧备份清理失败；请检查备份数量后再手动重启 OMP。"
                : result.Preview.IsNoOp
                    ? "没有需要替换的 GPT 路由，未写入文件。"
                    : "OMP 配置已替换；已运行的 OMP 需要手动重启后生效。";
        }
        finally
        {
            IsReplacingOmpGptProvider = false;
        }
    }

    private async Task CheckAsync()
    {
        if (_checkCancellation is not null) return;
        _checkCancellation = new CancellationTokenSource(); CheckCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); StatusText = "正在检查已启用站点的价格…";
        try
        {
            var outcome = await _pricingCheck.ExecuteAsync(_settings, _activeRoute.CurrentProviderId, _checkCancellation.Token);
            _settings = outcome.Settings;
            _lastResult = outcome.RefreshResult;
            MapResult(_lastResult, LocalAppSettings.DefaultUsageProfile);
            LastCheckedText = _lastResult.CompletedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); StatusText = "检查完成。";
        }
        catch (OperationCanceledException) { StatusText = "已取消检查。"; }
        catch (Exception) { throw; }
        finally { _checkCancellation.Dispose(); _checkCancellation = null; CheckCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); ApplyRouteCommand.RaiseCanExecuteChanged(); }
    }

    private void MapResult(PricingRefreshResult result, UsageProfile usage)
    {
        Rows.Clear();
        var previousSelection = SelectedProvider?.ProviderId;
        foreach (var site in _settings.Sites)
        {
            var state = result.Sites.FirstOrDefault(x => x.ProviderId == site.ProviderId);
            var pricing = state?.PricingResult;
            var snapshot = pricing?.Snapshot ?? state?.PreviousSnapshot ?? result.LatestSnapshots.GetValueOrDefault(site.ProviderId);
            AddRows(site, snapshot, usage, state, pricing);
        }
        var recommended = result.Recommendation.Selected?.Site.ProviderId;
        RecommendedProvider = recommended ?? "无可自动推荐项";
        LoadProviderChoices(recommended ?? previousSelection);
    }

    private void LoadPersistedPrices(string? preferredProvider = null)
    {
        var result = _snapshotQuery.Load();
        if (!result.IsSuccess)
        {
            StatusText = "无法读取上次价格记录，请检查本地数据文件。";
            LogUiFailure(_logger, "SnapshotReadFailed", null);
            return;
        }

        var snapshots = result.Snapshots;
        var usage = LocalAppSettings.DefaultUsageProfile;
        Rows.Clear();
        DateTimeOffset? latest = null;
        foreach (var site in _settings.Sites)
        {
            var snapshot = snapshots.GetValueOrDefault(site.ProviderId);
            AddRows(site, snapshot, usage, null, null);
            if (snapshot is not null && (latest is null || snapshot.RefreshedAt > latest)) latest = snapshot.RefreshedAt;
        }
        LastCheckedText = latest?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "尚未检查";
        RecommendedProvider = "等待手动检查";
        LoadProviderChoices(preferredProvider ?? CurrentProvider);
        StatusText = snapshots.Count > 0 ? "已加载上次检查结果；点击“检查价格”更新。" : "尚无价格记录；点击“检查价格”。";
    }

    private void AddRows(SiteConfiguration site, PricingSnapshot? snapshot, UsageProfile usage, PricingRefreshSiteResult? state, SitePricingResult? pricing)
    {
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
            Rows.Add(new PriceRow(site.ProviderId, status, site.CurrentGroup + " [当前]", site.CurrentGroupRatio, null, usage, "—", site.GroupRatioSource, null, issue, stale, warning, true, site.BaseUrl, site.ConfigurationApiAddress));
            return;
        }
        var effective = site.CurrentGroupRatio is > 0
            ? snapshot.WithCurrentRatio(site.CurrentGroupRatio.Value, site.GroupRatioSource)
            : snapshot;
        var minimumSame = string.Equals(effective.MinimumGroup, site.CurrentGroup, StringComparison.Ordinal);
        var currentLabel = site.CurrentGroup + (minimumSame ? " [当前][最低]" : " [当前]");
        Rows.Add(new PriceRow(site.ProviderId, status, currentLabel, effective.CurrentGroupRatio ?? site.CurrentGroupRatio, effective.Prices, usage, "—", effective.GroupRatioSource, effective.RefreshedAt, issue, stale, warning, true, site.BaseUrl, site.ConfigurationApiAddress));
        if (!minimumSame && !string.IsNullOrWhiteSpace(effective.MinimumGroup) && effective.MinimumGroupPrices is not null)
            Rows.Add(new PriceRow(site.ProviderId, status, effective.MinimumGroup + " [最低]", effective.MinimumGroupRatio, effective.MinimumGroupPrices, usage, "—", "自动", effective.RefreshedAt, state?.FailureKind == PricingRefreshFailureKind.Authentication ? "需先绑定或更新凭据" : "仅供手动选择，未参与自动推荐", stale, warning));
    }


    private async Task ApplyRouteAsync()
    {
        var choice = SelectedProvider;
        if (choice is null || IsApplyingRoute) return;
        IsApplyingRoute = true;
        try
        {
            var outcome = await _applyActiveRoute.ExecuteAsync(_settings, choice.ProviderId, _lifetimeCancellationToken);
            _settings = outcome.Settings;
            CurrentProvider = _activeRoute.CurrentProviderId ?? "未应用";
            OnPropertyChanged(nameof(ActiveRouteStatusText));
            OnPropertyChanged(nameof(ActiveRouteStatusBrush));
            StatusText = UserErrorMessages.ForApplyRouteStatus(outcome.Status);
        }
        catch (Exception)
        {
            StatusText = UserErrorMessages.Unexpected;
            LogUiFailure(_logger, "ApplyRouteUnexpected", null);
        }
        finally
        {
            IsApplyingRoute = false;
        }
    }

    private async Task EditSettingsAsync()
    {
        var dialog = new SettingsDialog(_settings);
        if (dialog.ShowDialog() == true)
        {
            _settings = _settingsUseCase.Save(dialog.Settings) with { CurrentGatewayPort = _settings.CurrentGatewayPort };
            LoadWorkingDirectories(_settings);
            OnPropertyChanged(nameof(GatewayPortStatus));
            OnPropertyChanged(nameof(GatewayStatusBrush));
            OnPropertyChanged(nameof(GatewayStatusText));
            OnPropertyChanged(nameof(ActiveRouteStatusText));
            OnPropertyChanged(nameof(ActiveRouteStatusBrush));
            CurrentProvider = _activeRoute.CurrentProviderId ?? "未应用";
            LoadPersistedPrices();
        }
        await Task.CompletedTask;
    }

    private void HandleCommandError(Exception exception)
    {
        if (exception is OperationCanceledException) return;
        LogUiFailure(_logger, "CommandUnexpected", exception);
        _notifications.ShowError(StatusText, "操作失败");
    }

    private void LoadProviderChoices(string? preferredProvider)
    {
        ProviderChoices.Clear();
        foreach (var providerId in _settings.Sites.Where(x => x.Enabled).Select(x => x.ProviderId).Distinct(StringComparer.Ordinal)) ProviderChoices.Add(new ProviderChoice(providerId));
        SelectedProvider = ProviderChoices.FirstOrDefault(x => string.Equals(x.ProviderId, preferredProvider, StringComparison.Ordinal)) ?? ProviderChoices.FirstOrDefault(x => string.Equals(x.ProviderId, CurrentProvider, StringComparison.Ordinal));
    }

    private void ManageSites()
    {
        var previousSelection = SelectedProvider?.ProviderId;
        var dialog = _sitesDialogFactory.Create(_settings, CurrentProvider);
        dialog.ShowDialog();
        _settings = dialog.Settings;
        CurrentProvider = _activeRoute.CurrentProviderId ?? "未应用";
        OnPropertyChanged(nameof(ActiveRouteStatusText));
        OnPropertyChanged(nameof(ActiveRouteStatusBrush));
        LoadPersistedPrices(previousSelection);
    }
}
public sealed record OmpConfigurationTargetChoice(string ProviderId)
{
    public string Display => ProviderId == OmpConfigurationReplacementTargets.LocalProviderId
        ? "provider-price-switcher（本地）"
        : "openai-codex（官方 OAuth）";
}

public sealed record ProviderChoice(string ProviderId) { public string Display => ProviderId; }
public sealed class PriceRow
{
    public PriceRow(string providerId, string status, string group, decimal? ratio, TokenPrices? prices, UsageProfile usage, string cacheHitRate, string ratioSource, DateTimeOffset? checkedAt, string issue, bool isStale, bool hasWarning, bool isSiteFirstRow = false, Uri? baseUrl = null, string? configurationApiAddress = null)
    { ProviderId = providerId; Status = status; Group = group; Ratio = ratio?.ToString("0.####", CultureInfo.InvariantCulture) ?? "—"; InputPrice = prices?.InputPerMillion.ToString("0.####") ?? "—"; CachedPrice = prices?.CachedInputPerMillion.ToString("0.####") ?? "—"; OutputPrice = prices?.OutputPerMillion.ToString("0.####") ?? "—"; EstimatedCost = prices is null ? "—" : PricingCalculator.Calculate(usage, prices).ToString("0.####"); CacheHitRate = cacheHitRate; RatioSource = string.IsNullOrWhiteSpace(ratioSource) ? "—" : ratioSource; UpdatedAt = checkedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—"; Issue = issue; IsStale = isStale; HasWarning = hasWarning; IsSiteFirstRow = isSiteFirstRow; KeysUri = isSiteFirstRow ? MainWindow.BuildKeysUri(baseUrl, configurationApiAddress) : null; }
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
