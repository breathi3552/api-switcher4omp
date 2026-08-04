using ProviderPriceSwitcher.Application;
using System.Text.RegularExpressions;

namespace ProviderPriceSwitcher.Infrastructure;
public sealed class OmpTakeoverStatusReadFailedException()
    : IOException("omp_takeover_status_read_failed");


public sealed class OmpConfigurationService(
    OmpConfigurationSwitcher switcher,
    IAppPathDefaults pathDefaults) : IOmpTakeoverService
{
    private const string SidecarProviderTemplate = """
          provider-price-switcher:
            baseUrl: http://127.0.0.1:{0}/v1
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

    public async Task<OmpTakeoverCheckResult> CheckAsync(
        string ompRootDirectory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = pathDefaults.OmpConfigPath(ompRootDirectory);
            if (!File.Exists(path))
                return new OmpTakeoverCheckResult(OmpTakeoverStatus.NotTakenOver);
            cancellationToken.ThrowIfCancellationRequested();
            var analysis = new OmpConfigurationAnalyzer().AnalyzeFile(path);
            var provider = analysis.CurrentProvider;
            var isTakenOver = string.Equals(provider, OmpSidecarProvider.Id, StringComparison.Ordinal);
            var currentPort = isTakenOver
                ? await ReadSidecarPortAsync(pathDefaults.OmpModelsPath(ompRootDirectory), cancellationToken).ConfigureAwait(false)
                : null;
            if (isTakenOver && currentPort is null)
                return new OmpTakeoverCheckResult(OmpTakeoverStatus.ReadFailed, provider);
            return new OmpTakeoverCheckResult(
                isTakenOver ? OmpTakeoverStatus.TakenOver : OmpTakeoverStatus.NotTakenOver,
                provider,
                currentPort);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return new OmpTakeoverCheckResult(OmpTakeoverStatus.ReadFailed);
        }
        catch (UnauthorizedAccessException)
        {
            return new OmpTakeoverCheckResult(OmpTakeoverStatus.ReadFailed);
        }
    }

    private static async Task<int?> ReadSidecarPortAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var match = Regex.Match(text, @"(?ms)^\s{2}provider-price-switcher:\s*$.*?^\s{4}baseUrl:\s*http://127\.0\.0\.1:(?<port>\d+)/v1\s*$", RegexOptions.CultureInvariant);
        return int.TryParse(match.Groups["port"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535
            ? port
            : null;
    }

    public async Task<OmpTakeoverOperationResult> TakeOverAsync(
        string ompRootDirectory,
        int gatewayPort,
        CancellationToken cancellationToken = default)
    {
        if (gatewayPort is < 1 or > 65535)
            return new(false, OmpTakeoverFailureKind.InvalidPort);

        var configPath = pathDefaults.OmpConfigPath(ompRootDirectory);
        var modelsPath = pathDefaults.OmpModelsPath(ompRootDirectory);
        var modelsExisted = File.Exists(modelsPath);
        // models.yml may contain a real provider key; keep rollback only in memory, never as a tool backup.
        string? originalModels = null;
        var modelsChanged = false;
        try
        {
            var configText = File.Exists(configPath)
                ? await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false)
                : OmpConfigurationSwitcher.BootstrapConfiguration;
            if (!switcher.Preview(configText, OmpSidecarProvider.Id).IsValid)
                return new(false, OmpTakeoverFailureKind.Configuration);

            originalModels = modelsExisted
                ? await File.ReadAllTextAsync(modelsPath, cancellationToken).ConfigureAwait(false)
                : null;
            modelsChanged = true;
            await EnsureSidecarProviderAsync(modelsPath, gatewayPort, cancellationToken).ConfigureAwait(false);
            var result = await switcher.SwitchFileAsync(configPath, OmpSidecarProvider.Id, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                var restored = await RestoreModelsAsync(modelsPath, modelsExisted, originalModels).ConfigureAwait(false);
                if (result.Exception is OperationCanceledException)
                {
                    if (!restored) throw new IOException("omp_models_rollback_failed", result.Exception);
                    throw new OperationCanceledException("omp_takeover_cancelled", result.Exception, cancellationToken);
                }
                return restored
                    ? new(false, OmpTakeoverFailureKind.Configuration, result.BackupRetentionSucceeded)
                    : new(false, OmpTakeoverFailureKind.RollbackFailed, result.BackupRetentionSucceeded);
            }
            return new(true, BackupRetentionSucceeded: result.BackupRetentionSucceeded);
        }
        catch (OperationCanceledException exception)
        {
            if (modelsChanged && !await RestoreModelsAsync(modelsPath, modelsExisted, originalModels).ConfigureAwait(false))
                throw new IOException("omp_models_rollback_failed", exception);
            throw;
        }
        catch (IOException)
        {
            var restored = !modelsChanged || await RestoreModelsAsync(modelsPath, modelsExisted, originalModels).ConfigureAwait(false);
            return restored
                ? new(false, OmpTakeoverFailureKind.Configuration)
                : new(false, OmpTakeoverFailureKind.RollbackFailed);
        }
        catch (UnauthorizedAccessException)
        {
            var restored = !modelsChanged || await RestoreModelsAsync(modelsPath, modelsExisted, originalModels).ConfigureAwait(false);
            return restored
                ? new(false, OmpTakeoverFailureKind.Configuration)
                : new(false, OmpTakeoverFailureKind.RollbackFailed);
        }
    }

    private static async Task<bool> RestoreModelsAsync(string path, bool existed, string? originalText)
    {
        try
        {
            if (!existed)
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            await WriteTextAtomicallyAsync(path, originalText ?? string.Empty, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }


    private static async Task EnsureSidecarProviderAsync(string path, int gatewayPort, CancellationToken cancellationToken)
    {
        var text = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : string.Empty;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var providerBlock = SidecarProviderTemplate.Replace(
                "{0}",
                gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", newline, StringComparison.Ordinal);
        var normalized = "providers:" + newline + providerBlock;
        await WriteTextAtomicallyAsync(path, normalized, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextAtomicallyAsync(string path, string text, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("OMP models path has no directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
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
        return new OmpLaunchResult(result.Succeeded);
    }
}
