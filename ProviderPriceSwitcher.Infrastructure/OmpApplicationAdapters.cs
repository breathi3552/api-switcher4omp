using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed class OmpConfigurationService(
    OmpConfigurationSwitcher switcher,
    IAppPathDefaults pathDefaults) : IOmpConfigurationService
{
    private const string SidecarProvider = """
          provider-price-switcher:
            baseUrl: http://127.0.0.1:8080/v1
            apiKey: PPS_SIDECAR_PLACEHOLDER
            api: openai-responses
            authHeader: true
            discovery:
              type: proxy
            models:
              - id: gpt-5.6-sol
                name: GPT 5.6 Sol via ProviderPriceSwitcher
                contextWindow: 400000
                maxTokens: 128000
        """;
    public async Task<OmpConfigurationOperationResult> SwitchAsync(
        string ompRootDirectory,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureSidecarProviderAsync(pathDefaults.OmpModelsPath(ompRootDirectory), cancellationToken).ConfigureAwait(false);
            var result = await switcher.SwitchFileAsync(
                pathDefaults.OmpConfigPath(ompRootDirectory),
                providerId,
                cancellationToken).ConfigureAwait(false);
            return new OmpConfigurationOperationResult(result.Succeeded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }
    private static async Task EnsureSidecarProviderAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("OMP models path has no directory.");
        Directory.CreateDirectory(directory);
        var text = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : string.Empty;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var providerBlock = SidecarProvider.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
        var providerPattern = @"(?m)^  provider-price-switcher:\r?\n(?:(?:    .*|\s*)\r?\n)*";
        string normalized;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, providerPattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            normalized = System.Text.RegularExpressions.Regex.Replace(text, providerPattern, providerBlock + newline, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        else if (text.Length == 0)
            normalized = "providers:" + newline + providerBlock;
        else if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(?m)^providers:\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            normalized = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^providers:\s*$", "providers:" + newline + providerBlock, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        else
            normalized = text.TrimEnd() + newline + "providers:" + newline + providerBlock;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                await writer.WriteAsync(normalized.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

public sealed class OmpProcessLauncher(OmpProcessService processService) : IOmpProcessLauncher
{
    public OmpLaunchResult Launch(string workingDirectory)
    {
        var result = processService.Start(new OmpProcessStartRequest(workingDirectory));
        return new OmpLaunchResult(result.Succeeded, result.ExistingProcess.Exists);
    }
}
