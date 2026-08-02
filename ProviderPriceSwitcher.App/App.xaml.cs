using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using System.Windows;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Adapters;
using ProviderPriceSwitcher.Infrastructure;

namespace ProviderPriceSwitcher.App;

public partial class App : System.Windows.Application
{
    private HttpClient? _httpClient;
    private IUserNotificationService? _notifications;
    private ILoggerFactory? _loggerFactory;
    private static readonly Action<ILogger, Exception?> LogApplicationStarted = LoggerMessage.Define(LogLevel.Information, new EventId(100, "ApplicationStarted"), "ProviderPriceSwitcher started");
    private static readonly Action<ILogger, string, Exception?> LogApplicationFailure = LoggerMessage.Define<string>(LogLevel.Error, new EventId(101, "ApplicationFailure"), "Application failure: {FailureKind}");

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
            _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new RollingFileLoggerProvider(new RollingFileLoggerOptions(Path.Combine(AppDataPaths.GetRoot(dataRoot), "logs")))));
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
            var siteManagement = new SiteManagementUseCase(settingsRepository, snapshotRepository, credentialStore, inferenceKeyStore, activeRoute);
            var pricingCheck = new PricingCheckUseCase(refreshService, settingsRepository, snapshotRepository);
            var settingsUseCase = new SettingsUseCase(settingsRepository);
            var switchAndStart = new SwitchAndStartUseCase(settingsRepository, new OmpConfigurationService(new OmpConfigurationSwitcher(), new AppPathDefaults()), new OmpProcessLauncher(new OmpProcessService()), _loggerFactory.CreateLogger<SwitchAndStartUseCase>());
            var editorFactory = new SiteEditorDialogFactory((original, localSettings) => new SiteEditorViewModel(new PricingProbeUseCase(adapterRegistry), adapterRegistry, credentialStore, _notifications, localSettings, original, inferenceKeyUseCase));
            var sitesFactory = new SitesDialogFactory((localSettings, currentProvider) => new SitesDialog(localSettings, siteManagement, snapshotQuery, editorFactory, currentProvider));
            var viewModel = new MainViewModel(pricingCheck, settingsUseCase, switchAndStart, currentProviderQuery, snapshotQuery, adapterRegistry, settings, sitesFactory, _notifications, _loggerFactory.CreateLogger<MainViewModel>());
            MainWindow = new MainWindow(viewModel);
            MainWindow.Show();
        }
        catch (Exception)
        {
            var logger = _loggerFactory?.CreateLogger<App>();
            if (logger is not null) LogApplicationFailure(logger, "Startup", null);
            _notifications.ShowError(UserErrorMessages.ApplicationStartupFailed, "ProviderPriceSwitcher");
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _httpClient?.Dispose();
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }
}
