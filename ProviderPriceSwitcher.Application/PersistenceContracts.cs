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
