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
    private CancellationTokenSource? _applicationCancellation;
    private static readonly Action<ILogger, string, Exception?> LogApplicationFailure = LoggerMessage.Define<string>(LogLevel.Error, new EventId(101, "ApplicationFailure"), "Application failure: {FailureKind}");
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
            var ompTakeover = new OmpConfigurationService(new OmpConfigurationSwitcher(), new AppPathDefaults());
            var ompStartup = new OmpStartupUseCase(
                settingsRepository,
                ompTakeover,
                _loggerFactory.CreateLogger<OmpStartupUseCase>());
            var activeRoute = new ActiveRouteState();
            _activeRoute = activeRoute;
            var resolver = new InferenceApiKeyResolverBridge(inferenceKeyStore, settingsRepository);
            RegisterAvailableInferenceKeys(settings, inferenceKeyStore, resolver, _loggerFactory.CreateLogger<App>());
            _sidecar = new WindowsSidecarSupervisor(
                new SidecarBinaryOptions(
                    Path.Combine(AppContext.BaseDirectory, "bifrost-sidecar.exe"),
                    "38c2c8a69e481a6561d07d7252f2fd100a50bef443bbb61beddf85e2e6ae4491",
                    "pps-sidecar-v1",
                    settings.GatewayPort),
                resolver);
            await _sidecar.StartAsync(startupCancellation);
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
                Shutdown(-1);
                return;
            }
            if (!startupOutcome.BackupRetentionSucceeded)
                _notifications.ShowWarning(UserErrorMessages.OmpBackupRetentionFailed, "ProviderPriceSwitcher");
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
            var applyActiveRoute = new ApplyActiveRouteUseCase(settingsRepository, _sidecar, inferenceKeyStore, activeRoute);
            var siteManagement = new SiteManagementUseCase(settingsRepository, snapshotRepository, credentialStore, inferenceKeyStore, activeRoute, _sidecar, resolver);
            var pricingCheck = new PricingCheckUseCase(refreshService, settingsRepository, snapshotRepository);
            var settingsUseCase = _settingsUseCase!;
            var ompLaunch = new OmpLaunchUseCase(
                settingsRepository,
                ompTakeover,
                new OmpProcessLauncher(new OmpProcessService(), new AppPathDefaults()),
                _loggerFactory.CreateLogger<OmpLaunchUseCase>());
            var editorFactory = new SiteEditorDialogFactory((original, localSettings) => new SiteEditorViewModel(new PricingProbeUseCase(adapterRegistry), adapterRegistry, credentialStore, _notifications, localSettings, original, inferenceKeyUseCase));
            var sitesFactory = new SitesDialogFactory((localSettings, currentProvider) => new SitesDialog(localSettings, siteManagement, snapshotQuery, editorFactory, currentProvider, _notifications));
            var viewModel = new MainViewModel(pricingCheck, settingsUseCase, applyActiveRoute, ompLaunch, activeRoute, snapshotQuery, settings, sitesFactory, _notifications, _loggerFactory.CreateLogger<MainViewModel>(), _sidecar, startupCancellation);
            MainWindow = new MainWindow(viewModel);
            MainWindow.Show();
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
        }
        catch (Exception exception)
        {
            var logger = _loggerFactory?.CreateLogger<App>();
            if (logger is not null) LogApplicationFailure(logger, "Startup", exception);
            _notifications.ShowError(UserErrorMessages.ApplicationStartupFailed, "ProviderPriceSwitcher");
            Shutdown(-1);
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
        _applicationCancellation?.Cancel();
        _sidecar?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _applicationCancellation?.Dispose();
        _activeRoute?.Dispose();
        _httpClient?.Dispose();
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }
}
