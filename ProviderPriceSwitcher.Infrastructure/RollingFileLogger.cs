using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed record RollingFileLoggerOptions(string RootDirectory, long MaxFileBytes = 5 * 1024 * 1024, int RetentionDays = 7);

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private static readonly Regex Secret = new("(?i)(Authorization|Cookie|api_key|token|access_token|refresh_token)(?:\\s*[:=]\\s*|\\\"\\s*:\\s*\\\")(?:Bearer\\s+)?[^\\s,;&\\\"}]+|(?i)Bearer\\s+[^\\s,;\\\"]+|(?i)(?:[A-Z]:\\\\Users\\\\)[^\\r\\n\\\"]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedState = new(["Operation", "ProviderId", "SiteType", "FailureKind", "ElapsedMilliseconds"], StringComparer.Ordinal);
    private readonly RollingFileLoggerOptions _options;
    private readonly object _gate = new();
    private bool _disposed;

    public RollingFileLoggerProvider(RollingFileLoggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        if (options.MaxFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        try { Directory.CreateDirectory(options.RootDirectory); DeleteExpired(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);
    public void Dispose() => _disposed = true;

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception, IReadOnlyList<KeyValuePair<string, object?>> state)
    {
        if (_disposed) return;
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_options.RootDirectory);
                var path = Path.Combine(_options.RootDirectory, $"app-{DateTime.UtcNow:yyyyMMdd}.log");
                var payload = new Dictionary<string, object?>
                {
                    ["timestamp"] = DateTimeOffset.UtcNow,
                    ["level"] = level.ToString(),
                    ["category"] = category,
                    ["eventId"] = eventId.Id,
                    ["message"] = Redact(message),
                    ["exception"] = exception?.GetType().FullName
                };
                foreach (var item in state)
                    if (AllowedState.Contains(item.Key)) payload[item.Key] = Redact(item.Value?.ToString());
                var line = JsonSerializer.Serialize(payload) + Environment.NewLine;
                var bytes = System.Text.Encoding.UTF8.GetByteCount(line);
                if (File.Exists(path) && new FileInfo(path).Length + bytes > _options.MaxFileBytes) Rotate(path);
                File.AppendAllText(path, line, System.Text.Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? Redact(string? value) => value is null ? null : Secret.Replace(value, match => match.Value.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) ? "Bearer [REDACTED]" : (match.Groups[1].Success ? match.Groups[1].Value : "credential") + "=[REDACTED]");

    private static void Rotate(string path)
    {
        var ninth = path + ".9";
        if (File.Exists(ninth)) File.Delete(ninth);
        for (var index = 8; index >= 1; index--)
        {
            var source = path + "." + index;
            if (File.Exists(source)) File.Move(source, path + "." + (index + 1), true);
        }
        File.Move(path, path + ".1", true);
    }

    private void DeleteExpired()
    {
        var cutoff = DateTime.UtcNow.Date.AddDays(-_options.RetentionDays);
        foreach (var path in Directory.EnumerateFiles(_options.RootDirectory, "app-*.log*"))
            if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
    }

    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            provider.Write(category, logLevel, eventId, formatter(state, exception), exception, values);
        }
    }
}
