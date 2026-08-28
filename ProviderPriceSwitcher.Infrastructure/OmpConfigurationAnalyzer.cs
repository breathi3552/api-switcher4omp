using System.Collections.ObjectModel;
using System.Text;

namespace ProviderPriceSwitcher.Infrastructure;

/// <summary>One direct provider/model scalar found in an OMP configuration.</summary>
internal sealed record OmpModelReference(
    string ConfigurationPath,
    string Key,
    string Value,
    string Provider,
    string Model,
    bool IsDefault,
    bool IsAgentModelOverride,
    int ValueStart,
    int ValueEnd);

/// <summary>Line-preserving inspection result for an OMP config.yml.</summary>
internal sealed class OmpConfigurationAnalysis
{
    public OmpConfigurationAnalysis(
        string text,
        OmpModelReference? defaultReference,
        IReadOnlyList<OmpModelReference> modelReferences,
        bool hasValidYamlSyntax = true)
    {
        Text = text;
        DefaultReference = defaultReference;
        ModelReferences = modelReferences;
        HasValidYamlSyntax = hasValidYamlSyntax;
    }

    public string Text { get; }
    public OmpModelReference? DefaultReference { get; }
    public IReadOnlyList<OmpModelReference> ModelReferences { get; }
    public bool HasValidYamlSyntax { get; }
    public bool HasValidDefault => DefaultReference is not null;
    public bool HasValidReferences => ModelReferences.Count != 0;
}

/// <summary>
/// Inspects the small, stable part of OMP config.yml that controls model selection.
/// It deliberately edits scalar spans in the source rather than round-tripping YAML.
/// </summary>
internal sealed class OmpConfigurationAnalyzer
{
    private readonly object _instanceState = new();
    public OmpConfigurationAnalysis Analyze(string yamlText)
    {
        _ = _instanceState;
        ArgumentNullException.ThrowIfNull(yamlText);

        var refs = new List<OmpModelReference>();
        OmpModelReference? defaultReference = null;
        var stack = new List<YamlKey>();
        var offset = 0;
        var hasValidYamlSyntax = HasValidYamlSyntax(yamlText);
        foreach (var line in EnumerateLines(yamlText))
        {
            var parsed = ParseKey(line.Content, offset);
            if (parsed is null)
            {
                offset += line.Length;
                continue;
            }

            while (stack.Count != 0 && stack[^1].Indent >= parsed.Value.Indent)
                stack.RemoveAt(stack.Count - 1);

            var parent = stack.Count == 0 ? null : stack[^1].Key;
            var key = UnquoteKey(parsed.Value.Key);
            var path = BuildPath(stack, key);
            var scalar = ParseScalar(parsed.Value.Rest, parsed.Value.RestStart);
            var isModelRolesEntry = string.Equals(parent, "modelRoles", StringComparison.OrdinalIgnoreCase);
            var isOverrideEntry = string.Equals(parent, "agentModelOverrides", StringComparison.OrdinalIgnoreCase)
                ? stack.Any(x => string.Equals(x.Key, "task", StringComparison.OrdinalIgnoreCase))
                : string.Equals(parent, "task.agentModelOverrides", StringComparison.OrdinalIgnoreCase);

            if (scalar is not null && (isModelRolesEntry || isOverrideEntry))
            {
                var value = scalar.Value.Value;
                if (TrySplitReference(value, out var provider, out var model))
                {
                    var reference = new OmpModelReference(
                        path,
                        key,
                        value,
                        provider,
                        model,
                        isModelRolesEntry && string.Equals(key, "default", StringComparison.OrdinalIgnoreCase),
                        isOverrideEntry,
                        scalar.Value.ValueStart,
                        scalar.Value.ValueEnd);
                    refs.Add(reference);
                    if (reference.IsDefault)
                        defaultReference = reference;
                }
                else if (isModelRolesEntry && string.Equals(key, "default", StringComparison.OrdinalIgnoreCase))
                {
                    // An explicitly present but indirect/invalid default must not be
                    // mistaken for a usable provider.
                    defaultReference = null;
                }
            }

            // A key with no scalar starts a mapping scope. Keep all mapping keys so
            // that task.agentModelOverrides is recognized without depending on width.
            if (scalar is null && parsed.Value.Rest.Trim().TrimStart().StartsWith('#') is false)
                stack.Add(new YamlKey(parsed.Value.Indent, key));

            offset += line.Length;
        }

        return new OmpConfigurationAnalysis(
            yamlText,
            defaultReference,
            new ReadOnlyCollection<OmpModelReference>(refs),
            hasValidYamlSyntax);
    }

    private static bool TrySplitReference(string value, out string provider, out string model)
    {
        provider = string.Empty;
        model = string.Empty;
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1 || value[0] == '@' || value.Any(char.IsWhiteSpace))
            return false;
        provider = value[..slash];
        model = value[(slash + 1)..];
        return true;
    }
    private static bool HasValidYamlSyntax(string text)
    {
        var syntaxLines = new List<string>();
        var blockIndent = -1;
        var previousIndent = -1;
        var previousAllowsSequence = false;
        var sequenceIndent = -1;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var indent = line.TakeWhile(ch => ch == ' ').Count();
            if (line.TakeWhile(ch => ch is ' ' or '\t').Contains('\t'))
                return false;
            var trimmed = line.Trim();
            if (blockIndent >= 0 && (trimmed.Length == 0 || indent > blockIndent))
            {
                syntaxLines.Add(string.Empty);
                continue;
            }

            blockIndent = -1;
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                syntaxLines.Add(string.Empty);
                continue;
            }

            if (trimmed.StartsWith('-'))
            {
                if (sequenceIndent < 0)
                {
                    if (!previousAllowsSequence || indent <= previousIndent)
                        return false;
                    sequenceIndent = indent;
                }
                else if (indent != sequenceIndent)
                {
                    sequenceIndent = -1;
                }
            }
            else if (sequenceIndent >= 0 && indent <= sequenceIndent)
            {
                sequenceIndent = -1;
            }

            var colon = FindUnquotedColon(trimmed);
            if (colon < 0
                && !trimmed.StartsWith('-')
                && !trimmed.StartsWith('[')
                && !trimmed.StartsWith('{')
                && !trimmed.StartsWith('?')
                && trimmed is not "..." and not "---")
                return false;

            syntaxLines.Add(RemoveYamlComment(line));
            previousIndent = indent;
            previousAllowsSequence = colon >= 0 && string.IsNullOrWhiteSpace(trimmed[(colon + 1)..]);
            if (colon >= 0 && trimmed[(colon + 1)..].TrimStart() is ['|' or '>', ..])
                blockIndent = indent;
        }

        var flow = new Stack<char>();
        var quote = '\0';
        var escaped = false;
        foreach (var ch in string.Join('\n', syntaxLines))
        {
            if (quote == '\'')
            {
                if (ch == '\'')
                    quote = '\0';
                continue;
            }
            if (quote == '"')
            {
                if (escaped)
                {
                    if (!"0abtnvfre \"/\\N_LP_xXuU".Contains(ch, StringComparison.Ordinal))
                        return false;
                    escaped = false;
                    continue;
                }
                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (ch == '"')
                    quote = '\0';
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }
            if (ch is '[' or '{')
            {
                flow.Push(ch);
                continue;
            }
            if (ch is ']' or '}')
            {
                if (flow.Count == 0 || (ch == ']' && flow.Pop() != '[') || (ch == '}' && flow.Pop() != '{'))
                    return false;
            }
        }

        return quote == '\0' && flow.Count == 0 && !escaped;
    }

    private static int FindUnquotedColon(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                else if (quote == '"' && ch == '\\')
                    index++;
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }
            if (ch == ':')
                return index;
        }
        return -1;
    }
    private static string RemoveYamlComment(string line)
    {
        var quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                else if (quote == '"' && ch == '\\')
                    index++;
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }
            if (ch == '#' && (index == 0 || char.IsWhiteSpace(line[index - 1])))
                return line[..index];
        }
        return line;
    }

    private static string BuildPath(IReadOnlyList<YamlKey> stack, string key)
    {
        if (stack.Count == 0)
            return key;
        var builder = new StringBuilder();
        foreach (var item in stack)
        {
            if (builder.Length != 0)
                builder.Append('.');
            builder.Append(item.Key);
        }
        if (builder.Length != 0)
            builder.Append('.');
        builder.Append(key);
        return builder.ToString();
    }

    private static string UnquoteKey(string key)
    {
        key = key.Trim();
        return key.Length >= 2 && ((key[0] == '\'' && key[^1] == '\'') || (key[0] == '"' && key[^1] == '"'))
            ? key[1..^1]
            : key;
    }

    private static ParsedScalar? ParseScalar(string rest, int absoluteRestStart)
    {
        var start = 0;
        while (start < rest.Length && char.IsWhiteSpace(rest[start]))
            start++;
        if (start == rest.Length || rest[start] == '#')
            return null;

        var end = rest.Length;
        var quote = '\0';
        for (var i = start; i < rest.Length; i++)
        {
            var ch = rest[i];
            if ((ch == '\'' || ch == '"') && (i == start || rest[i - 1] != '\\'))
            {
                if (quote == '\0') quote = ch;
                else if (quote == ch) quote = '\0';
            }
            else if (ch == '#' && quote == '\0' && (i == start || char.IsWhiteSpace(rest[i - 1])))
            {
                end = i;
                break;
            }
        }
        while (end > start && char.IsWhiteSpace(rest[end - 1]))
            end--;
        if (end <= start)
            return null;

        var raw = rest[start..end];
        var valueStart = absoluteRestStart + start;
        var valueEnd = absoluteRestStart + end;
        if (raw.Length >= 2 && ((raw[0] == '\'' && raw[^1] == '\'') || (raw[0] == '"' && raw[^1] == '"')))
        {
            valueStart++;
            valueEnd--;
            raw = raw[1..^1];
        }
        return new ParsedScalar(raw, valueStart, valueEnd);
    }

    private static ParsedKey? ParseKey(string content, int absoluteLineStart)
    {
        var indent = 0;
        while (indent < content.Length && (content[indent] == ' ' || content[indent] == '\t'))
            indent++;
        var body = content[indent..];
        if (body.Length == 0 || body.StartsWith('#') || body.StartsWith('-') || body == "---")
            return null;

        var quote = '\0';
        var colon = -1;
        for (var i = 0; i < body.Length; i++)
        {
            var ch = body[i];
            if ((ch == '\'' || ch == '"') && (i == 0 || body[i - 1] != '\\'))
            {
                if (quote == '\0') quote = ch;
                else if (quote == ch) quote = '\0';
            }
            else if (ch == ':' && quote == '\0' && (i + 1 == body.Length || char.IsWhiteSpace(body[i + 1])))
            {
                colon = i;
                break;
            }
        }
        if (colon <= 0)
            return null;
        var key = body[..colon].Trim();
        var rest = body[(colon + 1)..];
        return new ParsedKey(indent, key, rest, absoluteLineStart + indent + colon + 1);
    }

    private static IEnumerable<SourceLine> EnumerateLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;
            yield return new SourceLine(text[start..(i > start && text[i - 1] == '\r' ? i - 1 : i)], i + 1 - start);
            start = i + 1;
        }
        if (start < text.Length)
            yield return new SourceLine(text[start..], text.Length - start);
    }

    private readonly record struct YamlKey(int Indent, string Key);
    private readonly record struct ParsedKey(int Indent, string Key, string Rest, int RestStart);
    private readonly record struct ParsedScalar(string Value, int ValueStart, int ValueEnd);
    private readonly record struct SourceLine(string Content, int Length);
}
