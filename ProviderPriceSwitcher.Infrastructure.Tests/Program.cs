using System.Text.Json;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Infrastructure;
using ProviderPriceSwitcher.Application;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

var currentUserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("current user SID unavailable");
var privatePipeDescriptor = new System.Security.AccessControl.RawSecurityDescriptor($"O:{currentUserSid.Value}D:P(A;;FA;;;OW)");
var broadPipeDescriptor = new System.Security.AccessControl.RawSecurityDescriptor($"O:{currentUserSid.Value}D:P(A;;FA;;;WD)");
var foreignSid = new System.Security.Principal.SecurityIdentifier("S-1-5-18");
var foreignPipeDescriptor = new System.Security.AccessControl.RawSecurityDescriptor($"O:{currentUserSid.Value}D:P(A;;FA;;;OW)(A;;FA;;;{foreignSid.Value})");
Assert(WindowsNamedPipeSecurity.IsCurrentUserOnly(privatePipeDescriptor, currentUserSid) && !WindowsNamedPipeSecurity.IsCurrentUserOnly(broadPipeDescriptor, currentUserSid) && !WindowsNamedPipeSecurity.IsCurrentUserOnly(foreignPipeDescriptor, currentUserSid), "pipe ACL contract must accept owner-only access and reject world or foreign-SID access");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var root = Path.Combine(Path.GetTempPath(), "ProviderPriceSwitcher.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var settingsRepo = new JsonSettingsRepository(root);
    var defaults = settingsRepo.Load();
    Assert(defaults.Model == "gpt-5.6-sol" && defaults.RequestTimeoutSeconds == 10 && defaults.GatewayPort == 15722 && defaults.CurrentGatewayPort == 15722, "defaults");
    var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omp");
    Assert(defaults.OmpRootDirectory == expectedRoot && new AppPathDefaults().OmpConfigPath(defaults.OmpRootDirectory) == Path.Combine(expectedRoot, "agent", "config.yml"), "OMP root defaults and derived config path");

    settingsRepo.Save(defaults with { ActiveProviderId = "p", Sites = [new SiteConfiguration { ProviderId = "p", ConfigurationKey = "k", BaseUrl = new Uri("https://p.example"), Model = defaults.Model, CurrentGroup = "g" }] });
    Assert(settingsRepo.Load().ActiveProviderId == "p" && settingsRepo.Load().Sites.Single().ProviderId == "p" && settingsRepo.Load().Sites.Single().ConfigurationApiAddress == "/keys", "settings roundtrip, active provider identity and default configuration API address");
    File.WriteAllText(settingsRepo.FilePath, """
        {
          "model": "gpt-5.6-sol",
          "sites": [{
            "providerId": "legacy",
            "configurationKey": "legacy-key",
            "baseUrl": "https://legacy.example/",
            "model": "gpt-5.6-sol",
            "currentGroup": "g"
          }],
          "ompWorkingDirectories": ["C:\\Legacy", "D:\\Second"],
          "lastOmpWorkingDirectory": "C:\\Legacy",
          "unknownFutureField": { "keptOnlyOnRead": true }
        }
        """);
    var legacySettings = settingsRepo.Load();
    Assert(legacySettings.Sites.Single().ConfigurationApiAddress == "/keys" && legacySettings.OmpWorkingDirectories.SequenceEqual(["C:\\Legacy", "D:\\Second"]), "legacy settings arrays and defaults must load");
    settingsRepo.Save(legacySettings);
    using (var legacyRoundTrip = JsonDocument.Parse(File.ReadAllText(settingsRepo.FilePath)))
    {
        Assert(legacyRoundTrip.RootElement.GetProperty("sites").ValueKind == JsonValueKind.Array && legacyRoundTrip.RootElement.GetProperty("ompWorkingDirectories").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["C:\\Legacy", "D:\\Second"]), "legacy settings arrays must retain JSON shape and order");
        Assert(!legacyRoundTrip.RootElement.TryGetProperty("unknownFutureField", out _), "unknown settings fields must retain the existing ignore-on-save policy");
    }
    settingsRepo.Save(defaults with { Sites = [new SiteConfiguration { ProviderId = "explicit", ConfigurationKey = "k", BaseUrl = new Uri("https://p.example"), Model = defaults.Model, CurrentGroup = "g", ConfigurationApiAddress = "/custom/config" }] });
    Assert(settingsRepo.Load().Sites.Single().ConfigurationApiAddress == "/custom/config", "explicit configuration API address roundtrip");
    File.WriteAllText(settingsRepo.FilePath, """
        {
          "model": "gpt-5.6-sol",
          "resultTtl": "1.00:00:00",
          "ompModelsPath": "C:\\Users\\test\\.omp\\agent\\models.yml",
          "ompConfigPath": "C:\\Users\\test\\.omp\\agent\\config.yml"
        }
        """);
    var migrated = settingsRepo.Load();
    Assert(migrated.OmpRootDirectory == "C:\\Users\\test\\.omp" && new AppPathDefaults().OmpConfigPath(migrated.OmpRootDirectory) == "C:\\Users\\test\\.omp\\agent\\config.yml", "legacy OMP config path migration");
    var pawsRoot = new SiteConfiguration
    {
        ProviderId = "paws-root",
        DisplayName = "保留名称",
        ConfigurationKey = "old-root-key",
        BaseUrl = new Uri("https://ai.furry.edu.gr/"),
        SiteType = "new-api",
        Enabled = false,
        Model = "Model-X",
        CurrentGroup = " group ",
        CurrentGroupRatio = 2.5m,
        GroupRatioSource = "手动",
        AuthenticationMode = "令牌",
        Currency = "CNY",
        CnyConversionRate = 7.2m
    };
    var pawsEndpoint = pawsRoot with { ProviderId = "paws-endpoint", BaseUrl = new Uri("http://ai.furry.edu.gr/pawsai-pricing.json"), ConfigurationKey = "old-endpoint-key" };
    var unchanged = new[]
    {
        pawsRoot with { ProviderId = "other-host", BaseUrl = new Uri("https://other.example/"), ConfigurationKey = "keep-host" },
        pawsRoot with { ProviderId = "other-path", BaseUrl = new Uri("https://ai.furry.edu.gr/other"), ConfigurationKey = "keep-path" },
        pawsRoot with { ProviderId = "query", BaseUrl = new Uri("https://ai.furry.edu.gr/?x=1"), ConfigurationKey = "keep-query" },
        pawsRoot with { ProviderId = "fragment", BaseUrl = new Uri("https://ai.furry.edu.gr/#x"), ConfigurationKey = "keep-fragment" }
    };
    settingsRepo.Save(new LocalAppSettings { Sites = [pawsRoot, pawsEndpoint, .. unchanged] });
    var migratedSites = settingsRepo.Load().Sites;
    foreach (var migratedSite in migratedSites.Take(2))
    {
        Assert(migratedSite.SiteType == "pawsai" && migratedSite.BaseUrl.AbsoluteUri == $"{migratedSite.BaseUrl.Scheme}://{migratedSite.BaseUrl.Authority}/", "PawsAI root migration");
        Assert(migratedSite.ConfigurationKey == SiteConfigurationKey.Create(migratedSite.ProviderId, "pawsai", migratedSite.BaseUrl, migratedSite.Model, migratedSite.CurrentGroup), "PawsAI key rebuild");
        Assert(migratedSite.DisplayName == "保留名称" && !migratedSite.Enabled && migratedSite.CurrentGroupRatio == 2.5m && migratedSite.Currency == "CNY", "PawsAI fields preserved");
    }
    Assert(migratedSites[2].SiteType == "new-api" && migratedSites[2].ConfigurationKey == "keep-host", "other host not migrated");
    Assert(migratedSites[3].ConfigurationKey == "keep-path" && migratedSites[4].ConfigurationKey == "keep-query" && migratedSites[5].ConfigurationKey == "keep-fragment", "other path/query/fragment not migrated");
    settingsRepo.Save(new LocalAppSettings { Sites = migratedSites });
    var roundTripSites = settingsRepo.Load().Sites;
    Assert(roundTripSites.Select(x => x.ConfigurationKey).SequenceEqual(migratedSites.Select(x => x.ConfigurationKey)), "PawsAI migration idempotent and save roundtrip");
    var sevnxRoot = new SiteConfiguration
    {
        ProviderId = "sevnx",
        DisplayName = "SevnX",
        ConfigurationKey = "old-sevnx-key",
        BaseUrl = new Uri("https://www.sevnx.one/login"),
        SiteType = "new-api",
        Enabled = true,
        Model = "gpt-5.6-sol",
        CurrentGroup = "default",
        CurrentGroupRatio = 1.5m,
        GroupRatioSource = "手动",
        AuthenticationMode = "账户登录"
    };
    settingsRepo.Save(new LocalAppSettings { Sites = [sevnxRoot] });
    var migratedSevnx = settingsRepo.Load().Sites.Single();
    Assert(migratedSevnx.SiteType == "sevnx" && migratedSevnx.BaseUrl.AbsoluteUri == "https://www.sevnx.one/", "SevnX root migration");
    Assert(migratedSevnx.AuthenticationMode == "导入令牌", "SevnX auth mode migration");
    Assert(migratedSevnx.ConfigurationKey == SiteConfigurationKey.Create("sevnx", "sevnx", migratedSevnx.BaseUrl, migratedSevnx.Model, migratedSevnx.CurrentGroup), "SevnX key rebuild");
    var legacySevnx = sevnxRoot with
    {
        ProviderId = "legacy-sevnx",
        SiteType = "sub2api",
        ConfigurationKey = "legacy-sub2api-key",
        BaseUrl = new Uri("https://legacy.sevnx.example/api"),
        AuthenticationMode = "保留认证"
    };
    settingsRepo.Save(new LocalAppSettings { Sites = [legacySevnx] });
    var migratedLegacySevnx = settingsRepo.Load().Sites.Single();
    Assert(migratedLegacySevnx.SiteType == "sevnx" && migratedLegacySevnx.BaseUrl.AbsoluteUri == "https://legacy.sevnx.example/api", "legacy sub2api migration");
    Assert(migratedLegacySevnx.AuthenticationMode == "保留认证" && migratedLegacySevnx.ProviderId == "legacy-sevnx", "legacy migration preserves fields");
    Assert(migratedLegacySevnx.ConfigurationKey == SiteConfigurationKey.Create("legacy-sevnx", "sevnx", migratedLegacySevnx.BaseUrl, migratedLegacySevnx.Model, migratedLegacySevnx.CurrentGroup), "legacy SevnX key rebuild");
    settingsRepo.Save(new LocalAppSettings { Sites = [migratedLegacySevnx] });
    var idempotentSevnx = settingsRepo.Load().Sites.Single();
    Assert(idempotentSevnx == migratedLegacySevnx, "SevnX migration idempotent");
    var savedSevnxSettings = File.ReadAllText(settingsRepo.FilePath);
    Assert(!savedSevnxSettings.Contains("sub2api", StringComparison.Ordinal), "saved settings must not retain sub2api");
    Assert(SiteConfigurationKey.Create(" Provider ", "new-api", new Uri("HTTPS://Example.COM/base/"), " model ", " group ") == SiteConfigurationKey.Create("provider", "new-api", new Uri("https://example.com/base"), "model", "group"), "key regression");
    Assert(SiteConfigurationKey.Create("provider", "new-api", new Uri("https://example.com/base"), "model", "group") != SiteConfigurationKey.Create("provider", "pawsai", new Uri("https://example.com/base"), "model", "group"), "old snapshot key mismatch");
    settingsRepo.Save(migrated);
    var savedSettings = File.ReadAllText(settingsRepo.FilePath);
    Assert(savedSettings.Contains("ompRootDirectory", StringComparison.Ordinal) && savedSettings.Contains("gatewayPort", StringComparison.Ordinal) && savedSettings.Contains("currentGatewayPort", StringComparison.Ordinal) && !savedSettings.Contains("resultTtl", StringComparison.Ordinal) && !savedSettings.Contains("ompModelsPath", StringComparison.Ordinal) && !savedSettings.Contains("ompConfigPath", StringComparison.Ordinal), "settings port persistence and retired fields");
    Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "atomic settings write");
    settingsRepo.Save(defaults);
    try { settingsRepo.Save(defaults with { GatewayPort = 0 }); throw new InvalidOperationException("invalid gateway port was saved"); } catch (ArgumentOutOfRangeException) { }
    try { settingsRepo.Save(defaults with { CurrentGatewayPort = 0 }); throw new InvalidOperationException("invalid current gateway port was saved"); } catch (ArgumentOutOfRangeException) { }
    Assert(File.Exists(settingsRepo.FilePath + ".bak") && !Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "atomic settings write and backup");
    File.WriteAllText(settingsRepo.FilePath, "{\"gatewayPort\":0,\"currentGatewayPort\":15722}");
    try { settingsRepo.Load(); throw new InvalidOperationException("invalid gateway port accepted"); } catch (JsonDataException) { }
    settingsRepo.Save(defaults);
    File.WriteAllText(settingsRepo.FilePath, "{ invalid");
    try { settingsRepo.Load(); throw new InvalidOperationException("corrupt settings accepted"); } catch (JsonDataException) { }
    settingsRepo.Save(defaults);

    PricingSnapshot Snapshot(string provider, decimal value) => new()
    {
        ProviderId = provider,
        ConfigurationKey = "k",
        Model = "gpt-5.6-sol",
        CurrentGroup = "g",
        BasePrices = new TokenPrices { InputPerMillion = value, CachedInputPerMillion = 0, OutputPerMillion = 0 },
        CurrentGroupRatio = 1,
        Prices = new TokenPrices { InputPerMillion = value, CachedInputPerMillion = 0, OutputPerMillion = 0 },
        RefreshedAt = DateTimeOffset.UtcNow
    };
    var snapshots = new JsonPricingSnapshotRepository(root);
    snapshots.Save(Snapshot("../escape:name?", 1));
    snapshots.Save(Snapshot("../escape:name?", 2));
    Assert(snapshots.Load("../escape:name?")!.Prices.InputPerMillion == 2 && snapshots.LoadAll().Count == 1, "snapshot overwrite and safe provider id");
    snapshots.Delete("../escape:name?");
    Assert(snapshots.Load("../escape:name?") is null, "snapshot deletion");
    snapshots.Save(Snapshot("manual", 2));
    var manual = snapshots.Load("manual")!.WithCurrentRatio(0.5m, "手动");
    Assert(manual.CurrentGroupRatio == 0.5m && manual.GroupRatioSource == "手动" && manual.Prices.InputPerMillion == 1m, "manual ratio recalculates prices");
    File.WriteAllText(snapshots.FilePath, "not json");
    try { snapshots.LoadAll(); throw new InvalidOperationException("corrupt snapshots accepted"); } catch (JsonDataException) { }
    Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "atomic snapshot write");
    var credentialSummaryStore = new WindowsSiteCredentialStore(root);
    credentialSummaryStore.SaveCredential(new SiteCredentialRecord
    {
        ProviderId = "summary-provider",
        SiteType = "new-api",
        AuthorizationScheme = "Bearer",
        AccessToken = "abcdefgh12345678",
        CookieHeader = "short"
    });
    var credentialSummary = credentialSummaryStore.GetSummary("summary-provider");
    Assert(credentialSummary.AccessTokenSummary == "abcd********5678" && credentialSummary.CookieSummary == "********"
        && !credentialSummary.StatusText.Contains("abcdefgh12345678", StringComparison.Ordinal)
        && !credentialSummary.StatusText.Contains("short", StringComparison.Ordinal),
        "credential storage boundary must generate fixed summaries without exposing token or Cookie material");

    var logRoot = Path.Combine(root, "logs");
    const string syntheticToken = "synthetic-token-DO-NOT-LOG";
    const string syntheticQuery = "https://example.invalid/prices?api_key=synthetic-query-secret&token=synthetic-token&cookie=synthetic-cookie";
    const string syntheticPath = "C:\\Users\\Private\\Documents\\secret";
    using (var provider = new RollingFileLoggerProvider(new RollingFileLoggerOptions(logRoot, 1024, 7)))
    {
        var logger = provider.CreateLogger("Contract");
        for (var i = 0; i < 40; i++)
            logger.Log(Microsoft.Extensions.Logging.LogLevel.Warning, new Microsoft.Extensions.Logging.EventId(7), new[] { new KeyValuePair<string, object?>("ProviderId", "p"), new KeyValuePair<string, object?>("Unknown", "drop"), new KeyValuePair<string, object?>("Operation", $"Authorization: Bearer {syntheticToken} {syntheticQuery} {syntheticPath}") }, new InvalidOperationException($"Cookie=session refresh_token=rt {syntheticToken} {syntheticQuery} {syntheticPath}"), (state, exception) => $"Bearer {syntheticToken} Authorization=abc {syntheticQuery} {syntheticPath}");
    }
    var logFiles = Directory.GetFiles(logRoot, "app-*.log*");
    Assert(logFiles.Any(x => x.EndsWith(".1", StringComparison.Ordinal)), "log rotation missing");
    foreach (var line in logFiles.SelectMany(File.ReadAllLines).Where(x => !string.IsNullOrWhiteSpace(x)))
    {
        using var json = System.Text.Json.JsonDocument.Parse(line);
        Assert(json.RootElement.TryGetProperty("timestamp", out _) && json.RootElement.TryGetProperty("level", out _) && json.RootElement.TryGetProperty("message", out _), "log fields missing");
        Assert(!line.Contains(syntheticToken, StringComparison.Ordinal) && !line.Contains("synthetic-query-secret", StringComparison.Ordinal) && !line.Contains("synthetic-cookie", StringComparison.Ordinal) && !line.Contains(syntheticPath, StringComparison.Ordinal) && !line.Contains("session", StringComparison.Ordinal) && !line.Contains("refresh_token=rt", StringComparison.Ordinal), "log secret leaked");
        Assert(json.RootElement.GetProperty("exception").GetString() == typeof(InvalidOperationException).FullName, "log must retain only the exception type");
        Assert(!json.RootElement.TryGetProperty("Unknown", out _), "unknown state retained");
    }
    var bindingSettings = new JsonSettingsRepository(root);
    bindingSettings.Save(new LocalAppSettings { Sites = [new SiteConfiguration { ProviderId = "binding", ConfigurationKey = "binding-key", BaseUrl = new Uri("https://binding.example"), Model = "gpt-5.6-sol", CurrentGroup = "old-group" }] });
    var bindingKeys = new WindowsInferenceApiKeyStore(root);
    var bindingStore = new WindowsInferenceBindingStore(bindingSettings, bindingKeys, root);
    var bindingSummary = bindingStore.Save("binding", "synthetic-binding-secret", "new-group");
    Assert(bindingSettings.Load().Sites.Single().CurrentGroup == "new-group" && bindingKeys.Load("binding")?.BoundGroup == "new-group" && bindingSummary.MaskedKey == "synt…cret", "inference binding transaction must commit key and group together");
    var replaceCorruptSettings = new JsonSettingsRepository(root);
    replaceCorruptSettings.Save(new LocalAppSettings { Sites = [new SiteConfiguration { ProviderId = "replace-corrupt", ConfigurationKey = "replace-corrupt-key", BaseUrl = new Uri("https://replace-corrupt.example"), Model = "gpt-5.6-sol", CurrentGroup = "old-group" }] });
    var replaceCorruptKeys = new WindowsInferenceApiKeyStore(root);
    replaceCorruptKeys.Save(new InferenceApiKeyRecord { ProviderId = "replace-corrupt", KeyHandle = "old-handle", ApiKey = "synthetic-old-secret", BoundGroup = "old-group" });
    replaceCorruptKeys.WriteProtected("replace-corrupt", "not-a-protected-key"u8);
    var replacedCorruptSummary = new WindowsInferenceBindingStore(replaceCorruptSettings, replaceCorruptKeys, root).Save("replace-corrupt", "synthetic-new-secret", "new-group");
    Assert(replacedCorruptSummary.BoundGroup == "new-group" && replacedCorruptSummary.KeyHandle != "old-handle" && replaceCorruptKeys.Load("replace-corrupt")?.ApiKey == "synthetic-new-secret" && replaceCorruptSettings.Load().Sites.Single().CurrentGroup == "new-group", "updating a provider must replace an unreadable previous inference key and commit its group binding with a fresh handle");
    var transactionDirectory = Path.Combine(root, "inference-binding-transaction");
    Directory.CreateDirectory(transactionDirectory);
    var recoveredSettings = bindingSettings.Load() with { Sites = [bindingSettings.Load().Sites.Single() with { CurrentGroup = "recovered-group" }] };
    await File.WriteAllBytesAsync(Path.Combine(transactionDirectory, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(recoveredSettings, AtomicJsonFile.Options));
    var recoveredRecord = bindingKeys.Load("binding")! with { ApiKey = "synthetic-recovered-secret", BoundGroup = "recovered-group" };
    await File.WriteAllBytesAsync(Path.Combine(transactionDirectory, "key.bin"), WindowsInferenceApiKeyStore.Protect(recoveredRecord));
    await File.WriteAllTextAsync(Path.Combine(transactionDirectory, "provider-id.txt"), "binding");
    bindingStore.Recover();
    Assert(bindingSettings.Load().Sites.Single().CurrentGroup == "recovered-group" && bindingKeys.Load("binding")?.ApiKey == "synthetic-recovered-secret" && !Directory.Exists(transactionDirectory), "startup recovery must finish a prepared inference binding transaction");
    var sidecarPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "ProviderPriceSwitcher.App", "Assets", "Bifrost", "bifrost-sidecar.exe"));
    const string sidecarHash = "38c2c8a69e481a6561d07d7252f2fd100a50bef443bbb61beddf85e2e6ae4491";
    var upstream = new System.Net.HttpListener();
    var upstreamPort = Random.Shared.Next(20000, 30000);
    var secondUpstreamPort = upstreamPort + 10000;
    var secondUpstream = new System.Net.HttpListener();
    upstream.Prefixes.Add($"http://127.0.0.1:{upstreamPort}/");
    secondUpstream.Prefixes.Add($"http://127.0.0.1:{secondUpstreamPort}/");
    upstream.Start();
    secondUpstream.Start();
    using var upstreamCancellation = new CancellationTokenSource();
    var ompRequestCount = 0;
    var inFlightStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var repeatedOmpRequestCount = 0;
    var firstRepeatedOmpRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseRepeatedOmpRequests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task ServeResponsesAsync(System.Net.HttpListener listener, string expectedKey, string responseId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            System.Net.HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch when (cancellationToken.IsCancellationRequested) { return; }
            Assert(context.Request.Url?.AbsolutePath == "/v1/responses", "sidecar used unexpected upstream path");
            Assert(context.Request.Headers["Authorization"] == $"Bearer {expectedKey}", "sidecar key/endpoint isolation failed");
            using var reader = new StreamReader(context.Request.InputStream);
            var requestBody = await reader.ReadToEndAsync(cancellationToken);
            Assert(requestBody.Contains("gpt-5.6-sol", StringComparison.Ordinal), "sidecar did not preserve ModelId");
            if (requestBody.Contains("matrix", StringComparison.Ordinal)) Assert(requestBody.Contains("function_call_output", StringComparison.Ordinal), "sidecar did not preserve tool result input");
            if (requestBody.Contains("in-flight", StringComparison.Ordinal))
            {
                inFlightStarted.TrySetResult();
                await releaseInFlight.Task.WaitAsync(cancellationToken);
            }
            var isRepeatedOmpLaunch = requestBody.Contains("repeat-launch", StringComparison.Ordinal);
            if (isRepeatedOmpLaunch)
            {
                var requestCount = Interlocked.Increment(ref repeatedOmpRequestCount);
                if (requestCount == 1)
                    firstRepeatedOmpRequestStarted.TrySetResult();
                await releaseRepeatedOmpRequests.Task.WaitAsync(cancellationToken);
            }
            if (!requestBody.Contains("matrix", StringComparison.Ordinal)) Interlocked.Increment(ref ompRequestCount);
            var streaming = requestBody.Contains("\"stream\":true", StringComparison.Ordinal);
            var response = streaming
                ? $"event: response.reasoning_summary_text.delta\ndata: {{\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"reasoning\"}}\n\nevent: response.function_call_arguments.delta\ndata: {{\"type\":\"response.function_call_arguments.delta\",\"delta\":\"{{}}\"}}\n\nevent: response.completed\ndata: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"{responseId}\"}}}}\n\n"
                : isRepeatedOmpLaunch
                    ? $"{{\"id\":\"{responseId}\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"gpt-5.6-sol\",\"output\":[{{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{{\"type\":\"output_text\",\"text\":\"loopback-ok\",\"annotations\":[]}}]}}],\"usage\":{{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}}"
                    : $"{{\"id\":\"{responseId}\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"gpt-5.6-sol\",\"output\":[{{\"id\":\"reason_1\",\"type\":\"reasoning\",\"summary\":[]}},{{\"id\":\"call_1\",\"type\":\"function_call\",\"status\":\"completed\",\"name\":\"lookup\",\"call_id\":\"call_1\",\"arguments\":\"{{}}\"}},{{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{{\"type\":\"output_text\",\"text\":\"loopback-ok\",\"annotations\":[]}}]}}],\"usage\":{{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}}";
            context.Response.StatusCode = 200;
            context.Response.ContentType = streaming ? "text/event-stream" : "application/json";
            var bytes = System.Text.Encoding.UTF8.GetBytes(response);
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            context.Response.Close();
        }
    }
    var upstreamTask = ServeResponsesAsync(upstream, "synthetic-sidecar-secret", "resp_A", upstreamCancellation.Token);
    var secondUpstreamTask = ServeResponsesAsync(secondUpstream, "synthetic-sidecar-secret-B", "resp_B", upstreamCancellation.Token);
    const int sidecarPort = ProviderPriceSwitcher.Application.OmpSidecarProvider.DefaultPort;
    await using (var supervisor = new WindowsSidecarSupervisor(new SidecarBinaryOptions(sidecarPath, sidecarHash, "pps-sidecar-contract-" + Guid.NewGuid().ToString("N"), sidecarPort), new SyntheticResolver()))
    {
        var emptyRouteSettings = new MemorySettingsRepository(new LocalAppSettings());
        var emptyRouteState = new ActiveRouteState();
        var emptyRoute = new ApplyActiveRouteUseCase(emptyRouteSettings, supervisor, new SyntheticInferenceKeyStore(), emptyRouteState);
        var emptyOutcome = await emptyRoute.RestoreAsync(emptyRouteSettings.Load());
        Assert(emptyOutcome.Status == ApplyActiveRouteStatus.NoActiveRoute && supervisor.Status.IsReady, "empty active route must start the sidecar and expose its stable no-route response");
        var activeRouteSettings = new MemorySettingsRepository(new LocalAppSettings
        {
            Sites =
            [
                new SiteConfiguration
                {
                    ProviderId = "loopback",
                    ConfigurationKey = "loopback-key",
                    BaseUrl = new Uri($"http://127.0.0.1:{upstreamPort}"),
                    Model = "gpt-5.6-sol",
                    CurrentGroup = "g"
                }
            ]
        });
        var activeRouteKeys = new SyntheticInferenceKeyStore();
        activeRouteKeys.Save(new InferenceApiKeyRecord { ProviderId = "loopback", KeyHandle = "synthetic-handle", ApiKey = "synthetic-sidecar-secret", BoundGroup = "g" });
        var activeRouteState = new ActiveRouteState();
        var routeApply = new ApplyActiveRouteUseCase(activeRouteSettings, supervisor, activeRouteKeys, activeRouteState);
        var applied = await routeApply.ExecuteAsync(activeRouteSettings.Load(), "loopback");
        Assert(applied.Status == ApplyActiveRouteStatus.Applied && activeRouteSettings.Load().ActiveProviderId == "loopback" && activeRouteState.CurrentProviderId == "loopback", "application route use case must atomically apply and persist the sidecar snapshot");
        var disabledRestore = await routeApply.RestoreAsync(activeRouteSettings.Load() with { ActiveProviderId = "loopback", Sites = [activeRouteSettings.Load().Sites.Single() with { Enabled = false }] });
        Assert(disabledRestore.Status == ApplyActiveRouteStatus.Cleared && disabledRestore.Settings.ActiveProviderId is null && activeRouteState.CurrentProviderId is null, "restart restore must clear a disabled persisted provider without fallback");
        await routeApply.ExecuteAsync(activeRouteSettings.Load() with { Sites = [activeRouteSettings.Load().Sites.Single() with { Enabled = true }] }, "loopback");
        var ompAgentRoot = Path.Combine(root, "omp-agent");
        Directory.CreateDirectory(ompAgentRoot);
        await File.WriteAllTextAsync(Path.Combine(ompAgentRoot, "models.yml"), "providers:\n  provider-price-switcher:\n    baseUrl: http://127.0.0.1:15722/v1\n    apiKey: PPS_SIDECAR_PLACEHOLDER\n    api: openai-responses\n    authHeader: true\n    models:\n      - id: gpt-5.6-sol\n        name: GPT 5.6 Sol via ProviderPriceSwitcher\n        contextWindow: 400000\n        maxTokens: 128000\n");
        await File.WriteAllTextAsync(Path.Combine(ompAgentRoot, "config.yml"), "modelRoles:\n  default: provider-price-switcher/gpt-5.6-sol\n");
        var firstOmpWorkingDirectory = Path.Combine(root, "omp-working-a");
        var secondOmpWorkingDirectory = Path.Combine(root, "omp-working-b");
        Directory.CreateDirectory(firstOmpWorkingDirectory);
        Directory.CreateDirectory(secondOmpWorkingDirectory);
        System.Diagnostics.ProcessStartInfo CreateOmpStartInfo(string workingDirectory)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("D:\\.Pi Projects\\omp.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add("provider-price-switcher/gpt-5.6-sol");
            startInfo.ArgumentList.Add("--no-tools");
            startInfo.ArgumentList.Add("--no-session");
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("Return loopback-ok. repeat-launch");
            startInfo.Environment["PI_CODING_AGENT_DIR"] = ompAgentRoot;
            startInfo.Environment["PPS_SIDECAR_PLACEHOLDER"] = "not-a-secret";
            return startInfo;
        }
        var repeatedOmpProcesses = new List<System.Diagnostics.Process>();
        try
        {
            var firstOmp = System.Diagnostics.Process.Start(CreateOmpStartInfo(firstOmpWorkingDirectory))
                ?? throw new InvalidOperationException("Initial OMP loopback process did not start.");
            repeatedOmpProcesses.Add(firstOmp);
            await firstRepeatedOmpRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert(!firstOmp.HasExited, "Initial OMP instance must remain active while repeat launches are requested.");
            repeatedOmpProcesses.Add(System.Diagnostics.Process.Start(CreateOmpStartInfo(firstOmpWorkingDirectory))
                ?? throw new InvalidOperationException("Repeated OMP process from the same working directory did not start."));
            repeatedOmpProcesses.Add(System.Diagnostics.Process.Start(CreateOmpStartInfo(secondOmpWorkingDirectory))
                ?? throw new InvalidOperationException("Repeated OMP process from a different working directory did not start."));
            await Task.Delay(500);
            Assert(repeatedOmpProcesses.All(process => !process.HasExited)
                && repeatedOmpProcesses.Select(process => process.Id).Distinct().Count() == 3,
                "Existing OMP instances must not block new instances from the same or a different working directory.");
            var outputTasks = repeatedOmpProcesses.Select(async process =>
                (Output: await process.StandardOutput.ReadToEndAsync(), Error: await process.StandardError.ReadToEndAsync())).ToArray();
            releaseRepeatedOmpRequests.TrySetResult();
            await Task.WhenAll(repeatedOmpProcesses.Select(process => process.WaitForExitAsync()))
                .WaitAsync(TimeSpan.FromSeconds(30));
            var processOutputs = await Task.WhenAll(outputTasks);
            Assert(repeatedOmpProcesses.All(process => process.ExitCode == 0)
                && repeatedOmpRequestCount >= 3
                && ompRequestCount >= 3,
                $"Every same/different-directory OMP launch must complete through the isolated sidecar; exits={string.Join(",", repeatedOmpProcesses.Select(process => process.ExitCode))}; repeatedRequests={repeatedOmpRequestCount}; requests={ompRequestCount}; stdout={string.Join(" | ", processOutputs.Select(output => output.Output))}; stderr={string.Join(" | ", processOutputs.Select(output => output.Error))}.");
        }
        finally
        {
            releaseRepeatedOmpRequests.TrySetResult();
            foreach (var process in repeatedOmpProcesses)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var nonStream = await client.PostAsync("http://127.0.0.1:15722/v1/responses", new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":[{\"type\":\"function_call_output\",\"call_id\":\"call_1\",\"output\":\"ok\"}],\"tools\":[{\"type\":\"function\",\"name\":\"lookup\"}]}", System.Text.Encoding.UTF8, "application/json"));
        var nonStreamBody = await nonStream.Content.ReadAsStringAsync();
        Assert(nonStream.IsSuccessStatusCode && nonStreamBody.Contains("function_call", StringComparison.Ordinal) && nonStreamBody.Contains("reasoning", StringComparison.Ordinal), "sidecar non-stream/tools/reasoning matrix failed");
        using var stream = await client.PostAsync("http://127.0.0.1:15722/v1/responses", new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":[{\"type\":\"function_call_output\",\"call_id\":\"call_1\",\"output\":\"ok\"}],\"stream\":true}", System.Text.Encoding.UTF8, "application/json"));
        var streamBody = await stream.Content.ReadAsStringAsync();
        Assert(stream.IsSuccessStatusCode && stream.Content.Headers.ContentType?.MediaType == "text/event-stream" && streamBody.Contains("response.completed", StringComparison.Ordinal) && streamBody.Contains("response.function_call_arguments.delta", StringComparison.Ordinal), "sidecar SSE matrix failed");
        await supervisor.ApplyAsync(new RouteSnapshot("loopback", $"http://127.0.0.1:{upstreamPort}", "synthetic-handle"));
        var inFlightTask = client.PostAsync("http://127.0.0.1:15722/v1/responses", new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":\"in-flight\"}", System.Text.Encoding.UTF8, "application/json"));
        await inFlightStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await supervisor.ApplyAsync(new RouteSnapshot("loopback-B", $"http://127.0.0.1:{secondUpstreamPort}", "synthetic-handle-B"));
        using var postSwitch = await client.PostAsync("http://127.0.0.1:15722/v1/responses", new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":\"new-route\"}", System.Text.Encoding.UTF8, "application/json"));
        releaseInFlight.TrySetResult();
        using var inFlight = await inFlightTask;
        Assert((await inFlight.Content.ReadAsStringAsync()).Contains("resp_A", StringComparison.Ordinal) && (await postSwitch.Content.ReadAsStringAsync()).Contains("resp_B", StringComparison.Ordinal), "in-flight request must retain its original route snapshot while new requests use the replacement route");
        await supervisor.ApplyAsync(new RouteSnapshot("loopback-B", $"http://127.0.0.1:{secondUpstreamPort}", "synthetic-handle-B"));
        using var switched = await client.PostAsync("http://127.0.0.1:15722/v1/responses", new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":[{\"type\":\"function_call_output\",\"call_id\":\"call_2\",\"output\":\"ok\"}]}", System.Text.Encoding.UTF8, "application/json"));
        Assert(switched.IsSuccessStatusCode && (await switched.Content.ReadAsStringAsync()).Contains("resp_B", StringComparison.Ordinal), "sidecar route switch isolation failed");
        await supervisor.StopAsync();
        Assert(supervisor.Status.Status == SidecarConnectionStatus.Stopped, "sidecar stop must publish a stable stopped state");
        await supervisor.StartAsync();
        using var recoveredRoute = await client.PostAsync("http://127.0.0.1:15722/v1/responses", new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":\"after-recovery\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert(recoveredRoute.IsSuccessStatusCode && (await recoveredRoute.Content.ReadAsStringAsync()).Contains("resp_B", StringComparison.Ordinal), "sidecar restart must restore the last confirmed route before accepting new OMP requests");
        await supervisor.ClearAsync();
        using var noRoute = await client.PostAsync("http://127.0.0.1:15722/v1/responses", new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":\"matrix\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert(noRoute.StatusCode == System.Net.HttpStatusCode.BadRequest && (await noRoute.Content.ReadAsStringAsync()).Contains(SidecarProtocol.NoActiveRouteCode, StringComparison.Ordinal), "sidecar no-route contract failed");
        await supervisor.StopAsync();
        Assert(supervisor.Status.Status == SidecarConnectionStatus.Stopped, "sidecar stop after clearing the route must publish a stable stopped state");
        await supervisor.ApplyAsync(new RouteSnapshot("loopback", $"http://127.0.0.1:{upstreamPort}", "synthetic-handle"));
        using var afterRestart = await client.PostAsync("http://127.0.0.1:15722/v1/responses", new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":\"after-restart\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert(afterRestart.IsSuccessStatusCode && (await afterRestart.Content.ReadAsStringAsync()).Contains("resp_A", StringComparison.Ordinal), "sidecar restart must accept an explicitly confirmed replacement route");
    }
    using var conflictListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    conflictListener.Start();
    var conflictPort = ((System.Net.IPEndPoint)conflictListener.LocalEndpoint).Port;
    await using (var conflictSupervisor = new WindowsSidecarSupervisor(
        new SidecarBinaryOptions(sidecarPath, sidecarHash, "pps-sidecar-conflict-" + Guid.NewGuid().ToString("N"), conflictPort),
        new SyntheticResolver()))
    {
        Exception? conflictError = null;
        try { await conflictSupervisor.StartAsync(); }
        catch (Exception exception) { conflictError = exception; }
        Assert(conflictError is GatewayPortUnavailableException && !conflictSupervisor.Status.IsReady, "busy gateway port must fail with a structured port error without accepting an unknown listener");
        await conflictSupervisor.StopAsync();
    }
    conflictListener.Stop();
    upstreamCancellation.Cancel(); upstream.Stop(); secondUpstream.Stop(); await Task.WhenAll(upstreamTask, secondUpstreamTask);
    var sidecarExitDeadline = DateTime.UtcNow.AddSeconds(5);
    while (System.Diagnostics.Process.GetProcessesByName("bifrost-sidecar").Length != 0 && DateTime.UtcNow < sidecarExitDeadline)
        await Task.Delay(50);
    Assert(System.Diagnostics.Process.GetProcessesByName("bifrost-sidecar").Length == 0, "sidecar process remained after matrix");
    Console.WriteLine("Infrastructure persistence contract tests passed.");
}
finally { Directory.Delete(root, true); }

sealed class SyntheticResolver : IInferenceApiKeyResolver
{
    public ValueTask<string?> ResolveAsync(string keyHandle, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(keyHandle switch { "synthetic-handle" => "synthetic-sidecar-secret", "synthetic-handle-B" => "synthetic-sidecar-secret-B", _ => null });
}
sealed class MemorySettingsRepository(LocalAppSettings value) : ISettingsRepository
{
    public LocalAppSettings Value { get; private set; } = value;
    public LocalAppSettings Load() => Value;
    public void Save(LocalAppSettings settings) => Value = settings;
    public LocalAppSettings Update(Func<LocalAppSettings, LocalAppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var updated = update(Value);
        Value = updated;
        return updated;
    }
}

sealed class SyntheticInferenceKeyStore : IInferenceApiKeyStore
{
    private readonly Dictionary<string, InferenceApiKeyRecord> _records = new(StringComparer.Ordinal);
    public InferenceApiKeyRecord? Load(string providerId) => _records.GetValueOrDefault(providerId);
    public void Save(InferenceApiKeyRecord record) => _records[record.ProviderId] = record;
    public void Clear(string providerId) => _records.Remove(providerId);
    public InferenceApiKeySummary? GetSummary(string providerId) => Load(providerId) is { } record
        ? new InferenceApiKeySummary { ProviderId = record.ProviderId, KeyHandle = record.KeyHandle, BoundGroup = record.BoundGroup, MaskedKey = InferenceApiKeySummary.Mask(record.ApiKey), UpdatedAt = record.UpdatedAt }
        : null;
}
