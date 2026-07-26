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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _notifications = new WpfUserNotificationService();
        DispatcherUnhandledException += (_, args) =>
        {
            _notifications.ShowError("发生未处理错误：" + args.Exception.Message, "ProviderPriceSwitcher");
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
            _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new RollingFileLoggerProvider(new RollingFileLoggerOptions(Path.Combine(AppDataPaths.GetRoot(dataRoot), "logs")))));
            LogApplicationStarted(_loggerFactory.CreateLogger<App>(), null);
            var adapterRegistry = new PricingAdapterRegistry([
                new NewApiPricingAdapter(_httpClient),
                new PawsAiPricingAdapter(_httpClient),
                new Sub2ApiPricingAdapter(_httpClient, credentialStore)
            ]);
            var refreshService = new PricingRefreshService(adapterRegistry, snapshotRepository, _loggerFactory.CreateLogger<PricingRefreshService>());
            var viewModel = new MainViewModel(settingsRepository, refreshService, adapterRegistry, new OmpConfigurationSwitcher(), new OmpProcessService(), settings, credentialStore, _notifications);
            MainWindow = new MainWindow(viewModel);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            _notifications.ShowError("应用启动失败：无法加载本地设置或初始化服务。\n" + ex.Message, "ProviderPriceSwitcher");
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
