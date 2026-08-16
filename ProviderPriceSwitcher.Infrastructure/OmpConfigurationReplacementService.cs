using System.Security.Cryptography;
using System.Text;
using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed class OmpConfigurationService(
    OmpConfigurationSwitcher switcher,
    IAppPathDefaults pathDefaults,
    Func<string, CancellationToken, Task<string>>? readTextAsync = null) : IOmpConfigurationReplacementPort
{
    private const string LocalProviderTemplate = """
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

    public async Task<OmpConfigurationReplacementPreview> PreviewAsync(
        OmpConfigurationReplacementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OmpConfigurationReplacementTargets.IsSupported(request.TargetProvider))
            return InvalidPreview(request, OmpConfigurationReplacementFailureKind.InvalidTarget, "不支持的 OMP Provider 目标。");
        if (request.GatewayPort is < 1 or > 65535)
            return InvalidPreview(request, OmpConfigurationReplacementFailureKind.InvalidGatewayPort, "本地网关端口无效。");

        var configPath = pathDefaults.OmpConfigPath(request.OmpRootDirectory);
        if (!File.Exists(configPath))
            return InvalidPreview(request, OmpConfigurationReplacementFailureKind.ConfigurationMissing, "OMP 主 config.yml 不存在。");

        ConfigFile config;
        try
        {
            config = await ReadConfigAsync(configPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return InvalidPreview(request, OmpConfigurationReplacementFailureKind.ConfigurationReadFailed, "无法读取 OMP 主 config.yml。");
        }
        catch (UnauthorizedAccessException)
        {
            return InvalidPreview(request, OmpConfigurationReplacementFailureKind.ConfigurationReadFailed, "无法读取 OMP 主 config.yml。");
        }

        var configPlan = switcher.CreatePlan(config.Text, request.TargetProvider);
        if (!configPlan.IsValid)
            return InvalidPreview(request, OmpConfigurationReplacementFailureKind.ConfigurationInvalid, "OMP 主 config.yml 缺少有效的 modelRoles.default 或模型角色结构。");

        var changes = configPlan.RouteEdits
            .Select(edit => new OmpConfigurationReplacementChange(
                edit.ConfigurationPath,
                edit.OriginalProvider,
                request.TargetProvider,
                edit.ModelId,
                edit.OriginalReference,
                edit.NewReference))
            .ToArray();
        var modelsChangeKind = OmpModelsProviderChangeKind.None;
        string? modelsVersion = null;
        if (string.Equals(request.TargetProvider, OmpConfigurationReplacementTargets.LocalProviderId, StringComparison.Ordinal))
        {
            var modelsPath = pathDefaults.OmpModelsPath(request.OmpRootDirectory);
            try
            {
                var models = await ReadModelsAsync(modelsPath, cancellationToken).ConfigureAwait(false);
                modelsVersion = models.Version;
                var plan = ModelsProviderUpdater.Prepare(modelsPath, models.Exists ? models.Text : string.Empty, models.Exists, request.GatewayPort, models.OriginalBytes);
                modelsChangeKind = plan.ChangeKind;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                return InvalidPreview(request, OmpConfigurationReplacementFailureKind.ModelsReadFailed, "无法读取 OMP models.yml。");
            }
            catch (UnauthorizedAccessException)
            {
                return InvalidPreview(request, OmpConfigurationReplacementFailureKind.ModelsReadFailed, "无法读取 OMP models.yml。");
            }
        }

        return new(
            true,
            request,
            changes,
            modelsChangeKind,
            config.Version,
            modelsVersion);
    }

    public async Task<OmpConfigurationReplacementResult> ExecuteAsync(
        OmpConfigurationReplacementRequest request,
        OmpConfigurationReplacementPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.Succeeded)
            return Failed(preview, preview.FailureKind, preview.ErrorMessage);
        if (!RequestsMatch(request, preview.Request))
            return Failed(preview, OmpConfigurationReplacementFailureKind.StalePreview, "配置预览已失效，请重新预览后重试。");

        var configPath = pathDefaults.OmpConfigPath(request.OmpRootDirectory);
        if (!File.Exists(configPath))
            return Failed(preview, OmpConfigurationReplacementFailureKind.ConfigurationMissing, "OMP 主 config.yml 不存在。");

        ConfigFile config;
        try
        {
            config = await ReadConfigAsync(configPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return Failed(preview, OmpConfigurationReplacementFailureKind.ConfigurationReadFailed, "无法读取 OMP 主 config.yml。");
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(preview, OmpConfigurationReplacementFailureKind.ConfigurationReadFailed, "无法读取 OMP 主 config.yml。");
        }

        if (!string.Equals(config.Version, preview.ConfigurationVersion, StringComparison.Ordinal))
            return Failed(preview, OmpConfigurationReplacementFailureKind.StalePreview, "OMP config.yml 在确认前发生变化，请重新预览。");

        var configPlan = switcher.CreatePlan(config.Text, request.TargetProvider);
        if (!configPlan.IsValid || !Matches(preview, configPlan))
            return Failed(preview, OmpConfigurationReplacementFailureKind.StalePreview, "配置预览已失效，请重新预览后重试。");

        ModelsFile? models = null;
        ModelsPlan? modelsPlan = null;
        var isLocalTarget = string.Equals(request.TargetProvider, OmpConfigurationReplacementTargets.LocalProviderId, StringComparison.Ordinal);
        if (isLocalTarget)
        {
            var modelsPath = pathDefaults.OmpModelsPath(request.OmpRootDirectory);
            try
            {
                models = await ReadModelsAsync(modelsPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(models.Version, preview.ModelsVersion, StringComparison.Ordinal))
                    return Failed(preview, OmpConfigurationReplacementFailureKind.StalePreview, "OMP models.yml 在确认前发生变化，请重新预览。");
                modelsPlan = ModelsProviderUpdater.Prepare(modelsPath, models.Exists ? models.Text : string.Empty, models.Exists, request.GatewayPort, models.OriginalBytes);
                if (modelsPlan.ChangeKind != preview.ModelsChangeKind)
                    return Failed(preview, OmpConfigurationReplacementFailureKind.StalePreview, "OMP models.yml 在确认前发生变化，请重新预览。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                return Failed(preview, OmpConfigurationReplacementFailureKind.ModelsReadFailed, "无法读取 OMP models.yml。");
            }
            catch (UnauthorizedAccessException)
            {
                return Failed(preview, OmpConfigurationReplacementFailureKind.ModelsReadFailed, "无法读取 OMP models.yml。");
            }
        }
        if (!preview.HasChanges)
            return new(true, preview);

        string? modelsBackupPath = null;
        var modelsChanged = false;
        var backupRetentionSucceeded = true;
        try
        {
            if (modelsPlan is not null && modelsPlan.ChangeKind != OmpModelsProviderChangeKind.None)
            {
                ModelsWriteResult modelsWrite;
                try
                {
                    modelsWrite = await WriteModelsAsync(modelsPlan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (IOException)
                {
                    return Failed(preview, OmpConfigurationReplacementFailureKind.ModelsWriteFailed, "OMP models.yml 写入失败。");
                }
                catch (UnauthorizedAccessException)
                {
                    return Failed(preview, OmpConfigurationReplacementFailureKind.ModelsWriteFailed, "OMP models.yml 写入失败。");
                }
                if (!modelsWrite.Succeeded)
                {
                    if (modelsWrite.IsStale)
                        return Failed(preview, OmpConfigurationReplacementFailureKind.StalePreview, "OMP models.yml 在写入前发生变化，请重新预览。");
                    return Failed(preview, OmpConfigurationReplacementFailureKind.ModelsWriteFailed, "OMP models.yml 写入失败。");
                }
                modelsBackupPath = modelsWrite.BackupPath;
                modelsChanged = true;
                backupRetentionSucceeded &= modelsWrite.BackupRetentionSucceeded;
            }

            string? configBackupPath = null;
            var configChanged = false;
            if (preview.HasRouteChanges)
            {
                var switchResult = await OmpConfigurationSwitcher.ApplyPlanAsync(
                    configPath,
                    configPlan,
                    config.Version,
                    cancellationToken).ConfigureAwait(false);
                backupRetentionSucceeded &= switchResult.BackupRetentionSucceeded;
                if (!switchResult.Succeeded)
                {
                    if (modelsChanged && models is not null && !await RestoreModelsAsync(models, cancellationToken).ConfigureAwait(false))
                        return Failed(preview, OmpConfigurationReplacementFailureKind.RollbackFailed, "配置替换失败且无法回滚 models.yml。");
                    if (switchResult.Exception is OmpConfigurationStaleException)
                        return Failed(preview, OmpConfigurationReplacementFailureKind.StalePreview, "OMP config.yml 在写入前发生变化，请重新预览。");
                    return new(
                        false,
                        preview,
                        ModelsChanged: false,
                        ModelsBackupPath: modelsBackupPath,
                        BackupRetentionSucceeded: backupRetentionSucceeded,
                        FailureKind: OmpConfigurationReplacementFailureKind.ConfigurationWriteFailed,
                        ErrorMessage: "OMP config.yml 写入失败。");
                }
                configBackupPath = switchResult.BackupPath;
                configChanged = true;
            }

            return new(
                true,
                preview,
                configChanged,
                modelsChanged,
                configBackupPath,
                modelsBackupPath,
                backupRetentionSucceeded);
        }
        catch (OperationCanceledException)
        {
            if (modelsChanged && models is not null && !await RestoreModelsAsync(models, CancellationToken.None).ConfigureAwait(false))
                throw new IOException("omp_models_rollback_failed");
            throw;
        }
        catch (IOException)
        {
            if (modelsChanged && models is not null && !await RestoreModelsAsync(models, CancellationToken.None).ConfigureAwait(false))
                return Failed(preview, OmpConfigurationReplacementFailureKind.RollbackFailed, "配置替换失败且无法回滚 models.yml。");
            return Failed(preview, OmpConfigurationReplacementFailureKind.ConfigurationWriteFailed, "OMP 配置写入失败。");
        }
        catch (UnauthorizedAccessException)
        {
            if (modelsChanged && models is not null && !await RestoreModelsAsync(models, CancellationToken.None).ConfigureAwait(false))
                return Failed(preview, OmpConfigurationReplacementFailureKind.RollbackFailed, "配置替换失败且无法回滚 models.yml。");
            return Failed(preview, OmpConfigurationReplacementFailureKind.ConfigurationWriteFailed, "OMP 配置写入失败。");
        }
    }

    private async Task<ConfigFile> ReadConfigAsync(string path, CancellationToken cancellationToken)
    {
        if (readTextAsync is not null)
        {
            var customText = await readTextAsync(path, cancellationToken).ConfigureAwait(false);
            return new ConfigFile(customText, Hash(Encoding.UTF8.GetBytes(customText)));
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var encoding = OmpConfigurationSwitcher.DetectEncoding(bytes, out var preambleLength);
        var decodedText = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return new ConfigFile(decodedText, Hash(bytes));
    }

    private static async Task<ModelsFile> ReadModelsAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new ModelsFile(path, false, null, null, string.Empty);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var encoding = OmpConfigurationSwitcher.DetectEncoding(bytes, out var preambleLength);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return new ModelsFile(path, true, Hash(bytes), bytes, text);
    }

    private static bool Matches(OmpConfigurationReplacementPreview expected, OmpConfigurationRoutePlan actual)
    {
        if (expected.Changes.Count != actual.RouteEdits.Count)
            return false;
        return expected.Changes.Zip(actual.RouteEdits).All(pair =>
            string.Equals(pair.First.RolePath, pair.Second.ConfigurationPath, StringComparison.Ordinal)
            && string.Equals(pair.First.OriginalProvider, pair.Second.OriginalProvider, StringComparison.Ordinal)
            && string.Equals(pair.First.TargetProvider, actual.TargetProvider, StringComparison.Ordinal)
            && string.Equals(pair.First.ModelId, pair.Second.ModelId, StringComparison.Ordinal)
            && string.Equals(pair.First.OriginalReference, pair.Second.OriginalReference, StringComparison.Ordinal)
            && string.Equals(pair.First.NewReference, pair.Second.NewReference, StringComparison.Ordinal));
    }

    private static bool RequestsMatch(OmpConfigurationReplacementRequest current, OmpConfigurationReplacementRequest preview) =>
        string.Equals(current.OmpRootDirectory, preview.OmpRootDirectory, StringComparison.Ordinal)
        && string.Equals(current.TargetProvider, preview.TargetProvider, StringComparison.Ordinal)
        && current.GatewayPort == preview.GatewayPort;

    private static OmpConfigurationReplacementPreview InvalidPreview(
        OmpConfigurationReplacementRequest request,
        OmpConfigurationReplacementFailureKind failureKind,
        string errorMessage) =>
        new(false, request, Array.Empty<OmpConfigurationReplacementChange>(), FailureKind: failureKind, ErrorMessage: errorMessage);

    private static OmpConfigurationReplacementResult Failed(
        OmpConfigurationReplacementPreview preview,
        OmpConfigurationReplacementFailureKind failureKind,
        string? errorMessage) =>
        new(false, preview, FailureKind: failureKind, ErrorMessage: errorMessage);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task<ModelsWriteResult> WriteModelsAsync(ModelsPlan plan, CancellationToken cancellationToken)
    {
        var currentBytes = File.Exists(plan.Path)
            ? await File.ReadAllBytesAsync(plan.Path, cancellationToken).ConfigureAwait(false)
            : null;
        var currentVersion = currentBytes is null ? null : Hash(currentBytes);
        if (!string.Equals(currentVersion, plan.OriginalVersion, StringComparison.Ordinal))
            return new(false, null, true, true);
        var directory = Path.GetDirectoryName(Path.GetFullPath(plan.Path))!;
        Directory.CreateDirectory(directory);
        var encoding = plan.OriginalBytes is { Length: > 0 }
            ? OmpConfigurationSwitcher.DetectEncoding(plan.OriginalBytes, out _)
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var preamble = plan.OriginalBytes is { Length: > 0 } && plan.OriginalBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })
            ? encoding.GetPreamble()
            : Array.Empty<byte>();
        var output = preamble.Concat(encoding.GetBytes(plan.UpdatedText)).ToArray();
        string? backupPath = null;
        var tempPath = Path.Combine(directory, "." + Path.GetFileName(plan.Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var succeeded = false;
        try
        {
            await WriteBytesAtomicallyAsync(tempPath, plan.Path, output, cancellationToken).ConfigureAwait(false);
            succeeded = true;
        }
        catch (IOException)
        {
            TryDelete(tempPath);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(tempPath);
        }
        return new(succeeded, backupPath, true);
    }

    private static async Task WriteBytesAtomicallyAsync(
        string tempPath,
        string destinationPath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static async Task<bool> RestoreModelsAsync(ModelsFile original, CancellationToken cancellationToken)
    {
        try
        {
            if (!original.Exists)
            {
                if (File.Exists(original.Path))
                    File.Delete(original.Path);
                return true;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(original.Path))!;
            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(directory, "." + Path.GetFileName(original.Path) + ".rollback." + Guid.NewGuid().ToString("N") + ".tmp");
            await WriteBytesAtomicallyAsync(tempPath, original.Path, original.OriginalBytes ?? [], cancellationToken).ConfigureAwait(false);
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
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the write failure and let the caller report the stable failure kind.
        }
    }

    private sealed record ConfigFile(string Text, string Version);
    private sealed record ModelsFile(string Path, bool Exists, string? Version, byte[]? OriginalBytes, string Text);
    private sealed record ModelsWriteResult(
        bool Succeeded,
        string? BackupPath,
        bool BackupRetentionSucceeded,
        bool IsStale = false);

    private sealed record ModelsPlan(
        string Path,
        bool Exists,
        byte[] OriginalBytes,
        string UpdatedText,
        OmpModelsProviderChangeKind ChangeKind,
        string? OriginalVersion);

    private static class ModelsProviderUpdater
    {
        public static ModelsPlan Prepare(string path, string source, bool exists, int gatewayPort, byte[]? originalBytes = null)
        {
            var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var trailingNewline = source.EndsWith('\n') || source.EndsWith('\r');
            var lines = source.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).ToList();
            if (lines.Count == 1 && lines[0].Length == 0)
                lines.Clear();
            if (trailingNewline && lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);

            var providersIndex = lines.FindIndex(line => Indent(line) == 0 && ProviderKey(line) == "providers");
            var providerIndent = 2;
            if (providersIndex >= 0)
            {
                var providersIndent = Indent(lines[providersIndex]);
                providerIndent = providersIndent + 2;
                for (var index = providersIndex + 1; index < lines.Count; index++)
                {
                    var trimmed = lines[index].Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                        continue;
                    if (Indent(lines[index]) <= providersIndent)
                        break;
                    providerIndent = Indent(lines[index]);
                    break;
                }
            }
            var block = BuildProviderBlock(gatewayPort, newline, providerIndent).Split(newline, StringSplitOptions.None).ToList();
            var providerStarts = new List<int>();
            if (providersIndex >= 0)
            {
                for (var index = providersIndex + 1; index < lines.Count; index++)
                {
                    var trimmed = lines[index].Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                        continue;
                    if (Indent(lines[index]) <= Indent(lines[providersIndex]))
                        break;
                    if (Indent(lines[index]) == providerIndent && ProviderKey(lines[index]) == OmpConfigurationReplacementTargets.LocalProviderId)
                        providerStarts.Add(index);
                }
            }
            var changeKind = !exists || providerStarts.Count == 0
                ? OmpModelsProviderChangeKind.Added
                : OmpModelsProviderChangeKind.Updated;
            if (providersIndex < 0)
            {
                if (lines.Count != 0)
                    lines.Add(string.Empty);
                lines.Add("providers:");
                lines.AddRange(block);
            }
            else if (providerStarts.Count == 0)
            {
                lines.InsertRange(providersIndex + 1, block);
            }
            else
            {
                var ranges = providerStarts
                    .Select(start => (Start: start, End: FindBlockEnd(lines, start, providerIndent)))
                    .OrderByDescending(range => range.Start)
                    .ToArray();
                foreach (var range in ranges)
                    lines.RemoveRange(range.Start, range.End - range.Start);
                var firstStart = ranges[^1].Start;
                lines.InsertRange(firstStart, block);
            }

            var originalFileBytes = originalBytes ?? Encoding.UTF8.GetBytes(source);
            var updated = string.Join(newline, lines);
            if (trailingNewline || !exists)
                updated += newline;
            if (updated == source)
                changeKind = OmpModelsProviderChangeKind.None;
            return new(path, exists, originalFileBytes, updated, changeKind, exists ? Hash(originalFileBytes) : null);
        }

        private static int FindBlockEnd(IReadOnlyList<string> lines, int start, int providerIndent)
        {
            var lastContent = start;
            for (var index = start + 1; index < lines.Count; index++)
            {
                var trimmed = lines[index].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;
                if (Indent(lines[index]) <= providerIndent)
                    return lastContent + 1;
                lastContent = index;
            }
            return lastContent + 1;
        }

        private static int Indent(string line) => line.TakeWhile(ch => ch is ' ' or '\t').Count();
        private static string ProviderKey(string line)
        {
            var trimmed = line.Trim();
            var comment = trimmed.IndexOf(" #", StringComparison.Ordinal);
            var key = (comment >= 0 ? trimmed[..comment] : trimmed).Trim().TrimEnd(':').TrimEnd();
            return key.Length >= 2 && ((key[0] == '\'' && key[^1] == '\'') || (key[0] == '"' && key[^1] == '"'))
                ? key[1..^1]
                : key;
        }

        private static string BuildProviderBlock(int gatewayPort, string newline, int providerIndent)
        {
            var lines = LocalProviderTemplate
                .Replace("{0}", gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n');
            var templateIndent = lines.First(line => line.Length != 0).TakeWhile(ch => ch is ' ' or '\t').Count();
            var delta = providerIndent - templateIndent;
            return string.Join(
                newline,
                lines.Select(line =>
                {
                    if (line.Length == 0)
                        return line;
                    var leading = line.TakeWhile(ch => ch is ' ' or '\t').Count();
                    return delta >= 0
                        ? new string(' ', delta) + line
                        : line[Math.Min(-delta, leading)..];
                }))
                .TrimEnd('\r', '\n');
        }
    }
}
