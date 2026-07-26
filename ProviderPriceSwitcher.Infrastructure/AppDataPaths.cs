namespace ProviderPriceSwitcher.Infrastructure;

public static class AppDataPaths
{
    public const string ApplicationDirectoryName = "ProviderPriceSwitcher";

    public static string GetRoot(string? rootDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(rootDirectory))
            return Path.GetFullPath(rootDirectory);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationDirectoryName);
    }

    public static string SettingsFile(string? rootDirectory = null) =>
        Path.Combine(GetRoot(rootDirectory), "settings.json");

    public static string SnapshotsFile(string? rootDirectory = null) =>
        Path.Combine(GetRoot(rootDirectory), "pricing-snapshots.json");
}
