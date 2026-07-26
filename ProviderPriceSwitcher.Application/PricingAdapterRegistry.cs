namespace ProviderPriceSwitcher.Application;

public sealed class PricingAdapterRegistry : IPricingAdapterRegistry
{
    private readonly IReadOnlyList<PricingAdapterDescriptor> _descriptors;
    private readonly Dictionary<string, IPricingAdapter> _adapters;

    public PricingAdapterRegistry(IEnumerable<IPricingAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var descriptors = new List<PricingAdapterDescriptor>();
        var map = new Dictionary<string, IPricingAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            var descriptor = adapter.Descriptor;
            if (string.IsNullOrWhiteSpace(descriptor.SiteType))
                throw new ArgumentException("Pricing adapters must declare a site type.", nameof(adapters));
            if (!map.TryAdd(descriptor.SiteType, adapter))
                throw new ArgumentException($"Duplicate pricing adapter site type '{descriptor.SiteType}'.", nameof(adapters));
            descriptors.Add(descriptor);
        }

        _adapters = map;
        _descriptors = descriptors;
    }

    public IReadOnlyList<PricingAdapterDescriptor> Descriptors => _descriptors;

    public bool TryGet(string siteType, out IPricingAdapter adapter)
    {
        if (string.IsNullOrWhiteSpace(siteType))
        {
            adapter = null!;
            return false;
        }

        return _adapters.TryGetValue(siteType, out adapter!);
    }
}
