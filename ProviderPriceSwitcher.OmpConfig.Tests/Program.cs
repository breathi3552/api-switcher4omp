using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Infrastructure;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var runId = Guid.NewGuid().ToString("N");
var root = Path.Combine(Path.GetTempPath(), "ProviderPriceSwitcher-OmpConfig-" + runId);
var ompRoot = Path.Combine(root, "omp");
var paths = new TestPaths(ompRoot);
var configPath = paths.OmpConfigPath(ompRoot);
var modelsPath = paths.OmpModelsPath(ompRoot);
var config = "# keep this comment\r\nmodelRoles:\r\n  default: old-provider/gpt-5\r\n  fast: another-provider/GPT-4o\r\n  deepseek: deepseek/deepseek-chat\r\n  suffix: another-provider/my-gpt\r\n  official: openai-codex/gpt-5\r\ntask:\r\n  agentModelOverrides:\r\n    reviewer: another-provider/gpt-4o-mini\r\n    claude: anthropic/claude-sonnet\r\nunrelated: old-provider/untouched\r\n";
var models = "# preserve this catalog\r\nproviders:\r\n    existing:\r\n      baseUrl: https://example.test/v1\r\n      models:\r\n        - id: existing-model\r\n    provider-price-switcher: # managed definition\r\n      baseUrl: https://old.example/v1\r\n      apiKey: old-placeholder\r\n# keep comment between providers\r\n    other:\r\n      baseUrl: https://other.example/v1\r\nmetadata:\r\n  note: preserve-me\r\n";

try
{
    Directory.CreateDirectory(Path.Combine(ompRoot, "agent"));
    await File.WriteAllTextAsync(configPath, config);
    await File.WriteAllTextAsync(modelsPath, models);

    var service = new OmpConfigurationService(new OmpConfigurationSwitcher(), paths);
    var useCase = new OmpConfigurationReplacementUseCase(service);
    var settings = new LocalAppSettings
    {
        OmpRootDirectory = ompRoot,
        CurrentGatewayPort = 15722
    };
    paths.ModelsReadCount = 0;
    paths.RejectModelsReads = true;
    var officialPreview = await useCase.PreviewAsync(settings, OmpConfigurationReplacementTargets.OfficialOAuthProviderId);
    Assert(officialPreview.Succeeded && officialPreview.Changes.Count == 3, "official preview must contain only eligible GPT routes");
    Assert(officialPreview.Changes.All(change => change.TargetProvider == "openai-codex" && change.ModelId.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)), "official preview target or model id mismatch");
    Assert(paths.ModelsReadCount == 0, "official preview must not read models.yml");
    var modelsBeforeOfficial = await File.ReadAllTextAsync(modelsPath);
    var officialResult = await useCase.ExecuteAsync(settings, officialPreview);
    Assert(officialResult.Succeeded && paths.ModelsReadCount == 0, "official execution must not touch models.yml");
    Assert(await File.ReadAllTextAsync(modelsPath) == modelsBeforeOfficial, "official execution changed models.yml");
    var officialConfig = await File.ReadAllTextAsync(configPath);
    Assert(officialConfig.Contains("default: openai-codex/gpt-5", StringComparison.Ordinal)
        && officialConfig.Contains("fast: openai-codex/GPT-4o", StringComparison.Ordinal)
        && officialConfig.Contains("reviewer: openai-codex/gpt-4o-mini", StringComparison.Ordinal)
        && officialConfig.Contains("deepseek: deepseek/deepseek-chat", StringComparison.Ordinal)
        && officialConfig.Contains("suffix: another-provider/my-gpt", StringComparison.Ordinal)
        && officialConfig.Contains("unrelated: old-provider/untouched", StringComparison.Ordinal)
        && officialConfig.Contains("\r\n", StringComparison.Ordinal),
        "official execution must preserve non-GPT routes and unrelated configuration text");

    var officialNoOp = await useCase.PreviewAsync(settings, OmpConfigurationReplacementTargets.OfficialOAuthProviderId);
    Assert(officialNoOp.IsNoOp && officialNoOp.Changes.Count == 0, "repeated official replacement must be a no-op");
    var noOpResult = await useCase.ExecuteAsync(settings, officialNoOp);
    Assert(noOpResult.Succeeded && !noOpResult.ConfigurationChanged && !noOpResult.ModelsChanged, "no-op execution must not write files");

    paths.RejectModelsReads = false;
    var localPreview = await useCase.PreviewAsync(settings, OmpConfigurationReplacementTargets.LocalProviderId);
    Assert(localPreview.Succeeded && localPreview.Changes.Count == 4 && localPreview.ModelsChangeKind == OmpModelsProviderChangeKind.Updated, "local preview must include all eligible GPT routes and a managed provider update");
    await File.AppendAllTextAsync(configPath, "# concurrent config change\r\n");
    var staleConfigResult = await useCase.ExecuteAsync(settings, localPreview);
    Assert(!staleConfigResult.Succeeded && staleConfigResult.FailureKind == OmpConfigurationReplacementFailureKind.StalePreview, "config changes after preview must reject execution");
    localPreview = await useCase.PreviewAsync(settings, OmpConfigurationReplacementTargets.LocalProviderId);
    await File.AppendAllTextAsync(modelsPath, "# concurrent models change\r\n");
    var staleModelsResult = await useCase.ExecuteAsync(settings, localPreview);
    Assert(!staleModelsResult.Succeeded && staleModelsResult.FailureKind == OmpConfigurationReplacementFailureKind.StalePreview, "models changes after preview must reject execution");
    localPreview = await useCase.PreviewAsync(settings, OmpConfigurationReplacementTargets.LocalProviderId);
    var localResult = await useCase.ExecuteAsync(settings, localPreview);
    Assert(localResult.Succeeded && localResult.ConfigurationChanged && localResult.ModelsChanged, "local replacement must update both managed files");
    var localConfig = await File.ReadAllTextAsync(configPath);
    var localModels = await File.ReadAllTextAsync(modelsPath);
    Assert(localConfig.Contains("default: provider-price-switcher/gpt-5", StringComparison.Ordinal)
        && localConfig.Contains("fast: provider-price-switcher/GPT-4o", StringComparison.Ordinal)
        && localConfig.Contains("official: provider-price-switcher/gpt-5", StringComparison.Ordinal)
        && localConfig.Contains("reviewer: provider-price-switcher/gpt-4o-mini", StringComparison.Ordinal),
        "local replacement must preserve GPT ModelIds");
    Assert(localModels.Contains("existing:", StringComparison.Ordinal)
        && localModels.Contains("other:", StringComparison.Ordinal)
        && localModels.Contains("metadata:", StringComparison.Ordinal)
        && localModels.Contains("note: preserve-me", StringComparison.Ordinal)
        && localModels.Contains("# keep comment between providers", StringComparison.Ordinal)
        && localModels.Contains("baseUrl: http://127.0.0.1:15722/v1", StringComparison.Ordinal)
        && localModels.Contains("api: openai-responses", StringComparison.Ordinal)
        && localModels.Split("provider-price-switcher:", StringSplitOptions.None).Length == 2,
        "local provider update must preserve unrelated models.yml content and remain unique");
    File.Delete(modelsPath);
    var missingLocalProviderPreview = await useCase.PreviewAsync(settings, OmpConfigurationReplacementTargets.LocalProviderId);
    Assert(missingLocalProviderPreview.Succeeded && missingLocalProviderPreview.IsNoOp == false && missingLocalProviderPreview.ModelsChangeKind == OmpModelsProviderChangeKind.Added && missingLocalProviderPreview.Changes.Count == 0, "local target must maintain its provider definition even when every GPT route already uses it");
    var missingLocalProviderResult = await useCase.ExecuteAsync(settings, missingLocalProviderPreview);
    Assert(missingLocalProviderResult.Succeeded && !missingLocalProviderResult.ConfigurationChanged && missingLocalProviderResult.ModelsChanged && File.ReadAllText(modelsPath).Contains("provider-price-switcher:", StringComparison.Ordinal), "local target must restore a missing managed provider definition without route changes");

    var localNoOp = await useCase.PreviewAsync(settings, OmpConfigurationReplacementTargets.LocalProviderId);
    Assert(localNoOp.IsNoOp, "repeated local replacement with the same port must be a no-op");
    var modelsBeforeNoOp = await File.ReadAllTextAsync(modelsPath);
    var backupsBeforeNoOp = Directory.GetFiles(Path.GetDirectoryName(modelsPath)!, "models.yml.bak-*.yml").Length;
    var portSettings = settings with { CurrentGatewayPort = 17001 };
    var portPreview = await useCase.PreviewAsync(portSettings, OmpConfigurationReplacementTargets.LocalProviderId);
    Assert(!portPreview.IsNoOp && portPreview.Changes.Count == 0 && portPreview.ModelsChangeKind == OmpModelsProviderChangeKind.Updated, "a local target must update its managed Provider when the gateway port changes");
    var portResult = await useCase.ExecuteAsync(portSettings, portPreview);
    Assert(portResult.Succeeded && !portResult.ConfigurationChanged && portResult.ModelsChanged && (await File.ReadAllTextAsync(modelsPath)).Contains("baseUrl: http://127.0.0.1:17001/v1", StringComparison.Ordinal), "a local target port change must update models.yml even without route changes");
    Assert((await File.ReadAllTextAsync(modelsPath)) != modelsBeforeNoOp
        && Directory.GetFiles(Path.GetDirectoryName(modelsPath)!, "models.yml.bak-*.yml").Length == backupsBeforeNoOp,
        "a managed Provider update must not create a backup containing catalog credentials");

    await File.WriteAllTextAsync(configPath, "modelRoles:\r\n  default: old-provider/gpt-5\r\nnot-a-mapping\r\n");
    var malformedShapePreview = await useCase.PreviewAsync(portSettings, OmpConfigurationReplacementTargets.OfficialOAuthProviderId);
    Assert(!malformedShapePreview.Succeeded && malformedShapePreview.FailureKind == OmpConfigurationReplacementFailureKind.ConfigurationInvalid, "invalid YAML mapping shape must return a structured error");
    Assert((await File.ReadAllTextAsync(configPath)).Contains("not-a-mapping", StringComparison.Ordinal), "invalid YAML mapping shape must not be rewritten");

    await File.WriteAllTextAsync(configPath, "modelRoles:\r\n  default: old-provider/gpt-5\r\n  note: |\r\n    text containing [brackets]\r\n");
    var blockScalarPreview = await useCase.PreviewAsync(portSettings, OmpConfigurationReplacementTargets.OfficialOAuthProviderId);
    Assert(blockScalarPreview.Succeeded, "valid YAML block scalar must remain analyzable");

    await File.WriteAllTextAsync(configPath, "modelRoles:\r\n  default: old-provider/gpt-5\r\n  fast: [unterminated\r\n");
    var malformedPreview = await useCase.PreviewAsync(portSettings, OmpConfigurationReplacementTargets.OfficialOAuthProviderId);
    Assert(!malformedPreview.Succeeded && malformedPreview.FailureKind == OmpConfigurationReplacementFailureKind.ConfigurationInvalid, "malformed YAML must return a structured error");
    Assert((await File.ReadAllTextAsync(configPath)).Contains("[unterminated", StringComparison.Ordinal), "malformed YAML must not be rewritten");

    await File.WriteAllTextAsync(configPath, "modelRoles:\r\n  default: '@role'\r\n");
    var invalidPreview = await useCase.PreviewAsync(portSettings, OmpConfigurationReplacementTargets.LocalProviderId);
    Assert(!invalidPreview.Succeeded && invalidPreview.FailureKind == OmpConfigurationReplacementFailureKind.ConfigurationInvalid, "invalid config must return a structured error");
    Assert(!File.ReadAllText(configPath).Contains("provider-price-switcher/", StringComparison.Ordinal), "invalid config must not be bootstrapped or rewritten");

    await File.WriteAllTextAsync(configPath, config);
    var failurePreview = await useCase.PreviewAsync(portSettings, OmpConfigurationReplacementTargets.OfficialOAuthProviderId);
    using (var sourceLock = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var failed = await useCase.ExecuteAsync(portSettings, failurePreview);
        Assert(!failed.Succeeded && failed.FailureKind == OmpConfigurationReplacementFailureKind.ConfigurationWriteFailed, "locked config must report a write failure");
        Assert(await File.ReadAllTextAsync(configPath) == config, "failed config write must preserve the source");
    }

    Console.WriteLine("OMP configuration runner passed.");
}
finally
{
    if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
    Assert(!Directory.Exists(root), "isolated OMP root was not removed");
}

sealed class TestPaths(string root) : IAppPathDefaults
{
    public string OmpRootDirectory => root;
    public bool RejectModelsReads { get; set; }
    public int ModelsReadCount { get; set; }
    public string OmpConfigPath(string ompRootDirectory) => Path.Combine(ompRootDirectory, "agent", "config.yml");
    public string OmpModelsPath(string ompRootDirectory)
    {
        ModelsReadCount++;
        if (RejectModelsReads)
            throw new InvalidOperationException("models.yml must not be accessed for official Provider");
        return Path.Combine(ompRootDirectory, "agent", "models.yml");
    }
    public string OmpAgentDirectory(string ompRootDirectory) => Path.Combine(ompRootDirectory, "agent");
}
