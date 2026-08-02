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
    .Concat(Enum.GetValues<ProviderPriceSwitcher.Application.SwitchAndStartStatus>().Select(status => UserErrorMessages.ForSwitchStatus(status, "provider")))
    .Concat(Enum.GetValues<ProviderPriceSwitcher.Application.PricingAdapterFailure>().Select(UserErrorMessages.ForProbeFailure))
    .Append(UserErrorMessages.Unexpected)
    .ToArray();
Assert(publicMessages.All(message => !message.Contains(syntheticFailure, StringComparison.Ordinal) && !message.Contains("api_key", StringComparison.OrdinalIgnoreCase) && !message.Contains("Cookie", StringComparison.OrdinalIgnoreCase) && !message.Contains("Authorization", StringComparison.OrdinalIgnoreCase) && !message.Contains("已验证请求指纹", StringComparison.Ordinal)), "public error mapping must remain fixed and non-sensitive");
Assert(UserErrorMessages.ForPricingFailure(ProviderPriceSwitcher.Application.PricingRefreshFailureKind.Timeout) == "请求超时，请稍后重试。" && UserErrorMessages.ForPricingFailure(ProviderPriceSwitcher.Application.PricingRefreshFailureKind.Authentication) == "需要重新绑定凭据。", "pricing failure mapping mismatch");
Assert(UserErrorMessages.ForSwitchStatus(ProviderPriceSwitcher.Application.SwitchAndStartStatus.ConfigurationFailed, "provider").StartsWith("配置未切换，OMP 未启动", StringComparison.Ordinal) && UserErrorMessages.ForSwitchStatus(ProviderPriceSwitcher.Application.SwitchAndStartStatus.LaunchFailedAfterSwitch, "provider").StartsWith("配置已切换，但 OMP 启动失败", StringComparison.Ordinal), "switch failure mapping mismatch");
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
        var pathDefaults = new ProviderPriceSwitcher.Infrastructure.AppPathDefaults();
        var switcher = new ProviderPriceSwitcher.Infrastructure.OmpConfigurationSwitcher();
        var settings = settingsRepository.Load();
        var credentialStore = new FakeCredentialStore();
        var notifications = new FakeNotifications();
        var snapshotQuery = new ProviderPriceSwitcher.Infrastructure.PricingSnapshotQuery(snapshots);
        var currentProviderQuery = new ProviderPriceSwitcher.Infrastructure.OmpCurrentProviderQuery(switcher, pathDefaults);
        var siteManagement = new ProviderPriceSwitcher.Application.SiteManagementUseCase(settingsRepository, snapshots);
        var pricingCheck = new ProviderPriceSwitcher.Application.PricingCheckUseCase(refresh, settingsRepository, snapshots);
        var settingsUseCase = new ProviderPriceSwitcher.Application.SettingsUseCase(settingsRepository);
        var switchAndStart = new ProviderPriceSwitcher.Application.SwitchAndStartUseCase(settingsRepository, new ProviderPriceSwitcher.Infrastructure.OmpConfigurationService(switcher, pathDefaults), new ProviderPriceSwitcher.Infrastructure.OmpProcessLauncher(new ProviderPriceSwitcher.Infrastructure.OmpProcessService()), Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderPriceSwitcher.Application.SwitchAndStartUseCase>.Instance);
        var editorFactory = new SiteEditorDialogFactory((original, localSettings) => new SiteEditorViewModel(new ProviderPriceSwitcher.Application.PricingProbeUseCase(registry), registry, credentialStore, notifications, localSettings, original));
        var sitesFactory = new SitesDialogFactory((localSettings, currentProvider) => new SitesDialog(localSettings, siteManagement, snapshotQuery, editorFactory, currentProvider));
        var viewModel = new MainViewModel(pricingCheck, settingsUseCase, switchAndStart, currentProviderQuery, snapshotQuery, registry, settings, sitesFactory, notifications, Microsoft.Extensions.Logging.Abstractions.NullLogger<MainViewModel>.Instance);
        var window = new MainWindow(viewModel);
        window.Show();

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

        var editorFactoryForDialog = new SiteEditorDialogFactory((original, localSettings) => new SiteEditorViewModel(new ProviderPriceSwitcher.Application.PricingProbeUseCase(registry), registry, credentialStore, notifications, localSettings, original));
        var sitesDialog = new SitesDialog(settings, siteManagement, snapshotQuery, editorFactoryForDialog, null);
        Assert(ReferenceEquals(GetField<ISiteEditorDialogFactory>(sitesDialog, "_editorFactory"), editorFactoryForDialog), "sites dialog must retain injected editor factory");
        sitesDialog.Close();

        var dialog = editorFactoryForDialog.Create(site, settings, window);
        dialog.Show(); dialog.UpdateLayout();
        Assert(dialog.DataContext is SiteEditorViewModel, "editor dialog must bind the production view model");
        var editorVm = (SiteEditorViewModel)dialog.DataContext;
        Assert(editorVm.ProbeCommand.CanExecute(null) && editorVm.SaveCommand.CanExecute(null) && !editorVm.CancelProbeCommand.CanExecute(null), "valid editor fields must enable probe/save and leave cancel disabled");
        var tokenBox = (System.Windows.Controls.PasswordBox)dialog.FindName("TokenBox");
        var cookieBox = (System.Windows.Controls.PasswordBox)dialog.FindName("CookieBox");
        tokenBox.Password = "synthetic-token-ui";
        cookieBox.Password = "synthetic-cookie-ui";
        credentialStore.ExpectedToken = tokenBox.Password;
        credentialStore.ExpectedCookie = cookieBox.Password;
        var credentialButton = Descendants(dialog).OfType<System.Windows.Controls.Button>().Single(button => Equals(button.Content, "绑定/更新站点凭据"));
        credentialButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert(credentialStore.SaveCalls == 1 && credentialStore.LastSaveMatchedExpectedInput && tokenBox.Password.Length == 0 && cookieBox.Password.Length == 0, "real credential bridge must save once and immediately clear both inputs");
        Assert(credentialStore.LoadCalls == 0, "editor must never load credential material");
        probeAdapter.Block = true;
        editorVm.ProbeCommand.Execute(null);
        Assert(editorVm.IsProbing && !editorVm.ProbeCommand.CanExecute(null) && !editorVm.SaveCommand.CanExecute(null) && editorVm.CancelProbeCommand.CanExecute(null), "probe must become busy and prevent reentry while enabling cancel");
        editorVm.ProbeCommand.Execute(null);
        Assert(probeAdapter.FetchCalls == 1, "busy probe command must reject reentry");
        editorVm.CancelProbeCommand.Execute(null);
        WaitFor(() => !editorVm.IsProbing);
        Assert(editorVm.ProbeState == SiteEditorProbeState.Canceled && editorVm.ProbeCommand.CanExecute(null) && editorVm.SaveCommand.CanExecute(null) && !editorVm.CancelProbeCommand.CanExecute(null) && notifications.ErrorCalls == 0, "canceled probe must restore commands without an error notification");
        probeAdapter.Block = false;
        editorVm.ProbeCommand.Execute(null);
        WaitFor(() => !editorVm.IsProbing);
        Assert(editorVm.ProbeState == SiteEditorProbeState.Succeeded && probeAdapter.FetchCalls == 2, "successful probe must complete through the real dialog view model");
        dialog.Close();
        var saveDialog = editorFactoryForDialog.Create(site, settings, window);
        var saveViewModel = (SiteEditorViewModel)saveDialog.DataContext;
        saveDialog.Dispatcher.BeginInvoke(() => saveViewModel.SaveCommand.Execute(null));
        Assert(saveDialog.ShowDialog() == true && saveViewModel.SavedSite?.ProviderId == "synthetic-provider", "save command must close the modal dialog successfully and expose SavedSite");

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
    public int FetchCalls { get; private set; }
    public async Task<ProviderPriceSwitcher.Application.SitePricingResult> FetchAsync(ProviderPriceSwitcher.Core.SiteConfiguration site, CancellationToken cancellationToken = default)
    {
        FetchCalls++;
        if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new() { Snapshot = new() { ProviderId = site.ProviderId, ConfigurationKey = site.ConfigurationKey, Model = site.Model, CurrentGroup = site.CurrentGroup, Prices = new() { InputPerMillion = 1, CachedInputPerMillion = 1, OutputPerMillion = 1 }, CurrentGroupRatio = 1, RefreshedAt = DateTimeOffset.UtcNow }, ValidGroups = new HashSet<string>([site.CurrentGroup]), MinimumValidGroup = site.CurrentGroup, MinimumGroupRatio = 1, Warnings = [] };
    }
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
    public bool ConfirmResult { get; set; } = true;
    public void ShowWarning(string message, string title) => WarningCalls++;
    public void ShowError(string message, string title) => ErrorCalls++;
    public bool Confirm(string message, string title) => ConfirmResult;
}

sealed class FakeCredentialStore : ProviderPriceSwitcher.Core.ISiteAccessCredentialStore
{
    public int LoadCalls { get; private set; }
    public int SaveCalls { get; private set; }
    public int ClearCalls { get; private set; }
    public int SummaryCalls { get; private set; }
    public string? ExpectedToken { get; set; }
    public string? ExpectedCookie { get; set; }
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
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
