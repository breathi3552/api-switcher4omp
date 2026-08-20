using System.Collections.ObjectModel;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace ProviderPriceSwitcher.App;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly HomepageWorkflow _workflow;
    private readonly ISitesDialogFactory _sitesDialogFactory;
    private readonly IUserNotificationService _notifications;
    private readonly ILogger<MainViewModel> _logger;
    private static readonly Action<ILogger, string, Exception?> LogUiFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(200, "UiFailure"), "UI operation failed: {FailureKind}");

    private HomepageState _state;
    private bool _isApplyingState;

    public MainViewModel(
        HomepageWorkflow workflow,
        ISitesDialogFactory sitesDialogFactory,
        IUserNotificationService notifications,
        ILogger<MainViewModel> logger)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _sitesDialogFactory = sitesDialogFactory ?? throw new ArgumentNullException(nameof(sitesDialogFactory));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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

    public string StatusText => _state.StatusText;
    public string CurrentProvider => _state.CurrentProvider;
    public string RecommendedProvider => _state.RecommendedProvider;
    public string LastCheckedText => _state.LastCheckedText;
    public string OmpConfigurationStatus => _state.OmpConfigurationStatus;
    public string GatewayPortStatus => _state.GatewayPortStatus;
    public string GatewayStatusText => _state.GatewayStatusText;
    public string ActiveRouteStatusText => _state.ActiveRouteStatusText;
    public string SelectionHint => _state.SelectionHint;

    public Brush OmpConfigurationStatusBrush => ToBrush(_state.OmpConfigurationTone);
    public Brush GatewayStatusBrush => ToBrush(_state.GatewayTone);
    public Brush ActiveRouteStatusBrush => ToBrush(_state.ActiveRouteTone);

    public bool IsCheckingPrices => _state.IsCheckingPrices;
    public bool IsApplyingRoute => _state.IsApplyingRoute;
    public bool IsStartingOmp => _state.IsStartingOmp;
    public bool IsReplacingOmpGptProvider => _state.IsReplacingOmpGptProvider;

    public string ApplyRouteButtonText => _state.ApplyRouteButtonText;
    public string ReplaceOmpGptProviderButtonText => _state.ReplaceOmpGptProviderButtonText;
    public string StartOmpButtonText => _state.StartOmpButtonText;

    public ProviderChoice? SelectedProvider
    {
        get => _state.SelectedProvider;
        set
        {
            if (!_isApplyingState && _state.SelectedProvider != value)
            {
                _workflow.SelectProvider(value);
            }
        }
    }

    public OmpConfigurationTargetChoice? SelectedOmpConfigurationTarget
    {
        get => _state.SelectedOmpConfigurationTarget;
        set
        {
            if (!_isApplyingState && _state.SelectedOmpConfigurationTarget != value)
            {
                _workflow.SelectOmpConfigurationTarget(value);
            }
        }
    }

    public string? SelectedOmpWorkingDirectory
    {
        get => _state.SelectedOmpWorkingDirectory;
        set
        {
            if (!_isApplyingState && !string.Equals(_state.SelectedOmpWorkingDirectory, value, StringComparison.OrdinalIgnoreCase))
            {
                _workflow.SelectOmpWorkingDirectory(value);
            }
        }
    }

    private static Brush ToBrush(StatusTone tone) => tone switch
    {
        StatusTone.Success => Brushes.SeaGreen,
        StatusTone.Starting => Brushes.Goldenrod,
        StatusTone.Warning => Brushes.DarkOrange,
        StatusTone.Danger => Brushes.IndianRed,
        _ => Brushes.Gray
    };

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

            OnPropertyChanged(string.Empty);

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
