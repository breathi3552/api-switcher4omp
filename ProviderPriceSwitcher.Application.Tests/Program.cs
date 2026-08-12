using Microsoft.Extensions.Logging.Abstractions;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static SiteConfiguration Site(decimal? ratio = null, string source = "手动", string type = "fake") => new() { ProviderId = "p", ConfigurationKey = "k", BaseUrl = new Uri("https://example.test"), SiteType = type, Model = "m", CurrentGroup = "g", CurrentGroupRatio = ratio, GroupRatioSource = source };
static SitePricingResult Pricing(decimal ratio) => new() { Snapshot = new PricingSnapshot { ProviderId = "p", ConfigurationKey = "k", Model = "m", CurrentGroup = "g", BasePrices = new TokenPrices { InputPerMillion = 1, CachedInputPerMillion = 1, OutputPerMillion = 1 }, CurrentGroupRatio = ratio, Prices = new TokenPrices { InputPerMillion = ratio, CachedInputPerMillion = ratio, OutputPerMillion = ratio }, RefreshedAt = DateTimeOffset.UtcNow }, GroupRatios = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["g"] = ratio }, MinimumValidGroup = "g", MinimumGroupRatio = ratio, Warnings = [] };

var adapter = new FakeAdapter(new PricingAdapterDescriptor("fake", "Fake", false, ["无需认证"]), (_, _) => Task.FromResult(Pricing(2)));
var registry = new PricingAdapterRegistry([adapter]);
var snapshots = new MemorySnapshots();
var settingsRepo = new MemorySettings();
var refresh = new PricingRefreshService(registry, NullLogger<PricingRefreshService>.Instance);
var useCase = new PricingCheckUseCase(refresh, settingsRepo, snapshots);
var sourceSites = new[] { Site(1) };
var sourceDirectories = new[] { "C:\\One" };
var immutableSettings = new LocalAppSettings { Sites = sourceSites, OmpWorkingDirectories = sourceDirectories };
sourceSites[0] = Site(9);
sourceDirectories[0] = "C:\\Mutated";
Assert(immutableSettings.Sites.Single().CurrentGroupRatio == 1 && immutableSettings.OmpWorkingDirectories.Single() == "C:\\One", "settings collections must snapshot caller-owned arrays");
var immutableUpdated = immutableSettings with { Sites = [Site(2)], OmpWorkingDirectories = ["C:\\Two"] };
Assert(immutableSettings.Sites.Single().CurrentGroupRatio == 1 && immutableSettings.OmpWorkingDirectories.Single() == "C:\\One" && immutableUpdated.Sites.Single().CurrentGroupRatio == 2, "with update must not mutate original settings");
var changed = await useCase.ExecuteAsync(new LocalAppSettings { Sites = [Site(1)] }, null);
Assert(settingsRepo.SaveCount == 1 && changed.Settings.Sites.Single().CurrentGroupRatio == 2 && changed.Settings.Sites.Single().GroupRatioSource == "自动", "changed ratio must save once as automatic");
settingsRepo.SaveCount = 0;
await useCase.ExecuteAsync(new LocalAppSettings { Sites = [Site(2, "自动")] }, null);
Assert(settingsRepo.SaveCount == 0, "unchanged ratio must not save");

var cancelRegistry = new PricingAdapterRegistry([new FakeAdapter(new PricingAdapterDescriptor("fake", "Fake", false, []), async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Pricing(1); })]);
using (var cts = new CancellationTokenSource())
{
    cts.Cancel();
    try { await new PricingCheckUseCase(new PricingRefreshService(cancelRegistry, NullLogger<PricingRefreshService>.Instance), settingsRepo, new MemorySnapshots()).ExecuteAsync(new LocalAppSettings { Sites = [Site()] }, null, cts.Token); throw new InvalidOperationException("cancel swallowed"); } catch (OperationCanceledException) { }
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
var managed = await siteUseCase.SaveSiteAsync(new LocalAppSettings(), Site(1), null);
Assert(managed.Sites.Count == 1 && settingsRepo.SaveCount == 1, "add site saves once");
snapshots.Save(Pricing(1).Snapshot);
managed = await siteUseCase.SaveSiteAsync(managed, Site(2), "p");
Assert(snapshots.Load("p")?.CurrentGroupRatio == 2 && settingsRepo.SaveCount == 2, "manual ratio updates matching snapshot");
var renamed = Site(2) with { ProviderId = "renamed", ConfigurationKey = "renamed-key" };
managed = await siteUseCase.SaveSiteAsync(managed, renamed, "p");
Assert(snapshots.Load("p") is null && managed.Sites.Single().ProviderId == "renamed", "rename deletes old snapshot");
managed = siteUseCase.SetEnabled(managed, "renamed", false);
Assert(!managed.Sites.Single().Enabled, "set enabled updates target");
snapshots.Save(Pricing(1).Snapshot with { ProviderId = "renamed", ConfigurationKey = "renamed-key" });
managed = await siteUseCase.DeleteSiteAsync(managed, "renamed");
Assert(managed.Sites.Count == 0 && snapshots.Load("renamed") is null, "delete removes site and snapshot");
try { await siteUseCase.DeleteSiteAsync(managed, "missing"); throw new InvalidOperationException("missing site accepted"); } catch (InvalidOperationException) { }

settingsRepo.SaveCount = 0;
var normalized = new SettingsUseCase(settingsRepo).Save(new LocalAppSettings { OmpWorkingDirectories = ["C:\\Work", "c:\\work", "D:\\Other"], LastOmpWorkingDirectory = "missing" });
Assert(normalized.OmpWorkingDirectories.SequenceEqual(["C:\\Work", "D:\\Other"]) && normalized.LastOmpWorkingDirectory == "C:\\Work" && settingsRepo.SaveCount == 1, "settings normalization mismatch");
var startupStore = new MemorySettings();
var startupSettings = new LocalAppSettings { OmpRootDirectory = "root", GatewayPort = 17001, CurrentGatewayPort = 15722 };
startupStore.Save(startupSettings);
startupStore.SaveCount = 0;
var startupTakeover = new FakeTakeover { Status = OmpTakeoverStatus.TakenOver, CurrentGatewayPort = 15722 };
var startupUseCase = new OmpStartupUseCase(startupStore, startupTakeover, NullLogger<OmpStartupUseCase>.Instance);
var migratedStartup = await startupUseCase.InitializeAsync(startupSettings);
Assert(migratedStartup.Status == OmpStartupStatus.Ready
    && migratedStartup.Settings.CurrentGatewayPort == 17001
    && startupTakeover.LastPort == 17001
    && startupStore.SaveCount == 1,
    "Startup must migrate a taken-over OMP configuration to the saved target port and persist the observed port.");
var observedStore = new MemorySettings();
observedStore.Save(startupSettings);
observedStore.SaveCount = 0;
var observedTakeover = new FakeTakeover { Status = OmpTakeoverStatus.TakenOver, CurrentGatewayPort = 17001 };
var observedStartup = await new OmpStartupUseCase(observedStore, observedTakeover, NullLogger<OmpStartupUseCase>.Instance)
    .InitializeAsync(startupSettings);
Assert(observedStartup.Status == OmpStartupStatus.Ready
    && observedStartup.Settings.CurrentGatewayPort == 17001
    && observedTakeover.TakeoverCalls == 0
    && observedStore.SaveCount == 1,
    "Startup must persist a newly observed taken-over port without rewriting OMP configuration.");

var untakenStore = new MemorySettings();
untakenStore.Save(startupSettings);
untakenStore.SaveCount = 0;
var untakenStartup = await new OmpStartupUseCase(
        untakenStore,
        new FakeTakeover { Status = OmpTakeoverStatus.NotTakenOver },
        NullLogger<OmpStartupUseCase>.Instance)
    .InitializeAsync(startupSettings);
Assert(untakenStartup.Status == OmpStartupStatus.Ready
    && untakenStartup.Settings.CurrentGatewayPort == 17001
    && untakenStore.SaveCount == 1,
    "Startup must align the effective port with the saved target while OMP is not taken over.");

var readFailureStore = new MemorySettings();
readFailureStore.Save(startupSettings);
readFailureStore.SaveCount = 0;
var readFailureStartup = await new OmpStartupUseCase(
        readFailureStore,
        new FakeTakeover { Status = OmpTakeoverStatus.ReadFailed },
        NullLogger<OmpStartupUseCase>.Instance)
    .InitializeAsync(startupSettings);
Assert(readFailureStartup.Status == OmpStartupStatus.TakeoverReadFailed && readFailureStore.SaveCount == 0, "Startup must stop without persisting settings when takeover status cannot be read.");

var migrationFailureStore = new MemorySettings();
migrationFailureStore.Save(startupSettings);
migrationFailureStore.SaveCount = 0;
var migrationFailureStartup = await new OmpStartupUseCase(
        migrationFailureStore,
        new FakeTakeover
        {
            Status = OmpTakeoverStatus.TakenOver,
            CurrentGatewayPort = 15722,
            TakeoverResult = new(false, OmpTakeoverFailureKind.Configuration, BackupRetentionSucceeded: false)
        },
        NullLogger<OmpStartupUseCase>.Instance)
    .InitializeAsync(startupSettings);
Assert(migrationFailureStartup.Status == OmpStartupStatus.PortMigrationFailed
    && migrationFailureStartup.TakeoverFailureKind == OmpTakeoverFailureKind.Configuration
    && !migrationFailureStartup.BackupRetentionSucceeded
    && migrationFailureStore.SaveCount == 0,
    "Startup must preserve structured port migration failure and avoid settings persistence.");

var startupSaveFailureStore = new MemorySettings();
startupSaveFailureStore.Save(startupSettings);
startupSaveFailureStore.SaveCount = 0;
startupSaveFailureStore.ThrowOnSave = true;
var startupSaveFailure = await new OmpStartupUseCase(
        startupSaveFailureStore,
        new FakeTakeover { Status = OmpTakeoverStatus.NotTakenOver },
        NullLogger<OmpStartupUseCase>.Instance)
    .InitializeAsync(startupSettings);
Assert(startupSaveFailure.Status == OmpStartupStatus.SettingsPersistenceFailed && startupSaveFailureStore.SaveCount == 0, "Startup settings persistence failure must be returned as structured state.");

settingsRepo.SaveCount = 0;
var takeover = new FakeTakeover { Status = OmpTakeoverStatus.TakenOver };
var launcher = new FakeLauncher(new OmpLaunchResult(true));
var launchUseCase = new OmpLaunchUseCase(settingsRepo, takeover, launcher, NullLogger<OmpLaunchUseCase>.Instance);
var launchSettings = new LocalAppSettings { OmpRootDirectory = "root", OmpWorkingDirectories = ["C:\\One"] };
using var launchCancellation = new CancellationTokenSource();
var started = await launchUseCase.LaunchAsync(launchSettings, "C:\\Two", launchCancellation.Token);
Assert(started.Status == OmpLaunchStatus.Started
    && settingsRepo.SaveCount == 1
    && launcher.Calls == 1
    && launcher.LastRequest == new OmpLaunchRequest("C:\\Two", "root")
    && launcher.LastCancellationToken == launchCancellation.Token
    && started.Settings.LastOmpWorkingDirectory == "C:\\Two",
    "Launch must pass the managed OMP root and cancellation token to every process attempt.");

takeover.Status = OmpTakeoverStatus.NotTakenOver;
settingsRepo.SaveCount = 0;
launcher.Calls = 0;
var takeoverRequired = await launchUseCase.LaunchAsync(launchSettings, "C:\\Two");
Assert(takeoverRequired.Status == OmpLaunchStatus.TakeoverRequired && settingsRepo.SaveCount == 0 && launcher.Calls == 0, "launch must not bypass OMP takeover");

takeover.Status = OmpTakeoverStatus.TakenOver;
launcher.Result = new OmpLaunchResult(true);
var secondLaunch = await launchUseCase.LaunchAsync(launchSettings, "C:\\Three");
Assert(secondLaunch.Succeeded && launcher.Calls == 1, "every launch request must create a new OMP attempt");
launcher.Result = new OmpLaunchResult(false, OmpProcessFailureKind.AccessDenied);
launcher.Calls = 0;
var deniedLaunch = await launchUseCase.LaunchAsync(launchSettings, "C:\\Denied");
Assert(deniedLaunch.Status == OmpLaunchStatus.LaunchFailed
    && deniedLaunch.ProcessFailureKind == OmpProcessFailureKind.AccessDenied
    && launcher.Calls == 1,
    "Application launch outcome must preserve the process failure kind.");
launcher.Result = new OmpLaunchResult(true);

takeover.TakeoverResult = new OmpTakeoverOperationResult(false);
launcher.Calls = 0;
var failedTakeover = await launchUseCase.TakeOverAndLaunchAsync(launchSettings, "C:\\Four", 15722);
Assert(failedTakeover.Status == OmpLaunchStatus.TakeoverFailed && launcher.Calls == 0, "failed takeover must not launch OMP");

takeover.TakeoverResult = new OmpTakeoverOperationResult(true);
var takeoverLaunch = await launchUseCase.TakeOverAndLaunchAsync(launchSettings, "C:\\Five", 15722);
Assert(takeoverLaunch.Succeeded && takeover.LastPort == 15722 && launcher.Calls == 1, "takeover and launch must be one explicit workflow");
Console.WriteLine("Application contract tests passed.");

var keyStore = new MemoryInferenceKeyStore();
var keySettings = new MemorySettings();
keySettings.Save(new LocalAppSettings { Sites = [Site(1)] });
var bindingStore = new MemoryInferenceBindingStore(keyStore, keySettings);
var keyUseCase = new InferenceApiKeyUseCase(keyStore, bindingStore);
var keySummary = keyUseCase.Save("p", "synthetic-inference-key", "updated-group");
Assert(keySummary.MaskedKey == "synt…-key" && keySummary.BoundGroup == "updated-group" && keyStore.Record?.ApiKey == "synthetic-inference-key" && keySettings.Value.Sites.Single().CurrentGroup == "updated-group", "inference key and bound group must be stored together");
var resolver = new InferenceApiKeyResolverBridge(keyStore, keySettings);
var routedKeyUseCase = new InferenceApiKeyUseCase(keyStore, bindingStore, keyResolver: resolver);
var routedSummary = routedKeyUseCase.Save("p", "replacement-inference-key", "updated-group");
Assert(await resolver.ResolveAsync(routedSummary.KeyHandle) == "replacement-inference-key", "saved key must be immediately resolvable by its handle");
bindingStore.ThrowOnSave = true;
try { routedKeyUseCase.Save("p", "must-not-commit", "failed-group"); throw new InvalidOperationException("binding failure accepted"); } catch (IOException) { }
bindingStore.ThrowOnSave = false;
Assert(keyStore.Record?.ApiKey == "replacement-inference-key" && keyStore.Record.BoundGroup == "updated-group" && keySettings.Value.Sites.Single().CurrentGroup == "updated-group", "binding save failure must retain the previous key and group");
var routeController = new FakeRouteController();
var launchRouteState = new ActiveRouteState();
launchRouteState.Apply(new RouteSnapshot("p", "https://example.test", "active-handle"));
var routeBeforeLaunch = launchRouteState.Current;
takeover.Status = OmpTakeoverStatus.TakenOver;
launcher.Result = new OmpLaunchResult(true);
launcher.Calls = 0;
var independentLaunch = await launchUseCase.LaunchAsync(launchSettings, "C:\\Six");
Assert(independentLaunch.Succeeded && launcher.Calls == 1 && launchRouteState.Current == routeBeforeLaunch, "launch must not apply, clear or change the active route");
settingsRepo.SaveCount = 0;
launcher.Calls = 0;
using (var canceledLaunch = new CancellationTokenSource())
{
    canceledLaunch.Cancel();
    try { await launchUseCase.LaunchAsync(launchSettings, "C:\\Canceled", canceledLaunch.Token); throw new InvalidOperationException("canceled launch accepted"); } catch (OperationCanceledException) { }
}
Assert(settingsRepo.SaveCount == 0 && launcher.Calls == 0, "canceled launch must not save settings or start OMP");
launchRouteState.Apply(new RouteSnapshot("p", "https://example.test", keyStore.Record!.KeyHandle));
var management = new SiteManagementUseCase(keySettings, new MemorySnapshots(), null, keyStore, launchRouteState, routeController, resolver);
var clearCountBeforeDelete = routeController.ClearCount;
await management.DeleteSiteAsync(keySettings.Value, "p");
Assert(keyStore.Record is null && launchRouteState.CurrentProviderId is null && await resolver.ResolveAsync(routedSummary.KeyHandle) is null && routeController.ClearCount == clearCountBeforeDelete + 1, "deleting supplier must clear key, resolver and active sidecar route");
var routeState = new ActiveRouteState();
routeState.Apply(new RouteSnapshot("p", "https://example.test", "handle"));
routeState.ClearIfProvider("other");
Assert(routeState.Current is not null, "unrelated route clear must preserve active snapshot");
routeState.ClearIfProvider("p");
Assert(routeState.CurrentProviderId is null, "cleared route provider id must be absent");
Assert(routeState.Current is null, "matching route clear must remove active snapshot");

var serializedRouteState = new ActiveRouteState();
var firstRouteLease = await serializedRouteState.AcquireAsync();
using (var canceledRouteWait = new CancellationTokenSource())
{
    canceledRouteWait.Cancel();
    try { await serializedRouteState.AcquireAsync(canceledRouteWait.Token); throw new InvalidOperationException("canceled route wait accepted"); }
    catch (OperationCanceledException) { }
}
var nextRouteLeaseTask = serializedRouteState.AcquireAsync();
firstRouteLease.Dispose();
using var nextRouteLease = await nextRouteLeaseTask;
Assert(nextRouteLease is not null, "route operation lock must remain usable after a canceled waiter");

var activeRouteSettings = new LocalAppSettings { Sites = [Site(1)], ActiveProviderId = null };
var activeRouteStore = new MemorySettings();
activeRouteStore.Save(activeRouteSettings);
var activeKeyStore = new MemoryInferenceKeyStore();
activeKeyStore.Save(new InferenceApiKeyRecord { ProviderId = "p", KeyHandle = "active-handle", ApiKey = "active-secret", BoundGroup = "g" });
var activeRouteState = new ActiveRouteState();
var activeRouteController = new FakeRouteController();
var activeRouteUseCase = new ApplyActiveRouteUseCase(activeRouteStore, activeRouteController, activeKeyStore, activeRouteState);
var appliedRoute = await activeRouteUseCase.ExecuteAsync(activeRouteSettings, "p");
Assert(appliedRoute.Status == ApplyActiveRouteStatus.Applied
    && appliedRoute.Settings.ActiveProviderId == "p"
    && activeRouteStore.Value.ActiveProviderId == "p"
    && activeRouteController.Applied?.ProviderId == "p"
    && activeRouteState.CurrentProviderId == "p", "explicit route apply must commit the sidecar route and non-secret active provider");

var routeCallsBeforePricing = activeRouteController.ApplyCount;
await new PricingCheckUseCase(refresh, activeRouteStore, new MemorySnapshots()).ExecuteAsync(activeRouteStore.Value, "p");
Assert(activeRouteController.ApplyCount == routeCallsBeforePricing && activeRouteState.CurrentProviderId == "p" && activeRouteStore.Value.ActiveProviderId == "p", "price checks must not apply or clear the active route");

var routeSaveFailureSettings = activeRouteStore.Value;
activeRouteStore.ThrowOnSave = true;
var oldRoute = activeRouteState.Current;
var routeSaveFailure = await activeRouteUseCase.ExecuteAsync(routeSaveFailureSettings, "p");
activeRouteStore.ThrowOnSave = false;
Assert(routeSaveFailure.Status == ApplyActiveRouteStatus.PersistenceFailed
    && activeRouteController.Applied == oldRoute
    && activeRouteState.Current == oldRoute
    && activeRouteStore.Value.ActiveProviderId == "p", "settings persistence failure must restore the previous sidecar route");

var rollbackFailureStore = new MemorySettings();
var rollbackFailureSettings = new LocalAppSettings { Sites = [Site(1)] };
rollbackFailureStore.Save(rollbackFailureSettings);
var rollbackFailureKeyStore = new MemoryInferenceKeyStore();
rollbackFailureKeyStore.Save(new InferenceApiKeyRecord { ProviderId = "p", KeyHandle = "rollback-handle", ApiKey = "rollback-secret", BoundGroup = "g" });
var rollbackFailureState = new ActiveRouteState();
var rollbackFailureController = new FakeRouteController { ThrowOnApplyCall = 3 };
var rollbackFailureUseCase = new ApplyActiveRouteUseCase(rollbackFailureStore, rollbackFailureController, rollbackFailureKeyStore, rollbackFailureState);
await rollbackFailureUseCase.ExecuteAsync(rollbackFailureSettings, "p");
rollbackFailureStore.ThrowOnSave = true;
var rollbackFailure = await rollbackFailureUseCase.ExecuteAsync(
    rollbackFailureStore.Value with { Sites = [Site(1) with { BaseUrl = new Uri("https://new.example.test") }], ActiveProviderId = "p" },
    "p");
rollbackFailureStore.ThrowOnSave = false;
Assert(rollbackFailure.Status == ApplyActiveRouteStatus.RollbackFailed && rollbackFailureState.Current?.BaseUrl == "https://example.test/" && rollbackFailureController.Applied?.BaseUrl == "https://new.example.test/", "rollback failure must be returned as structured state without hiding the sidecar divergence");
activeRouteController.ThrowOnApply = new IOException("synthetic sidecar failure");
var sidecarFailure = await activeRouteUseCase.ExecuteAsync(activeRouteStore.Value, "p");
activeRouteController.ThrowOnApply = null;
Assert(sidecarFailure.Status == ApplyActiveRouteStatus.SidecarFailed
    && sidecarFailure.SidecarFailure == SidecarFailureKind.GatewayUnavailable
    && activeRouteStore.Value.ActiveProviderId == "p", "sidecar failure must not persist a new active provider or lose the gateway failure kind");

var disabledRoute = await activeRouteUseCase.ExecuteAsync(activeRouteStore.Value with { Sites = [Site(1) with { Enabled = false }] }, "p");
Assert(disabledRoute.Status == ApplyActiveRouteStatus.ProviderDisabled && activeRouteController.ApplyCount == 3, "disabled provider must not become an active route");

var restoredRouteState = new ActiveRouteState();
var restoredRouteController = new FakeRouteController();
var restoreUseCase = new ApplyActiveRouteUseCase(activeRouteStore, restoredRouteController, activeKeyStore, restoredRouteState);
var restoreSaveCount = activeRouteStore.SaveCount;
var restored = await restoreUseCase.RestoreAsync();
Assert(restored.Status == ApplyActiveRouteStatus.Applied && restoredRouteController.Applied?.ProviderId == "p" && restoredRouteState.CurrentProviderId == "p" && activeRouteStore.SaveCount == restoreSaveCount, "valid persisted provider must restore sidecar state without rewriting unchanged settings");
var invalidRestoreStore = new MemorySettings();

var deleteRouteSettings = new MemorySettings();
deleteRouteSettings.Save(activeRouteStore.Value with { ActiveProviderId = "p" });
var deleteRouteState = new ActiveRouteState();
deleteRouteState.Apply(new RouteSnapshot("p", "https://example.test", "active-handle"));
var deleteRouteController = new FakeRouteController();
var deleteKeyStore = new MemoryInferenceKeyStore();
deleteKeyStore.Save(new InferenceApiKeyRecord { ProviderId = "p", KeyHandle = "active-handle", ApiKey = "active-secret", BoundGroup = "g" });
var deleteBindingStore = new MemoryInferenceBindingStore(deleteKeyStore, deleteRouteSettings);
var deleteKeyUseCase = new InferenceApiKeyUseCase(deleteKeyStore, deleteBindingStore, deleteRouteState, deleteRouteController, settingsRepository: deleteRouteSettings);
await deleteKeyUseCase.DeleteAsync("p");
Assert(deleteRouteSettings.Value.ActiveProviderId is null && deleteRouteState.CurrentProviderId is null && deleteRouteController.ClearCount == 1, "deleting an active key must clear persisted and sidecar activity without fallback");
invalidRestoreStore.Save(activeRouteStore.Value with { ActiveProviderId = "p" });
var invalidRestore = await new ApplyActiveRouteUseCase(invalidRestoreStore, new FakeRouteController(), new MemoryInferenceKeyStore(), new ActiveRouteState())
    .RestoreAsync();
Assert(invalidRestore.Status == ApplyActiveRouteStatus.Cleared && invalidRestoreStore.Value.ActiveProviderId is null, "disabled or missing-key persisted provider must be cleared without fallback");

var emptyRestoreState = new ActiveRouteState();
emptyRestoreState.Apply(new RouteSnapshot("stale", "https://example.test", "stale-handle"));
var emptyRestoreController = new FakeRouteController();
var emptyRestoreSettings = new MemorySettings();
emptyRestoreSettings.Save(new LocalAppSettings());
var emptyRestore = await new ApplyActiveRouteUseCase(emptyRestoreSettings, emptyRestoreController, activeKeyStore, emptyRestoreState)
    .RestoreAsync();
Assert(emptyRestore.Status == ApplyActiveRouteStatus.NoActiveRoute && emptyRestoreController.ClearCount == 1 && emptyRestoreState.Current is null, "empty persisted activity must clear the sidecar before serving stable no-route errors");

var recoverySettings = new MemorySettings();
recoverySettings.Save(new LocalAppSettings { Sites = [Site(1)], ActiveProviderId = "p" });
var recoveryKeys = new MemoryInferenceKeyStore();
recoveryKeys.Save(new InferenceApiKeyRecord { ProviderId = "p", KeyHandle = "recovery-handle", ApiKey = "recovery-secret", BoundGroup = "g" });
var recoveryState = new ActiveRouteState();
var recoveryController = new FakeRouteController();
var recoveryLifecycle = new FakeSidecarLifecycle();
var recovery = new GatewayRecoveryUseCase(
    recoveryLifecycle,
    new ApplyActiveRouteUseCase(recoverySettings, recoveryController, recoveryKeys, recoveryState));
var recoveredGateway = await recovery.ExecuteAsync();
Assert(recoveredGateway.Status == GatewayRecoveryStatus.Recovered
    && recoveryLifecycle.StartCalls == 1
    && recoveryController.Applied?.ProviderId == "p"
    && recoveryState.CurrentProviderId == "p",
    "gateway recovery must start the sidecar and restore the persisted active route before serving new requests");

using var racingSettings = new CoordinatedSettings();
var providerA = Site(1) with { ProviderId = "a", BaseUrl = new Uri("https://a.example.test") };
var providerB = Site(1) with { ProviderId = "b", BaseUrl = new Uri("https://b.example.test") };
racingSettings.Save(new LocalAppSettings { Sites = [providerA, providerB], ActiveProviderId = "a" });
var racingKeys = new FixedInferenceKeyStore(
    new InferenceApiKeyRecord { ProviderId = "a", KeyHandle = "a-handle", ApiKey = "a-secret", BoundGroup = "g" },
    new InferenceApiKeyRecord { ProviderId = "b", KeyHandle = "b-handle", ApiKey = "b-secret", BoundGroup = "g" });
var racingState = new ActiveRouteState();
var racingController = new FakeRouteController();
var racingRouteUseCase = new ApplyActiveRouteUseCase(racingSettings, racingController, racingKeys, racingState);
var racingRecovery = new GatewayRecoveryUseCase(new FakeSidecarLifecycle(), racingRouteUseCase);
racingSettings.BlockNextLoad();
var racingRecoveryTask = Task.Run(() => racingRecovery.ExecuteAsync());
await racingSettings.WaitForBlockedLoadAsync();
var applyProviderBTask = racingRouteUseCase.ExecuteAsync(racingSettings.Current, "b");
racingSettings.ReleaseBlockedLoad();
var applyProviderB = await applyProviderBTask;
await racingRecoveryTask;
Assert(applyProviderB.Status == ApplyActiveRouteStatus.Applied
    && racingController.Applied?.ProviderId == "b"
    && racingState.CurrentProviderId == "b"
    && racingSettings.Current.ActiveProviderId == "b",
    "gateway recovery that read provider A must not overwrite a concurrent user apply of provider B");
recoveryLifecycle.ThrowOnStart = true;
var failedRecovery = await recovery.ExecuteAsync();
Assert(failedRecovery.Status == GatewayRecoveryStatus.Failed
    && failedRecovery.RouteStatus is null
    && failedRecovery.FailureKind == GatewayRecoveryFailureKind.Protocol,
    "gateway recovery must expose a stable classified failure without leaking startup exceptions");
recoveryLifecycle.ThrowOnStart = false;
recoveryController.ThrowOnApply = new InvalidOperationException("synthetic protocol failure");
var protocolRecovery = await recovery.ExecuteAsync();
recoveryController.ThrowOnApply = null;
Assert(protocolRecovery.Status == GatewayRecoveryStatus.Failed
    && protocolRecovery.FailureKind == GatewayRecoveryFailureKind.Protocol,
    "route restore must preserve protocol failures instead of relabeling them as gateway-unavailable");

sealed class FakeAdapter(PricingAdapterDescriptor descriptor, Func<SiteConfiguration, CancellationToken, Task<SitePricingResult>> fetch) : IPricingAdapter
{
    public PricingAdapterDescriptor Descriptor { get; } = descriptor;
    public Task<SitePricingResult> FetchAsync(SiteConfiguration site, CancellationToken cancellationToken = default) => fetch(site, cancellationToken);
}
sealed class MemorySettings : ISettingsRepository
{
    public int SaveCount { get; set; }
    public bool ThrowOnSave { get; set; }
    public LocalAppSettings Value { get; private set; } = new();
    public LocalAppSettings Load() => Value;
    public void Save(LocalAppSettings settings) { if (ThrowOnSave) throw new IOException("synthetic settings save failure"); SaveCount++; Value = settings; }
    public LocalAppSettings Update(Func<LocalAppSettings, LocalAppSettings> update) { ArgumentNullException.ThrowIfNull(update); var updated = update(Value); Save(updated); return updated; }
}

sealed class CoordinatedSettings : ISettingsRepository, IDisposable
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _blockedLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _releaseBlockedLoad = new(false);
    private LocalAppSettings _value = new();
    private bool _blockNextLoad;

    public LocalAppSettings Current
    {
        get
        {
            lock (_gate)
                return _value;
        }
    }

    public void BlockNextLoad()
    {
        lock (_gate)
            _blockNextLoad = true;
    }

    public Task WaitForBlockedLoadAsync() => _blockedLoad.Task.WaitAsync(TimeSpan.FromSeconds(5));
    public void ReleaseBlockedLoad() => _releaseBlockedLoad.Set();

    public LocalAppSettings Load()
    {
        LocalAppSettings snapshot;
        bool shouldBlock;
        lock (_gate)
        {
            snapshot = _value;
            shouldBlock = _blockNextLoad;
            _blockNextLoad = false;
        }

        if (shouldBlock)
        {
            _blockedLoad.TrySetResult();
            if (!_releaseBlockedLoad.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Timed out waiting to release the coordinated settings load.");
        }

        return snapshot;
    }

    public void Save(LocalAppSettings settings)
    {
        lock (_gate)
            _value = settings;
    }

    public LocalAppSettings Update(Func<LocalAppSettings, LocalAppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            _value = update(_value);
            return _value;
        }
    }

    public void Dispose() => _releaseBlockedLoad.Dispose();
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
sealed class FakeTakeover : IOmpTakeoverService
{
    public OmpTakeoverStatus Status { get; set; } = OmpTakeoverStatus.TakenOver;
    public OmpTakeoverOperationResult TakeoverResult { get; set; } = new(true);
    public int? CurrentGatewayPort { get; set; }
    public int TakeoverCalls { get; private set; }
    public int LastPort { get; private set; }
    public Task<OmpTakeoverCheckResult> CheckAsync(string ompRootDirectory, CancellationToken cancellationToken = default) =>
        Task.FromResult(new OmpTakeoverCheckResult(Status, CurrentGatewayPort: CurrentGatewayPort));
    public Task<OmpTakeoverOperationResult> TakeOverAsync(string ompRootDirectory, int gatewayPort, CancellationToken cancellationToken = default)
    {
        TakeoverCalls++;
        LastPort = gatewayPort;
        return Task.FromResult(TakeoverResult);
    }
}
sealed class FakeLauncher(OmpLaunchResult result) : IOmpProcessLauncher
{
    public OmpLaunchResult Result { get; set; } = result;
    public OmpLaunchRequest? LastRequest { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }
    public int Calls { get; set; }
    public OmpLaunchResult Launch(OmpLaunchRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRequest = request;
        LastCancellationToken = cancellationToken;
        return Result;
    }
}

sealed class MemoryInferenceBindingStore(MemoryInferenceKeyStore keyStore, MemorySettings settings) : IInferenceBindingStore
{
    public bool ThrowOnSave { get; set; }
    public void Recover() { }
    public InferenceApiKeySummary Save(string providerId, string apiKey, string boundGroup)
    {
        if (ThrowOnSave) throw new IOException("synthetic binding save failure");
        var current = settings.Load();
        var sites = current.Sites.ToArray();
        var index = Array.FindIndex(sites, site => string.Equals(site.ProviderId, providerId, StringComparison.Ordinal));
        if (index < 0) throw new InvalidOperationException("provider missing");
        sites[index] = sites[index] with { CurrentGroup = boundGroup };
        var existing = keyStore.Load(providerId);
        keyStore.Save(new InferenceApiKeyRecord { ProviderId = providerId, KeyHandle = existing?.KeyHandle ?? Guid.NewGuid().ToString("N"), ApiKey = apiKey, BoundGroup = boundGroup });
        settings.Save(current with { Sites = sites });
        return keyStore.GetSummary(providerId)!;
    }
}

sealed class MemoryInferenceKeyStore : IInferenceApiKeyStore
{
    public InferenceApiKeyRecord? Record { get; private set; }
    public bool ThrowOnSave { get; set; }
    public InferenceApiKeyRecord? Load(string providerId) => Record;
    public void Save(InferenceApiKeyRecord record) { if (ThrowOnSave) throw new IOException("synthetic key save failure"); Record = record; }
    public void Clear(string providerId) => Record = null;
    public InferenceApiKeySummary? GetSummary(string providerId) => Record is null ? null : new() { ProviderId = Record.ProviderId, KeyHandle = Record.KeyHandle, BoundGroup = Record.BoundGroup, MaskedKey = InferenceApiKeySummary.Mask(Record.ApiKey), UpdatedAt = Record.UpdatedAt };
}

sealed class FixedInferenceKeyStore(params InferenceApiKeyRecord[] records) : IInferenceApiKeyStore
{
    private readonly Dictionary<string, InferenceApiKeyRecord> _records = records.ToDictionary(record => record.ProviderId, StringComparer.Ordinal);
    public InferenceApiKeyRecord? Load(string providerId) => _records.GetValueOrDefault(providerId);
    public void Save(InferenceApiKeyRecord record) => _records[record.ProviderId] = record;
    public void Clear(string providerId) => _records.Remove(providerId);
    public InferenceApiKeySummary? GetSummary(string providerId)
        => Load(providerId) is { } record
            ? new InferenceApiKeySummary
            {
                ProviderId = record.ProviderId,
                KeyHandle = record.KeyHandle,
                BoundGroup = record.BoundGroup,
                MaskedKey = InferenceApiKeySummary.Mask(record.ApiKey),
                UpdatedAt = record.UpdatedAt
            }
            : null;
}

sealed class FakeSidecarLifecycle : ISidecarLifecycle
{
    public int StartCalls { get; private set; }
    public bool ThrowOnStart { get; set; }
    public SidecarStatus Status { get; private set; } = new(SidecarConnectionStatus.Stopped);
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCalls++;
        if (ThrowOnStart)
            throw new SidecarLifecycleException(SidecarFailureKind.Protocol, new InvalidOperationException("synthetic sidecar startup failure"));
        Status = new(SidecarConnectionStatus.Ready);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Status = new(SidecarConnectionStatus.Stopped);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeRouteController : IRouteController
{
    public RouteSnapshot? Applied { get; private set; }
    public int ApplyCount { get; private set; }
    public int ClearCount { get; private set; }
    public Exception? ThrowOnApply { get; set; }
    public int? ThrowOnApplyCall { get; set; }
    public Task ApplyAsync(RouteSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (ThrowOnApply is not null) throw ThrowOnApply;
        ApplyCount++;
        if (ThrowOnApplyCall == ApplyCount) throw new IOException("synthetic apply failure");
        Applied = snapshot;
        return Task.CompletedTask;
    }
    public Task ClearAsync(CancellationToken cancellationToken = default) { ClearCount++; return Task.CompletedTask; }
}

sealed class FakeActiveRoute : IActiveRouteController
{
    public RouteSnapshot? Current { get; private set; }
    public string? CurrentProviderId => Current?.ProviderId;
    public event Action? Changed;
    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default) => Task.FromResult<IDisposable>(NoopLease.Instance);
    public void Apply(RouteSnapshot snapshot) { Current = snapshot; Changed?.Invoke(); }
    public void ClearIfProvider(string providerId)
    {
        if (!string.Equals(Current?.ProviderId, providerId, StringComparison.Ordinal)) return;
        Current = null;
        Changed?.Invoke();
    }
    private sealed class NoopLease : IDisposable
    {
        public static NoopLease Instance { get; } = new();
        public void Dispose() { }
    }
}
