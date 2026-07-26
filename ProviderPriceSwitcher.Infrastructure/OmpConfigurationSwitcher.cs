using System.Collections.ObjectModel;
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
    public bool CanApply => IsValid;
    public string? CurrentProvider => Analysis.CurrentProvider;
    public int AffectedCount => Changes.Count;
    public string NewText => IsValid ? OmpConfigurationSwitcher.ApplyChanges(SourceText, Analysis.ModelReferences, TargetProvider) : SourceText;
}

public sealed class OmpConfigurationSwitchResult
{
    internal OmpConfigurationSwitchResult(
        bool succeeded,
        string path,
        string? backupPath,
        OmpConfigurationPreview preview,
        Exception? exception)
    {
        Succeeded = succeeded;
        Path = path;
        BackupPath = backupPath;
        Preview = preview;
        Exception = exception;
    }

    public bool Succeeded { get; }
    public bool Success => Succeeded;
    public string Path { get; }
    public string? BackupPath { get; }
    public string? BackupFilePath => BackupPath;
    public OmpConfigurationPreview Preview { get; }
    public IReadOnlyList<OmpConfigurationChange> Changes => Preview.Changes;
    public Exception? Exception { get; }
    public string? Error => Exception?.Message ?? Preview.Error;
}

/// <summary>Creates previews and safely applies OMP provider changes.</summary>
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
        else if (!analysis.HasValidDefault)
            error = "modelRoles.default must contain a direct provider/model reference.";
        else if (!analysis.HasValidReferences)
            error = "No direct model references were found.";

        var changes = error is null
            ? analysis.ModelReferences
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

    public async Task<OmpConfigurationSwitchResult> SwitchFileAsync(string path, string targetProvider, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        var preview = Preview(text, targetProvider);
        if (!preview.IsValid)
            return new OmpConfigurationSwitchResult(false, path, null, preview, null);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var fileName = Path.GetFileName(path);
        var backupPath = path + ".bak-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + ".yml";
        var tempPath = Path.Combine(directory, "." + fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // The backup is a complete copy made before touching the source.
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
            File.Move(tempPath, path, overwrite: true);
            return new OmpConfigurationSwitchResult(true, path, backupPath, preview, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            TryDelete(tempPath);
            return new OmpConfigurationSwitchResult(false, path, backupPath, preview, exception);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public Task<OmpConfigurationSwitchResult> SwitchAsync(string path, string targetProvider, CancellationToken cancellationToken = default) => SwitchFileAsync(path, targetProvider, cancellationToken);

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

    private static UTF8Encoding DetectEncoding(byte[] bytes, out int preambleLength)
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
