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
    private HomepageState _state;
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

        _state = HomepageStateProjector.ProjectInitial(settings, _activeRoute.CurrentProviderId, _gatewayStatus, null);
        ApplyState(_state);
    }

    public HomepageState State => _state;
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
    public string GatewayPortStatus => _state.GatewayPortStatus;
    public string GatewayStatusText => _state.GatewayStatusText;
    public string ActiveRouteStatusText => _state.ActiveRouteStatusText;
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
            {
                _state = HomepageStateProjector.ProjectSelectedOmpTarget(_state, value);
                ReplaceOmpGptProviderCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public ProviderChoice? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                _state = HomepageStateProjector.ProjectSelectedProvider(_state, value);
                OnPropertyChanged(nameof(SelectionHint));
                ApplyRouteCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string? SelectedOmpWorkingDirectory
    {
        get => _selectedOmpWorkingDirectory;
        set
        {
            if (SetProperty(ref _selectedOmpWorkingDirectory, value))
            {
                _state = HomepageStateProjector.ProjectSelectedWorkingDirectory(_state, value);
            }
        }
    }
    public bool IsApplyingRoute { get => _isApplyingRoute; private set { if (SetProperty(ref _isApplyingRoute, value)) OnPropertyChanged(nameof(ApplyRouteButtonText)); } }
    public bool IsStartingOmp { get => _isStartingOmp; private set { if (SetProperty(ref _isStartingOmp, value)) OnPropertyChanged(nameof(StartOmpButtonText)); } }
    public bool IsReplacingOmpGptProvider { get => _isReplacingOmpGptProvider; private set { if (SetProperty(ref _isReplacingOmpGptProvider, value)) OnPropertyChanged(nameof(ReplaceOmpGptProviderButtonText)); } }
    public string ApplyRouteButtonText => _state.ApplyRouteButtonText;
    public string ReplaceOmpGptProviderButtonText => _state.ReplaceOmpGptProviderButtonText;
    public string StartOmpButtonText => _state.StartOmpButtonText;
    public static string SelectionHint => HomepageState.DefaultSelectionHint;

    private void ApplyState(HomepageState state)
    {
        _state = state;

        Rows.Clear();
        foreach (var row in state.Rows) Rows.Add(row);

        ProviderChoices.Clear();
        foreach (var choice in state.ProviderChoices) ProviderChoices.Add(choice);

        OmpConfigurationTargetChoices.Clear();
        foreach (var target in state.OmpConfigurationTargetChoices) OmpConfigurationTargetChoices.Add(target);

        OmpWorkingDirectoryChoices.Clear();
        foreach (var dir in state.OmpWorkingDirectoryChoices) OmpWorkingDirectoryChoices.Add(dir);

        StatusText = state.StatusText;
        CurrentProvider = state.CurrentProvider;
        RecommendedProvider = state.RecommendedProvider;
        LastCheckedText = state.LastCheckedText;
        OmpConfigurationStatus = state.OmpConfigurationStatus;
        SelectedProvider = state.SelectedProvider;
        SelectedOmpConfigurationTarget = state.SelectedOmpConfigurationTarget;
        SelectedOmpWorkingDirectory = state.SelectedOmpWorkingDirectory;
        IsApplyingRoute = state.IsApplyingRoute;
        IsStartingOmp = state.IsStartingOmp;
        IsReplacingOmpGptProvider = state.IsReplacingOmpGptProvider;

        OnPropertyChanged(nameof(GatewayPortStatus));
        OnPropertyChanged(nameof(GatewayStatusText));
        OnPropertyChanged(nameof(ActiveRouteStatusText));
        OnPropertyChanged(nameof(GatewayStatusBrush));
        OnPropertyChanged(nameof(ActiveRouteStatusBrush));
        OnPropertyChanged(nameof(OmpConfigurationStatusBrush));
        OnPropertyChanged(nameof(ApplyRouteButtonText));
        OnPropertyChanged(nameof(ReplaceOmpGptProviderButtonText));
        OnPropertyChanged(nameof(StartOmpButtonText));

        CheckCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ApplyRouteCommand.RaiseCanExecuteChanged();
        ReplaceOmpGptProviderCommand.RaiseCanExecuteChanged();
        StartOmpCommand.RaiseCanExecuteChanged();
    }

    public async Task InitializeAsync()
    {
        var restored = await _applyActiveRoute.RestoreAsync(_lifetimeCancellationToken);
        _settings = restored.Settings with { CurrentGatewayPort = _settings.CurrentGatewayPort };
        var snapshotResult = _snapshotQuery.Load();
        var snapshots = snapshotResult.IsSuccess ? snapshotResult.Snapshots : null;
        var initialState = HomepageStateProjector.ProjectInitial(
            _settings,
            _activeRoute.CurrentProviderId,
            _gatewayStatus,
            snapshots,
            snapshotResult.IsSuccess);

        if (!restored.Succeeded)
        {
            initialState = initialState with { StatusText = UserErrorMessages.ForApplyRouteStatus(restored.Status) };
        }

        ApplyState(initialState);

        if (!snapshotResult.IsSuccess)
        {
            LogUiFailure(_logger, "SnapshotReadFailed", null);
        }
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
        ApplyState(HomepageStateProjector.ProjectActiveRoute(_state, _activeRoute.CurrentProviderId, _gatewayStatus));
    }

    private void SetGatewayStatus(SidecarStatus status)
    {
        if (_gatewayStatus == status) return;
        _gatewayStatus = status;
        ApplyState(HomepageStateProjector.ProjectGatewayStatus(_state, status, _activeRoute.CurrentProviderId, _settings.CurrentGatewayPort, _settings.GatewayPort));
    }

    private async Task StartOmpAsync()
    {
        if (IsStartingOmp) return;
        ApplyState(HomepageStateProjector.ProjectStartingOmpStarted(_state));
        try
        {
            _lifetimeCancellationToken.ThrowIfCancellationRequested();
            var workingDirectory = SelectedOmpWorkingDirectory ?? _settings.OmpRootDirectory;
            var outcome = await _ompLaunch.LaunchAsync(_settings, workingDirectory, _lifetimeCancellationToken);
            _settings = outcome.Settings;
            ApplyState(HomepageStateProjector.ProjectStartingOmpCompleted(_state, outcome, _settings));
        }
        finally
        {
            if (IsStartingOmp)
                ApplyState(_state with { IsStartingOmp = false });
        }
    }

    private async Task ReplaceOmpGptProviderAsync()
    {
        if (_ompReplacement is null || SelectedOmpConfigurationTarget is null || IsReplacingOmpGptProvider)
            return;

        ApplyState(HomepageStateProjector.ProjectReplacingOmpStarted(_state));
        try
        {
            _lifetimeCancellationToken.ThrowIfCancellationRequested();
            var target = SelectedOmpConfigurationTarget.ProviderId;
            var preview = await _ompReplacement.PreviewAsync(_settings, target, _lifetimeCancellationToken);
            if (!preview.Succeeded)
            {
                ApplyState(HomepageStateProjector.ProjectReplacingOmpPreview(_state, preview, false));
                return;
            }

            if (!preview.HasChanges)
            {
                ApplyState(HomepageStateProjector.ProjectReplacingOmpPreview(_state, preview, false));
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
                ApplyState(HomepageStateProjector.ProjectReplacingOmpPreview(_state, preview, false, "已取消 OMP 配置替换，未写入文件。"));
                return;
            }

            var result = await _ompReplacement.ExecuteAsync(_settings, preview, _lifetimeCancellationToken);
            ApplyState(HomepageStateProjector.ProjectReplacingOmpCompleted(_state, result));
        }
        finally
        {
            if (IsReplacingOmpGptProvider)
                ApplyState(_state with { IsReplacingOmpGptProvider = false });
        }
    }

    private async Task CheckAsync()
    {
        if (_checkCancellation is not null) return;
        using var checkCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationToken);
        _checkCancellation = checkCancellation;
        ApplyState(HomepageStateProjector.ProjectPriceCheckStarted(_state));
        try
        {
            var outcome = await _pricingCheck.ExecuteAsync(_settings, _activeRoute.CurrentProviderId, checkCancellation.Token);
            _settings = outcome.Settings;
            _lastResult = outcome.RefreshResult;
            ApplyState(HomepageStateProjector.ProjectPriceCheckCompleted(_state, _settings, _lastResult, _activeRoute.CurrentProviderId, LocalAppSettings.DefaultUsageProfile));
        }
        catch (OperationCanceledException)
        {
            ApplyState(HomepageStateProjector.ProjectPriceCheckCanceled(_state));
        }
        finally
        {
            _checkCancellation = null;
            CheckCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            ApplyRouteCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task ApplyRouteAsync()
    {
        var choice = SelectedProvider;
        if (choice is null || IsApplyingRoute) return;
        ApplyState(HomepageStateProjector.ProjectApplyingRouteStarted(_state));
        try
        {
            var outcome = await _applyActiveRoute.ExecuteAsync(_settings, choice.ProviderId, _lifetimeCancellationToken);
            _settings = outcome.Settings;
            ApplyState(HomepageStateProjector.ProjectApplyingRouteCompleted(_state, outcome, _activeRoute.CurrentProviderId, _gatewayStatus));
        }
        catch (Exception)
        {
            ApplyState(_state with { IsApplyingRoute = false, StatusText = UserErrorMessages.Unexpected });
            LogUiFailure(_logger, "ApplyRouteUnexpected", null);
        }
    }

    private async Task EditSettingsAsync()
    {
        var dialog = new SettingsDialog(_settings);
        if (dialog.ShowDialog() == true)
        {
            _settings = _settingsUseCase.Save(dialog.Settings) with { CurrentGatewayPort = _settings.CurrentGatewayPort };
            var snapshotResult = _snapshotQuery.Load();
            var snapshots = snapshotResult.IsSuccess ? snapshotResult.Snapshots : null;
            ApplyState(HomepageStateProjector.ProjectSettingsUpdated(_state, _settings, _activeRoute.CurrentProviderId, snapshots, snapshotResult.IsSuccess));
        }
        await Task.CompletedTask;
    }

    private void HandleCommandError(Exception exception)
    {
        if (exception is OperationCanceledException) return;
        LogUiFailure(_logger, "CommandUnexpected", exception);
        _notifications.ShowError(StatusText, "操作失败");
    }

    private void ManageSites()
    {
        var previousSelection = SelectedProvider?.ProviderId;
        var dialog = _sitesDialogFactory.Create(_settings, CurrentProvider);
        dialog.ShowDialog();
        _settings = dialog.Settings;
        var snapshotResult = _snapshotQuery.Load();
        var snapshots = snapshotResult.IsSuccess ? snapshotResult.Snapshots : null;
        ApplyState(HomepageStateProjector.ProjectSettingsUpdated(_state, _settings, _activeRoute.CurrentProviderId, snapshots, snapshotResult.IsSuccess, previousSelection));
    }
}
