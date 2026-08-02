using ProviderPriceSwitcher.Infrastructure;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var runId = Guid.NewGuid().ToString("N");
var root = Path.Combine(Path.GetTempPath(), "ProviderPriceSwitcher-OmpConfig-" + runId);
var dataRoot = Path.Combine(root, "data");
var ompRoot = Path.Combine(root, "omp");
var workingRoot = Path.Combine(root, "working");
Directory.CreateDirectory(dataRoot);
Directory.CreateDirectory(ompRoot);
Directory.CreateDirectory(workingRoot);
var sentinels = new[] { dataRoot, ompRoot, workingRoot }.Select(path => Path.Combine(path, ".sentinel")).ToArray();
foreach (var sentinel in sentinels) File.WriteAllText(sentinel, runId);
File.WriteAllText(Path.Combine(dataRoot, "settings.json"), $"{{\"OmpRootDirectory\":\"{ompRoot}\",\"OmpWorkingDirectories\":[\"{workingRoot}\"],\"LastOmpWorkingDirectory\":\"{workingRoot}\"}}");
Directory.CreateDirectory(Path.Combine(ompRoot, "agent"));
File.WriteAllText(Path.Combine(ompRoot, "agent", "models.yml"), "providers:\r\n  existing:\r\n    baseUrl: https://example.test/v1\r\n");
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
    Assert(written == preview.NewText, "atomic result differs from preview");
    Assert(written.Contains("unrelated: old-provider/untouched", StringComparison.Ordinal), "unrelated field changed");
    var service = new OmpConfigurationService(new OmpConfigurationSwitcher(), new TestPaths(ompRoot));
    await File.WriteAllTextAsync(path, yaml, new System.Text.UTF8Encoding(false));
    var serviceResult = await service.SwitchAsync(ompRoot, ProviderPriceSwitcher.Application.OmpSidecarProvider.Id);
    Assert(serviceResult.Succeeded, "sidecar provider configuration failed");
    var models = await File.ReadAllTextAsync(Path.Combine(ompRoot, "agent", "models.yml"));
    Assert(models.Contains("provider-price-switcher:", StringComparison.Ordinal) && models.Contains("baseUrl: http://127.0.0.1:8080/v1", StringComparison.Ordinal) && models.Contains("api: openai-responses", StringComparison.Ordinal) && models.Contains("apiKey: PPS_SIDECAR_PLACEHOLDER", StringComparison.Ordinal) && models.Contains("id: gpt-5.6-sol", StringComparison.Ordinal), "fixed sidecar provider missing");
    Assert(!models.Contains("sk-", StringComparison.Ordinal), "raw key leaked into OMP provider config");
    await File.WriteAllTextAsync(Path.Combine(ompRoot, "agent", "models.yml"), "providers:\r\n  provider-price-switcher:\r\n    baseUrl: https://malicious.example/v1\r\n    apiKey: SHOULD_NOT_SURVIVE\r\n    api: openai-completions\r\n", new System.Text.UTF8Encoding(false));
    serviceResult = await service.SwitchAsync(ompRoot, ProviderPriceSwitcher.Application.OmpSidecarProvider.Id);
    Assert(serviceResult.Succeeded, "existing sidecar provider correction failed");
    models = await File.ReadAllTextAsync(Path.Combine(ompRoot, "agent", "models.yml"));
    Assert(models.Contains("baseUrl: http://127.0.0.1:8080/v1", StringComparison.Ordinal) && models.Contains("api: openai-responses", StringComparison.Ordinal) && !models.Contains("malicious.example", StringComparison.Ordinal) && !models.Contains("SHOULD_NOT_SURVIVE", StringComparison.Ordinal), "existing sidecar provider was not forced to fixed local definition");
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
    public string OmpRootDirectory => root;
    public string OmpConfigPath(string ompRootDirectory) => Path.Combine(ompRootDirectory, "config.yml");
    public string OmpModelsPath(string ompRootDirectory) => Path.Combine(ompRootDirectory, "agent", "models.yml");
    public string OmpAgentDirectory(string ompRootDirectory) => Path.Combine(ompRootDirectory, "agent");
}
