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
    Assert(settingsRepo.Load().Sites.Single().ProviderId == "p", "settings roundtrip");
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
    Assert(migratedSevnx.SiteType == "sub2api" && migratedSevnx.BaseUrl.AbsoluteUri == "https://www.sevnx.one/", "SevnX root migration");
    Assert(migratedSevnx.AuthenticationMode == "导入令牌", "SevnX auth mode migration");
    Assert(migratedSevnx.ConfigurationKey == SiteConfigurationKey.Create("sevnx", "sub2api", migratedSevnx.BaseUrl, migratedSevnx.Model, migratedSevnx.CurrentGroup), "SevnX key rebuild");
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
    using (var provider = new RollingFileLoggerProvider(new RollingFileLoggerOptions(logRoot, 1024, 7)))
    {
        var logger = provider.CreateLogger("Contract");
        for (var i = 0; i < 40; i++)
            logger.Log(Microsoft.Extensions.Logging.LogLevel.Warning, new Microsoft.Extensions.Logging.EventId(7), new[] { new KeyValuePair<string, object?>("ProviderId", "p"), new KeyValuePair<string, object?>("Unknown", "drop"), new KeyValuePair<string, object?>("Operation", "Authorization: Bearer secret access_token=abc") }, new InvalidOperationException("Cookie=session refresh_token=rt"), (state, exception) => "Bearer secret Authorization=abc");
    }
    var logFiles = Directory.GetFiles(logRoot, "app-*.log*");
    Assert(logFiles.Any(x => x.EndsWith(".1", StringComparison.Ordinal)), "log rotation missing");
    foreach (var line in logFiles.SelectMany(File.ReadAllLines).Where(x => !string.IsNullOrWhiteSpace(x)))
    {
        using var json = System.Text.Json.JsonDocument.Parse(line);
        Assert(json.RootElement.TryGetProperty("timestamp", out _) && json.RootElement.TryGetProperty("level", out _) && json.RootElement.TryGetProperty("message", out _), "log fields missing");
        Assert(!line.Contains("secret", StringComparison.Ordinal) && !line.Contains("session", StringComparison.Ordinal) && !line.Contains("refresh_token=rt", StringComparison.Ordinal), "log secret leaked");
        Assert(!json.RootElement.TryGetProperty("Unknown", out _), "unknown state retained");
    }
    Console.WriteLine("Infrastructure persistence contract tests passed.");
}
finally { Directory.Delete(root, true); }
