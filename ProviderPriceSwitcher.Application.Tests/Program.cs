using Microsoft.Extensions.Logging.Abstractions;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static SiteConfiguration Site(decimal? ratio = null, string source = "手动", string type = "fake") => new() { ProviderId = "p", ConfigurationKey = "k", BaseUrl = new Uri("https://example.test"), SiteType = type, Model = "m", CurrentGroup = "g", CurrentGroupRatio = ratio, GroupRatioSource = source };
static SitePricingResult Pricing(decimal ratio) => new() { Snapshot = new PricingSnapshot { ProviderId = "p", ConfigurationKey = "k", Model = "m", CurrentGroup = "g", BasePrices = new TokenPrices { InputPerMillion = 1, CachedInputPerMillion = 1, OutputPerMillion = 1 }, CurrentGroupRatio = ratio, Prices = new TokenPrices { InputPerMillion = ratio, CachedInputPerMillion = ratio, OutputPerMillion = ratio }, RefreshedAt = DateTimeOffset.UtcNow }, ValidGroups = new HashSet<string>(["g"]), MinimumValidGroup = "g", MinimumGroupRatio = ratio, Warnings = [] };

var adapter = new FakeAdapter(new PricingAdapterDescriptor("fake", "Fake", false, ["无需认证"]), (_, _) => Task.FromResult(Pricing(2)));
var registry = new PricingAdapterRegistry([adapter]);
var snapshots = new MemorySnapshots();
var settingsRepo = new MemorySettings();
var refresh = new PricingRefreshService(registry, snapshots, NullLogger<PricingRefreshService>.Instance);
var useCase = new PricingCheckUseCase(refresh, settingsRepo);
var changed = await useCase.ExecuteAsync(new LocalAppSettings { Sites = [Site(1)] }, null);
Assert(settingsRepo.SaveCount == 1 && changed.Settings.Sites.Single().CurrentGroupRatio == 2 && changed.Settings.Sites.Single().GroupRatioSource == "自动", "changed ratio must save once as automatic");
settingsRepo.SaveCount = 0;
await useCase.ExecuteAsync(new LocalAppSettings { Sites = [Site(2, "自动")] }, null);
Assert(settingsRepo.SaveCount == 0, "unchanged ratio must not save");

var cancelRegistry = new PricingAdapterRegistry([new FakeAdapter(new PricingAdapterDescriptor("fake", "Fake", false, []), async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Pricing(1); })]);
using (var cts = new CancellationTokenSource())
{
    cts.Cancel();
    try { await new PricingCheckUseCase(new PricingRefreshService(cancelRegistry, new MemorySnapshots(), NullLogger<PricingRefreshService>.Instance), settingsRepo).ExecuteAsync(new LocalAppSettings { Sites = [Site()] }, null, cts.Token); throw new InvalidOperationException("cancel swallowed"); } catch (OperationCanceledException) { }
}
Assert(settingsRepo.SaveCount == 0, "cancellation must not save");

var probe = new PricingProbeUseCase(registry);
try { await probe.ExecuteAsync(Site(type: "missing"), 1); throw new InvalidOperationException("unknown accepted"); } catch (PricingAdapterException ex) when (ex.Failure == PricingAdapterFailure.InvalidResponse) { }
var timeoutProbe = new PricingProbeUseCase(cancelRegistry);
try { await timeoutProbe.ExecuteAsync(Site(), 0); throw new InvalidOperationException("timeout accepted"); } catch (PricingAdapterException ex) when (ex.Failure == PricingAdapterFailure.Timeout) { }
using (var cts = new CancellationTokenSource())
{
    cts.Cancel();
    try { await timeoutProbe.ExecuteAsync(Site(), 30, cts.Token); throw new InvalidOperationException("probe cancel swallowed"); } catch (OperationCanceledException) { }
}

settingsRepo.SaveCount = 0;
var siteUseCase = new SiteManagementUseCase(settingsRepo, snapshots);
var managed = siteUseCase.SaveSite(new LocalAppSettings(), Site(1), null);
Assert(managed.Sites.Count == 1 && settingsRepo.SaveCount == 1, "add site saves once");
snapshots.Save(Pricing(1).Snapshot);
managed = siteUseCase.SaveSite(managed, Site(2), "p");
Assert(snapshots.Load("p")?.CurrentGroupRatio == 2 && settingsRepo.SaveCount == 2, "manual ratio updates matching snapshot");
var renamed = Site(2) with { ProviderId = "renamed", ConfigurationKey = "renamed-key" };
managed = siteUseCase.SaveSite(managed, renamed, "p");
Assert(snapshots.Load("p") is null && managed.Sites.Single().ProviderId == "renamed", "rename deletes old snapshot");
managed = siteUseCase.SetEnabled(managed, "renamed", false);
Assert(!managed.Sites.Single().Enabled, "set enabled updates target");
snapshots.Save(Pricing(1).Snapshot with { ProviderId = "renamed", ConfigurationKey = "renamed-key" });
managed = siteUseCase.DeleteSite(managed, "renamed");
Assert(managed.Sites.Count == 0 && snapshots.Load("renamed") is null, "delete removes site and snapshot");
try { siteUseCase.DeleteSite(managed, "missing"); throw new InvalidOperationException("missing site accepted"); } catch (InvalidOperationException) { }

settingsRepo.SaveCount = 0;
var normalized = new SettingsUseCase(settingsRepo).Save(new LocalAppSettings { OmpWorkingDirectories = ["C:\\Work", "c:\\work", "D:\\Other"], LastOmpWorkingDirectory = "missing" });
Assert(normalized.OmpWorkingDirectories.SequenceEqual(["C:\\Work", "D:\\Other"]) && normalized.LastOmpWorkingDirectory == "C:\\Work" && settingsRepo.SaveCount == 1, "settings normalization mismatch");

settingsRepo.SaveCount = 0;
var configuration = new FakeConfiguration(true);
var launcher = new FakeLauncher(new OmpLaunchResult(true, false));
var switchUseCase = new SwitchAndStartUseCase(settingsRepo, configuration, launcher, NullLogger<SwitchAndStartUseCase>.Instance);
var switchSettings = new LocalAppSettings { OmpRootDirectory = "root", OmpWorkingDirectories = ["C:\\One"] };
var started = await switchUseCase.ExecuteAsync(switchSettings, "provider", "C:\\Two");
Assert(started.Status == SwitchAndStartStatus.Started && settingsRepo.SaveCount == 1 && launcher.Calls == 1 && started.Settings.LastOmpWorkingDirectory == "C:\\Two", "started outcome/order mismatch");
configuration.Result = new OmpConfigurationOperationResult(false);
settingsRepo.SaveCount = 0; launcher.Calls = 0;
var configurationFailed = await switchUseCase.ExecuteAsync(switchSettings, "provider", "C:\\Two");
Assert(configurationFailed.Status == SwitchAndStartStatus.ConfigurationFailed && settingsRepo.SaveCount == 0 && launcher.Calls == 0, "configuration failure must stop pipeline");
configuration.Result = new OmpConfigurationOperationResult(true);
launcher.Result = new OmpLaunchResult(true, true);
var existing = await switchUseCase.ExecuteAsync(switchSettings, "provider", "C:\\Two");
Assert(existing.Status == SwitchAndStartStatus.StartedWithExistingProcess, "existing process outcome mismatch");
launcher.Result = new OmpLaunchResult(false, false);
settingsRepo.SaveCount = 0;
var launchFailed = await switchUseCase.ExecuteAsync(switchSettings, "provider", "C:\\Two");
Assert(launchFailed.Status == SwitchAndStartStatus.LaunchFailedAfterSwitch && settingsRepo.SaveCount == 1 && configuration.Calls == 4, "launch failure must preserve switched settings without rollback");
Console.WriteLine("Application contract tests passed.");

sealed class FakeAdapter(PricingAdapterDescriptor descriptor, Func<SiteConfiguration, CancellationToken, Task<SitePricingResult>> fetch) : IPricingAdapter
{
    public PricingAdapterDescriptor Descriptor { get; } = descriptor;
    public Task<SitePricingResult> FetchAsync(SiteConfiguration site, CancellationToken cancellationToken = default) => fetch(site, cancellationToken);
}
sealed class MemorySettings : ISettingsRepository
{
    public int SaveCount { get; set; }
    public LocalAppSettings Value { get; private set; } = new();
    public LocalAppSettings Load() => Value;
    public void Save(LocalAppSettings settings) { SaveCount++; Value = settings; }
}
sealed class MemorySnapshots : IPricingSnapshotRepository
{
    private readonly Dictionary<string, PricingSnapshot> _values = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, PricingSnapshot> LoadAll() => _values;
    public PricingSnapshot? Load(string providerId) => _values.GetValueOrDefault(providerId);
    public void SaveAll(IEnumerable<PricingSnapshot> snapshots) { _values.Clear(); foreach (var snapshot in snapshots) _values[snapshot.ProviderId] = snapshot; }
    public void Save(PricingSnapshot snapshot) => _values[snapshot.ProviderId] = snapshot;
    public void Delete(string providerId) => _values.Remove(providerId);
}
sealed class FakeConfiguration(bool succeeded) : IOmpConfigurationService
{
    public OmpConfigurationOperationResult Result { get; set; } = new(succeeded);
    public int Calls { get; private set; }
    public Task<OmpConfigurationOperationResult> SwitchAsync(string ompRootDirectory, string providerId, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(Result); }
}
sealed class FakeLauncher(OmpLaunchResult result) : IOmpProcessLauncher
{
    public OmpLaunchResult Result { get; set; } = result;
    public int Calls { get; set; }
    public OmpLaunchResult Launch(string workingDirectory) { Calls++; return Result; }
}
