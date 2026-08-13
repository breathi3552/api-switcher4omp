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

public sealed class OmpConfigurationPreview
{
    internal OmpConfigurationPreview(
        string sourceText,
        string targetProvider,
        OmpConfigurationAnalysis analysis,
        IReadOnlyList<OmpConfigurationChange> changes,
        string? error)
    {
        SourceText = sourceText;
        TargetProvider = targetProvider;
        Analysis = analysis;
        Changes = changes;
        Error = error;
    }

    public string SourceText { get; }
    public string TargetProvider { get; }
    public OmpConfigurationAnalysis Analysis { get; }
    public IReadOnlyList<OmpConfigurationChange> Changes { get; }
    public IReadOnlyList<OmpConfigurationChange> Items => Changes;
    public string? Error { get; }
    public bool IsValid => Error is null;
    public bool CanApply => IsValid && Changes.Count != 0;
    public string? CurrentProvider => Analysis.CurrentProvider;
    public int AffectedCount => Changes.Count;
    public string NewText => IsValid
        ? OmpConfigurationSwitcher.ApplyChanges(
            SourceText,
            Analysis.ModelReferences.Where(reference => IsEligible(reference, TargetProvider)).ToArray(),
            TargetProvider)
        : SourceText;

    internal static bool IsEligible(OmpModelReference reference, string targetProvider) =>
        reference.Model.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(reference.Provider, targetProvider, StringComparison.Ordinal);
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
    private static readonly Regex SafeProviderId = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly OmpConfigurationAnalyzer _analyzer;

    public OmpConfigurationSwitcher(OmpConfigurationAnalyzer? analyzer = null)
    {
        _analyzer = analyzer ?? new OmpConfigurationAnalyzer();
    }

    public OmpConfigurationPreview Preview(string text, string targetProvider)
    {
        ArgumentNullException.ThrowIfNull(text);
        targetProvider ??= string.Empty;
        var analysis = _analyzer.Analyze(text);
        string? error = null;
        if (!SafeProviderId.IsMatch(targetProvider))
            error = "Target provider must be a non-empty safe provider ID.";
        else if (!analysis.HasValidYamlSyntax)
            error = "OMP config.yml contains invalid YAML syntax.";
        else if (!analysis.HasValidDefault)
            error = "modelRoles.default must contain a direct provider/model reference.";
        else if (!analysis.HasValidReferences)
            error = "No direct model references were found.";

        var changes = error is null
            ? analysis.ModelReferences
                .Where(reference => OmpConfigurationPreview.IsEligible(reference, targetProvider))
                .Select(reference => new OmpConfigurationChange(
                    reference.ConfigurationPath,
                    reference.Key,
                    reference.Value,
                    targetProvider + "/" + reference.Model,
                    reference.IsDefault,
                    reference.IsAgentModelOverride))
                .ToArray()
            : Array.Empty<OmpConfigurationChange>();
        return new OmpConfigurationPreview(text, targetProvider, analysis, new ReadOnlyCollection<OmpConfigurationChange>(changes), error);
    }

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
        var preview = Preview(text, targetProvider);
        if (expectedVersion is not null && (!exists || !string.Equals(Hash(bytes), expectedVersion, StringComparison.Ordinal)))
            return new OmpConfigurationSwitchResult(false, path, null, preview, new OmpConfigurationStaleException());
        if (!exists)
        {
            preview = new OmpConfigurationPreview(
                text,
                targetProvider,
                preview.Analysis,
                preview.Changes,
                "OMP 主 config.yml 不存在。");
        }
        if (!preview.IsValid)
            return new OmpConfigurationSwitchResult(false, path, null, preview, null);
        if (preview.Changes.Count == 0)
            return new OmpConfigurationSwitchResult(true, path, null, preview, null);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var fileName = Path.GetFileName(path);
        var backupPath = exists ? CreateBackupPath(path) : null;
        var tempPath = Path.Combine(directory, "." + fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        Exception? backupRetentionException = null;
        OmpConfigurationSwitchResult result;
        try
        {
            if (backupPath is not null)
                File.Copy(path, backupPath, overwrite: false);
            cancellationToken.ThrowIfCancellationRequested();
            var output = encoding.GetPreamble().Concat(encoding.GetBytes(preview.NewText)).ToArray();
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
            {
                await stream.WriteAsync(output, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedVersion is not null)
            {
                var currentBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Hash(currentBytes), expectedVersion, StringComparison.Ordinal))
                    throw new OmpConfigurationStaleException();
            }
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

    internal static string ApplyChanges(string source, IReadOnlyList<OmpModelReference> references, string targetProvider)
    {
        var replacements = references
            .Select(reference => (reference.ValueStart, reference.ValueEnd, Value: targetProvider + "/" + reference.Model))
            .OrderByDescending(item => item.ValueStart)
            .ToArray();
        var builder = new StringBuilder(source);
        foreach (var replacement in replacements)
            builder.Remove(replacement.ValueStart, replacement.ValueEnd - replacement.ValueStart)
                .Insert(replacement.ValueStart, replacement.Value);
        return builder.ToString();
    }
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
