using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Application;

public static class SidecarProtocol
{
    public const string Version = "pps.sidecar.v1";
    public const string NoActiveRouteCode = "pps_no_active_route";
}

public enum SidecarConnectionStatus { Stopped, Starting, Ready, Disconnected, Faulted }
public sealed record SidecarStatus(SidecarConnectionStatus Status, string? Detail = null)
{
    public bool IsReady => Status == SidecarConnectionStatus.Ready;
}

public interface ISidecarStatus
{
    SidecarStatus Current { get; }
    event Action<SidecarStatus>? Changed;
}

public interface IRouteController
{
    Task ApplyAsync(RouteSnapshot snapshot, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface ISidecarLifecycle : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    SidecarStatus Status { get; }
}

public interface IInferenceApiKeyResolver
{
    ValueTask<string?> ResolveAsync(string keyHandle, CancellationToken cancellationToken = default);
}

public sealed class InferenceApiKeyResolverBridge(IInferenceApiKeyStore store, ISettingsRepository? settingsRepository = null) : IInferenceApiKeyResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _providers = new(StringComparer.Ordinal);

    public void Register(InferenceApiKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate) _providers[record.KeyHandle] = record.ProviderId;
    }

    public void Remove(string providerId)
    {
        lock (_gate)
        {
            foreach (var pair in _providers.Where(x => string.Equals(x.Value, providerId, StringComparison.Ordinal)).ToArray()) _providers.Remove(pair.Key);
        }
    }

    public ValueTask<string?> ResolveAsync(string keyHandle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHandle);
        cancellationToken.ThrowIfCancellationRequested();
        string? provider;
        lock (_gate) _providers.TryGetValue(keyHandle, out provider);
        if (provider is null && settingsRepository is not null)
        {
            provider = settingsRepository.Load().Sites.Select(site => site.ProviderId).FirstOrDefault(id => string.Equals(store.Load(id)?.KeyHandle, keyHandle, StringComparison.Ordinal));
        }
        if (provider is null) return ValueTask.FromResult<string?>(null);
        var record = store.Load(provider);
        return ValueTask.FromResult(record is not null && string.Equals(record.KeyHandle, keyHandle, StringComparison.Ordinal) ? record.ApiKey : null);
    }
}

public sealed class SidecarRouteUseCase(IRouteController routes)
{
    public Task ApplyAsync(RouteSnapshot snapshot, CancellationToken cancellationToken = default) => routes.ApplyAsync(snapshot, cancellationToken);
    public Task ClearAsync(CancellationToken cancellationToken = default) => routes.ClearAsync(cancellationToken);
}
