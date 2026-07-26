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
    Assert(!missingDirectory.Succeeded && missingDirectory.ErrorMessage!.Contains("Working directory", StringComparison.OrdinalIgnoreCase), "Missing directory must fail clearly.");

    var missingExecutable = service.Start(new OmpProcessStartRequest(tempDirectory, Path.Combine(tempDirectory, "missing.exe"), UseWindowsTerminal: false));
    Assert(!missingExecutable.Succeeded && missingExecutable.ErrorMessage!.Contains("executable", StringComparison.OrdinalIgnoreCase), "Missing absolute executable must fail clearly.");

    var bareCommand = service.Start(new OmpProcessStartRequest(tempDirectory, "dotnet", ["--version"], UseWindowsTerminal: false));
    Assert(bareCommand.Succeeded, "Bare commands must be allowed through PATH.");
    Assert(bareCommand.ExistingProcess.Exists && bareCommand.ExistingProcess.Count == 1, "Existing process hint must be returned without blocking.");
    Assert(gateway.LastStartInfo!.WorkingDirectory == Path.GetFullPath(tempDirectory), "WorkingDirectory must be passed to ProcessStartInfo.");
    Assert(gateway.LastStartInfo.UseShellExecute == false, "Shell execution must be disabled.");
    Assert(bareCommand.ProcessId is > 0 && bareCommand.StartTime is not null, "Successful launch must return PID and start time.");

    var terminalLaunch = service.Start(new OmpProcessStartRequest(tempDirectory, "omp", ["--help"]));
    Assert(terminalLaunch.Succeeded, "Windows Terminal launch must succeed through the gateway.");
    Assert(gateway.LastStartInfo!.FileName == "wt.exe", "Windows Terminal must be the launched executable.");
    Assert(gateway.LastStartInfo.ArgumentList.SequenceEqual(["-w", "new", "new-tab", "--startingDirectory", Path.GetFullPath(tempDirectory), "omp", "--help"]), "Windows Terminal arguments must preserve working directory and OMP arguments.");

    gateway.StartException = new InvalidOperationException("stub launch failure");
    var failedLaunch = service.Start(new OmpProcessStartRequest(tempDirectory, "dotnet", UseWindowsTerminal: false));
    Assert(!failedLaunch.Succeeded && failedLaunch.ErrorMessage!.Contains("stub launch failure", StringComparison.Ordinal), "Launch exceptions must become explicit failure results.");

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
