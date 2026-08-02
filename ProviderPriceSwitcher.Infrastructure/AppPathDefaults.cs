using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed class AppPathDefaults : IAppPathDefaults
{
    public string OmpRootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".omp");

    public string OmpConfigPath(string ompRootDirectory) =>
        Path.Combine(ompRootDirectory, "agent", "config.yml");
    public string OmpModelsPath(string ompRootDirectory) =>
        Path.Combine(ompRootDirectory, "agent", "models.yml");

    public string OmpAgentDirectory(string ompRootDirectory) =>
        Path.Combine(ompRootDirectory, "agent");
}
