using System.IO;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Infrastructure;

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
