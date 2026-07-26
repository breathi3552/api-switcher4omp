using ProviderPriceSwitcher.Infrastructure;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var yaml = "# keep this comment\r\nmodelRoles:\r\n  DEFAULT: old-provider/alpha/variant\r\n  fast: another-provider/beta@2024\r\n  indirect: '@smol'\r\ntask:\r\n  agentModelOverrides:\r\n    reviewer: old-provider/review\r\n    helper: '@role'\r\nunrelated: old-provider/untouched\r\n";
var switcher = new OmpConfigurationSwitcher();
var preview = switcher.Preview(yaml, "new-provider");
Assert(preview.IsValid, preview.Error ?? "preview invalid");
Assert(preview.Changes.Count == 3, "expected default, model and override");
Assert(preview.NewText.Contains("new-provider/alpha/variant", StringComparison.Ordinal), "suffix not preserved");
Assert(preview.NewText.Contains("new-provider/beta@2024", StringComparison.Ordinal), "second model not switched");
Assert(preview.NewText.Contains("new-provider/review", StringComparison.Ordinal), "override not switched");
Assert(preview.NewText.Contains("'@smol'", StringComparison.Ordinal) && preview.NewText.Contains("'@role'", StringComparison.Ordinal), "indirect role changed");
Assert(preview.NewText.Contains("unrelated: old-provider/untouched", StringComparison.Ordinal), "unrelated text changed");
Assert(preview.NewText.Contains("\r\n", StringComparison.Ordinal), "CRLF not preserved");
Assert(!switcher.Preview(yaml, "bad/provider").IsValid, "unsafe provider accepted");
Assert(!switcher.Preview(yaml.Replace("DEFAULT: old-provider/alpha/variant", "DEFAULT: '@smol'"), "new-provider").IsValid, "invalid default accepted");

var root = Path.Combine(Path.GetTempPath(), "ProviderPriceSwitcher-Omp-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var path = Path.Combine(root, "config.yml");
    await File.WriteAllTextAsync(path, yaml, new System.Text.UTF8Encoding(false));
    var result = await switcher.SwitchFileAsync(path, "new-provider");
    Assert(result.Succeeded, result.Error ?? "switch failed");
    Assert(result.BackupPath is not null && File.Exists(result.BackupPath), "backup missing");
    var written = await File.ReadAllTextAsync(path);
    Assert(written == preview.NewText, "atomic result differs from preview");
    Assert(written.Contains("unrelated: old-provider/untouched", StringComparison.Ordinal), "unrelated field changed");
}
finally
{
    Directory.Delete(root, recursive: true);
}

Console.WriteLine("OMP configuration runner passed.");
