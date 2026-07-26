using System.Text.Json.Serialization;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed record LocalAppSettings
{
    public string Model { get; init; } = "gpt-5.6-sol";
    public int RequestTimeoutSeconds { get; init; } = 10;
    public List<SiteConfiguration> Sites { get; init; } = [];
    public string OmpRootDirectory { get; init; } = DefaultOmpRootDirectory();
    [JsonIgnore]
    public string OmpConfigPath => Path.Combine(OmpRootDirectory, "agent", "config.yml");
    public List<string> OmpWorkingDirectories { get; init; } = [DefaultOmpAgentDirectory()];
    public string? LastOmpWorkingDirectory { get; init; } = DefaultOmpAgentDirectory();

    public static UsageProfile DefaultUsageProfile { get; } = new()
    {
        ProfileId = "codex-high-cache",
        UncachedInputTokens = 200_000,
        CachedInputTokens = 800_000,
        OutputTokens = 100_000
    };

    private static string DefaultOmpRootDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omp");

    private static string DefaultOmpAgentDirectory() =>
        Path.Combine(DefaultOmpRootDirectory(), "agent");
}
public sealed class JsonDataException : IOException
{
    public JsonDataException(string path, Exception innerException)
        : base($"The JSON data file '{path}' is invalid or corrupted.", innerException) { }
}
