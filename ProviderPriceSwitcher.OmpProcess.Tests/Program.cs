using System.Diagnostics;
using ProviderPriceSwitcher.Infrastructure;
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
var runId = Guid.NewGuid().ToString("N");
var root = Path.Combine(Path.GetTempPath(), "ProviderPriceSwitcher-OmpProcess-" + runId);
var dataRoot = Path.Combine(root, "data"); var ompRoot = Path.Combine(root, "omp"); var workingRoot = Path.Combine(root, "working");
Directory.CreateDirectory(dataRoot); Directory.CreateDirectory(ompRoot); Directory.CreateDirectory(workingRoot);
var sentinels = new[] { dataRoot, ompRoot, workingRoot }.Select(path => Path.Combine(path, ".sentinel")).ToArray();
foreach (var sentinel in sentinels) File.WriteAllText(sentinel, runId);
var gateway = new FakeGateway();
try
{
    var service = new OmpProcessService(gateway);
    var missingDirectory = service.Start(new OmpProcessStartRequest(Path.Combine(workingRoot, "missing")));
    Assert(!missingDirectory.Succeeded && missingDirectory.FailureKind == OmpProcessFailureKind.WorkingDirectoryUnavailable, "Missing directory must return a stable failure kind.");
    var missingExecutable = service.Start(new OmpProcessStartRequest(workingRoot, Path.Combine(workingRoot, "missing.exe"), UseWindowsTerminal: false));
    Assert(!missingExecutable.Succeeded && missingExecutable.FailureKind == OmpProcessFailureKind.ExecutableUnavailable, "Missing absolute executable must return a stable failure kind.");
    var bareCommand = service.Start(new OmpProcessStartRequest(workingRoot, "dotnet", ["--version"], UseWindowsTerminal: false));
    Assert(bareCommand.Succeeded && bareCommand.ProcessId is > 0 && bareCommand.StartTime is not null, "Fake process launch must return PID and start time.");
    Assert(gateway.LastStartInfo!.WorkingDirectory == Path.GetFullPath(workingRoot) && !gateway.LastStartInfo.UseShellExecute, "Working directory and shell policy must be explicit.");
    var terminalLaunch = service.Start(new OmpProcessStartRequest(workingRoot, "omp", ["--help"]));
    Assert(terminalLaunch.Succeeded && gateway.LastStartInfo!.FileName == "wt.exe", "Windows Terminal launch must use gateway.");
    var rootedExecutable = Path.Combine(workingRoot, "omp tool's.exe"); File.WriteAllText(rootedExecutable, string.Empty);
    var literalLaunch = service.Start(new OmpProcessStartRequest(workingRoot, rootedExecutable, ["value with spaces", "quote'and", "; $() & |", "--flag"]));
    Assert(literalLaunch.Succeeded, "Rooted executable launch must succeed through fake gateway.");
    const string syntheticSecret = "synthetic-token-DO-NOT-LOG https://example.invalid/prices?api_key=synthetic-query-secret&token=synthetic-token&cookie=synthetic-cookie C:\\Users\\Private\\Documents\\secret";
    gateway.StartException = new InvalidOperationException(syntheticSecret);
    var failedLaunch = service.Start(new OmpProcessStartRequest(workingRoot, "dotnet", UseWindowsTerminal: false));
    Assert(!failedLaunch.Succeeded && failedLaunch.FailureKind == OmpProcessFailureKind.StartFailed && !failedLaunch.ToString().Contains(syntheticSecret, StringComparison.Ordinal), "Launch exceptions must be safe.");
    foreach (var sentinel in sentinels) Assert(File.ReadAllText(sentinel) == runId, "isolation sentinel changed");
    Console.WriteLine("OmpProcess contract tests passed.");
}
finally
{
    gateway.Dispose();
    Directory.Delete(root, recursive: true);
    Assert(!Directory.Exists(root), "isolated process root was not removed");
}
sealed class FakeGateway : IOmpProcessGateway, IDisposable
{
    private readonly List<Process> started = [];
    public IReadOnlyList<Process> Processes { get; set; } = Array.Empty<Process>();
    public ProcessStartInfo? LastStartInfo { get; private set; }
    public Exception? StartException { get; set; }
    public IReadOnlyList<Process> GetProcessesByName(string processName) => Processes;
    public Process? Start(ProcessStartInfo startInfo)
    {
        LastStartInfo = startInfo;
        if (StartException is not null) throw StartException;
        var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 1") { UseShellExecute = false });
        if (process is null) throw new InvalidOperationException("fake process did not start");
        started.Add(process); return process;
    }
    public void Dispose()
    {
        foreach (var process in started) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(2000); } finally { process.Dispose(); } }
        started.Clear();
    }
}
