using System.Security.Cryptography;
using System.Text;
using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed class OmpConfigurationService(
    OmpConfigurationSwitcher switcher,
    IAppPathDefaults pathDefaults,
    Func<string, CancellationToken, Task<string>>? readTextAsync = null,
    Action<string>? onFileWrittenForTesting = null) : IOmpConfigurationReplacementPort
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

        var buildResult = await BuildPlanAsync(request, null, cancellationToken).ConfigureAwait(false);
        if (buildResult.Plan is null)
            return InvalidPreview(request, buildResult.FailureKind, buildResult.ErrorMessage!);

        return CreatePreview(buildResult.Plan);
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

        var buildResult = await BuildPlanAsync(request, preview, cancellationToken).ConfigureAwait(false);
        if (buildResult.Plan is null)
            return Failed(preview, buildResult.FailureKind, buildResult.ErrorMessage);

        var replacementPlan = buildResult.Plan;
        if (!Matches(preview, replacementPlan))
            return Failed(preview, OmpConfigurationReplacementFailureKind.StalePreview, "配置预览已失效，请重新预览后重试。");

        return await OmpConfigurationTransaction.CommitAsync(
            replacementPlan,
            preview,
            onFileWrittenForTesting,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlanBuildResult> BuildPlanAsync(
        OmpConfigurationReplacementRequest request,
        OmpConfigurationReplacementPreview? expectedPreview,
        CancellationToken cancellationToken)
    {
        var configPath = pathDefaults.OmpConfigPath(request.OmpRootDirectory);
        if (!File.Exists(configPath))
            return PlanBuildResult.Failed(OmpConfigurationReplacementFailureKind.ConfigurationMissing, "OMP 主 config.yml 不存在。");

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
            return PlanBuildResult.Failed(OmpConfigurationReplacementFailureKind.ConfigurationReadFailed, "无法读取 OMP 主 config.yml。");
        }
        catch (UnauthorizedAccessException)
        {
            return PlanBuildResult.Failed(OmpConfigurationReplacementFailureKind.ConfigurationReadFailed, "无法读取 OMP 主 config.yml。");
        }
        if (expectedPreview is not null
            && !string.Equals(config.Version, expectedPreview.ConfigurationVersion, StringComparison.Ordinal))
        {
            return PlanBuildResult.Failed(
                OmpConfigurationReplacementFailureKind.StalePreview,
                "OMP config.yml 在确认前发生变化，请重新预览。");
        }

        var routePlan = switcher.CreatePlan(config.Text, request.TargetProvider);
        if (!routePlan.IsValid)
            return PlanBuildResult.Failed(OmpConfigurationReplacementFailureKind.ConfigurationInvalid, "OMP 主 config.yml 缺少有效的 modelRoles.default 或模型角色结构。");

        var changes = routePlan.RouteEdits
            .Select(edit => new OmpConfigurationReplacementChange(
                edit.ConfigurationPath,
                edit.OriginalProvider,
                request.TargetProvider,
                edit.ModelId,
                edit.OriginalReference,
                edit.NewReference))
            .ToArray();

        ModelsFile? models = null;
        ModelsPlan? modelsPlan = null;
        var isLocalTarget = string.Equals(
            request.TargetProvider,
            OmpConfigurationReplacementTargets.LocalProviderId,
            StringComparison.Ordinal);
        if (isLocalTarget)
        {
            var modelsPath = pathDefaults.OmpModelsPath(request.OmpRootDirectory);
            try
            {
                models = await ReadModelsAsync(modelsPath, cancellationToken).ConfigureAwait(false);
                if (expectedPreview is not null
                    && !string.Equals(models.Version, expectedPreview.ModelsVersion, StringComparison.Ordinal))
                {
                    return PlanBuildResult.Failed(
                        OmpConfigurationReplacementFailureKind.StalePreview,
                        "OMP models.yml 在确认前发生变化，请重新预览。");
                }
                modelsPlan = ModelsProviderUpdater.Prepare(
                    modelsPath,
                    models.Exists ? models.Text : string.Empty,
                    models.Exists,
                    request.GatewayPort,
                    models.OriginalBytes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                return PlanBuildResult.Failed(OmpConfigurationReplacementFailureKind.ModelsReadFailed, "无法读取 OMP models.yml。");
            }
            catch (UnauthorizedAccessException)
            {
                return PlanBuildResult.Failed(OmpConfigurationReplacementFailureKind.ModelsReadFailed, "无法读取 OMP models.yml。");
            }
        }

        return new(new OmpConfigurationReplacementPlan(
            request,
            configPath,
            config,
            routePlan,
            changes,
            isLocalTarget,
            models,
            modelsPlan));
    }

    private static OmpConfigurationReplacementPreview CreatePreview(OmpConfigurationReplacementPlan plan) =>
        new(
            true,
            plan.Request,
            plan.Changes,
            plan.ModelsChangeKind,
            plan.Configuration.Version,
            plan.ModelsVersion);

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

    private static bool Matches(
        OmpConfigurationReplacementPreview expected,
        OmpConfigurationReplacementPlan actual) =>
        RequestsMatch(actual.Request, expected.Request)
        && string.Equals(actual.Configuration.Version, expected.ConfigurationVersion, StringComparison.Ordinal)
        && string.Equals(actual.ModelsVersion, expected.ModelsVersion, StringComparison.Ordinal)
        && actual.ModelsChangeKind == expected.ModelsChangeKind
        && expected.Changes.SequenceEqual(actual.Changes);

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

    private static class OmpConfigurationTransaction
    {
        public static async Task<OmpConfigurationReplacementResult> CommitAsync(
            OmpConfigurationReplacementPlan plan,
            OmpConfigurationReplacementPreview preview,
            Action<string>? onFileWrittenForTesting,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!plan.HasChanges)
                return new(true, preview);

            var configPath = plan.ConfigurationPath;
            var config = plan.Configuration;
            var configPlan = plan.RoutePlan;
            var models = plan.Models;
            var modelsPlan = plan.ModelsPlan;

            var modelsChanged = false;
            var configChanged = false;
            string? configBackupPath = null;
            var backupRetentionSucceeded = true;

            try
            {
                if (modelsPlan is not null && modelsPlan.ChangeKind != OmpModelsProviderChangeKind.None)
                {
                    var modelsWrite = await WriteModelsAtomicallyAsync(
                        modelsPlan,
                        onFileWrittenForTesting,
                        cancellationToken).ConfigureAwait(false);
                    if (!modelsWrite.Succeeded)
                    {
                        if (modelsWrite.IsStale)
                            return Failed(preview, OmpConfigurationReplacementFailureKind.StalePreview, "OMP models.yml 在写入前发生变化，请重新预览。");
                        return Failed(preview, OmpConfigurationReplacementFailureKind.ModelsWriteFailed, "OMP models.yml 写入失败。");
                    }
                    modelsChanged = true;
                }

                if (plan.HasRouteChanges)
                {
                    var configWrite = await WriteConfigAtomicallyAsync(
                        configPath,
                        configPlan,
                        config.Version,
                        onFileWrittenForTesting,
                        cancellationToken).ConfigureAwait(false);
                    backupRetentionSucceeded &= configWrite.BackupRetentionSucceeded;
                    if (!configWrite.Succeeded)
                    {
                        if (modelsChanged && models is not null && !await RestoreModelsAsync(models).ConfigureAwait(false))
                            return Failed(preview, OmpConfigurationReplacementFailureKind.RollbackFailed, "配置替换失败且无法回滚 models.yml。");
                        if (configWrite.IsStale)
                            return Failed(preview, OmpConfigurationReplacementFailureKind.StalePreview, "OMP config.yml 在写入前发生变化，请重新预览。");
                        return new(
                            false,
                            preview,
                            ModelsChanged: false,
                            BackupRetentionSucceeded: backupRetentionSucceeded,
                            FailureKind: OmpConfigurationReplacementFailureKind.ConfigurationWriteFailed,
                            ErrorMessage: "OMP config.yml 写入失败。");
                    }
                    configBackupPath = configWrite.BackupPath;
                    configChanged = true;
                }

                return new(
                    true,
                    preview,
                    ConfigurationChanged: configChanged,
                    ModelsChanged: modelsChanged,
                    ConfigurationBackupPath: configBackupPath,
                    ModelsBackupPath: null,
                    BackupRetentionSucceeded: backupRetentionSucceeded);
            }
            catch (OperationCanceledException)
            {
                if (modelsChanged && models is not null && !await RestoreModelsAsync(models).ConfigureAwait(false))
                    throw new IOException("omp_models_rollback_failed");
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (modelsChanged && models is not null && !await RestoreModelsAsync(models).ConfigureAwait(false))
                    return Failed(preview, OmpConfigurationReplacementFailureKind.RollbackFailed, "配置替换失败且无法回滚 models.yml。");
                return Failed(preview, OmpConfigurationReplacementFailureKind.ConfigurationWriteFailed, "OMP 配置写入失败。");
            }
        }

        private static async Task<ModelsWriteResult> WriteModelsAtomicallyAsync(
            ModelsPlan plan,
            Action<string>? onFileWrittenForTesting,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentBytes = File.Exists(plan.Path)
                ? await File.ReadAllBytesAsync(plan.Path, cancellationToken).ConfigureAwait(false)
                : null;
            var currentVersion = currentBytes is null ? null : Hash(currentBytes);
            if (!string.Equals(currentVersion, plan.OriginalVersion, StringComparison.Ordinal))
                return new(false, null, true, IsStale: true);

            var directory = Path.GetDirectoryName(Path.GetFullPath(plan.Path))!;
            Directory.CreateDirectory(directory);
            var encoding = plan.OriginalBytes is { Length: > 0 }
                ? OmpConfigurationSwitcher.DetectEncoding(plan.OriginalBytes, out _)
                : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var preamble = plan.OriginalBytes is { Length: > 0 } && plan.OriginalBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })
                ? encoding.GetPreamble()
                : Array.Empty<byte>();
            var output = preamble.Concat(encoding.GetBytes(plan.UpdatedText)).ToArray();
            var tempPath = Path.Combine(directory, "." + Path.GetFileName(plan.Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            var succeeded = false;
            try
            {
                await WriteBytesAtomicallyAsync(tempPath, plan.Path, output, cancellationToken).ConfigureAwait(false);
                succeeded = true;
                onFileWrittenForTesting?.Invoke(plan.Path);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDelete(tempPath);
                return new(false, null, true, IsStale: false);
            }
            finally
            {
                TryDelete(tempPath);
            }
            return new(succeeded, null, true, IsStale: false);
        }

        private static async Task<ConfigWriteResult> WriteConfigAtomicallyAsync(
            string path,
            OmpConfigurationRoutePlan plan,
            string expectedVersion,
            Action<string>? onFileWrittenForTesting,
            CancellationToken cancellationToken)
        {
            if (!plan.IsValid)
                return new(false, null, true, IsStale: false);
            if (!plan.CanApply)
                return new(true, null, true, IsStale: false);

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
                return new(false, null, true, IsStale: true);

            var currentBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Hash(currentBytes), expectedVersion, StringComparison.Ordinal))
                return new(false, null, true, IsStale: true);

            var encoding = OmpConfigurationSwitcher.DetectEncoding(currentBytes, out var preambleLength);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var fileName = Path.GetFileName(path);
            Directory.CreateDirectory(directory);

            string? backupPath = null;
            string? backupCandidatePath = null;
            var tempPath = Path.Combine(directory, "." + fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            var backupRetentionSucceeded = true;
            var succeeded = false;

            try
            {
                var output = encoding.GetPreamble().Concat(encoding.GetBytes(plan.UpdatedText)).ToArray();
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
                {
                    await stream.WriteAsync(output, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var recheckBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Hash(recheckBytes), expectedVersion, StringComparison.Ordinal))
                    return new(false, null, true, IsStale: true);

                cancellationToken.ThrowIfCancellationRequested();
                backupCandidatePath = OmpConfigurationSwitcher.CreateBackupPath(path);
                File.Copy(path, backupCandidatePath, overwrite: false);
                backupPath = backupCandidatePath;

                File.Move(tempPath, path, overwrite: true);
                succeeded = true;
                onFileWrittenForTesting?.Invoke(path);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                if (backupPath is null && backupCandidatePath is not null)
                    TryDelete(backupCandidatePath);
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDelete(tempPath);
                if (backupPath is null && backupCandidatePath is not null)
                    TryDelete(backupCandidatePath);
                return new(false, null, true, IsStale: false);
            }
            finally
            {
                TryDelete(tempPath);
                if (succeeded)
                {
                    try
                    {
                        OmpConfigurationSwitcher.PruneBackups(directory, fileName);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        backupRetentionSucceeded = false;
                    }
                }
            }

            return new(succeeded, backupPath, backupRetentionSucceeded, IsStale: false);
        }

        private static async Task<bool> RestoreModelsAsync(ModelsFile original)
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
                await WriteBytesAtomicallyAsync(tempPath, original.Path, original.OriginalBytes ?? [], CancellationToken.None).ConfigureAwait(false);
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
    }

    private sealed record ConfigWriteResult(
        bool Succeeded,
        string? BackupPath,
        bool BackupRetentionSucceeded,
        bool IsStale = false);
    private sealed record PlanBuildResult(
        OmpConfigurationReplacementPlan? Plan,
        OmpConfigurationReplacementFailureKind FailureKind = OmpConfigurationReplacementFailureKind.None,
        string? ErrorMessage = null)
    {
        public static PlanBuildResult Failed(
            OmpConfigurationReplacementFailureKind failureKind,
            string errorMessage) =>
            new(null, failureKind, errorMessage);
    }

    private sealed record OmpConfigurationReplacementPlan(
        OmpConfigurationReplacementRequest Request,
        string ConfigurationPath,
        ConfigFile Configuration,
        OmpConfigurationRoutePlan RoutePlan,
        IReadOnlyList<OmpConfigurationReplacementChange> Changes,
        bool IsLocalTarget,
        ModelsFile? Models,
        ModelsPlan? ModelsPlan)
    {
        public bool HasRouteChanges => Changes.Count != 0;
        public OmpModelsProviderChangeKind ModelsChangeKind =>
            ModelsPlan?.ChangeKind ?? OmpModelsProviderChangeKind.None;
        public string? ModelsVersion => Models?.Version;
        public bool HasChanges =>
            HasRouteChanges || ModelsChangeKind != OmpModelsProviderChangeKind.None;
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
