using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using System.Windows;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Adapters;
using ProviderPriceSwitcher.Infrastructure;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.App;

public partial class App : System.Windows.Application
{
    private HttpClient? _httpClient;
    private IUserNotificationService? _notifications;
    private ILoggerFactory? _loggerFactory;
    private static readonly Action<ILogger, Exception?> LogApplicationStarted = LoggerMessage.Define(LogLevel.Information, new EventId(100, "ApplicationStarted"), "ProviderPriceSwitcher started");
    private WindowsSidecarSupervisor? _sidecar;
    private ActiveRouteState? _activeRoute;
    private SettingsUseCase? _settingsUseCase;
    private GatewayRecoveryUseCase? _gatewayRecovery;
    private HomepageWorkflow? _workflow;
    private MainViewModel? _viewModel;
    private TrayApplicationController? _trayController;
    private CancellationTokenSource? _applicationCancellation;
    private readonly object _gatewayRecoveryGate = new();
    private Task? _gatewayRecoveryTask;
    private int _shutdownRequested;
    private static readonly Action<ILogger, string, Exception?> LogApplicationFailure = LoggerMessage.Define<string>(LogLevel.Error, new EventId(101, "ApplicationFailure"), "Application failure: {FailureKind}");
    private static readonly Action<ILogger, string, Exception?> LogGatewayRecoveryFailure = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(103, "GatewayRecoveryFailure"), "Gateway recovery failed: {RecoveryStatus}");
    private static readonly Action<ILogger, string, Exception?> LogInferenceKeyLoadFailure = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(102, "InferenceKeyLoadFailure"), "Inference API key unavailable for provider {ProviderId}");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _notifications = new WpfUserNotificationService();
        _applicationCancellation = new CancellationTokenSource();
        DispatcherUnhandledException += (_, args) =>
        {
            var logger = _loggerFactory?.CreateLogger<App>();
            if (logger is not null) LogApplicationFailure(logger, "Unhandled", args.Exception);
            _notifications.ShowError(UserErrorMessages.Unhandled, "ProviderPriceSwitcher");
            args.Handled = true;
        };
        SessionEnding += HandleSessionEnding;
        string? dataRoot = null;
        for (var index = 0; index < e.Args.Length; index++)
            if (string.Equals(e.Args[index], "--data-root", StringComparison.Ordinal) && index + 1 < e.Args.Length)
                dataRoot = e.Args[++index];
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new RollingFileLoggerProvider(new RollingFileLoggerOptions(Path.Combine(AppDataPaths.GetRoot(dataRoot), "logs")))));
        LocalAppSettings? startupSettings = null;
        try
        {
            var settingsRepository = new JsonSettingsRepository(dataRoot);
            _settingsUseCase = new SettingsUseCase(settingsRepository);
            var settings = startupSettings = settingsRepository.Load();
            _httpClient = new HttpClient();
            var snapshotRepository = new JsonPricingSnapshotRepository(dataRoot);
            var credentialStore = new WindowsSiteCredentialStore(dataRoot);
            var inferenceKeyStore = new WindowsInferenceApiKeyStore(dataRoot);
            var inferenceBindingStore = new WindowsInferenceBindingStore(settingsRepository, inferenceKeyStore, dataRoot);
            inferenceBindingStore.Recover();
            settings = startupSettings = settingsRepository.Load();
            var startupCancellation = _applicationCancellation?.Token ?? CancellationToken.None;
            var ompConfiguration = new OmpConfigurationService(new AppPathDefaults());
            var ompStartup = new OmpStartupUseCase(
                settingsRepository,
                _loggerFactory.CreateLogger<OmpStartupUseCase>());
            var activeRoute = new ActiveRouteState();
            _activeRoute = activeRoute;
            var resolver = new InferenceApiKeyResolverBridge(inferenceKeyStore, settingsRepository);
            RegisterAvailableInferenceKeys(settings, inferenceKeyStore, resolver, _loggerFactory.CreateLogger<App>());
            _sidecar = new WindowsSidecarSupervisor(
                new SidecarBinaryOptions(
                    Path.Combine(AppContext.BaseDirectory, "bifrost-sidecar.exe"),
                    "5173977eee7a0e75ca5cca069b3ee26923413550a0474973d04f0a625e059bd9",
                    "pps-sidecar-v1",
                    settings.GatewayPort),
                resolver);
            await _sidecar.StartAsync(startupCancellation);
            var applyActiveRoute = new ApplyActiveRouteUseCase(settingsRepository, _sidecar, inferenceKeyStore, activeRoute);
            _gatewayRecovery = new GatewayRecoveryUseCase(_sidecar, applyActiveRoute);
            _sidecar.Changed += HandleSidecarStatusChanged;
            HandleSidecarStatusChanged(_sidecar.Status);
            var startupOutcome = await ompStartup.InitializeAsync(settings, startupCancellation);
            if (!startupOutcome.Succeeded)
            {
                LogApplicationFailure(
                    _loggerFactory.CreateLogger<App>(),
                    startupOutcome.Status.ToString(),
                    null);
                _notifications.ShowError(
                    UserErrorMessages.ForOmpStartupOutcome(startupOutcome),
                    "ProviderPriceSwitcher");
                ShowSettingsRecovery(_settingsUseCase, startupSettings);
                await ShutdownAsync(-1);
                return;
            }
            settings = startupSettings = startupOutcome.Settings;
            var inferenceKeyUseCase = new InferenceApiKeyUseCase(inferenceKeyStore, inferenceBindingStore, activeRoute, _sidecar, resolver, settingsRepository);
            LogApplicationStarted(_loggerFactory.CreateLogger<App>(), null);
            var adapterRegistry = new PricingAdapterRegistry([
                new NewApiPricingAdapter(_httpClient),
                new PawsAiPricingAdapter(_httpClient),
                new SevnXPricingAdapter(_httpClient, credentialStore),
                new AiHubPricingAdapter(_httpClient, credentialStore)
            ]);
            var refreshService = new PricingRefreshService(adapterRegistry, _loggerFactory.CreateLogger<PricingRefreshService>());
            var snapshotQuery = new PricingSnapshotQuery(snapshotRepository);
            var siteManagement = new SiteManagementUseCase(settingsRepository, snapshotRepository, credentialStore, inferenceKeyStore, activeRoute, _sidecar, resolver);
            var pricingCheck = new PricingCheckUseCase(refreshService, settingsRepository, snapshotRepository);
            var settingsUseCase = _settingsUseCase!;
            var ompLaunch = new OmpLaunchUseCase(
                settingsRepository,
                new OmpProcessLauncher(new OmpProcessService(), new AppPathDefaults()),
                _loggerFactory.CreateLogger<OmpLaunchUseCase>());
            var ompReplacement = new OmpConfigurationReplacementUseCase(ompConfiguration);
            var editorFactory = new SiteEditorDialogFactory((original, localSettings) => new SiteEditorViewModel(new PricingProbeUseCase(adapterRegistry), adapterRegistry, credentialStore, _notifications, localSettings, original, inferenceKeyUseCase));
            var sitesFactory = new SitesDialogFactory((localSettings, currentProvider) => new SitesDialog(localSettings, siteManagement, snapshotQuery, editorFactory, currentProvider, _notifications));
            _workflow = new HomepageWorkflow(
                pricingCheck,
                settingsUseCase,
                applyActiveRoute,
                ompLaunch,
                activeRoute,
                snapshotQuery,
                settings,
                _loggerFactory.CreateLogger<HomepageWorkflow>(),
                _sidecar,
                ompReplacement,
                _notifications,
                startupCancellation);
            _viewModel = new MainViewModel(
                _workflow,
                sitesFactory,
                _notifications,
                _loggerFactory.CreateLogger<MainViewModel>());
            var mainWindow = new MainWindow(_viewModel);
            MainWindow = mainWindow;
            _trayController = new TrayApplicationController(mainWindow, _viewModel, new WindowsTrayHost(), _notifications, RequestShutdown);
            mainWindow.Show();
        }
        catch (OperationCanceledException) when (_applicationCancellation?.IsCancellationRequested == true)
        {
        }
        catch (GatewayPortUnavailableException exception)
        {
            var logger = _loggerFactory?.CreateLogger<App>();
            if (logger is not null) LogApplicationFailure(logger, "GatewayPortUnavailable", exception);
            _notifications.ShowError(UserErrorMessages.GatewayPortUnavailable, "ProviderPriceSwitcher");
            ShowSettingsRecovery(_settingsUseCase, startupSettings);
            await ShutdownAsync(-1);
        }
        catch (Exception exception)
        {
            var logger = _loggerFactory?.CreateLogger<App>();
            if (logger is not null) LogApplicationFailure(logger, "Startup", exception);
            _notifications.ShowError(UserErrorMessages.ApplicationStartupFailed, "ProviderPriceSwitcher");
            await ShutdownAsync(-1);
        }
    }
    private void HandleSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        if (Volatile.Read(ref _shutdownRequested) == 0)
            RequestShutdown();
    }

    private void RequestShutdown() => _ = ShutdownAsync(0);

    private async Task ShutdownAsync(int exitCode)
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
            return;

        _applicationCancellation?.Cancel();
        Task? recovery;
        lock (_gatewayRecoveryGate) recovery = _gatewayRecoveryTask;
        try
        {
            if (recovery is not null)
                await recovery.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_applicationCancellation?.IsCancellationRequested == true)
        {
        }
        catch (Exception exception)
        {
            var logger = _loggerFactory?.CreateLogger<App>();
            if (logger is not null) LogApplicationFailure(logger, "ShutdownRecovery", exception);
        }
        _viewModel?.Dispose();
        _workflow?.Dispose();
        if (_sidecar is not null)
        {
            _sidecar.Changed -= HandleSidecarStatusChanged;
            try { await _sidecar.DisposeAsync().ConfigureAwait(true); }
            catch (Exception exception)
            {
                var logger = _loggerFactory?.CreateLogger<App>();
                if (logger is not null) LogApplicationFailure(logger, "ShutdownGateway", exception);
            }
        }
        _trayController?.Dispose();
        _activeRoute?.Dispose();
        _httpClient?.Dispose();
        _applicationCancellation?.Dispose();
        _loggerFactory?.Dispose();
        Shutdown(exitCode);
    }


    private void HandleSidecarStatusChanged(SidecarStatus status)
    {
        if (status.Status is not (SidecarConnectionStatus.Disconnected or SidecarConnectionStatus.Faulted)
            || _applicationCancellation?.IsCancellationRequested == true)
            return;
        lock (_gatewayRecoveryGate)
        {
            if (_gatewayRecoveryTask is { IsCompleted: false })
                return;
            _gatewayRecoveryTask = RecoverGatewayAsync();
        }
    }

    private async Task RecoverGatewayAsync()
    {
        try
        {
            var cancellationToken = _applicationCancellation?.Token ?? CancellationToken.None;
            if (_gatewayRecovery is null || _sidecar is null)
                return;

            var logger = _loggerFactory?.CreateLogger<App>();
            while (!cancellationToken.IsCancellationRequested)
            {
                var outcome = await RecoverGatewayWithRetryAsync(
                    _gatewayRecovery.ExecuteAsync,
                    recoveryOutcome =>
                    {
                        if (logger is not null)
                            LogGatewayRecoveryFailure(logger, $"{recoveryOutcome.Status}:{recoveryOutcome.FailureKind}", null);
                    },
                    cancellationToken).ConfigureAwait(false);

                lock (_gatewayRecoveryGate)
                {
                    if (outcome.Succeeded && !_sidecar.Status.IsReady)
                        continue;
                    _gatewayRecoveryTask = null;
                    return;
                }
            }
            lock (_gatewayRecoveryGate)
                _gatewayRecoveryTask = null;
        }
        catch (OperationCanceledException) when (_applicationCancellation?.IsCancellationRequested == true)
        {
            lock (_gatewayRecoveryGate)
                _gatewayRecoveryTask = null;
        }
        catch (Exception exception)
        {
            var logger = _loggerFactory?.CreateLogger<App>();
            if (logger is not null) LogGatewayRecoveryFailure(logger, "Unexpected", exception);
            lock (_gatewayRecoveryGate)
                _gatewayRecoveryTask = null;
        }
    }

    internal static async Task<GatewayRecoveryOutcome> RecoverGatewayWithRetryAsync(
        Func<CancellationToken, Task<GatewayRecoveryOutcome>> recover,
        Action<GatewayRecoveryOutcome> onFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recover);
        ArgumentNullException.ThrowIfNull(onFailure);
        cancellationToken.ThrowIfCancellationRequested();
        var retryDelay = TimeSpan.FromMilliseconds(250);
        while (true)
        {
            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            var outcome = await recover(cancellationToken).ConfigureAwait(false);
            if (outcome.Succeeded)
                return outcome;
            onFailure(outcome);
            if (outcome.FailureKind is not (GatewayRecoveryFailureKind.GatewayUnavailable or GatewayRecoveryFailureKind.Protocol))
                return outcome;
            retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 5000));
        }
    }

    private void ShowSettingsRecovery(SettingsUseCase? settingsUseCase, LocalAppSettings? settings)
    {
        if (settingsUseCase is null || settings is null)
            return;
        try
        {
            var dialog = new SettingsDialog(settings);
            if (dialog.ShowDialog() == true)
                settingsUseCase.Save(dialog.Settings with { CurrentGatewayPort = settings.CurrentGatewayPort });
        }
        catch (Exception exception)
        {
            var logger = _loggerFactory?.CreateLogger<App>();
            if (logger is not null) LogApplicationFailure(logger, "SettingsRecovery", exception);
            _notifications?.ShowError(UserErrorMessages.SettingsRecoveryFailed, "ProviderPriceSwitcher");
        }
    }

    internal static void RegisterAvailableInferenceKeys(LocalAppSettings settings, IInferenceApiKeyStore store, InferenceApiKeyResolverBridge resolver, ILogger logger)
    {
        foreach (var site in settings.Sites)
        {
            try
            {
                var record = store.Load(site.ProviderId);
                if (record is not null) resolver.Register(record);
            }
            catch (JsonDataException exception)
            {
                LogInferenceKeyLoadFailure(logger, site.ProviderId, exception);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }
}
