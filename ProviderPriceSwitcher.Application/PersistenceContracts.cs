using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Application;

public interface IAppPathDefaults
{
    string OmpRootDirectory { get; }
    string OmpConfigPath(string ompRootDirectory);
    string OmpModelsPath(string ompRootDirectory);
    string OmpAgentDirectory(string ompRootDirectory);
}

public interface ISettingsRepository
{
    LocalAppSettings Load();
    void Save(LocalAppSettings settings);
    LocalAppSettings Update(Func<LocalAppSettings, LocalAppSettings> update);
}

public interface IInferenceBindingStore
{
    void Recover();
    InferenceApiKeySummary Save(string providerId, string apiKey, string boundGroup);
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
    RouteSnapshot? Current { get; }
    string? CurrentProviderId { get; }
    event Action? Changed;
    Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default);
    void Apply(RouteSnapshot snapshot);
    void ClearIfProvider(string providerId);
}

public sealed class ActiveRouteState : IActiveRouteController, IDisposable
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private RouteSnapshot? _current;

    public RouteSnapshot? Current { get { lock (_stateGate) return _current; } }
    public string? CurrentProviderId => Current?.ProviderId;
    public event Action? Changed;

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new RouteLease(_operationGate);
    }

    public void Apply(RouteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_stateGate) _current = snapshot;
        Changed?.Invoke();
    }

    public void ClearIfProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var cleared = false;
        lock (_stateGate)
        {
            if (string.Equals(_current?.ProviderId, providerId, StringComparison.Ordinal))
            {
                _current = null;
                cleared = true;
            }
        }
        if (cleared) Changed?.Invoke();
    }

    public void Dispose() => _operationGate.Dispose();

    private sealed class RouteLease(SemaphoreSlim operationGate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                operationGate.Release();
        }
    }
}
