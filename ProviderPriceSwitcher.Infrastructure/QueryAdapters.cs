using System.IO;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed class OmpCurrentProviderQuery(
    OmpConfigurationSwitcher switcher,
    IAppPathDefaults pathDefaults) : IOmpCurrentProviderQuery
{
    public async Task<OmpCurrentProviderResult> ReadAsync(string ompRootDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ompRootDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var path = pathDefaults.OmpConfigPath(ompRootDirectory);
        if (!File.Exists(path))
            return new(OmpCurrentProviderStatus.ConfigurationFileMissing);
        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var preview = switcher.Preview(text, "temporary");
            return preview.CurrentProvider is { Length: > 0 } provider
                ? new(OmpCurrentProviderStatus.Identified, provider)
                : new(OmpCurrentProviderStatus.Unrecognized);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return new(OmpCurrentProviderStatus.ReadFailed);
        }
        catch (UnauthorizedAccessException)
        {
            return new(OmpCurrentProviderStatus.ReadFailed);
        }
    }
}

public sealed class PricingSnapshotQuery(IPricingSnapshotRepository repository) : IPricingSnapshotQuery
{
    public PricingSnapshotQueryResult Load()
    {
        try
        {
            return new(PricingSnapshotQueryStatus.Succeeded, repository.LoadAll());
        }
        catch (IOException)
        {
            return new(PricingSnapshotQueryStatus.ReadFailed, new Dictionary<string, PricingSnapshot>(StringComparer.Ordinal));
        }
        catch (UnauthorizedAccessException)
        {
            return new(PricingSnapshotQueryStatus.ReadFailed, new Dictionary<string, PricingSnapshot>(StringComparer.Ordinal));
        }
        catch (System.Text.Json.JsonException)
        {
            return new(PricingSnapshotQueryStatus.ReadFailed, new Dictionary<string, PricingSnapshot>(StringComparer.Ordinal));
        }
    }
}
