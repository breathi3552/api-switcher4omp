using System.Net.Http;
using System.Windows;
using ProviderPriceSwitcher.Adapters;
using ProviderPriceSwitcher.Infrastructure;

namespace ProviderPriceSwitcher.App;

public partial class App : Application
{
    private HttpClient? _httpClient;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var settingsRepository = new JsonSettingsRepository();
            var settings = settingsRepository.Load();
            _httpClient = new HttpClient();
            var snapshotRepository = new JsonPricingSnapshotRepository();
            var credentialStore = new WindowsSiteCredentialStore();
            var refreshService = new PricingRefreshService([
                new NewApiPricingAdapter(_httpClient),
                new PawsAiPricingAdapter(_httpClient),
                new Sub2ApiPricingAdapter(_httpClient, credentialStore)
            ], snapshotRepository);
            var viewModel = new MainViewModel(settingsRepository, refreshService, new OmpConfigurationSwitcher(), new OmpProcessService(), settings);
            MainWindow = new MainWindow(viewModel);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("应用启动失败：无法加载本地设置或初始化服务。\n" + ex.Message, "ProviderPriceSwitcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _httpClient?.Dispose();
        base.OnExit(e);
    }
}
