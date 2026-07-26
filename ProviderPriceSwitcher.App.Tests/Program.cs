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
var registry = new ProviderPriceSwitcher.Application.PricingAdapterRegistry([
    new FakeAdapter(new("one", "One", false, ["无"])),
    new FakeAdapter(new("two", "Two", true, ["令牌", "账户"])),
    new FakeAdapter(new("three", "Three", false, ["匿名"]))
]);
var editor = new SiteEditorViewModel(registry);
Assert(editor.Descriptors.Count == 3 && editor.AuthenticationMode == "无" && !editor.CredentialVisible, "descriptor initialization mismatch");
editor.Descriptor = editor.Descriptors[1];
Assert(editor.AuthenticationMode == "令牌" && editor.CredentialVisible, "descriptor switch must reset auth and credential visibility");
editor.IsProbing = true; Assert(!editor.CanSave, "probe must disable save"); editor.IsProbing = false; Assert(editor.CanSave, "probe completion must restore save");
var typed = editor.ApplySiteType(new ProviderPriceSwitcher.Core.SiteConfiguration { ProviderId = "p", ConfigurationKey = "k", BaseUrl = new Uri("https://example.test"), Model = "m", CurrentGroup = "g" });
Assert(typed.SiteType == "two" && typed.AuthenticationMode == "令牌", "saved site type mismatch");

Exception? windowFailure = null;
var windowThread = new Thread(() =>
{
    var root = Path.Combine(Path.GetTempPath(), $"ProviderPriceSwitcher-AppTests-{Guid.NewGuid():N}");
    try
    {
        var application = new System.Windows.Application();
        application.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/ProviderPriceSwitcher.App;component/Styles.xaml", UriKind.Relative) });
        var settingsRepository = new ProviderPriceSwitcher.Infrastructure.JsonSettingsRepository(root);
        var snapshots = new ProviderPriceSwitcher.Infrastructure.JsonPricingSnapshotRepository(root);
        var refresh = new ProviderPriceSwitcher.Application.PricingRefreshService(registry, snapshots, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderPriceSwitcher.Application.PricingRefreshService>.Instance);
        var pathDefaults = new ProviderPriceSwitcher.Infrastructure.AppPathDefaults();
        var switcher = new ProviderPriceSwitcher.Infrastructure.OmpConfigurationSwitcher();
        var settings = settingsRepository.Load();
        var credentialStore = new FakeCredentialStore();
        var notifications = new FakeNotifications();
        var snapshotQuery = new ProviderPriceSwitcher.Infrastructure.PricingSnapshotQuery(snapshots);
        var currentProviderQuery = new ProviderPriceSwitcher.Infrastructure.OmpCurrentProviderQuery(switcher, pathDefaults);
        var siteManagement = new ProviderPriceSwitcher.Application.SiteManagementUseCase(settingsRepository, snapshots);
        var pricingCheck = new ProviderPriceSwitcher.Application.PricingCheckUseCase(refresh, settingsRepository);
        var settingsUseCase = new ProviderPriceSwitcher.Application.SettingsUseCase(settingsRepository);
        var switchAndStart = new ProviderPriceSwitcher.Application.SwitchAndStartUseCase(settingsRepository, new ProviderPriceSwitcher.Infrastructure.OmpConfigurationService(switcher, pathDefaults), new ProviderPriceSwitcher.Infrastructure.OmpProcessLauncher(new ProviderPriceSwitcher.Infrastructure.OmpProcessService()), Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderPriceSwitcher.Application.SwitchAndStartUseCase>.Instance);
        var viewModel = new MainViewModel(pricingCheck, settingsUseCase, switchAndStart, siteManagement, currentProviderQuery, snapshotQuery, registry, settings, credentialStore, notifications, Microsoft.Extensions.Logging.Abstractions.NullLogger<MainViewModel>.Instance);
        var window = new MainWindow(viewModel);
        window.Show();
        Assert(ReferenceEquals(window.DataContext, viewModel), "main window must bind its view model as DataContext");
        Assert(ReferenceEquals(GetField<ProviderPriceSwitcher.Core.ISiteCredentialStore>(viewModel, "_credentialStore"), credentialStore), "main view model must retain the injected credential store");

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

        var sitesDialog = new SitesDialog(settings, siteManagement, snapshotQuery, registry, credentialStore, notifications, null);
        Assert(ReferenceEquals(GetField<ProviderPriceSwitcher.Core.ISiteCredentialStore>(sitesDialog, "_credentialStore"), credentialStore), "sites dialog must retain the same credential store");
        sitesDialog.Close();

        var dialog = new SiteEditorDialog(site, settings, snapshotQuery, registry, credentialStore, notifications);
        dialog.Show();
        dialog.UpdateLayout();
        var tokenInput = FindDescendant<System.Windows.Controls.PasswordBox>(dialog);
        var cookieInput = FindField<System.Windows.Controls.TextBox>(dialog, "本次绑定/更新的浏览器会话 Cookie（形如 refresh_token=...）");
        Assert(string.IsNullOrEmpty(tokenInput.Password) && string.IsNullOrEmpty(cookieInput.Text), "stored credentials must never populate credential inputs");
        Assert(credentialStore.LoadCalls == 0 && credentialStore.SummaryCalls > 0, "editor must use summary without loading credential material");

        FindButton(dialog, "绑定/更新令牌").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert(credentialStore.SaveCalls == 0 && notifications.WarningCalls == 1, "empty credential input must not overwrite the store");

        var token = $"synthetic-token-{Guid.NewGuid():N}";
        var cookie = $"synthetic-cookie-{Guid.NewGuid():N}";
        credentialStore.ExpectedToken = token;
        credentialStore.ExpectedCookie = cookie;
        tokenInput.Password = token;
        cookieInput.Text = cookie;
        FindButton(dialog, "绑定/更新令牌").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert(credentialStore.SaveCalls == 1 && credentialStore.LastSaveMatchedExpectedInput, "explicit credential update must save the current provider and site type once");
        Assert(string.IsNullOrEmpty(tokenInput.Password) && string.IsNullOrEmpty(cookieInput.Text), "credential inputs must clear immediately after update");
        Assert(!GetVisibleText(dialog).Contains(token, StringComparison.Ordinal) && !GetVisibleText(dialog).Contains(cookie, StringComparison.Ordinal), "credential material must not remain in visible UI text");

        notifications.ConfirmResult = false;
        FindButton(dialog, "清除凭据").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert(credentialStore.ClearCalls == 0, "declined credential clear must not touch the store");
        notifications.ConfirmResult = true;
        FindButton(dialog, "清除凭据").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert(credentialStore.ClearCalls == 1 && credentialStore.LastClearedProvider == site.ProviderId, "confirmed credential clear must target the current provider once");

        dialog.Close();
        window.Close();
        application.Shutdown();
        var persistedText = string.Join('\n', Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(File.ReadAllText) : []);
        Assert(!persistedText.Contains(token, StringComparison.Ordinal) && !persistedText.Contains(cookie, StringComparison.Ordinal), "credential material must not enter app JSON or other ordinary files");
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

static T FindField<T>(System.Windows.DependencyObject root, string label) where T : System.Windows.Controls.Control =>
    Descendants(root).OfType<System.Windows.Controls.StackPanel>()
        .Where(panel => panel.Children.OfType<System.Windows.Controls.TextBlock>().Any(text => string.Equals(text.Text, label, StringComparison.Ordinal)))
        .SelectMany(panel => panel.Children.OfType<T>())
        .FirstOrDefault() ?? throw new InvalidOperationException($"field labeled {label} was not found");

static System.Windows.Controls.Button FindButton(System.Windows.DependencyObject root, string content) =>
    Descendants(root).OfType<System.Windows.Controls.Button>().FirstOrDefault(button => string.Equals(button.Content as string, content, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"button {content} was not found");

static string GetVisibleText(System.Windows.DependencyObject root) => string.Join('\n',
    Descendants(root).Select(control => control switch
    {
        System.Windows.Controls.TextBlock text => text.Text,
        System.Windows.Controls.TextBox text => text.Text,
        System.Windows.Controls.PasswordBox password => password.Password,
        _ => null
    }).Where(text => text is not null));

sealed class FakeAdapter(ProviderPriceSwitcher.Application.PricingAdapterDescriptor descriptor) : ProviderPriceSwitcher.Application.IPricingAdapter
{
    public ProviderPriceSwitcher.Application.PricingAdapterDescriptor Descriptor { get; } = descriptor;
    public Task<ProviderPriceSwitcher.Application.SitePricingResult> FetchAsync(ProviderPriceSwitcher.Core.SiteConfiguration site, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
    public bool ConfirmResult { get; set; } = true;

    public void ShowWarning(string message, string title) => WarningCalls++;
    public void ShowError(string message, string title) { }
    public bool Confirm(string message, string title) => ConfirmResult;
}

sealed class FakeCredentialStore : ProviderPriceSwitcher.Core.ISiteCredentialStore
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
