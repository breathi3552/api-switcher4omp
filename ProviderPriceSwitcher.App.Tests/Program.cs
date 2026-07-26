using ProviderPriceSwitcher.App;
using System.Windows.Input;

static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
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
    try
    {
        var application = new System.Windows.Application();
        application.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/ProviderPriceSwitcher.App;component/Styles.xaml", UriKind.Relative) });
        var root = Path.Combine(Path.GetTempPath(), $"ProviderPriceSwitcher-AppTests-{Guid.NewGuid():N}");
        var settingsRepository = new ProviderPriceSwitcher.Infrastructure.JsonSettingsRepository(root);
        var snapshots = new ProviderPriceSwitcher.Infrastructure.JsonPricingSnapshotRepository(root);
        var refresh = new ProviderPriceSwitcher.Application.PricingRefreshService(registry, snapshots, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderPriceSwitcher.Application.PricingRefreshService>.Instance);
        var viewModel = new MainViewModel(settingsRepository, refresh, registry, new ProviderPriceSwitcher.Infrastructure.OmpConfigurationSwitcher(), new ProviderPriceSwitcher.Infrastructure.OmpProcessService(), settingsRepository.Load(), new FakeNotifications());
        var window = new MainWindow(viewModel);
        Assert(ReferenceEquals(window.DataContext, viewModel), "main window must bind its view model as DataContext");
        window.Close();
        application.Shutdown();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    catch (Exception ex) { windowFailure = ex; }
});
windowThread.SetApartmentState(ApartmentState.STA);
windowThread.Start();
windowThread.Join();
if (windowFailure is not null) throw windowFailure;

Console.WriteLine("App contract tests passed.");

sealed class FakeAdapter(ProviderPriceSwitcher.Application.PricingAdapterDescriptor descriptor) : ProviderPriceSwitcher.Application.IPricingAdapter
{
    public ProviderPriceSwitcher.Application.PricingAdapterDescriptor Descriptor { get; } = descriptor;
    public Task<ProviderPriceSwitcher.Application.SitePricingResult> FetchAsync(ProviderPriceSwitcher.Core.SiteConfiguration site, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

sealed class FakeNotifications : IUserNotificationService
{
    public void ShowWarning(string message, string title) { }
    public void ShowError(string message, string title) { }
    public bool Confirm(string message, string title) => true;
}
