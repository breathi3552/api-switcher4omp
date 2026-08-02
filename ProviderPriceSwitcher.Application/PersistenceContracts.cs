using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Application;

public interface IAppPathDefaults
{
    string OmpRootDirectory { get; }
    string OmpConfigPath(string ompRootDirectory);
    string OmpAgentDirectory(string ompRootDirectory);
}

public interface ISettingsRepository
{
    LocalAppSettings Load();
    void Save(LocalAppSettings settings);
}

public interface IPricingSnapshotRepository
{
    IReadOnlyDictionary<string, PricingSnapshot> LoadAll();
    PricingSnapshot? Load(string providerId);
    void SaveAll(IEnumerable<PricingSnapshot> snapshots);
    void Save(PricingSnapshot snapshot);
    void Delete(string providerId);
}

public enum OmpCurrentProviderStatus
{
    Identified,
    ConfigurationFileMissing,
    Unrecognized,
    ReadFailed
}

public sealed record OmpCurrentProviderResult(OmpCurrentProviderStatus Status, string? ProviderId = null)
{
    public bool IsIdentified => Status == OmpCurrentProviderStatus.Identified && !string.IsNullOrWhiteSpace(ProviderId);
}

public interface IOmpCurrentProviderQuery
{
    Task<OmpCurrentProviderResult> ReadAsync(string ompRootDirectory, CancellationToken cancellationToken = default);
}

public enum PricingSnapshotQueryStatus
{
    Succeeded,
    ReadFailed
}

public sealed record PricingSnapshotQueryResult(
    PricingSnapshotQueryStatus Status,
    IReadOnlyDictionary<string, PricingSnapshot> Snapshots)
{
    public bool IsSuccess => Status == PricingSnapshotQueryStatus.Succeeded;
}

public interface IPricingSnapshotQuery
{
    PricingSnapshotQueryResult Load();
}

public interface IActiveRouteController
{
    void ClearIfProvider(string providerId);
}

public sealed class ActiveRouteState : IActiveRouteController
{
    private readonly object _gate = new();
    private RouteSnapshot? _current;

    public RouteSnapshot? Current { get { lock (_gate) return _current; } }
    public void Apply(RouteSnapshot snapshot) { ArgumentNullException.ThrowIfNull(snapshot); lock (_gate) _current = snapshot; }
    public void ClearIfProvider(string providerId) { ArgumentException.ThrowIfNullOrWhiteSpace(providerId); lock (_gate) { if (string.Equals(_current?.ProviderId, providerId, StringComparison.Ordinal)) _current = null; } }
}
