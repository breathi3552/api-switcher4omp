using System.Collections.Immutable;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Application;

public sealed record LocalAppSettings
{
    private ImmutableArray<SiteConfiguration> _sites = ImmutableArray<SiteConfiguration>.Empty;
    private ImmutableArray<string> _ompWorkingDirectories = ImmutableArray<string>.Empty;

    public string Model { get; init; } = "gpt-5.6-sol";
    public int RequestTimeoutSeconds { get; init; } = 10;
    public IReadOnlyList<SiteConfiguration> Sites
    {
        get => _sites;
        init => _sites = value is null ? ImmutableArray<SiteConfiguration>.Empty : value.ToImmutableArray();
    }
    public string OmpRootDirectory { get; init; } = string.Empty;
    public IReadOnlyList<string> OmpWorkingDirectories
    {
        get => _ompWorkingDirectories;
        init => _ompWorkingDirectories = value is null ? ImmutableArray<string>.Empty : value.ToImmutableArray();
    }
    public string? LastOmpWorkingDirectory { get; init; }

    public static UsageProfile DefaultUsageProfile { get; } = new()
    {
        ProfileId = "codex-high-cache",
        UncachedInputTokens = 200_000,
        CachedInputTokens = 800_000,
        OutputTokens = 100_000
    };
}
