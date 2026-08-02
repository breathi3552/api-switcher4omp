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
    private static readonly Action<ILogger, string, Exception?> LogApplicationFailure = LoggerMessage.Define<string>(LogLevel.Error, new EventId(101, "ApplicationFailure"), "Application failure: {FailureKind}");
    private static readonly Action<ILogger, string, Exception?> LogInferenceKeyLoadFailure = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(102, "InferenceKeyLoadFailure"), "Inference API key unavailable for provider {ProviderId}");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _notifications = new WpfUserNotificationService();
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
        try
        {
            var settingsRepository = new JsonSettingsRepository(dataRoot);
            var settings = settingsRepository.Load();
            _httpClient = new HttpClient();
            var snapshotRepository = new JsonPricingSnapshotRepository(dataRoot);
            var credentialStore = new WindowsSiteCredentialStore(dataRoot);
            var inferenceKeyStore = new WindowsInferenceApiKeyStore(dataRoot);
            var activeRoute = new ActiveRouteState();
            var inferenceKeyUseCase = new InferenceApiKeyUseCase(inferenceKeyStore, settingsRepository, activeRoute);
            var resolver = new InferenceApiKeyResolverBridge(inferenceKeyStore, settingsRepository);
            RegisterAvailableInferenceKeys(settings, inferenceKeyStore, resolver, _loggerFactory.CreateLogger<App>());
            _sidecar = new WindowsSidecarSupervisor(new SidecarBinaryOptions(Path.Combine(AppContext.BaseDirectory, "bifrost-sidecar.exe"), "38c2c8a69e481a6561d07d7252f2fd100a50bef443bbb61beddf85e2e6ae4491", "pps-sidecar-v1"), resolver);
            inferenceKeyUseCase = new InferenceApiKeyUseCase(inferenceKeyStore, settingsRepository, activeRoute, _sidecar, resolver);
            LogApplicationStarted(_loggerFactory.CreateLogger<App>(), null);
            var adapterRegistry = new PricingAdapterRegistry([
                new NewApiPricingAdapter(_httpClient),
                new PawsAiPricingAdapter(_httpClient),
                new SevnXPricingAdapter(_httpClient, credentialStore),
                new AiHubPricingAdapter(_httpClient, credentialStore)
            ]);
            var refreshService = new PricingRefreshService(adapterRegistry, _loggerFactory.CreateLogger<PricingRefreshService>());
            var currentProviderQuery = new OmpCurrentProviderQuery(new OmpConfigurationSwitcher(), new AppPathDefaults());
            var snapshotQuery = new PricingSnapshotQuery(snapshotRepository);
            var siteManagement = new SiteManagementUseCase(settingsRepository, snapshotRepository, credentialStore, inferenceKeyStore, activeRoute, _sidecar, resolver);
            var pricingCheck = new PricingCheckUseCase(refreshService, settingsRepository, snapshotRepository);
            var settingsUseCase = new SettingsUseCase(settingsRepository);
            var switchAndStart = new SwitchAndStartUseCase(settingsRepository, new OmpConfigurationService(new OmpConfigurationSwitcher(), new AppPathDefaults()), new OmpProcessLauncher(new OmpProcessService()), _loggerFactory.CreateLogger<SwitchAndStartUseCase>(), _sidecar, inferenceKeyStore, activeRoute);
            var editorFactory = new SiteEditorDialogFactory((original, localSettings) => new SiteEditorViewModel(new PricingProbeUseCase(adapterRegistry), adapterRegistry, credentialStore, _notifications, localSettings, original, inferenceKeyUseCase));
            var sitesFactory = new SitesDialogFactory((localSettings, currentProvider) => new SitesDialog(localSettings, siteManagement, snapshotQuery, editorFactory, currentProvider));
            var viewModel = new MainViewModel(pricingCheck, settingsUseCase, switchAndStart, currentProviderQuery, snapshotQuery, adapterRegistry, settings, sitesFactory, _notifications, _loggerFactory.CreateLogger<MainViewModel>());
            MainWindow = new MainWindow(viewModel);
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            var logger = _loggerFactory?.CreateLogger<App>();
            if (logger is not null) LogApplicationFailure(logger, "Startup", exception);
            _notifications.ShowError(UserErrorMessages.ApplicationStartupFailed, "ProviderPriceSwitcher");
            Shutdown(-1);
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
        _sidecar?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpClient?.Dispose();
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }
}
