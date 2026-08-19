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

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly HomepageWorkflow _workflow;
    private readonly ISitesDialogFactory _sitesDialogFactory;
    private readonly IUserNotificationService _notifications;
    private readonly ILogger<MainViewModel> _logger;
    private readonly ISidecarStatus? _sidecarStatus;
    private readonly IActiveRouteController? _activeRoute;
    private static readonly Action<ILogger, string, Exception?> LogUiFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(200, "UiFailure"), "UI operation failed: {FailureKind}");
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
    private bool _isApplyingState;

    public MainViewModel(
        HomepageWorkflow workflow,
        ISitesDialogFactory sitesDialogFactory,
        IUserNotificationService notifications,
        ILogger<MainViewModel> logger,
        ISidecarStatus? sidecarStatus = null,
        IActiveRouteController? activeRoute = null)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _sitesDialogFactory = sitesDialogFactory ?? throw new ArgumentNullException(nameof(sitesDialogFactory));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sidecarStatus = sidecarStatus;
        _activeRoute = activeRoute;

        _workflow.StateChanged += OnWorkflowStateChanged;

        InitializeCommand = new AsyncCommand(InitializeAsync, HandleCommandError);
        CheckCommand = new AsyncCommand(CheckAsync, HandleCommandError, () => _workflow.CanCheckPrices);
        CancelCommand = new RelayCommand(() => _workflow.CancelPriceCheck(), () => _workflow.CanCancelPriceCheck);
        ApplyRouteCommand = new AsyncCommand(ApplyRouteAsync, HandleCommandError, () => _workflow.CanApplyRoute);
        ReplaceOmpGptProviderCommand = new AsyncCommand(ReplaceOmpGptProviderAsync, HandleCommandError, () => _workflow.CanReplaceOmpGptProvider);
        StartOmpCommand = new AsyncCommand(StartOmpAsync, HandleCommandError, () => _workflow.CanStartOmp);
        ManageSitesCommand = new RelayCommand(ManageSites);
        SettingsCommand = new AsyncCommand(EditSettingsAsync, HandleCommandError);

        _state = _workflow.State;
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
    public Brush GatewayStatusBrush => _sidecarStatus?.Current?.Status switch
    {
        SidecarConnectionStatus.Ready => Brushes.SeaGreen,
        SidecarConnectionStatus.Starting => Brushes.Goldenrod,
        SidecarConnectionStatus.Disconnected or SidecarConnectionStatus.Faulted => Brushes.IndianRed,
        SidecarConnectionStatus.Stopped => Brushes.Gray,
        _ => _workflow.Settings.CurrentGatewayPort is >= 1 and <= 65535 ? Brushes.SeaGreen : Brushes.IndianRed
    };
    public Brush ActiveRouteStatusBrush => _sidecarStatus?.Current?.Status switch
    {
        SidecarConnectionStatus.Ready when _activeRoute?.CurrentProviderId is not null => Brushes.SeaGreen,
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
                if (!_isApplyingState)
                {
                    _workflow.SelectOmpConfigurationTarget(value);
                }
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
                if (!_isApplyingState)
                {
                    _workflow.SelectProvider(value);
                }
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
                if (!_isApplyingState)
                {
                    _workflow.SelectOmpWorkingDirectory(value);
                }
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

    private void OnWorkflowStateChanged(HomepageState state)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyState(state);
            return;
        }
        _ = dispatcher.BeginInvoke(() => ApplyState(state));
    }

    private static void SyncCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> source, Func<T, T, bool> equals)
    {
        if (target.Count == source.Count && target.Zip(source).All(pair => equals(pair.First, pair.Second)))
            return;

        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private void ApplyState(HomepageState state)
    {
        _isApplyingState = true;
        try
        {
            _state = state;

            SyncCollection(Rows, state.Rows, (a, b) => ReferenceEquals(a, b));
            SyncCollection(ProviderChoices, state.ProviderChoices, (a, b) => a.ProviderId == b.ProviderId);
            SyncCollection(OmpConfigurationTargetChoices, state.OmpConfigurationTargetChoices, (a, b) => a.ProviderId == b.ProviderId);
            SyncCollection(OmpWorkingDirectoryChoices, state.OmpWorkingDirectoryChoices, (a, b) => a == b);

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
            OnPropertyChanged(nameof(SelectionHint));

            CheckCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            ApplyRouteCommand.RaiseCanExecuteChanged();
            ReplaceOmpGptProviderCommand.RaiseCanExecuteChanged();
            StartOmpCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    public async Task InitializeAsync()
    {
        await _workflow.InitializeAsync();
    }

    private async Task StartOmpAsync()
    {
        await _workflow.StartOmpAsync();
    }

    private async Task ReplaceOmpGptProviderAsync()
    {
        await _workflow.ReplaceOmpGptProviderAsync();
    }

    private async Task CheckAsync()
    {
        await _workflow.CheckPricesAsync();
    }

    private async Task ApplyRouteAsync()
    {
        await _workflow.ApplyActiveRouteAsync();
    }

    private async Task EditSettingsAsync()
    {
        var dialog = new SettingsDialog(_workflow.Settings);
        if (dialog.ShowDialog() == true)
        {
            _workflow.SaveSettings(dialog.Settings);
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
        var dialog = _sitesDialogFactory.Create(_workflow.Settings, CurrentProvider);
        dialog.ShowDialog();
        _workflow.UpdateSettings(dialog.Settings, previousSelection);
    }

    public void Dispose()
    {
        _workflow.StateChanged -= OnWorkflowStateChanged;
    }
}
