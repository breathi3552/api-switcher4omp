using ProviderPriceSwitcher.Infrastructure;
using System.Runtime.Versioning;
[assembly: SupportedOSPlatform("windows")]

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var runId = Guid.NewGuid().ToString("N");
var root = Path.Combine(Path.GetTempPath(), "ProviderPriceSwitcher-OmpConfig-" + runId);
var dataRoot = Path.Combine(root, "data");
var ompRoot = Path.Combine(root, "omp");
var workingRoot = Path.Combine(root, "working");
string[] sentinels = [];
const string secretSentinel = "synthetic-config-secret-DO-NOT-LOG";
const string cookieSentinel = "synthetic-config-cookie-DO-NOT-LOG";
var credentialStore = new WindowsSiteCredentialStore(dataRoot);
try
{
    Directory.CreateDirectory(dataRoot);
    Directory.CreateDirectory(ompRoot);
    Directory.CreateDirectory(workingRoot);
    sentinels = new[] { dataRoot, ompRoot, workingRoot }.Select(path => Path.Combine(path, ".sentinel")).ToArray();
    foreach (var sentinel in sentinels) File.WriteAllText(sentinel, runId);
    File.WriteAllText(Path.Combine(dataRoot, "settings.json"), $"{{\"OmpRootDirectory\":\"{ompRoot}\",\"OmpWorkingDirectories\":[\"{workingRoot}\"],\"LastOmpWorkingDirectory\":\"{workingRoot}\"}}");
    Directory.CreateDirectory(Path.Combine(ompRoot, "agent"));
    File.WriteAllText(Path.Combine(ompRoot, "agent", "models.yml"), "providers:\r\n  existing:\r\n    baseUrl: https://example.test/v1\r\n");
    credentialStore.SaveCredential(new ProviderPriceSwitcher.Core.SiteCredentialRecord
    {
        ProviderId = "new-provider",
        SiteType = "new-api",
        AuthorizationScheme = "Bearer",
        AccessToken = secretSentinel,
        CookieHeader = cookieSentinel
    });
}
catch
{
    if (Directory.Exists(root))
        Directory.Delete(root, true);
    throw;
}
try
{
    var yaml = "# keep this comment\r\nmodelRoles:\r\n  DEFAULT: old-provider/alpha/variant\r\n  fast: another-provider/beta@2024\r\n  indirect: '@smol'\r\ntask:\r\n  agentModelOverrides:\r\n    reviewer: old-provider/review\r\n    helper: '@role'\r\nunrelated: old-provider/untouched\r\n";
    var switcher = new OmpConfigurationSwitcher();
    var preview = switcher.Preview(yaml, "new-provider");
    Assert(preview.IsValid, preview.Error ?? "preview invalid");
    Assert(preview.Changes.Count == 3, "expected default, model and override");
    Assert(preview.NewText.Contains("new-provider/alpha/variant", StringComparison.Ordinal), "suffix not preserved");
    Assert(preview.NewText.Contains("new-provider/beta@2024", StringComparison.Ordinal), "second model not switched");
    Assert(preview.NewText.Contains("new-provider/review", StringComparison.Ordinal), "override not switched");
    Assert(preview.NewText.Contains("'@smol'", StringComparison.Ordinal) && preview.NewText.Contains("'@role'", StringComparison.Ordinal), "indirect role changed");
    Assert(preview.NewText.Contains("unrelated: old-provider/untouched", StringComparison.Ordinal), "unrelated text changed");
    Assert(preview.NewText.Contains("\r\n", StringComparison.Ordinal), "CRLF not preserved");
    Assert(!switcher.Preview(yaml, "bad/provider").IsValid, "unsafe provider accepted");
    Assert(!switcher.Preview(yaml.Replace("DEFAULT: old-provider/alpha/variant", "DEFAULT: '@smol'"), "new-provider").IsValid, "invalid default accepted");

    var path = Path.Combine(ompRoot, "config.yml");
    await File.WriteAllTextAsync(path, yaml, new System.Text.UTF8Encoding(false));
    var result = await switcher.SwitchFileAsync(path, "new-provider");
    Assert(result.Succeeded, result.Error ?? "switch failed");
    Assert(result.BackupPath is not null && File.Exists(result.BackupPath), "backup missing");
    Assert(Path.GetFullPath(result.BackupPath!).StartsWith(Path.GetFullPath(ompRoot), StringComparison.Ordinal), "backup escaped omp root");
    var written = await File.ReadAllTextAsync(path);
    var backupText = await File.ReadAllTextAsync(result.BackupPath!);
    var settingsText = await File.ReadAllTextAsync(Path.Combine(dataRoot, "settings.json"));
    Assert(!written.Contains(secretSentinel, StringComparison.Ordinal)
        && !written.Contains(cookieSentinel, StringComparison.Ordinal)
        && !backupText.Contains(secretSentinel, StringComparison.Ordinal)
        && !backupText.Contains(cookieSentinel, StringComparison.Ordinal)
        && !settingsText.Contains(secretSentinel, StringComparison.Ordinal)
        && !settingsText.Contains(cookieSentinel, StringComparison.Ordinal),
        "synthetic credentials must not enter OMP config, backups or settings");
    Assert(written == preview.NewText, "atomic result differs from preview");
    Assert(written.Contains("unrelated: old-provider/untouched", StringComparison.Ordinal), "unrelated field changed");
    var testPaths = new TestPaths(ompRoot);
    Func<string, CancellationToken, Task<string>> readTextAsync = (file, cancellationToken) => File.ReadAllTextAsync(file, cancellationToken);
    var service = new OmpConfigurationService(new OmpConfigurationSwitcher(), testPaths, (file, cancellationToken) => readTextAsync(file, cancellationToken));
    File.Delete(path);
    var bootstrapResult = await service.TakeOverAsync(ompRoot, 15722);
    Assert(bootstrapResult.Succeeded && File.Exists(path), "first takeover must bootstrap a missing OMP config");
    var bootstrapped = await File.ReadAllTextAsync(path);
    Assert(bootstrapped.Contains("provider-price-switcher/gpt-5.6-sol", StringComparison.Ordinal), "bootstrap config must point model roles to the fixed local provider");
    await File.WriteAllTextAsync(path, yaml, new System.Text.UTF8Encoding(false));
    var serviceResult = await service.TakeOverAsync(ompRoot, 15722);
    Assert(serviceResult.Succeeded, "sidecar provider configuration failed");
    var takeoverStatus = await service.CheckAsync(ompRoot);
    Assert(takeoverStatus.Status == ProviderPriceSwitcher.Application.OmpTakeoverStatus.TakenOver && takeoverStatus.CurrentProviderId == ProviderPriceSwitcher.Application.OmpSidecarProvider.Id && takeoverStatus.CurrentGatewayPort == 15722, "takeover status must compare the current model role provider and endpoint");
    var partialFastDrift = "modelRoles:\r\n  default: provider-price-switcher/alpha\r\n  fast: old-provider/beta\r\ntask:\r\n  agentModelOverrides:\r\n    reviewer: provider-price-switcher/review\r\n";
    await File.WriteAllTextAsync(path, partialFastDrift, new System.Text.UTF8Encoding(false));
    takeoverStatus = await service.CheckAsync(ompRoot);
    Assert(takeoverStatus.Status == ProviderPriceSwitcher.Application.OmpTakeoverStatus.NotTakenOver, "takeover status must reject a direct fast role that still points to an old provider");
    var partialOverrideDrift = "modelRoles:\r\n  default: provider-price-switcher/alpha\r\n  fast: provider-price-switcher/beta\r\ntask:\r\n  agentModelOverrides:\r\n    reviewer: old-provider/review\r\n";
    await File.WriteAllTextAsync(path, partialOverrideDrift, new System.Text.UTF8Encoding(false));
    takeoverStatus = await service.CheckAsync(ompRoot);
    Assert(takeoverStatus.Status == ProviderPriceSwitcher.Application.OmpTakeoverStatus.NotTakenOver, "takeover status must reject a direct agent model override that still points to an old provider");
    await File.WriteAllTextAsync(path, "modelRoles:\r\n  default: provider-price-switcher/alpha\r\ntask:\r\n  agentModelOverrides:\r\n    reviewer: provider-price-switcher/review\r\n", new System.Text.UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(ompRoot, "agent", "models.yml"), "providers:\r\n  provider-price-switcher:\r\n    baseUrl: https://malicious.example/v1\r\n  old-provider:\r\n    baseUrl: http://127.0.0.1:15722/v1\r\n", new System.Text.UTF8Encoding(false));
    takeoverStatus = await service.CheckAsync(ompRoot);
    Assert(takeoverStatus.Status == ProviderPriceSwitcher.Application.OmpTakeoverStatus.ReadFailed, "takeover status must not read a loopback port from a sibling provider block");
    serviceResult = await service.TakeOverAsync(ompRoot, 15722);
    await File.WriteAllTextAsync(Path.Combine(ompRoot, "agent", "models.yml"), "providers:\r\n  provider-price-switcher:\r\n    api: openai-completions\r\nmetadata:\r\n    baseUrl: http://127.0.0.1:16666/v1\r\n", new System.Text.UTF8Encoding(false));
    takeoverStatus = await service.CheckAsync(ompRoot);
    Assert(takeoverStatus.Status == ProviderPriceSwitcher.Application.OmpTakeoverStatus.ReadFailed, "takeover status must stop at a top-level mapping after the sidecar provider block");
    serviceResult = await service.TakeOverAsync(ompRoot, 15722);
    Assert(serviceResult.Succeeded, "sidecar provider repair after top-level-boundary check failed");
    Assert(serviceResult.Succeeded, "sidecar provider repair after endpoint-boundary check failed");
    var models = await File.ReadAllTextAsync(Path.Combine(ompRoot, "agent", "models.yml"));
    Assert(models.Contains("provider-price-switcher:", StringComparison.Ordinal) && models.Contains("baseUrl: http://127.0.0.1:15722/v1", StringComparison.Ordinal) && models.Contains("api: openai-responses", StringComparison.Ordinal) && models.Contains("apiKey: PPS_SIDECAR_PLACEHOLDER", StringComparison.Ordinal) && models.Contains("id: gpt-5.6-sol", StringComparison.Ordinal) && !models.Contains("existing:", StringComparison.Ordinal), "fixed sidecar provider missing or old providers retained");
    Assert(!models.Contains("sk-", StringComparison.Ordinal), "raw key leaked into OMP provider config");
    await File.WriteAllTextAsync(Path.Combine(ompRoot, "agent", "models.yml"), "providers:\r\n  provider-price-switcher:\r\n    baseUrl: https://malicious.example/v1\r\n    apiKey: SHOULD_NOT_SURVIVE\r\n    api: openai-completions\r\n", new System.Text.UTF8Encoding(false));
    serviceResult = await service.TakeOverAsync(ompRoot, 15722);
    Assert(serviceResult.Succeeded, "existing sidecar provider correction failed");
    models = await File.ReadAllTextAsync(Path.Combine(ompRoot, "agent", "models.yml"));
    Assert(models.Contains("baseUrl: http://127.0.0.1:15722/v1", StringComparison.Ordinal) && models.Contains("api: openai-responses", StringComparison.Ordinal) && !models.Contains("malicious.example", StringComparison.Ordinal) && !models.Contains("SHOULD_NOT_SURVIVE", StringComparison.Ordinal), "existing sidecar provider was not forced to fixed local definition");
    for (var index = 0; index < 8; index++)
    {
        await Task.Delay(2);
        var repeated = await new OmpConfigurationSwitcher().SwitchFileAsync(path, ProviderPriceSwitcher.Application.OmpSidecarProvider.Id);
        Assert(repeated.Succeeded, "repeated takeover rewrite failed");
    }
    var retainedBackups = Directory.GetFiles(ompRoot, "config.yml.bak-*.yml");
    Assert(retainedBackups.Length == 5, "OMP configuration backup retention must keep exactly the newest five tool backups");
    var contentBeforeFailedWrite = await File.ReadAllTextAsync(path);
    await using (var sourceLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var failedWrite = await new OmpConfigurationSwitcher().SwitchFileAsync(path, ProviderPriceSwitcher.Application.OmpSidecarProvider.Id);
        Assert(!failedWrite.Succeeded, $"A locked source must simulate configuration replacement failure after backup creation; exception={failedWrite.Exception?.GetType().Name ?? "none"}.");
        Assert(failedWrite.BackupPath is not null && File.Exists(failedWrite.BackupPath), "The complete backup created before a failed configuration write must remain available.");
        retainedBackups = Directory.GetFiles(ompRoot, "config.yml.bak-*.yml");
        Assert(retainedBackups.Length == 5 && retainedBackups.Contains(failedWrite.BackupPath, StringComparer.OrdinalIgnoreCase), "A failed-write backup must participate in newest-five retention.");
        Assert(await File.ReadAllTextAsync(path) == contentBeforeFailedWrite, "A failed configuration replacement must preserve the source file.");
    }
    var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    readTextAsync = async (file, cancellationToken) =>
    {
        readStarted.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return string.Empty;
    };
    using (var readCancellation = new CancellationTokenSource())
    {
        var canceledCheck = service.CheckAsync(ompRoot, readCancellation.Token);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        readCancellation.Cancel();
        try
        {
            await canceledCheck;
            throw new InvalidOperationException("Configuration check ignored cancellation while reading.");
        }
        catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            readTextAsync = (file, cancellationToken) => File.ReadAllTextAsync(file, cancellationToken);
        }
    }
    foreach (var sentinel in sentinels) Assert(File.ReadAllText(sentinel) == runId, "isolation sentinel changed");
    Console.WriteLine("OMP configuration runner passed.");
}
finally
{
    Directory.Delete(root, recursive: true);
    Assert(!Directory.Exists(root), "isolated OMP root was not removed");
}

sealed class TestPaths(string root) : ProviderPriceSwitcher.Application.IAppPathDefaults
{
    public ManualResetEventSlim? ConfigPathObserved { get; set; }
    public string OmpRootDirectory => root;
    public string OmpConfigPath(string ompRootDirectory)
    {
        ConfigPathObserved?.Set();
        return Path.Combine(ompRootDirectory, "config.yml");
    }
    public string OmpModelsPath(string ompRootDirectory) => Path.Combine(ompRootDirectory, "agent", "models.yml");
    public string OmpAgentDirectory(string ompRootDirectory) => Path.Combine(ompRootDirectory, "agent");
}
