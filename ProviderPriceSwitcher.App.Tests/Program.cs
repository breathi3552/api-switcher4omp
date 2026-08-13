using ProviderPriceSwitcher.App;
using System.Windows.Input;

static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
Assert(MainWindow.BuildKeysUri(new Uri("https://example.test/root///?old=query#old"), "/console/keys")?.AbsoluteUri == "https://example.test/root/console/keys", "config API URI must append configured path and clear query/fragment");
Assert(MainWindow.BuildKeysUri(new Uri("https://example.test/root?old=query#old"), null)?.AbsoluteUri == "https://example.test/root/keys", "missing config API URI must default to /keys");
Assert(MainWindow.BuildKeysUri(new Uri("ftp://example.test/root"), "/keys") is null, "non-http config API URI must not be launchable");
var errors = new List<Exception>();
var command = new AsyncCommand(() => throw new InvalidOperationException("boom"), errors.Add);
command.Execute(null);
await Task.Delay(100);
Assert(errors.Count == 1 && errors[0].Message == "boom" && command.CanExecute(null), "error must notify once and restore CanExecute");
errors.Clear();
var canceled = new AsyncCommand(() => Task.FromCanceled(new CancellationToken(true)), ex => { if (ex is not OperationCanceledException) errors.Add(ex); });
canceled.Execute(null);
await Task.Delay(100);
Assert(errors.Count == 0 && canceled.CanExecute(null), "cancellation must not notify and must restore CanExecute");
const string syntheticFailure = "synthetic-token-DO-NOT-LOG https://example.invalid/prices?api_key=synthetic-query-secret&token=synthetic-token&cookie=synthetic-cookie C:\\Users\\Private\\Documents\\secret";
var publicMessages = Enum.GetValues<ProviderPriceSwitcher.Application.PricingRefreshFailureKind>().Select(kind => UserErrorMessages.ForPricingFailure(kind))
    .Concat(Enum.GetValues<ProviderPriceSwitcher.Application.PricingAdapterFailure>().Select(UserErrorMessages.ForProbeFailure))
    .Append(UserErrorMessages.Unexpected)
    .ToArray();
Assert(publicMessages.All(message => !message.Contains(syntheticFailure, StringComparison.Ordinal) && !message.Contains("api_key", StringComparison.OrdinalIgnoreCase) && !message.Contains("Cookie", StringComparison.OrdinalIgnoreCase) && !message.Contains("Authorization", StringComparison.OrdinalIgnoreCase) && !message.Contains("已验证请求指纹", StringComparison.Ordinal)), "public error mapping must remain fixed and non-sensitive");
Assert(UserErrorMessages.ForPricingFailure(ProviderPriceSwitcher.Application.PricingRefreshFailureKind.Timeout) == "请求超时，请稍后重试。" && UserErrorMessages.ForPricingFailure(ProviderPriceSwitcher.Application.PricingRefreshFailureKind.Authentication) == "需要重新绑定凭据。", "pricing failure mapping mismatch");
var startupSettings = new ProviderPriceSwitcher.Application.LocalAppSettings
{
    Sites =
    [
        new ProviderPriceSwitcher.Core.SiteConfiguration { ProviderId = "healthy", ConfigurationKey = "k", BaseUrl = new Uri("https://healthy.example"), Model = "model", CurrentGroup = "group" },
        new ProviderPriceSwitcher.Core.SiteConfiguration { ProviderId = "corrupt", ConfigurationKey = "k", BaseUrl = new Uri("https://corrupt.example"), Model = "model", CurrentGroup = "group" }
    ]
};
var startupKeyStore = new StartupKeyStore();
var startupResolver = new ProviderPriceSwitcher.Application.InferenceApiKeyResolverBridge(startupKeyStore);
App.RegisterAvailableInferenceKeys(startupSettings, startupKeyStore, startupResolver, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
Assert(await startupResolver.ResolveAsync("healthy-handle") == "healthy-secret", "one corrupt inference key must not prevent healthy keys or application startup");
var recoveryAttempts = 0;
var recoveryFailures = new List<string>();
await App.RecoverGatewayWithRetryAsync(
    _ => Task.FromResult(++recoveryAttempts == 1
        ? new ProviderPriceSwitcher.Application.GatewayRecoveryOutcome(
            ProviderPriceSwitcher.Application.GatewayRecoveryStatus.Failed,
            FailureKind: ProviderPriceSwitcher.Application.GatewayRecoveryFailureKind.GatewayUnavailable)
        : new ProviderPriceSwitcher.Application.GatewayRecoveryOutcome(ProviderPriceSwitcher.Application.GatewayRecoveryStatus.Recovered)),
    outcome => recoveryFailures.Add($"{outcome.Status}:{outcome.FailureKind}"),
    CancellationToken.None);
Assert(recoveryAttempts == 2 && recoveryFailures.SequenceEqual(["Failed:GatewayUnavailable"]), "gateway recovery must retry a transient failure until the gateway is restored");
var deterministicRecoveryAttempts = 0;
await App.RecoverGatewayWithRetryAsync(
    _ =>
    {
        deterministicRecoveryAttempts++;
        return Task.FromResult(new ProviderPriceSwitcher.Application.GatewayRecoveryOutcome(
            ProviderPriceSwitcher.Application.GatewayRecoveryStatus.Failed,
            FailureKind: ProviderPriceSwitcher.Application.GatewayRecoveryFailureKind.ExecutableUnavailable));
    },
    _ => { },
    CancellationToken.None);
Assert(deterministicRecoveryAttempts == 1, "deterministic gateway recovery failures must not be retried indefinitely");
using var transientRecoveryCancellation = new CancellationTokenSource();
var transientRecoveryAttempts = 0;
try
{
    await App.RecoverGatewayWithRetryAsync(
        _ =>
        {
            transientRecoveryAttempts++;
            transientRecoveryCancellation.Cancel();
            return Task.FromResult(new ProviderPriceSwitcher.Application.GatewayRecoveryOutcome(
                ProviderPriceSwitcher.Application.GatewayRecoveryStatus.Failed,
                FailureKind: ProviderPriceSwitcher.Application.GatewayRecoveryFailureKind.GatewayUnavailable));
        },
        _ => { },
        transientRecoveryCancellation.Token);
    throw new InvalidOperationException("transient recovery did not remain cancellable");
}
catch (OperationCanceledException)
{
}
Assert(transientRecoveryAttempts == 1, "transient gateway recovery must continue until cancellation rather than silently stopping after a bounded retry window");
using var recoveryCancellation = new CancellationTokenSource();
recoveryCancellation.Cancel();
var canceledRecoveryAttempts = 0;
try
{
    await App.RecoverGatewayWithRetryAsync(
        _ =>
        {
            canceledRecoveryAttempts++;
            return Task.FromResult(new ProviderPriceSwitcher.Application.GatewayRecoveryOutcome(ProviderPriceSwitcher.Application.GatewayRecoveryStatus.Recovered));
        },
        _ => { },
        recoveryCancellation.Token);
    throw new InvalidOperationException("canceled gateway recovery was accepted");
}
catch (OperationCanceledException)
{
}
Assert(canceledRecoveryAttempts == 0, "canceled gateway recovery must not start another attempt");
Assert(UserErrorMessages.ForOmpLaunchStatus(ProviderPriceSwitcher.Application.OmpLaunchStatus.Started).Contains("活动供应商未改变", StringComparison.Ordinal), "launch status mapping mismatch");
var launchFailureMapping = new ProviderPriceSwitcher.Application.OmpLaunchOutcome(
    ProviderPriceSwitcher.Application.OmpLaunchStatus.SettingsPersistenceFailed,
    new ProviderPriceSwitcher.Application.LocalAppSettings());
Assert(UserErrorMessages.ForOmpLaunchStatus(launchFailureMapping).Contains("无法保存工作目录", StringComparison.Ordinal), "launch persistence failure mapping mismatch");
var probeAdapter = new FakeAdapter(new("two", "Two", true, ["令牌", "账户"]));
var registry = new ProviderPriceSwitcher.Application.PricingAdapterRegistry([
    new FakeAdapter(new("one", "One", false, ["无"])),
    probeAdapter,
    new FakeAdapter(new("three", "Three", false, ["匿名"]))
]);
var testSettings = new ProviderPriceSwitcher.Application.LocalAppSettings { Model = "model", RequestTimeoutSeconds = 1, Sites = [] };
var testCredentials = new FakeCredentialStore();
var testNotifications = new FakeNotifications();
var testProbe = new ProviderPriceSwitcher.Application.PricingProbeUseCase(registry);
var editor = new SiteEditorViewModel(testProbe, registry, testCredentials, testNotifications, testSettings);
Assert(editor.Descriptors.Count == 3 && editor.AuthenticationMode == "无" && !editor.CredentialVisible, "descriptor initialization mismatch");
editor.Descriptor = editor.Descriptors[1];
Assert(editor.AuthenticationMode == "令牌" && editor.CredentialVisible, "descriptor switch must reset auth and credential visibility");
Assert(editor.CanSave == false, "invalid draft must not save");

Exception? windowFailure = null;
var windowThread = new Thread(() =>
{
    System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
    var root = Path.Combine(Path.GetTempPath(), $"ProviderPriceSwitcher-AppTests-{Guid.NewGuid():N}");
    try
    {
        var application = new System.Windows.Application();
        application.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/ProviderPriceSwitcher.App;component/Styles.xaml", UriKind.Relative) });
        var settingsRepository = new ProviderPriceSwitcher.Infrastructure.JsonSettingsRepository(root);
        var snapshots = new ProviderPriceSwitcher.Infrastructure.JsonPricingSnapshotRepository(root);
        var refresh = new ProviderPriceSwitcher.Application.PricingRefreshService(registry, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderPriceSwitcher.Application.PricingRefreshService>.Instance);
        var settings = settingsRepository.Load();
        settings = settings with { GatewayPort = 16222, CurrentGatewayPort = 15722, OmpRootDirectory = root };
        settings = settings with
        {
            OmpWorkingDirectories = [root, Path.Combine(root, "other-working")],
            LastOmpWorkingDirectory = Path.Combine(root, "other-working"),
            Sites =
            [
                new ProviderPriceSwitcher.Core.SiteConfiguration
                {
                    ProviderId = "healthy",
                    ConfigurationKey = "healthy-key",
                    BaseUrl = new Uri("https://healthy.example"),
                    Model = "model",
                    CurrentGroup = "group"
                }
            ]
        };
        settingsRepository.Save(settings);
        Directory.CreateDirectory(Path.Combine(root, "agent"));
        File.WriteAllText(Path.Combine(root, "agent", "config.yml"), "modelRoles:\r\n  default: another-provider/gpt-5\r\n  deepseek: deepseek/deepseek-chat\r\n");
        File.WriteAllText(Path.Combine(root, "agent", "models.yml"), "providers:\r\n  existing:\r\n    baseUrl: https://existing.example/v1\r\n");
        var credentialStore = new FakeCredentialStore();
        var notifications = new FakeNotifications();
        var sidecarStatus = new FakeSidecarStatus();
        var snapshotQuery = new ProviderPriceSwitcher.Infrastructure.PricingSnapshotQuery(snapshots);
        var activeRoute = new ProviderPriceSwitcher.Application.ActiveRouteState();
        var routeController = new FakeRouteController();
        var keyStore = new StartupKeyStore();
        var applyActiveRoute = new ProviderPriceSwitcher.Application.ApplyActiveRouteUseCase(settingsRepository, routeController, keyStore, activeRoute);
        var siteManagement = new ProviderPriceSwitcher.Application.SiteManagementUseCase(settingsRepository, snapshots);
        var pricingCheck = new ProviderPriceSwitcher.Application.PricingCheckUseCase(refresh, settingsRepository, snapshots);
        var settingsUseCase = new ProviderPriceSwitcher.Application.SettingsUseCase(settingsRepository);
        var fakeInferenceKeys = new FakeInferenceApiKeyUseCase();
        var editorFactory = new SiteEditorDialogFactory((original, localSettings) => new SiteEditorViewModel(new ProviderPriceSwitcher.Application.PricingProbeUseCase(registry), registry, credentialStore, notifications, localSettings, original, fakeInferenceKeys));
        var fakeOmpLauncher = new FakeOmpLauncher();
        var ompLaunch = new ProviderPriceSwitcher.Application.OmpLaunchUseCase(
            settingsRepository,
            fakeOmpLauncher,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderPriceSwitcher.Application.OmpLaunchUseCase>.Instance);
        var sitesFactory = new SitesDialogFactory((localSettings, currentProvider) => new SitesDialog(localSettings, siteManagement, snapshotQuery, editorFactory, currentProvider, notifications));
        var ompReplacement = new ProviderPriceSwitcher.Application.OmpConfigurationReplacementUseCase(
            new ProviderPriceSwitcher.Infrastructure.OmpConfigurationService(
                new ProviderPriceSwitcher.Infrastructure.OmpConfigurationSwitcher(),
                new ProviderPriceSwitcher.Infrastructure.AppPathDefaults()));
        var viewModel = new MainViewModel(pricingCheck, settingsUseCase, applyActiveRoute, ompLaunch, activeRoute, snapshotQuery, settings, sitesFactory, notifications, Microsoft.Extensions.Logging.Abstractions.NullLogger<MainViewModel>.Instance, sidecarStatus: sidecarStatus, ompReplacement: ompReplacement);
        var window = new MainWindow(viewModel);
        window.Show();
        activeRoute.Apply(new ProviderPriceSwitcher.Core.RouteSnapshot("active", "https://active.example", "active-handle"));
        var activeBeforeLaunch = activeRoute.Current;
        viewModel.StartOmpCommand.Execute(null);
        WaitFor(() => viewModel.StatusText.Contains("活动供应商未改变", StringComparison.Ordinal));
        Assert(fakeOmpLauncher.Calls == 1
            && fakeOmpLauncher.LastRequest?.WorkingDirectory == Path.Combine(root, "other-working")
            && !viewModel.IsStartingOmp
            && activeRoute.Current == activeBeforeLaunch,
            "Start OMP must remain independent from configuration replacement and preserve the active supplier.");

        Assert(viewModel.OmpWorkingDirectoryChoices.SequenceEqual([root, Path.Combine(root, "other-working")])
            && viewModel.SelectedOmpWorkingDirectory == Path.Combine(root, "other-working")
            && window.FindName("OmpWorkingDirectoryBox") is System.Windows.Controls.ComboBox
            && window.FindName("OmpConfigurationTargetBox") is System.Windows.Controls.ComboBox
            && window.FindName("OmpConfigurationStatusDot") is System.Windows.Shapes.Ellipse statusDot
            && window.FindName("GatewayStatusDot") is System.Windows.Shapes.Ellipse gatewayDot
            && window.FindName("RouteStatusDot") is System.Windows.Shapes.Ellipse routeDot
            && Equals(statusDot.Fill, System.Windows.Media.Brushes.DarkOrange)
            && Equals(gatewayDot.Fill, System.Windows.Media.Brushes.SeaGreen)
            && Equals(routeDot.Fill, System.Windows.Media.Brushes.SeaGreen)
            && viewModel.ActiveRouteStatusText == "活动路由已应用",
            "main page must expose the independent OMP replacement target and distinct lifecycle status points");

        var configBeforeCancel = File.ReadAllText(Path.Combine(root, "agent", "config.yml"));
        var modelsBeforeCancel = File.ReadAllText(Path.Combine(root, "agent", "models.yml"));
        notifications.ConfirmResult = false;
        viewModel.SelectedOmpConfigurationTarget = viewModel.OmpConfigurationTargetChoices.First(choice => choice.ProviderId == "provider-price-switcher");
        viewModel.ReplaceOmpGptProviderCommand.Execute(null);
        WaitFor(() => viewModel.StatusText.Contains("已取消 OMP 配置替换", StringComparison.Ordinal));
        Assert(File.ReadAllText(Path.Combine(root, "agent", "config.yml")) == configBeforeCancel
            && File.ReadAllText(Path.Combine(root, "agent", "models.yml")) == modelsBeforeCancel
            && activeRoute.Current == activeBeforeLaunch
            && fakeOmpLauncher.Calls == 1,
            "cancelling the OMP preview must not write files, change routes or launch OMP");

        notifications.ConfirmResult = true;
        viewModel.ReplaceOmpGptProviderCommand.Execute(null);
        WaitFor(() => viewModel.StatusText.Contains("手动重启", StringComparison.Ordinal));
        Assert(viewModel.StatusText.Contains("手动重启", StringComparison.Ordinal)
            && viewModel.OmpConfigurationStatus == "配置已替换"
            && activeRoute.Current == activeBeforeLaunch
            && fakeOmpLauncher.Calls == 1
            && notifications.LastConfirmMessage?.Contains("another-provider -> provider-price-switcher", StringComparison.Ordinal) == true,
            "confirmed replacement must show a preview, write OMP files and preserve process/active-route state");

        var modelsAfterLocal = File.ReadAllText(Path.Combine(root, "agent", "models.yml"));
        viewModel.SelectedOmpConfigurationTarget = viewModel.OmpConfigurationTargetChoices.First(choice => choice.ProviderId == "openai-codex");
        viewModel.ReplaceOmpGptProviderCommand.Execute(null);
        WaitFor(() => viewModel.StatusText.Contains("手动重启", StringComparison.Ordinal));
        Assert(File.ReadAllText(Path.Combine(root, "agent", "models.yml")) == modelsAfterLocal
            && activeRoute.Current == activeBeforeLaunch
            && fakeOmpLauncher.Calls == 1,
            "official OAuth replacement must not touch models.yml or independent runtime state");

        using var realTray = new WindowsTrayHost();
        realTray.Update(new TrayStatus("网关运行中", "活动路由已应用"));
        sidecarStatus.Set(new ProviderPriceSwitcher.Application.SidecarStatus(ProviderPriceSwitcher.Application.SidecarConnectionStatus.Disconnected, "synthetic-disconnect"));
        WaitFor(() => Equals(viewModel.GatewayStatusBrush, System.Windows.Media.Brushes.IndianRed));
        Assert(Equals(((System.Windows.Shapes.Ellipse)window.FindName("GatewayStatusDot")).Fill, System.Windows.Media.Brushes.IndianRed), "gateway status dot must follow the sidecar lifecycle status");
        viewModel.SelectedProvider = new ProviderChoice("healthy");
        routeController.ApplyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.ApplyRouteCommand.Execute(null);
        WaitFor(() => viewModel.IsApplyingRoute);
        var launchesBeforeIndependentStart = fakeOmpLauncher.Calls;
        viewModel.StartOmpCommand.Execute(null);
        WaitFor(() => fakeOmpLauncher.Calls == launchesBeforeIndependentStart + 1 && !viewModel.IsStartingOmp);
        Assert(viewModel.IsApplyingRoute && !viewModel.IsStartingOmp, "application and OMP start commands must keep independent busy states");
        routeController.ApplyGate.SetResult();
        WaitFor(() => !viewModel.IsApplyingRoute);
        Assert(activeRoute.CurrentProviderId == "healthy", "application command must commit the selected target after its independent busy period");
        activeRoute.Apply(activeBeforeLaunch!);

        var site = new ProviderPriceSwitcher.Core.SiteConfiguration
        {
            ProviderId = "synthetic-provider",
            ConfigurationKey = "synthetic-key",
            BaseUrl = new Uri("https://example.test/root///?old=query#old"),
            ConfigurationApiAddress = "/console/keys",
            SiteType = "two",
            Model = "model",
            CurrentGroup = "group",
            CurrentGroupRatio = 1m,
            AuthenticationMode = "令牌"
        };
        settings = settings with { Sites = [site] };
        var pricingSettings = settings with { Sites = [site with { SiteType = "aihub", BaseUrl = new Uri("https://example.test/") }] };
        settingsRepository.Save(pricingSettings);
        var pricingHandler = new CurrentRatioHandler();
        using var pricingClient = new HttpClient(pricingHandler);
        var pricingRegistry = new ProviderPriceSwitcher.Application.PricingAdapterRegistry([
            new ProviderPriceSwitcher.Adapters.AiHubPricingAdapter(pricingClient, new PricingCredentialStore())
        ]);
        var pricingRefresh = new ProviderPriceSwitcher.Application.PricingRefreshService(pricingRegistry, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderPriceSwitcher.Application.PricingRefreshService>.Instance);
        var currentRatioViewModel = new MainViewModel(
            new ProviderPriceSwitcher.Application.PricingCheckUseCase(pricingRefresh, settingsRepository, snapshots),
            settingsUseCase,
            applyActiveRoute,
            ompLaunch,
            activeRoute,
            snapshotQuery,
            pricingSettings,
            sitesFactory,
            notifications,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MainViewModel>.Instance);
        currentRatioViewModel.CheckCommand.Execute(null);
        WaitFor(() => currentRatioViewModel.Rows.Count > 0);
        Assert(currentRatioViewModel.RecommendedProvider == "synthetic-provider"
            && currentRatioViewModel.SelectedProvider?.ProviderId == "synthetic-provider"
            && activeRoute.CurrentProviderId == "active",
            "a completed price check must select the recommendation as pending target while leaving the active provider unchanged");
        var currentRatioRow = currentRatioViewModel.Rows.Single();
        var persistedSite = settingsRepository.Load().Sites.Single();
        Assert(currentRatioRow.Ratio == "0.1", $"home price check must render the latest successful current-group ratio; actual ratio/status/issue: {currentRatioRow.Ratio}/{currentRatioRow.Status}/{currentRatioRow.Issue}");
        Assert(currentRatioRow.InputPrice == "0.5", $"home price check must price with the latest successful current-group ratio; actual input price: {currentRatioRow.InputPrice}");
        Assert(currentRatioRow.Group == "group [当前][最低]", $"home price check must keep one row when the current group is also minimum; actual group: {currentRatioRow.Group}");
        Assert(persistedSite.CurrentGroupRatio == 0.1m && persistedSite.GroupRatioSource == "自动", "home price check must persist the latest successful current-group ratio");
        pricingHandler.Fail = true;
        currentRatioViewModel.CheckCommand.Execute(null);
        WaitFor(() => pricingHandler.RequestCount >= 4 && currentRatioViewModel.CheckCommand.CanExecute(null));
        Assert(currentRatioViewModel.Rows.Single().Ratio == "0.1" && settingsRepository.Load().Sites.Single().CurrentGroupRatio == 0.1m, "failed home price check must preserve the last successful current-group ratio");
        pricingHandler.Fail = false;
        pricingHandler.OmitCurrentGroup = true;
        currentRatioViewModel.CheckCommand.Execute(null);
        WaitFor(() => pricingHandler.RequestCount >= 6 && currentRatioViewModel.CheckCommand.CanExecute(null));
        Assert(currentRatioViewModel.Rows.Single().Ratio == "0.1" && settingsRepository.Load().Sites.Single().CurrentGroupRatio == 0.1m, "home price check missing the current group must preserve the last successful ratio");
        var projectionRoot = Path.Combine(root, "projection");
        var projectionSettingsRepository = new ProviderPriceSwitcher.Infrastructure.JsonSettingsRepository(projectionRoot);
        var projectionSnapshots = new ProviderPriceSwitcher.Infrastructure.JsonPricingSnapshotRepository(projectionRoot);
        var projectionSite = new ProviderPriceSwitcher.Core.SiteConfiguration
        {
            ProviderId = "projection-provider",
            ConfigurationKey = "projection-key",
            BaseUrl = new Uri("https://projection.example"),
            SiteType = "projection",
            Model = "model",
            CurrentGroup = "group",
            CurrentGroupRatio = 1m
        };
        var projectionSettings = new ProviderPriceSwitcher.Application.LocalAppSettings { Sites = [projectionSite] };
        projectionSettingsRepository.Save(projectionSettings);
        var projectionAdapter = new FakeAdapter(new("projection", "Projection", false, ["无"]))
        {
            ReturnedGroupRatios = new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                ["group"] = 0.2m,
                ["minimum"] = 0.05m,
                ["third"] = 0.1m
            }
        };
        var projectionRegistry = new ProviderPriceSwitcher.Application.PricingAdapterRegistry([projectionAdapter]);
        var projectionRefresh = new ProviderPriceSwitcher.Application.PricingRefreshService(projectionRegistry, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderPriceSwitcher.Application.PricingRefreshService>.Instance);
        var projectionViewModel = new MainViewModel(
            new ProviderPriceSwitcher.Application.PricingCheckUseCase(projectionRefresh, projectionSettingsRepository, projectionSnapshots),
            new ProviderPriceSwitcher.Application.SettingsUseCase(projectionSettingsRepository),
            new ProviderPriceSwitcher.Application.ApplyActiveRouteUseCase(projectionSettingsRepository, routeController, keyStore, activeRoute),
            ompLaunch,
            activeRoute,
            new ProviderPriceSwitcher.Infrastructure.PricingSnapshotQuery(projectionSnapshots),
            projectionSettings,
            sitesFactory,
            notifications,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MainViewModel>.Instance);
        projectionViewModel.CheckCommand.Execute(null);
        WaitFor(() => projectionViewModel.Rows.Count > 0 && projectionViewModel.CheckCommand.CanExecute(null));
        var projectionRows = projectionViewModel.Rows.ToArray();
        var projectionCurrent = projectionRows.Single(row => row.Group == "group [当前]");
        var projectionMinimum = projectionRows.Single(row => row.Group == "minimum [最低]");
        Assert(projectionRows.Length == 2
            && projectionRows.All(row => !row.Group.Contains("third", StringComparison.Ordinal))
            && projectionCurrent.IsSiteFirstRow && projectionCurrent.KeysUri is not null
            && !projectionMinimum.IsSiteFirstRow && projectionMinimum.KeysUri is null,
            "price table must project only current and minimum rows, leaving the minimum row read-only and hiding other groups");


        var launcher = new FakeUriLauncher();
        var navigationWindow = new MainWindow(viewModel, launcher);
        navigationWindow.Show();
        navigationWindow.UpdateLayout();
        var grid = FindDescendant<System.Windows.Controls.DataGrid>(navigationWindow);
        var firstRow = new PriceRow(site.ProviderId, "成功", "group", 1m, null, ProviderPriceSwitcher.Application.LocalAppSettings.DefaultUsageProfile, "—", "手动", null, string.Empty, false, false, true, site.BaseUrl, site.ConfigurationApiAddress);
        grid.ItemsSource = new[] { firstRow };
        grid.SelectedItem = firstRow;
        grid.UpdateLayout();
        var firstRowVisual = (System.Windows.Controls.DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(firstRow) ?? throw new InvalidOperationException("first row visual was not generated");
        var launchHit = typeof(MainWindow).GetMethod("TryLaunchPriceRow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert((bool)launchHit.Invoke(navigationWindow, [grid, firstRowVisual, System.Windows.Input.MouseButton.Left])!, "actual first site row left double-click must be handled");
        Assert(launcher.Calls == 1 && launcher.Last?.AbsoluteUri == "https://example.test/root/console/keys", "actual first site row left double-click must launch configured same-origin keys URI exactly once");
        Assert(!(bool)launchHit.Invoke(navigationWindow, [grid, grid, System.Windows.Input.MouseButton.Left])!, "header or blank double-click must not be handled");
        Assert(launcher.Calls == 1, "header or blank double-click must not launch the previously selected row");
        Assert(!(bool)launchHit.Invoke(navigationWindow, [grid, firstRowVisual, System.Windows.Input.MouseButton.Right])!, "right double-click must not be handled");
        Assert(launcher.Calls == 1, "right double-click must not launch");
        var minimumRow = new PriceRow(site.ProviderId, "成功", "minimum [最低]", 1m, null, ProviderPriceSwitcher.Application.LocalAppSettings.DefaultUsageProfile, "—", "自动", null, string.Empty, false, false);
        grid.ItemsSource = new[] { minimumRow };
        grid.SelectedItem = minimumRow;
        grid.UpdateLayout();
        var minimumRowVisual = (System.Windows.Controls.DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(minimumRow) ?? throw new InvalidOperationException("minimum row visual was not generated");
        Assert(!(bool)launchHit.Invoke(navigationWindow, [grid, minimumRowVisual, System.Windows.Input.MouseButton.Left])!, "minimum group row double-click must not be handled");
        Assert(launcher.Calls == 1, "minimum group row must not launch");
        navigationWindow.Close();

        var editorFactoryForDialog = new SiteEditorDialogFactory((original, localSettings) => new SiteEditorViewModel(new ProviderPriceSwitcher.Application.PricingProbeUseCase(registry), registry, credentialStore, notifications, localSettings, original, fakeInferenceKeys));
        var cascadeKeys = new CascadeInferenceKeyStore();
        var cascadeSettings = settings with { ActiveProviderId = site.ProviderId };
        settingsRepository.Save(cascadeSettings);
        activeRoute.Apply(new ProviderPriceSwitcher.Core.RouteSnapshot(site.ProviderId, site.BaseUrl.ToString(), "cascade-handle"));
        var cascadeSiteManagement = new ProviderPriceSwitcher.Application.SiteManagementUseCase(settingsRepository, snapshots, credentialStore, cascadeKeys, activeRoute, routeController);
        var sitesDialog = new SitesDialog(cascadeSettings, cascadeSiteManagement, snapshotQuery, editorFactoryForDialog, null, notifications);
        sitesDialog.Show();
        sitesDialog.UpdateLayout();
        Assert(ReferenceEquals(GetField<ISiteEditorDialogFactory>(sitesDialog, "_editorFactory"), editorFactoryForDialog), "sites dialog must retain injected editor factory");
        var deleteButton = Descendants(sitesDialog).OfType<System.Windows.Controls.Button>().Single(button => Equals(button.Content, "删除"));
        var confirmCallsBeforeDelete = notifications.ConfirmCalls;
        notifications.ConfirmResult = false;
        deleteButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert(notifications.ConfirmCalls == confirmCallsBeforeDelete + 1 && sitesDialog.Settings.Sites.Count == 1 && cascadeKeys.ClearCalls == 0, "cancelled supplier deletion must preserve settings and credentials");
        notifications.ConfirmResult = true;
        deleteButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        WaitFor(() => cascadeKeys.ClearCalls == 1 && sitesDialog.Settings.Sites.Count == 0);
        Assert(cascadeKeys.ClearCalls == 1 && credentialStore.ClearCalls == 1 && routeController.ClearCount > 0 && activeRoute.CurrentProviderId is null && snapshots.Load(site.ProviderId) is null, "confirmed supplier deletion must clear the snapshot, access credential, inference key and active route");
        sitesDialog.Close();
        activeRoute.Apply(activeBeforeLaunch!);

        fakeInferenceKeys.Summary = new ProviderPriceSwitcher.Core.InferenceApiKeySummary
        {
            ProviderId = site.ProviderId,
            KeyHandle = "old-handle",
            BoundGroup = site.CurrentGroup,
            MaskedKey = "old-…key",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var closeKeyDialog = editorFactoryForDialog.Create(site, settings, window);
        closeKeyDialog.Show();
        closeKeyDialog.UpdateLayout();
        var closeKeyPlaceholder = (System.Windows.Controls.TextBlock)closeKeyDialog.FindName("InferenceKeyPlaceholder");
        var closeKeyBox = (System.Windows.Controls.PasswordBox)closeKeyDialog.FindName("InferenceKeyBox");
        Assert(closeKeyPlaceholder.Text == "old-…key" && closeKeyBox.Password.Length == 0,
            $"opening the editor must show the saved inference-key summary without loading key material; actual text='{closeKeyPlaceholder.Text}', passwordLength={closeKeyBox.Password.Length}, summary='{fakeInferenceKeys.Summary?.MaskedKey}'");
        closeKeyBox.Password = "unsaved-inference-secret";
        closeKeyDialog.Close();
        Assert(fakeInferenceKeys.SaveCalls == 0 && fakeInferenceKeys.DeleteCalls == 0 && fakeInferenceKeys.Summary?.MaskedKey == "old-…key", "closing the editor without update must preserve the existing inference-key summary");

        var dialog = editorFactoryForDialog.Create(site, settings, window);
        dialog.Show(); dialog.UpdateLayout();
        var editorSections = Descendants(dialog).OfType<System.Windows.Controls.GroupBox>().Select(group => group.Header?.ToString()).ToArray();
        Assert(editorSections.SequenceEqual(["基础配置", "价格查询", "模型推理"]), "supplier editor must expose exactly the three agreed sections");
        Assert(dialog.DataContext is SiteEditorViewModel, "editor dialog must bind the production view model");
        var editorVm = (SiteEditorViewModel)dialog.DataContext;
        Assert(editorVm.ProbeCommand.CanExecute(null) && editorVm.SaveCommand.CanExecute(null) && !editorVm.CancelProbeCommand.CanExecute(null), "valid editor fields must enable probe/save and leave cancel disabled");
        Assert(((System.Windows.Controls.ComboBox)dialog.FindName("CurrentGroupBox")).Text == site.CurrentGroup, "opening the editor must backfill the editable current-group ComboBox with the saved binding");
        var tokenBox = (System.Windows.Controls.PasswordBox)dialog.FindName("TokenBox");
        var cookieBox = (System.Windows.Controls.PasswordBox)dialog.FindName("CookieBox");
        tokenBox.Password = "synthetic-token-ui";
        cookieBox.Password = "synthetic-cookie-ui";
        credentialStore.ExpectedToken = tokenBox.Password;
        credentialStore.ExpectedCookie = cookieBox.Password;
        var credentialButton = Descendants(dialog).OfType<System.Windows.Controls.Button>().Single(button => Equals(button.Content, "绑定/更新站点凭据"));
        credentialButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var tokenPlaceholder = (System.Windows.Controls.TextBlock)dialog.FindName("TokenPlaceholder");
        var cookiePlaceholder = (System.Windows.Controls.TextBlock)dialog.FindName("CookiePlaceholder");
        Assert(credentialStore.SaveCalls == 1 && credentialStore.LastSaveMatchedExpectedInput && tokenBox.Password.Length == 0 && cookieBox.Password.Length == 0
            && tokenPlaceholder.Text == "synt********n-ui" && cookiePlaceholder.Text == "cook********e-ui",
            "credential inputs must clear and show storage-generated masked summaries in overlays");
        Assert(credentialStore.LoadCalls == 0 && dialog.FindName("CredentialStatusText") is null, "editor must not load credential material or render a separate credential status line");
        credentialStore.AccessTokenSummaryOverride = "********";
        credentialStore.CookieSummaryOverride = "********";
        editorVm.UpdateCredentialStatus();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert(tokenBox.Password.Length == 0 && cookieBox.Password.Length == 0 && tokenPlaceholder.Text == "********" && cookiePlaceholder.Text == "********", "short credential summaries must use the fixed mask in both empty overlays");
        probeAdapter.Block = true;
        editorVm.ProbeCommand.Execute(null);
        Assert(editorVm.IsProbing && !editorVm.ProbeCommand.CanExecute(null) && !editorVm.SaveCommand.CanExecute(null) && editorVm.CancelProbeCommand.CanExecute(null), "probe must become busy and prevent reentry while enabling cancel");
        editorVm.CurrentGroup = "edited-during-probe";
        editorVm.ProbeCommand.Execute(null);
        Assert(probeAdapter.FetchCalls == 1, "busy probe command must reject reentry");
        editorVm.CancelProbeCommand.Execute(null);
        WaitFor(() => !editorVm.IsProbing);
        WaitFor(() => editorVm.ProbeCommand.CanExecute(null));
        Assert(editorVm.ProbeState == SiteEditorProbeState.Canceled && editorVm.CurrentGroup == "edited-during-probe" && editorVm.CurrentGroupRatio == "1" && editorVm.ProbeCommand.CanExecute(null) && editorVm.SaveCommand.CanExecute(null) && !editorVm.CancelProbeCommand.CanExecute(null) && notifications.ErrorCalls == 0, "canceled probe must preserve group and ratio edits and restore commands without an error notification");
        probeAdapter.Block = false;
        probeAdapter.ReturnedFailure = ProviderPriceSwitcher.Application.PricingAdapterFailure.Request;
        editorVm.ProbeCommand.Execute(null);
        WaitFor(() => probeAdapter.FetchCalls == 2);
        WaitFor(() => !editorVm.IsProbing);
        Assert(editorVm.ProbeState == SiteEditorProbeState.Failed && editorVm.CurrentGroupRatio == "1", "failed probe must preserve the previous current-group ratio");
        probeAdapter.ReturnedFailure = null;
        probeAdapter.ReturnedGroupRatios = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["different-group"] = 0.05m };
        editorVm.ProbeCommand.Execute(null);
        WaitFor(() => probeAdapter.FetchCalls == 3);
        WaitFor(() => !editorVm.IsProbing);
        Assert(editorVm.ProbeState == SiteEditorProbeState.Succeeded && editorVm.CurrentGroupRatio == "1", "successful probe missing the current group must preserve the previous ratio");
        probeAdapter.ReturnedGroupRatios = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["edited-during-probe"] = 0.2m,
            ["different-group"] = 0.05m
        };
        var currentGroupBox = (System.Windows.Controls.ComboBox)dialog.FindName("CurrentGroupBox");
        Assert(currentGroupBox.IsEditable && dialog.FindName("GroupOptionsBox") is null, "current group must use one editable ComboBox for both manual input and candidates");
        var probeButton = Descendants(dialog).OfType<System.Windows.Controls.Button>().Single(button => Equals(button.Content, "测试价格查询"));
        currentGroupBox.Focus();
        currentGroupBox.Text = "edited-during-probe";
        Assert(editorVm.CurrentGroup == "edited-during-probe" && currentGroupBox.Text == "edited-during-probe", "editable current group ComboBox must update before clicking probe");
        var groupMissingDuringRefresh = false;
        editorVm.GroupOptions.CollectionChanged += (_, _) => groupMissingDuringRefresh |= !editorVm.GroupOptions.Contains("edited-during-probe", StringComparer.OrdinalIgnoreCase);
        probeButton.Focus();
        probeButton.Command.Execute(probeButton.CommandParameter);
        WaitFor(() => probeAdapter.FetchCalls == 4);
        WaitFor(() => !editorVm.IsProbing);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert(editorVm.ProbeState == SiteEditorProbeState.Succeeded && probeAdapter.FetchCalls == 4 && !groupMissingDuringRefresh && editorVm.CurrentGroup == "edited-during-probe" && currentGroupBox.Text == "edited-during-probe", "price probe must preserve the editable current group after focus moves to the probe button");
        Assert(editorVm.CurrentGroupRatio == "0.2", $"successful probe must replace the current group's stale ratio; actual ratio: {editorVm.CurrentGroupRatio}");
        currentGroupBox.Focus();
        currentGroupBox.SelectedItem = "different-group";
        Assert(editorVm.CurrentGroup == "different-group" && currentGroupBox.Text == "different-group" && editorVm.CurrentGroupRatio == "0.05", "explicit candidate selection must update the editable current group and its latest ratio");

        var inferenceBox = (System.Windows.Controls.PasswordBox)dialog.FindName("InferenceKeyBox");
        var inferencePlaceholder = (System.Windows.Controls.TextBlock)dialog.FindName("InferenceKeyPlaceholder");
        var updateInferenceButton = Descendants(dialog).OfType<System.Windows.Controls.Button>().Single(button => Equals(button.Content, "更新 API key"));
        inferenceBox.Password = "new-inference-secret";
        updateInferenceButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert(fakeInferenceKeys.SaveCalls == 1 && inferenceBox.Password.Length == 0 && inferencePlaceholder.Text == "new-…cret", "inference key update must clear input and show only its safe summary");
        inferenceBox.Password = string.Empty;
        updateInferenceButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert(fakeInferenceKeys.SaveCalls == 1 && fakeInferenceKeys.Summary?.MaskedKey == "new-…cret", "empty inference key update must preserve the existing key");
        notifications.ConfirmResult = false;
        var deleteInferenceButton = Descendants(dialog).OfType<System.Windows.Controls.Button>().Single(button => Equals(button.Content, "删除 API key"));
        deleteInferenceButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert(fakeInferenceKeys.DeleteCalls == 0 && fakeInferenceKeys.Summary?.MaskedKey == "new-…cret", "cancelled inference key deletion must preserve the existing key");
        notifications.ConfirmResult = true;
        deleteInferenceButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        WaitFor(() => fakeInferenceKeys.DeleteCalls == 1);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert(fakeInferenceKeys.Summary is null && inferencePlaceholder.Text == "未配置", $"confirmed inference key deletion must clear the saved key and summary; summary='{fakeInferenceKeys.Summary?.MaskedKey}', placeholder='{inferencePlaceholder.Text}', deletes={fakeInferenceKeys.DeleteCalls}");
        dialog.Close();

        var saveDialog = editorFactoryForDialog.Create(site, settings, window);
        var saveViewModel = (SiteEditorViewModel)saveDialog.DataContext;
        saveDialog.Dispatcher.BeginInvoke(() => saveViewModel.SaveCommand.Execute(null));
        Assert(saveDialog.ShowDialog() == true && saveViewModel.SavedSite?.ProviderId == "synthetic-provider", "save command must close the modal dialog successfully and expose SavedSite");
        var statusViewModel = new MainViewModel(pricingCheck, settingsUseCase, applyActiveRoute, ompLaunch, activeRoute, snapshotQuery, settings, sitesFactory, notifications, Microsoft.Extensions.Logging.Abstractions.NullLogger<MainViewModel>.Instance);
        statusViewModel.InitializeAsync().GetAwaiter().GetResult();
        var statusWindow = new MainWindow(statusViewModel);
        statusWindow.Show();
        statusWindow.UpdateLayout();
        var statusDot2 = (System.Windows.Shapes.Ellipse)statusWindow.FindName("OmpConfigurationStatusDot");
        Assert(statusViewModel.OmpConfigurationStatus == "可手动替换" && Equals(statusDot2.Fill, System.Windows.Media.Brushes.DarkOrange), "homepage must expose an independent OMP configuration status");
        statusWindow.Close();
        var trayHost = new FakeTrayHost();
        var trayExitRequested = false;
        activeRoute.Apply(new ProviderPriceSwitcher.Core.RouteSnapshot("active", "https://active.example", "active-handle"));
        using var trayController = new TrayApplicationController(window, viewModel, trayHost, notifications, () => trayExitRequested = true);
        sidecarStatus.Set(new ProviderPriceSwitcher.Application.SidecarStatus(ProviderPriceSwitcher.Application.SidecarConnectionStatus.Ready));
        WaitFor(() => trayHost.Status?.GatewayText == "网关运行中" && trayHost.Status?.RouteText == "活动路由已应用");
        activeRoute.ClearIfProvider("active");
        sidecarStatus.Set(new ProviderPriceSwitcher.Application.SidecarStatus(ProviderPriceSwitcher.Application.SidecarConnectionStatus.Disconnected, "synthetic-disconnect"));
        sidecarStatus.Set(new ProviderPriceSwitcher.Application.SidecarStatus(ProviderPriceSwitcher.Application.SidecarConnectionStatus.Ready));
        WaitFor(() => trayHost.Status?.RouteText == "无活动路由");
        activeRoute.Apply(new ProviderPriceSwitcher.Core.RouteSnapshot("active", "https://active.example", "active-handle"));
        sidecarStatus.Set(new ProviderPriceSwitcher.Application.SidecarStatus(ProviderPriceSwitcher.Application.SidecarConnectionStatus.Ready));
        WaitFor(() => trayHost.Status?.GatewayText == "网关运行中" && trayHost.Status?.RouteText == "活动路由已应用");
        window.Close();
        Assert(!window.IsVisible && sidecarStatus.Current.Status == ProviderPriceSwitcher.Application.SidecarConnectionStatus.Ready, "closing the main window must hide it without stopping the gateway");
        sidecarStatus.Set(new ProviderPriceSwitcher.Application.SidecarStatus(ProviderPriceSwitcher.Application.SidecarConnectionStatus.Disconnected, "synthetic-disconnect"));
        WaitFor(() => trayHost.Status?.GatewayText == "网关连接断开" && trayHost.Status?.RouteText == "网关不可用");
        trayHost.Raise(TrayCommand.OpenWindow);
        Assert(window.IsVisible, "tray open command must restore the main window");
        var launchesBeforeTrayStart = fakeOmpLauncher.Calls;
        trayHost.Raise(TrayCommand.StartOmp);
        WaitFor(() => fakeOmpLauncher.Calls == launchesBeforeTrayStart + 1);
        Assert(trayHost.Commands.SequenceEqual([TrayCommand.OpenWindow, TrayCommand.StartOmp]), "tray must expose open and start commands without a provider-switch command");
        notifications.ConfirmResult = false;
        trayHost.Raise(TrayCommand.Exit);
        Assert(!trayExitRequested, "cancelled tray exit must keep the gateway session alive");
        notifications.ConfirmResult = true;
        trayHost.Raise(TrayCommand.Exit);
        Assert(trayExitRequested && notifications.LastConfirmMessage?.Contains("停止本地网关", StringComparison.Ordinal) == true && notifications.LastConfirmMessage.Contains("不会终止", StringComparison.Ordinal), "confirmed tray exit must request shutdown only after explaining that OMP processes remain running");

    }
    catch (Exception ex) { windowFailure = ex; }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
});
windowThread.SetApartmentState(ApartmentState.STA);
windowThread.Start();
windowThread.Join();
if (windowFailure is not null) throw windowFailure;

Console.WriteLine("App contract tests passed.");

static T GetField<T>(object instance, string name) where T : class =>
    (T)(instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(instance)
        ?? throw new InvalidOperationException($"field {name} was not found"));

static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
{
    for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
    {
        var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
        yield return child;
        foreach (var descendant in Descendants(child)) yield return descendant;
    }
}

static T FindDescendant<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject =>
    Descendants(root).OfType<T>().FirstOrDefault() ?? throw new InvalidOperationException($"control {typeof(T).Name} was not found");


static void WaitFor(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(2);
    while (!condition() && DateTime.UtcNow < deadline) System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    Assert(condition(), "timed out waiting for UI operation");
}

sealed class FakeAdapter(ProviderPriceSwitcher.Application.PricingAdapterDescriptor descriptor) : ProviderPriceSwitcher.Application.IPricingAdapter
{
    public ProviderPriceSwitcher.Application.PricingAdapterDescriptor Descriptor { get; } = descriptor;
    public bool Block { get; set; }
    public IReadOnlyDictionary<string, decimal>? ReturnedGroupRatios { get; set; }
    public ProviderPriceSwitcher.Application.PricingAdapterFailure? ReturnedFailure { get; set; }
    public int FetchCalls { get; private set; }
    public async Task<ProviderPriceSwitcher.Application.SitePricingResult> FetchAsync(ProviderPriceSwitcher.Core.SiteConfiguration site, CancellationToken cancellationToken = default)
    {
        FetchCalls++;
        if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (ReturnedFailure is { } failure) throw new ProviderPriceSwitcher.Application.PricingAdapterException(failure, "synthetic failure");
        var groupRatios = ReturnedGroupRatios ?? new Dictionary<string, decimal>(StringComparer.Ordinal) { [site.CurrentGroup] = 1m };
        var currentRatio = groupRatios.GetValueOrDefault(site.CurrentGroup, 1m);
        var minimum = groupRatios.OrderBy(pair => pair.Value).First();
        return new()
        {
            Snapshot = new()
            {
                ProviderId = site.ProviderId,
                ConfigurationKey = site.ConfigurationKey,
                Model = site.Model,
                CurrentGroup = site.CurrentGroup,
                Prices = new() { InputPerMillion = 1, CachedInputPerMillion = 1, OutputPerMillion = 1 },
                CurrentGroupRatio = currentRatio,
                MinimumGroup = minimum.Key,
                MinimumGroupRatio = minimum.Value,
                MinimumGroupPrices = new() { InputPerMillion = 1, CachedInputPerMillion = 1, OutputPerMillion = 1 },
                RefreshedAt = DateTimeOffset.UtcNow
            },
            GroupRatios = groupRatios,
            MinimumValidGroup = minimum.Key,
            MinimumGroupRatio = minimum.Value,
            Warnings = []
        };
    }
}

sealed class CurrentRatioHandler : HttpMessageHandler
{
    private int _requestCount;
    public bool Fail { get; set; }
    public bool OmitCurrentGroup { get; set; }
    public int RequestCount => Volatile.Read(ref _requestCount);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        if (Fail)
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        var body = request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/groups/available" => OmitCurrentGroup
                ? "{\"data\":[{\"id\":1,\"name\":\"other-group\",\"platform\":\"openai\",\"status\":\"active\",\"rate_multiplier\":0.1}]}"
                : "{\"data\":[{\"id\":1,\"name\":\"group\",\"platform\":\"openai\",\"status\":\"active\",\"rate_multiplier\":0.1}]}",
            "/api/v1/groups/rates" => "{\"data\":{\"1\":0.1}}",
            _ => "{}"
        };
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}

sealed class PricingCredentialStore : ProviderPriceSwitcher.Core.ISiteAccessCredentialStore
{
    public ProviderPriceSwitcher.Core.SiteCredentialRecord? LoadCredential(string providerId) => new()
    {
        ProviderId = providerId,
        SiteType = "aihub",
        AuthorizationScheme = "Bearer",
        AccessToken = "synthetic-token",
        CookieHeader = "session=synthetic"
    };
    public void SaveCredential(ProviderPriceSwitcher.Core.SiteCredentialRecord credential) => throw new NotSupportedException();
    public void ClearCredential(string providerId) => throw new NotSupportedException();
    public ProviderPriceSwitcher.Core.SiteCredentialSummary GetSummary(string providerId) => new() { ProviderId = providerId, Status = ProviderPriceSwitcher.Core.SiteCredentialStatus.Available };
}

sealed class CascadeInferenceKeyStore : ProviderPriceSwitcher.Core.IInferenceApiKeyStore
{
    public int ClearCalls { get; private set; }
    public ProviderPriceSwitcher.Core.InferenceApiKeyRecord? Load(string providerId) =>
        providerId == "synthetic-provider"
            ? new ProviderPriceSwitcher.Core.InferenceApiKeyRecord { ProviderId = providerId, KeyHandle = "cascade-handle", ApiKey = "cascade-secret", BoundGroup = "group" }
            : null;
    public void Save(ProviderPriceSwitcher.Core.InferenceApiKeyRecord record) { }
    public void Clear(string providerId) => ClearCalls++;
    public ProviderPriceSwitcher.Core.InferenceApiKeySummary? GetSummary(string providerId) => null;
}

sealed class StartupKeyStore : ProviderPriceSwitcher.Core.IInferenceApiKeyStore
{
    private static readonly ProviderPriceSwitcher.Core.InferenceApiKeyRecord Healthy = new() { ProviderId = "healthy", KeyHandle = "healthy-handle", ApiKey = "healthy-secret", BoundGroup = "group" };
    public ProviderPriceSwitcher.Core.InferenceApiKeyRecord? Load(string providerId) => providerId == "corrupt" ? throw new ProviderPriceSwitcher.Infrastructure.JsonDataException("corrupt.bin", new System.Text.Json.JsonException()) : Healthy;
    public void Save(ProviderPriceSwitcher.Core.InferenceApiKeyRecord record) => throw new NotSupportedException();
    public void Clear(string providerId) => throw new NotSupportedException();
    public ProviderPriceSwitcher.Core.InferenceApiKeySummary? GetSummary(string providerId) => null;
}

sealed class FakeInferenceApiKeyUseCase : ProviderPriceSwitcher.Application.IInferenceApiKeyUseCase
{
    public int SaveCalls { get; private set; }
    public int DeleteCalls { get; private set; }
    public ProviderPriceSwitcher.Core.InferenceApiKeySummary? Summary { get; set; }
    public ProviderPriceSwitcher.Core.InferenceApiKeySummary? GetSummary(string providerId) => Summary;
    public ProviderPriceSwitcher.Core.InferenceApiKeySummary Save(string providerId, string apiKey, string boundGroup)
    {
        SaveCalls++;
        Summary = new ProviderPriceSwitcher.Core.InferenceApiKeySummary
        {
            ProviderId = providerId,
            KeyHandle = "synthetic-handle",
            BoundGroup = boundGroup,
            MaskedKey = "new-…cret",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return Summary;
    }
    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        DeleteCalls++;
        Summary = null;
        return Task.CompletedTask;
    }
}

sealed class FakeRouteController : ProviderPriceSwitcher.Application.IRouteController
{
    public TaskCompletionSource? ApplyGate { get; set; }
    public int ClearCount { get; private set; }
    public async Task ApplyAsync(ProviderPriceSwitcher.Core.RouteSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (ApplyGate is not null)
            await ApplyGate.Task.WaitAsync(cancellationToken);
    }
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ClearCount++;
        return Task.CompletedTask;
    }
}

sealed class FakeSidecarStatus : ProviderPriceSwitcher.Application.ISidecarStatus
{
    public ProviderPriceSwitcher.Application.SidecarStatus Current { get; private set; } =
        new(ProviderPriceSwitcher.Application.SidecarConnectionStatus.Ready);
    public event Action<ProviderPriceSwitcher.Application.SidecarStatus>? Changed;
    public void Set(ProviderPriceSwitcher.Application.SidecarStatus status)
    {
        Current = status;
        Changed?.Invoke(status);
    }
}

sealed class FakeOmpLauncher : ProviderPriceSwitcher.Application.IOmpProcessLauncher
{
    public int Calls { get; private set; }
    public ProviderPriceSwitcher.Application.OmpLaunchRequest? LastRequest { get; private set; }
    public ProviderPriceSwitcher.Application.OmpLaunchResult Launch(ProviderPriceSwitcher.Application.OmpLaunchRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRequest = request;
        return new(true);
    }
}
sealed class FakeTrayHost : ITrayHost
{
    public event Action<TrayCommand>? CommandRequested;
    public TrayStatus? Status { get; private set; }
    public List<TrayCommand> Commands { get; } = [];
    public void Update(TrayStatus status) => Status = status;
    public void Raise(TrayCommand command)
    {
        Commands.Add(command);
        CommandRequested?.Invoke(command);
    }
    public void Dispose() { }
}



sealed class FakeUriLauncher : IExternalUriLauncher
{
    public int Calls { get; private set; }
    public Uri? Last { get; private set; }
    public void Launch(Uri uri) { Calls++; Last = uri; }
}

sealed class FakeNotifications : IUserNotificationService
{
    public int WarningCalls { get; private set; }
    public int ErrorCalls { get; private set; }
    public int ConfirmCalls { get; private set; }
    public string? LastConfirmMessage { get; private set; }
    public bool ConfirmResult { get; set; } = true;
    public void ShowWarning(string message, string title) => WarningCalls++;
    public void ShowError(string message, string title) => ErrorCalls++;
    public bool Confirm(string message, string title)
    {
        ConfirmCalls++;
        LastConfirmMessage = message;
        return ConfirmResult;
    }
}

sealed class FakeCredentialStore : ProviderPriceSwitcher.Core.ISiteAccessCredentialStore
{
    public int LoadCalls { get; private set; }
    public int SaveCalls { get; private set; }
    public int ClearCalls { get; private set; }
    public int SummaryCalls { get; private set; }
    public string? ExpectedToken { get; set; }
    public string? ExpectedCookie { get; set; }
    public string? AccessTokenSummaryOverride { get; set; }
    public string? CookieSummaryOverride { get; set; }
    public bool LastSaveMatchedExpectedInput { get; private set; }
    public string? LastClearedProvider { get; private set; }

    public ProviderPriceSwitcher.Core.SiteCredentialRecord? LoadCredential(string providerId)
    {
        LoadCalls++;
        throw new InvalidOperationException("credential material must not be loaded by the UI");
    }

    public void SaveCredential(ProviderPriceSwitcher.Core.SiteCredentialRecord credential)
    {
        SaveCalls++;
        LastSaveMatchedExpectedInput = credential.ProviderId == "synthetic-provider"
            && credential.SiteType == "two"
            && credential.AccessToken == ExpectedToken
            && credential.CookieHeader == ExpectedCookie;
    }

    public void ClearCredential(string providerId)
    {
        ClearCalls++;
        LastClearedProvider = providerId;
    }

    public ProviderPriceSwitcher.Core.SiteCredentialSummary GetSummary(string providerId)
    {
        SummaryCalls++;
        return new ProviderPriceSwitcher.Core.SiteCredentialSummary
        {
            ProviderId = providerId,
            Status = ClearCalls == 0 ? ProviderPriceSwitcher.Core.SiteCredentialStatus.Available : ProviderPriceSwitcher.Core.SiteCredentialStatus.NotConfigured,
            StatusText = ClearCalls == 0 ? "已配置" : "未配置",
            AccessTokenSummary = SaveCalls > 0 ? AccessTokenSummaryOverride ?? "synt********n-ui" : null,
            CookieSummary = SaveCalls > 0 ? CookieSummaryOverride ?? "cook********e-ui" : null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
