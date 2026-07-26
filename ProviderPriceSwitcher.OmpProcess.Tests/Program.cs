using System.Diagnostics;
using ProviderPriceSwitcher.Infrastructure;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var tempDirectory = Path.Combine(Path.GetTempPath(), "omp-process-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDirectory);
try
{
    var gateway = new FakeGateway { Processes = new[] { Process.GetCurrentProcess() } };
    var service = new OmpProcessService(gateway);

    var missingDirectory = service.Start(new OmpProcessStartRequest(Path.Combine(tempDirectory, "missing")));
    Assert(!missingDirectory.Succeeded && missingDirectory.FailureKind == OmpProcessFailureKind.WorkingDirectoryUnavailable, "Missing directory must return a stable failure kind.");

    var missingExecutable = service.Start(new OmpProcessStartRequest(tempDirectory, Path.Combine(tempDirectory, "missing.exe"), UseWindowsTerminal: false));
    Assert(!missingExecutable.Succeeded && missingExecutable.FailureKind == OmpProcessFailureKind.ExecutableUnavailable, "Missing absolute executable must return a stable failure kind.");

    var bareCommand = service.Start(new OmpProcessStartRequest(tempDirectory, "dotnet", ["--version"], UseWindowsTerminal: false));
    Assert(bareCommand.Succeeded, "Bare commands must be allowed through PATH.");
    Assert(bareCommand.ExistingProcess.Exists && bareCommand.ExistingProcess.Count == 1, "Existing process hint must be returned without blocking.");
    Assert(gateway.LastStartInfo!.WorkingDirectory == Path.GetFullPath(tempDirectory), "WorkingDirectory must be passed to ProcessStartInfo.");
    Assert(gateway.LastStartInfo.UseShellExecute == false, "Shell execution must be disabled.");
    Assert(bareCommand.ProcessId is > 0 && bareCommand.StartTime is not null, "Successful launch must return PID and start time.");

    var terminalLaunch = service.Start(new OmpProcessStartRequest(tempDirectory, "omp", ["--help"]));
    Assert(terminalLaunch.Succeeded, "Windows Terminal launch must succeed through the gateway.");
    Assert(gateway.LastStartInfo!.FileName == "wt.exe", "Windows Terminal must be the launched executable.");
    Assert(gateway.LastStartInfo.ArgumentList.SequenceEqual(["-d", Path.GetFullPath(tempDirectory), "powershell.exe", "-NoExit", "-Command", "& 'omp' '--help'"]), "Windows Terminal must use one literal PowerShell invocation for the default omp shape.");
    Assert(gateway.LastStartInfo.WorkingDirectory == Path.GetFullPath(tempDirectory), "Windows Terminal WorkingDirectory must be passed to ProcessStartInfo.");
    Assert(gateway.LastStartInfo.UseShellExecute == false, "Windows Terminal shell execution must be disabled.");

    var rootedExecutable = Path.Combine(tempDirectory, "omp tool's.exe");
    File.WriteAllText(rootedExecutable, string.Empty);
    var literalArguments = new[] { "value with spaces", "quote'and", "; $() & |", "--flag" };
    var literalLaunch = service.Start(new OmpProcessStartRequest(tempDirectory, rootedExecutable, literalArguments));
    Assert(literalLaunch.Succeeded, "Rooted executable launch must succeed through the gateway.");
    Assert(gateway.LastStartInfo!.ArgumentList.SequenceEqual(["-d", Path.GetFullPath(tempDirectory), "powershell.exe", "-NoExit", "-Command", $"& '{rootedExecutable.Replace("'", "''", StringComparison.Ordinal)}' 'value with spaces' 'quote''and' '; $() & |' '--flag'"]), "Executable and arguments must be PowerShell single-quoted literals.");

    const string syntheticSecret = "synthetic-token-DO-NOT-LOG https://example.invalid/prices?api_key=synthetic-query-secret&token=synthetic-token&cookie=synthetic-cookie C:\\Users\\Private\\Documents\\secret";
    gateway.StartException = new InvalidOperationException(syntheticSecret);
    var failedLaunch = service.Start(new OmpProcessStartRequest(tempDirectory, "dotnet", UseWindowsTerminal: false));
    Assert(!failedLaunch.Succeeded && failedLaunch.FailureKind == OmpProcessFailureKind.StartFailed && !failedLaunch.ToString().Contains(syntheticSecret, StringComparison.Ordinal), "Launch exceptions must become a stable, non-leaking failure result.");

    Console.WriteLine("OmpProcess contract tests passed.");
}
finally
{
    Directory.Delete(tempDirectory, recursive: true);
}

sealed class FakeGateway : IOmpProcessGateway
{
    public IReadOnlyList<Process> Processes { get; set; } = Array.Empty<Process>();
    public ProcessStartInfo? LastStartInfo { get; private set; }
    public Exception? StartException { get; set; }

    public IReadOnlyList<Process> GetProcessesByName(string processName) => Processes;

    public Process? Start(ProcessStartInfo startInfo)
    {
        LastStartInfo = startInfo;
        if (StartException is not null) throw StartException;
        return Process.GetCurrentProcess();
    }
}
