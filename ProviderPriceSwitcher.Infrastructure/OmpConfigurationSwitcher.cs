using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace ProviderPriceSwitcher.Infrastructure;

public sealed record OmpConfigurationChange(
    string ConfigurationPath,
    string Key,
    string OldValue,
    string NewValue,
    bool IsDefault,
    bool IsAgentModelOverride)
{
    public string Path => ConfigurationPath;
    public string Old => OldValue;
    public string New => NewValue;
}

internal sealed record OmpConfigurationRouteEdit(
    string ConfigurationPath,
    string Key,
    string OriginalProvider,
    string ModelId,
    string OriginalReference,
    string NewReference,
    bool IsDefault,
    bool IsAgentModelOverride,
    int ValueStart,
    int ValueEnd);

internal sealed class OmpConfigurationRoutePlan
{
    private static readonly Regex SafeProviderId = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private OmpConfigurationRoutePlan(
        string sourceText,
        string targetProvider,
        OmpConfigurationAnalysis analysis,
        IReadOnlyList<OmpConfigurationRouteEdit> routeEdits,
        string updatedText,
        string? error)
    {
        SourceText = sourceText;
        TargetProvider = targetProvider;
        Analysis = analysis;
        RouteEdits = routeEdits;
        Changes = new ReadOnlyCollection<OmpConfigurationChange>(
            routeEdits.Select(edit => new OmpConfigurationChange(
                edit.ConfigurationPath,
                edit.Key,
                edit.OriginalReference,
                edit.NewReference,
                edit.IsDefault,
                edit.IsAgentModelOverride)).ToArray());
        UpdatedText = updatedText;
        Error = error;
    }

    public string SourceText { get; }
    public string TargetProvider { get; }
    public OmpConfigurationAnalysis Analysis { get; }
    public IReadOnlyList<OmpConfigurationRouteEdit> RouteEdits { get; }
    public IReadOnlyList<OmpConfigurationChange> Changes { get; }
    public string UpdatedText { get; }
    public string? Error { get; }
    public bool IsValid => Error is null;
    public bool CanApply => IsValid && RouteEdits.Count != 0;

    public static OmpConfigurationRoutePlan Create(
        OmpConfigurationAnalyzer analyzer,
        string sourceText,
        string targetProvider)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(sourceText);
        targetProvider ??= string.Empty;

        var analysis = analyzer.Analyze(sourceText);
        string? error = null;
        if (!SafeProviderId.IsMatch(targetProvider))
            error = "Target provider must be a non-empty safe provider ID.";
        else if (!analysis.HasValidYamlSyntax)
            error = "OMP config.yml contains invalid YAML syntax.";
        else if (!analysis.HasValidDefault)
            error = "modelRoles.default must contain a direct provider/model reference.";
        else if (!analysis.HasValidReferences)
            error = "No direct model references were found.";

        var routeEdits = error is null
            ? analysis.ModelReferences
                .Where(reference =>
                    reference.Model.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(reference.Provider, targetProvider, StringComparison.Ordinal))
                .Select(reference => new OmpConfigurationRouteEdit(
                    reference.ConfigurationPath,
                    reference.Key,
                    reference.Provider,
                    reference.Model,
                    reference.Value,
                    targetProvider + "/" + reference.Model,
                    reference.IsDefault,
                    reference.IsAgentModelOverride,
                    reference.ValueStart,
                    reference.ValueEnd))
                .ToArray()
            : [];
        var updatedText = error is null
            ? ApplyChanges(sourceText, routeEdits)
            : sourceText;
        return new OmpConfigurationRoutePlan(
            sourceText,
            targetProvider,
            analysis,
            new ReadOnlyCollection<OmpConfigurationRouteEdit>(routeEdits),
            updatedText,
            error);
    }

    public OmpConfigurationRoutePlan WithError(string error) =>
        new(SourceText, TargetProvider, Analysis, RouteEdits, SourceText, error);

    public OmpConfigurationPreview ToPreview() => new(this);

    private static string ApplyChanges(
        string source,
        IReadOnlyList<OmpConfigurationRouteEdit> routeEdits)
    {
        var replacements = routeEdits
            .OrderByDescending(edit => edit.ValueStart)
            .ToArray();
        var builder = new StringBuilder(source);
        foreach (var replacement in replacements)
            builder.Remove(replacement.ValueStart, replacement.ValueEnd - replacement.ValueStart)
                .Insert(replacement.ValueStart, replacement.NewReference);
        return builder.ToString();
    }
}

public sealed class OmpConfigurationPreview
{
    private readonly OmpConfigurationRoutePlan _plan;

    internal OmpConfigurationPreview(OmpConfigurationRoutePlan plan)
    {
        _plan = plan;
    }

    public string SourceText => _plan.SourceText;
    public string TargetProvider => _plan.TargetProvider;
    public OmpConfigurationAnalysis Analysis => _plan.Analysis;
    public IReadOnlyList<OmpConfigurationChange> Changes => _plan.Changes;
    public IReadOnlyList<OmpConfigurationChange> Items => Changes;
    public string? Error => _plan.Error;
    public bool IsValid => _plan.IsValid;
    public bool CanApply => _plan.CanApply;
    public string? CurrentProvider => Analysis.CurrentProvider;
    public int AffectedCount => Changes.Count;
    public string NewText => _plan.UpdatedText;
}

public sealed class OmpConfigurationSwitchResult
{
    internal OmpConfigurationSwitchResult(
        bool succeeded,
        string path,
        string? backupPath,
        OmpConfigurationPreview preview,
        Exception? exception,
        Exception? backupRetentionException = null)
    {
        Succeeded = succeeded;
        Path = path;
        BackupPath = backupPath;
        Preview = preview;
        Exception = exception;
        BackupRetentionException = backupRetentionException;
    }

    public bool Succeeded { get; }
    public bool Success => Succeeded;
    public string Path { get; }
    public string? BackupPath { get; }
    public string? BackupFilePath => BackupPath;
    public OmpConfigurationPreview Preview { get; }
    public IReadOnlyList<OmpConfigurationChange> Changes => Preview.Changes;
    public Exception? Exception { get; }
    public Exception? BackupRetentionException { get; }
    public bool BackupRetentionSucceeded => BackupRetentionException is null;
    public string? Error => Exception?.Message ?? Preview.Error;
}

/// <summary>Creates previews and safely applies GPT-only OMP provider changes.</summary>
internal sealed class OmpConfigurationStaleException : IOException
{
}
public sealed class OmpConfigurationSwitcher
{
    private readonly OmpConfigurationAnalyzer _analyzer;

    public OmpConfigurationSwitcher(OmpConfigurationAnalyzer? analyzer = null)
    {
        _analyzer = analyzer ?? new OmpConfigurationAnalyzer();
    }

    internal OmpConfigurationRoutePlan CreatePlan(string text, string targetProvider) =>
        OmpConfigurationRoutePlan.Create(_analyzer, text, targetProvider);

    public OmpConfigurationPreview Preview(string text, string targetProvider) =>
        CreatePlan(text, targetProvider).ToPreview();

    public OmpConfigurationPreview PreviewText(string text, string targetProvider) => Preview(text, targetProvider);

    public Task<OmpConfigurationSwitchResult> SwitchFileAsync(string path, string targetProvider, CancellationToken cancellationToken = default) =>
        SwitchFileAsync(path, targetProvider, expectedVersion: null, cancellationToken);

    public async Task<OmpConfigurationSwitchResult> SwitchFileAsync(
        string path,
        string targetProvider,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var exists = File.Exists(path);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        byte[] bytes = exists ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : [];
        var preambleLength = 0;
        if (exists)
            encoding = DetectEncoding(bytes, out preambleLength);
        var text = exists
            ? encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength)
            : string.Empty;
        var plan = CreatePlan(text, targetProvider);
        if (expectedVersion is not null && (!exists || !string.Equals(Hash(bytes), expectedVersion, StringComparison.Ordinal)))
            return new OmpConfigurationSwitchResult(false, path, null, plan.ToPreview(), new OmpConfigurationStaleException());
        if (!exists)
        {
            plan = plan.WithError("OMP 主 config.yml 不存在。");
            return new OmpConfigurationSwitchResult(false, path, null, plan.ToPreview(), null);
        }

        return await WritePlanAsync(
            path,
            plan,
            expectedVersion ?? Hash(bytes),
            encoding,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<OmpConfigurationSwitchResult> ApplyPlanAsync(
        string path,
        OmpConfigurationRoutePlan plan,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(path))
            return new OmpConfigurationSwitchResult(false, path, null, plan.ToPreview(), new OmpConfigurationStaleException());
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Hash(bytes), expectedVersion, StringComparison.Ordinal))
            return new OmpConfigurationSwitchResult(false, path, null, plan.ToPreview(), new OmpConfigurationStaleException());
        var encoding = DetectEncoding(bytes, out _);
        return await WritePlanAsync(path, plan, expectedVersion, encoding, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OmpConfigurationSwitchResult> WritePlanAsync(
        string path,
        OmpConfigurationRoutePlan plan,
        string expectedVersion,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        var preview = plan.ToPreview();
        if (!plan.IsValid)
            return new OmpConfigurationSwitchResult(false, path, null, preview, null);
        if (!plan.CanApply)
            return new OmpConfigurationSwitchResult(true, path, null, preview, null);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var fileName = Path.GetFileName(path);
        string? backupPath = null;
        string? backupCandidatePath = null;
        var tempPath = Path.Combine(directory, "." + fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        Exception? backupRetentionException = null;
        OmpConfigurationSwitchResult result;
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
            var currentBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Hash(currentBytes), expectedVersion, StringComparison.Ordinal))
                throw new OmpConfigurationStaleException();
            cancellationToken.ThrowIfCancellationRequested();
            backupCandidatePath = CreateBackupPath(path);
            File.Copy(path, backupCandidatePath, overwrite: false);
            backupPath = backupCandidatePath;
            File.Move(tempPath, path, overwrite: true);
            result = new OmpConfigurationSwitchResult(true, path, backupPath, preview, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            if (backupPath is null && backupCandidatePath is not null)
                TryDelete(backupCandidatePath);
            result = new OmpConfigurationSwitchResult(false, path, backupPath, preview, exception);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            try
            {
                PruneBackups(directory, fileName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                backupRetentionException = exception;
            }
        }
        return backupRetentionException is null
            ? result
            : new OmpConfigurationSwitchResult(
                result.Succeeded,
                result.Path,
                result.BackupPath,
                result.Preview,
                result.Exception,
                backupRetentionException);
    }

    public Task<OmpConfigurationSwitchResult> SwitchAsync(string path, string targetProvider, CancellationToken cancellationToken = default) =>
        SwitchFileAsync(path, targetProvider, cancellationToken);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static string CreateBackupPath(string path)
    {
        var stamp = DateTime.UtcNow;
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidate = path + ".bak-" + stamp.AddMilliseconds(attempt).ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + ".yml";
            if (!File.Exists(candidate))
                return candidate;
        }
        throw new IOException("Unable to allocate a unique OMP configuration backup path.");
    }

    internal static void PruneBackups(string directory, string fileName)
    {
        var prefix = fileName + ".bak-";
        var backups = Directory.EnumerateFiles(directory, prefix + "*.yml")
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)
                && Regex.IsMatch(Path.GetFileName(path), "^" + Regex.Escape(fileName) + @"\.bak-\d{17}\.yml$", RegexOptions.CultureInvariant))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        foreach (var old in backups.Skip(5))
            File.Delete(old);
    }

    internal static UTF8Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            preambleLength = 3;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }
        preambleLength = 0;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the original exception and source file on cleanup failure.
        }
    }
}
