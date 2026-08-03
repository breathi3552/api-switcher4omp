using System.Text.Json;
using System.Text.Json.Serialization;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed class JsonDataException : IOException
{
    public JsonDataException(string path, Exception innerException)
        : base($"The JSON data file '{path}' is invalid or corrupted.", innerException) { }
}

internal static class AtomicJsonFile
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static T Read<T>(string path, T missingValue)
    {
        if (!File.Exists(path)) return missingValue;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options) ?? missingValue;
        }
        catch (JsonException ex) { throw new JsonDataException(path, ex); }
        catch (NotSupportedException ex) { throw new JsonDataException(path, ex); }
    }

    internal static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Data path has no directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backup = path + ".bak";
        try
        {
            var options = new JsonWriterOptions { Indented = true };
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using var writer = new Utf8JsonWriter(stream, options);
                JsonSerializer.Serialize(writer, value, Options);
                writer.Flush();
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Copy(path, backup, true);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    internal static void WriteBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Data path has no directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backup = path + ".bak";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Copy(path, backup, true);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

public sealed class JsonSettingsRepository : ISettingsRepository
{
    public string FilePath { get; }
    private readonly IAppPathDefaults _pathDefaults;
    private readonly object _gate = new();
    public JsonSettingsRepository(string? rootDirectory = null, IAppPathDefaults? pathDefaults = null)
    {
        FilePath = AppDataPaths.SettingsFile(rootDirectory);
        _pathDefaults = pathDefaults ?? new AppPathDefaults();
    }

    public LocalAppSettings Load()
    {
        var settings = ApplyDefaults(AtomicJsonFile.Read(FilePath, new LocalAppSettings()));
        if (!File.Exists(FilePath)) return settings;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;
            var migrated = settings;

            if ((!root.TryGetProperty("ompRootDirectory", out var current) || current.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(current.GetString()))
                && root.TryGetProperty("ompConfigPath", out var legacy) && legacy.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(legacy.GetString()))
            {
                var oldPath = legacy.GetString()!;
                var parent = Path.GetDirectoryName(oldPath);
                var migratedRoot = string.Equals(Path.GetFileName(oldPath), "config.yml", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileName(parent), "agent", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetDirectoryName(parent)
                        : parent;
                if (!string.IsNullOrWhiteSpace(migratedRoot)) migrated = migrated with { OmpRootDirectory = migratedRoot };
            }

            var sites = migrated.Sites.Select(site =>
            {
                if (string.Equals(site.SiteType, "new-api", StringComparison.Ordinal)
                    && string.Equals(site.BaseUrl.Host, "ai.furry.edu.gr", StringComparison.OrdinalIgnoreCase)
                    && site.BaseUrl.Query.Length == 0 && site.BaseUrl.Fragment.Length == 0
                    && (site.BaseUrl.AbsolutePath == "/" || site.BaseUrl.AbsolutePath == "/pawsai-pricing.json"))
                {
                    var rootUrl = new Uri($"{site.BaseUrl.Scheme}://{site.BaseUrl.Authority}/");
                    return site with
                    {
                        SiteType = "pawsai",
                        BaseUrl = rootUrl,
                        ConfigurationKey = SiteConfigurationKey.Create(site.ProviderId, "pawsai", rootUrl, site.Model, site.CurrentGroup)
                    };
                }

                if (string.Equals(site.SiteType, "new-api", StringComparison.Ordinal)
                    && string.Equals(site.ProviderId, "sevnx", StringComparison.OrdinalIgnoreCase)
                    && site.BaseUrl.Host.Contains("sevnx", StringComparison.OrdinalIgnoreCase))
                {
                    var rootUrl = new Uri($"{site.BaseUrl.Scheme}://{site.BaseUrl.Authority}/");
                    return site with
                    {
                        SiteType = "sevnx",
                        BaseUrl = rootUrl,
                        AuthenticationMode = "导入令牌",
                        ConfigurationKey = SiteConfigurationKey.Create(site.ProviderId, "sevnx", rootUrl, site.Model, site.CurrentGroup)
                    };
                }

                if (string.Equals(site.SiteType, "sub2api", StringComparison.Ordinal))
                {
                    return site with
                    {
                        SiteType = "sevnx",
                        ConfigurationKey = SiteConfigurationKey.Create(site.ProviderId, "sevnx", site.BaseUrl, site.Model, site.CurrentGroup)
                    };
                }

                return site;
            }).ToList();

            return sites.SequenceEqual(migrated.Sites)
                ? migrated
                : migrated with { Sites = sites };
        }
        catch (JsonException ex)
        {
            throw new JsonDataException(FilePath, ex);
        }
    }
    private LocalAppSettings ApplyDefaults(LocalAppSettings settings)
    {
        var root = string.IsNullOrWhiteSpace(settings.OmpRootDirectory) ? _pathDefaults.OmpRootDirectory : settings.OmpRootDirectory;
        var agent = _pathDefaults.OmpAgentDirectory(root);
        var directories = settings.OmpWorkingDirectories.Count == 0 ? new[] { agent } : settings.OmpWorkingDirectories.ToArray();
        var last = string.IsNullOrWhiteSpace(settings.LastOmpWorkingDirectory) ? directories[0] : settings.LastOmpWorkingDirectory;
        return settings with { OmpRootDirectory = root, OmpWorkingDirectories = directories, LastOmpWorkingDirectory = last, Sites = settings.Sites.ToArray() };
    }

    public LocalAppSettings Update(Func<LocalAppSettings, LocalAppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var updated = update(Load());
            Save(updated);
            return updated;
        }
    }
    internal void ExecuteLocked(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate) action();
    }

    internal T ExecuteLocked<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate) return action();
    }

    public void Save(LocalAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate) AtomicJsonFile.Write(FilePath, settings);
    }
}

public sealed class JsonPricingSnapshotRepository : IPricingSnapshotRepository
{
    public string FilePath { get; }
    public JsonPricingSnapshotRepository(string? rootDirectory = null) => FilePath = AppDataPaths.SnapshotsFile(rootDirectory);

    public IReadOnlyDictionary<string, PricingSnapshot> LoadAll() =>
        new Dictionary<string, PricingSnapshot>(AtomicJsonFile.Read(FilePath, new Dictionary<string, PricingSnapshot>(StringComparer.Ordinal)), StringComparer.Ordinal);

    public PricingSnapshot? Load(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return LoadAll().TryGetValue(providerId, out var snapshot) ? snapshot : null;
    }

    public void SaveAll(IEnumerable<PricingSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var all = new Dictionary<string, PricingSnapshot>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            all[snapshot.ProviderId] = snapshot;
        }
        AtomicJsonFile.Write(FilePath, all);
    }
    public void Save(PricingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var all = new Dictionary<string, PricingSnapshot>(LoadAll(), StringComparer.Ordinal) { [snapshot.ProviderId] = snapshot };
        AtomicJsonFile.Write(FilePath, all);
    }
    public void Delete(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var all = new Dictionary<string, PricingSnapshot>(LoadAll(), StringComparer.Ordinal);
        if (all.Remove(providerId)) AtomicJsonFile.Write(FilePath, all);
    }
}
