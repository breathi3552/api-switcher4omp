using System.Text.Json;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Infrastructure;
using ProviderPriceSwitcher.Application;

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
    Assert(defaults.Model == "gpt-5.6-sol" && defaults.RequestTimeoutSeconds == 10, "defaults");
    var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omp");
    Assert(defaults.OmpRootDirectory == expectedRoot && new AppPathDefaults().OmpConfigPath(defaults.OmpRootDirectory) == Path.Combine(expectedRoot, "agent", "config.yml"), "OMP root defaults and derived config path");

    settingsRepo.Save(defaults with { Sites = [new SiteConfiguration { ProviderId = "p", ConfigurationKey = "k", BaseUrl = new Uri("https://p.example"), Model = defaults.Model, CurrentGroup = "g" }] });
    Assert(settingsRepo.Load().Sites.Single().ProviderId == "p" && settingsRepo.Load().Sites.Single().ConfigurationApiAddress == "/keys", "settings roundtrip and default configuration API address");
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
    Assert(savedSettings.Contains("ompRootDirectory", StringComparison.Ordinal) && !savedSettings.Contains("resultTtl", StringComparison.Ordinal) && !savedSettings.Contains("ompModelsPath", StringComparison.Ordinal) && !savedSettings.Contains("ompConfigPath", StringComparison.Ordinal), "retired settings must disappear after save");
    Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "atomic settings write");
    settingsRepo.Save(defaults);
    Assert(File.Exists(settingsRepo.FilePath + ".bak") && !Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "atomic settings write and backup");
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
            if (!requestBody.Contains("matrix", StringComparison.Ordinal)) Interlocked.Increment(ref ompRequestCount);
            var streaming = requestBody.Contains("\"stream\":true", StringComparison.Ordinal);
            var response = streaming
                ? $"event: response.reasoning_summary_text.delta\ndata: {{\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"reasoning\"}}\n\nevent: response.function_call_arguments.delta\ndata: {{\"type\":\"response.function_call_arguments.delta\",\"delta\":\"{{}}\"}}\n\nevent: response.completed\ndata: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"{responseId}\"}}}}\n\n"
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
    await using (var supervisor = new WindowsSidecarSupervisor(new SidecarBinaryOptions(sidecarPath, sidecarHash, "pps-sidecar-contract-" + Guid.NewGuid().ToString("N")), new SyntheticResolver()))
    {
        await supervisor.ApplyAsync(new RouteSnapshot("loopback", $"http://127.0.0.1:{upstreamPort}", "synthetic-handle"));
        var ompAgentRoot = Path.Combine(root, "omp-agent");
        Directory.CreateDirectory(ompAgentRoot);
        await File.WriteAllTextAsync(Path.Combine(ompAgentRoot, "models.yml"), "providers:\n  provider-price-switcher:\n    baseUrl: http://127.0.0.1:8080/v1\n    apiKey: PPS_SIDECAR_PLACEHOLDER\n    api: openai-responses\n    authHeader: true\n    models:\n      - id: gpt-5.6-sol\n        name: GPT 5.6 Sol via ProviderPriceSwitcher\n        contextWindow: 400000\n        maxTokens: 128000\n");
        await File.WriteAllTextAsync(Path.Combine(ompAgentRoot, "config.yml"), "modelRoles:\n  default: provider-price-switcher/gpt-5.6-sol\n");
        var ompInfo = new System.Diagnostics.ProcessStartInfo("D:\\.Pi Projects\\omp.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
        ompInfo.ArgumentList.Add("--model"); ompInfo.ArgumentList.Add("provider-price-switcher/gpt-5.6-sol"); ompInfo.ArgumentList.Add("--no-tools"); ompInfo.ArgumentList.Add("--no-session"); ompInfo.ArgumentList.Add("-p"); ompInfo.ArgumentList.Add("Return loopback-ok.");
        ompInfo.Environment["PI_CODING_AGENT_DIR"] = ompAgentRoot;
        ompInfo.Environment["PPS_SIDECAR_PLACEHOLDER"] = "not-a-secret";
        using var ompProcess = System.Diagnostics.Process.Start(ompInfo) ?? throw new InvalidOperationException("OMP loopback process did not start");
        var ompOutput = await ompProcess.StandardOutput.ReadToEndAsync();
        var ompError = await ompProcess.StandardError.ReadToEndAsync();
        await ompProcess.WaitForExitAsync();
        Assert(ompProcess.ExitCode == 0 && ompRequestCount > 0, $"real OMP sidecar request failed: exit={ompProcess.ExitCode}, requests={ompRequestCount}, stdout={ompOutput}, stderr={ompError}");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var nonStream = await client.PostAsync("http://127.0.0.1:8080/v1/responses", new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":[{\"type\":\"function_call_output\",\"call_id\":\"call_1\",\"output\":\"ok\"}],\"tools\":[{\"type\":\"function\",\"name\":\"lookup\"}]}", System.Text.Encoding.UTF8, "application/json"));
        var nonStreamBody = await nonStream.Content.ReadAsStringAsync();
        Assert(nonStream.IsSuccessStatusCode && nonStreamBody.Contains("function_call", StringComparison.Ordinal) && nonStreamBody.Contains("reasoning", StringComparison.Ordinal), "sidecar non-stream/tools/reasoning matrix failed");
        using var stream = await client.PostAsync("http://127.0.0.1:8080/v1/responses", new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":[{\"type\":\"function_call_output\",\"call_id\":\"call_1\",\"output\":\"ok\"}],\"stream\":true}", System.Text.Encoding.UTF8, "application/json"));
        var streamBody = await stream.Content.ReadAsStringAsync();
        Assert(stream.IsSuccessStatusCode && stream.Content.Headers.ContentType?.MediaType == "text/event-stream" && streamBody.Contains("response.completed", StringComparison.Ordinal) && streamBody.Contains("response.function_call_arguments.delta", StringComparison.Ordinal), "sidecar SSE matrix failed");
        await supervisor.ApplyAsync(new RouteSnapshot("loopback-B", $"http://127.0.0.1:{secondUpstreamPort}", "synthetic-handle-B"));
        using var switched = await client.PostAsync("http://127.0.0.1:8080/v1/responses", new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":[{\"type\":\"function_call_output\",\"call_id\":\"call_2\",\"output\":\"ok\"}]}", System.Text.Encoding.UTF8, "application/json"));
        Assert(switched.IsSuccessStatusCode && (await switched.Content.ReadAsStringAsync()).Contains("resp_B", StringComparison.Ordinal), "sidecar route switch isolation failed");
        await supervisor.ClearAsync();
        using var noRoute = await client.PostAsync("http://127.0.0.1:8080/v1/responses", new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":\"matrix\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert(noRoute.StatusCode == System.Net.HttpStatusCode.BadRequest && (await noRoute.Content.ReadAsStringAsync()).Contains(SidecarProtocol.NoActiveRouteCode, StringComparison.Ordinal), "sidecar no-route contract failed");
    }
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
