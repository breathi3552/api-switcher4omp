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
var expectedOfficialConfig = "# keep this comment\r\nmodelRoles:\r\n  default: openai-codex/gpt-5\r\n  fast: openai-codex/GPT-4o\r\n  deepseek: deepseek/deepseek-chat\r\n  suffix: another-provider/my-gpt\r\n  official: openai-codex/gpt-5\r\ntask:\r\n  agentModelOverrides:\r\n    reviewer: openai-codex/gpt-4o-mini\r\n    claude: anthropic/claude-sonnet\r\nunrelated: old-provider/untouched\r\n";
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
    var expectedOfficialChanges = new[]
    {
        new OmpConfigurationReplacementChange("modelRoles.default", "old-provider", "openai-codex", "gpt-5", "old-provider/gpt-5", "openai-codex/gpt-5"),
        new OmpConfigurationReplacementChange("modelRoles.fast", "another-provider", "openai-codex", "GPT-4o", "another-provider/GPT-4o", "openai-codex/GPT-4o"),
        new OmpConfigurationReplacementChange("task.agentModelOverrides.reviewer", "another-provider", "openai-codex", "gpt-4o-mini", "another-provider/gpt-4o-mini", "openai-codex/gpt-4o-mini")
    };
    Assert(officialPreview.Changes.SequenceEqual(expectedOfficialChanges), "preview must describe the exact ordered route plan");
    Assert(paths.ModelsReadCount == 0, "official preview must not read models.yml");
    var modelsBeforeOfficial = await File.ReadAllTextAsync(modelsPath);
    var tamperedChanges = officialPreview.Changes
        .Select((change, index) => index == 0 ? change with { ModelId = "gpt-tampered" } : change)
        .ToArray();
    var tamperedPreview = officialPreview with { Changes = tamperedChanges };
    var tamperedResult = await useCase.ExecuteAsync(settings, tamperedPreview);
    Assert(
        !tamperedResult.Succeeded
        && tamperedResult.FailureKind == OmpConfigurationReplacementFailureKind.StalePreview
        && await File.ReadAllTextAsync(configPath) == config,
        "execution must reject a preview whose semantic route items do not match the shared plan");
    var tamperedOfficialOwnershipPreview = officialPreview with
    {
        ModelsChangeKind = OmpModelsProviderChangeKind.Added,
        ModelsVersion = "tampered-models-version"
    };
    var tamperedOfficialOwnershipResult = await useCase.ExecuteAsync(settings, tamperedOfficialOwnershipPreview);
    Assert(
        !tamperedOfficialOwnershipResult.Succeeded
        && tamperedOfficialOwnershipResult.FailureKind == OmpConfigurationReplacementFailureKind.StalePreview
        && paths.ModelsReadCount == 0
        && await File.ReadAllTextAsync(configPath) == config,
        "official execution must reject models ownership metadata that is not part of its unified plan without reading models.yml");
    var officialResult = await useCase.ExecuteAsync(settings, officialPreview);
    Assert(officialResult.Succeeded && paths.ModelsReadCount == 0, "official execution must not touch models.yml");
    Assert(await File.ReadAllTextAsync(modelsPath) == modelsBeforeOfficial, "official execution changed models.yml");
    var officialConfig = await File.ReadAllTextAsync(configPath);
    Assert(officialConfig == expectedOfficialConfig, "execution must write every previewed route and preserve every unpreviewed route and source line");

    var officialNoOp = await useCase.PreviewAsync(settings, OmpConfigurationReplacementTargets.OfficialOAuthProviderId);
    Assert(officialNoOp.IsNoOp && officialNoOp.Changes.Count == 0, "repeated official replacement must be a no-op");
    var noOpResult = await useCase.ExecuteAsync(settings, officialNoOp);
    Assert(noOpResult.Succeeded && !noOpResult.ConfigurationChanged && !noOpResult.ModelsChanged, "no-op execution must not write files");

    paths.RejectModelsReads = false;
    var localPreview = await useCase.PreviewAsync(settings, OmpConfigurationReplacementTargets.LocalProviderId);
    Assert(localPreview.Succeeded && localPreview.Changes.Count == 4 && localPreview.ModelsChangeKind == OmpModelsProviderChangeKind.Updated, "local preview must include all eligible GPT routes and a managed provider update");
    await File.AppendAllTextAsync(configPath, "# concurrent config change\r\n");
    var modelsReadsBeforeStaleConfig = paths.ModelsReadCount;
    paths.RejectModelsReads = true;
    var staleConfigResult = await useCase.ExecuteAsync(settings, localPreview);
    paths.RejectModelsReads = false;
    Assert(
        !staleConfigResult.Succeeded
        && staleConfigResult.FailureKind == OmpConfigurationReplacementFailureKind.StalePreview
        && paths.ModelsReadCount == modelsReadsBeforeStaleConfig,
        "a stale config must reject execution before local models.yml is accessed");
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
    var syntheticCredential = "PPS_SYNTHETIC_CREDENTIAL_MUST_NOT_SURVIVE";
    var duplicateManagedModels = $"""
        # preserve this catalog
        providers:
            existing:
              baseUrl: https://example.test/v1
            provider-price-switcher: # first managed definition
              baseUrl: https://old.example/v1
              apiKey: {syntheticCredential}
        # keep comment between duplicates
            'provider-price-switcher': # duplicate managed definition
              baseUrl: https://duplicate.example/v1
              apiKey: another-old-placeholder
            other:
              baseUrl: https://other.example/v1
        metadata:
          note: preserve-me
        """.Replace("\n", "\r\n", StringComparison.Ordinal);
    await File.WriteAllTextAsync(modelsPath, duplicateManagedModels);
    var duplicateManagedPreview = await useCase.PreviewAsync(settings, OmpConfigurationReplacementTargets.LocalProviderId);
    Assert(
        duplicateManagedPreview.Succeeded
        && duplicateManagedPreview.Changes.Count == 0
        && duplicateManagedPreview.ModelsChangeKind == OmpModelsProviderChangeKind.Updated,
        "duplicate managed Provider definitions must preview as one local ownership update");
    var duplicateManagedResult = await useCase.ExecuteAsync(settings, duplicateManagedPreview);
    var convergedModels = await File.ReadAllTextAsync(modelsPath);
    Assert(
        duplicateManagedResult.Succeeded
        && duplicateManagedResult.ModelsChanged
        && convergedModels.Split("provider-price-switcher:", StringSplitOptions.None).Length == 2
        && convergedModels.Contains("# keep comment between duplicates", StringComparison.Ordinal)
        && convergedModels.Contains("existing:", StringComparison.Ordinal)
        && convergedModels.Contains("other:", StringComparison.Ordinal)
        && convergedModels.Contains("metadata:", StringComparison.Ordinal)
        && !convergedModels.Contains(syntheticCredential, StringComparison.Ordinal),
        "local ownership execution must converge duplicate inline-comment keys without leaking replaced credentials or changing unrelated content");

    var modelsWithoutManagedProvider = """
        # preserve this catalog
        providers:
            existing:
              baseUrl: https://example.test/v1
            other:
              baseUrl: https://other.example/v1
        metadata:
          note: preserve-me
        """.Replace("\n", "\r\n", StringComparison.Ordinal);
    await File.WriteAllTextAsync(modelsPath, modelsWithoutManagedProvider);
    var missingLocalProviderPreview = await useCase.PreviewAsync(settings, OmpConfigurationReplacementTargets.LocalProviderId);
    Assert(missingLocalProviderPreview.Succeeded && missingLocalProviderPreview.IsNoOp == false && missingLocalProviderPreview.ModelsChangeKind == OmpModelsProviderChangeKind.Added && missingLocalProviderPreview.Changes.Count == 0, "local target must maintain its provider definition even when every GPT route already uses it");
    var missingLocalProviderResult = await useCase.ExecuteAsync(settings, missingLocalProviderPreview);
    var modelsWithManagedProvider = await File.ReadAllTextAsync(modelsPath);
    Assert(
        missingLocalProviderResult.Succeeded
        && !missingLocalProviderResult.ConfigurationChanged
        && missingLocalProviderResult.ModelsChanged
        && modelsWithManagedProvider.Contains("provider-price-switcher:", StringComparison.Ordinal)
        && modelsWithManagedProvider.Contains("existing:", StringComparison.Ordinal)
        && modelsWithManagedProvider.Contains("other:", StringComparison.Ordinal)
        && modelsWithManagedProvider.Contains("metadata:", StringComparison.Ordinal),
        "local target must add only the missing managed Provider definition");

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
    var configBeforeModelsReadFailure = await File.ReadAllTextAsync(configPath);
    using (var modelsLock = new FileStream(modelsPath, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var localReadFailure = await useCase.PreviewAsync(portSettings, OmpConfigurationReplacementTargets.LocalProviderId);
        Assert(
            !localReadFailure.Succeeded
            && localReadFailure.FailureKind == OmpConfigurationReplacementFailureKind.ModelsReadFailed
            && localReadFailure.ErrorMessage is not null
            && !localReadFailure.ErrorMessage.Contains(syntheticCredential, StringComparison.Ordinal)
            && await File.ReadAllTextAsync(configPath) == configBeforeModelsReadFailure,
            "a local models.yml read failure must be structured, sanitized, and leave config.yml unchanged");
        var officialWhileModelsUnreadable = await useCase.PreviewAsync(portSettings, OmpConfigurationReplacementTargets.OfficialOAuthProviderId);
        Assert(
            officialWhileModelsUnreadable.Succeeded,
            "official Provider preview must not read models.yml even when that file is unreadable");
    }


    await File.WriteAllTextAsync(configPath, "modelRoles:\r\n  default: old-provider/gpt-5\r\nnot-a-mapping\r\n");
    var malformedShapePreview = await useCase.PreviewAsync(portSettings, OmpConfigurationReplacementTargets.OfficialOAuthProviderId);
    Assert(!malformedShapePreview.Succeeded && malformedShapePreview.FailureKind == OmpConfigurationReplacementFailureKind.ConfigurationInvalid, "invalid YAML mapping shape must return a structured error");
    Assert((await File.ReadAllTextAsync(configPath)).Contains("not-a-mapping", StringComparison.Ordinal), "invalid YAML mapping shape must not be rewritten");

    await File.WriteAllTextAsync(configPath, "modelRoles:\r\n  default: old-provider/gpt-5\r\n  - invalid\r\n");
    var malformedSequencePreview = await useCase.PreviewAsync(portSettings, OmpConfigurationReplacementTargets.OfficialOAuthProviderId);
    Assert(!malformedSequencePreview.Succeeded && malformedSequencePreview.FailureKind == OmpConfigurationReplacementFailureKind.ConfigurationInvalid, "invalid YAML sequence shape must return a structured error");
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
    var configBackupsBeforeCancellation = Directory.GetFiles(
        Path.GetDirectoryName(configPath)!,
        "config.yml.bak-*.yml").Length;
    var modelsBeforeCancellation = await File.ReadAllTextAsync(modelsPath);
    using (var previewCancellation = new CancellationTokenSource())
    {
        previewCancellation.Cancel();
        try
        {
            await useCase.PreviewAsync(
                portSettings,
                OmpConfigurationReplacementTargets.OfficialOAuthProviderId,
                previewCancellation.Token);
            throw new InvalidOperationException("canceled configuration preview was accepted");
        }
        catch (OperationCanceledException)
        {
        }
    }
    using (var executionCancellation = new CancellationTokenSource())
    {
        executionCancellation.Cancel();
        try
        {
            await useCase.ExecuteAsync(
                portSettings,
                failurePreview,
                executionCancellation.Token);
            throw new InvalidOperationException("canceled configuration execution was accepted");
        }
        catch (OperationCanceledException)
        {
        }
    }
    Assert(
        await File.ReadAllTextAsync(configPath) == config
        && await File.ReadAllTextAsync(modelsPath) == modelsBeforeCancellation
        && Directory.GetFiles(Path.GetDirectoryName(configPath)!, "config.yml.bak-*.yml").Length == configBackupsBeforeCancellation,
        "preview and execution cancellation must propagate without file writes");

    File.Delete(configPath);
    var missingPreview = await useCase.PreviewAsync(portSettings, OmpConfigurationReplacementTargets.OfficialOAuthProviderId);
    var missingResult = await useCase.ExecuteAsync(portSettings, missingPreview);
    Assert(
        !missingPreview.Succeeded
        && missingPreview.FailureKind == OmpConfigurationReplacementFailureKind.ConfigurationMissing
        && !missingResult.Succeeded
        && missingResult.FailureKind == OmpConfigurationReplacementFailureKind.ConfigurationMissing
        && !File.Exists(configPath)
        && await File.ReadAllTextAsync(modelsPath) == modelsBeforeCancellation,
        "a missing primary configuration must remain a structured zero-write failure");

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
