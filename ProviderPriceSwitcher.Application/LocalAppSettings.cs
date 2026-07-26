using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Application;

public sealed record LocalAppSettings
{
    public string Model { get; init; } = "gpt-5.6-sol";
    public int RequestTimeoutSeconds { get; init; } = 10;
    public List<SiteConfiguration> Sites { get; init; } = [];
    public string OmpRootDirectory { get; init; } = string.Empty;
    public List<string> OmpWorkingDirectories { get; init; } = [];
    public string? LastOmpWorkingDirectory { get; init; }

    public static UsageProfile DefaultUsageProfile { get; } = new()
    {
        ProfileId = "codex-high-cache",
        UncachedInputTokens = 200_000,
        CachedInputTokens = 800_000,
        OutputTokens = 100_000
    };

}
