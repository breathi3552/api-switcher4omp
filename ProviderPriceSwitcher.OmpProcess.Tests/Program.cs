using System.Diagnostics;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Infrastructure;
using System.Runtime.Versioning;
[assembly: SupportedOSPlatform("windows")]
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
var runId = Guid.NewGuid().ToString("N");
var root = Path.Combine(Path.GetTempPath(), "ProviderPriceSwitcher-OmpProcess-" + runId);
var dataRoot = Path.Combine(root, "data"); var ompRoot = Path.Combine(root, "omp"); var workingRoot = Path.Combine(root, "working"); var otherWorkingRoot = Path.Combine(root, "working-other");
Directory.CreateDirectory(dataRoot); Directory.CreateDirectory(ompRoot); Directory.CreateDirectory(workingRoot); Directory.CreateDirectory(otherWorkingRoot);
var sentinels = new[] { dataRoot, ompRoot, workingRoot, otherWorkingRoot }.Select(path => Path.Combine(path, ".sentinel")).ToArray();
foreach (var sentinel in sentinels) File.WriteAllText(sentinel, runId);
var gateway = new FakeGateway();
try
{
    var service = new OmpProcessService(gateway);
    var missingDirectory = service.Start(new OmpProcessStartRequest(Path.Combine(workingRoot, "missing")));
    Assert(!missingDirectory.Succeeded && missingDirectory.FailureKind == OmpProcessFailureKind.WorkingDirectoryUnavailable, "Missing directory must return a stable failure kind.");
    var missingExecutable = service.Start(new OmpProcessStartRequest(workingRoot, Path.Combine(workingRoot, "missing.exe"), UseWindowsTerminal: false));
    Assert(!missingExecutable.Succeeded && missingExecutable.FailureKind == OmpProcessFailureKind.ExecutableUnavailable, "Missing absolute executable must return a stable failure kind.");
    var bareCommand = service.Start(new OmpProcessStartRequest(workingRoot, "dotnet", ["--version"], UseWindowsTerminal: false, OmpAgentDirectory: Path.Combine(ompRoot, "agent")));
    Assert(bareCommand.Succeeded && bareCommand.ProcessId is > 0 && bareCommand.StartTime is not null, "Fake process launch must return PID and start time.");
    Assert(gateway.LastStartInfo!.WorkingDirectory == Path.GetFullPath(workingRoot)
        && !gateway.LastStartInfo.UseShellExecute
        && gateway.LastStartInfo.Environment["PI_CODING_AGENT_DIR"] == Path.GetFullPath(Path.Combine(ompRoot, "agent")),
        "Direct launch must use the requested working directory and managed OMP agent directory.");
    var terminalLaunch = service.Start(new OmpProcessStartRequest(workingRoot, "omp", ["--help"], OmpAgentDirectory: Path.Combine(ompRoot, "agent")));
    Assert(terminalLaunch.Succeeded
        && gateway.LastStartInfo!.FileName == "wt.exe"
        && gateway.LastStartInfo.Environment["PI_CODING_AGENT_DIR"] == Path.GetFullPath(Path.Combine(ompRoot, "agent")),
        "Windows Terminal launch must inherit the managed OMP agent directory.");
    var adapterLaunch = new OmpProcessLauncher(service, new TestPaths(ompRoot))
        .Launch(new OmpLaunchRequest(workingRoot, ompRoot));
    Assert(adapterLaunch.Succeeded
        && gateway.LastStartInfo!.Environment["PI_CODING_AGENT_DIR"] == Path.GetFullPath(Path.Combine(ompRoot, "agent")),
        "Application OMP root must be translated to the managed agent directory at the process boundary.");
    var rootedExecutable = Path.Combine(workingRoot, "omp tool's.exe"); File.WriteAllText(rootedExecutable, string.Empty);
    var literalLaunch = service.Start(new OmpProcessStartRequest(workingRoot, rootedExecutable, ["value with spaces", "quote'and", "; $() & |", "--flag"]));
    Assert(literalLaunch.Succeeded, "Rooted executable launch must succeed through fake gateway.");
    var startsBeforeCancellation = gateway.StartCount;
    using var launchCancellation = new CancellationTokenSource();
    launchCancellation.Cancel();
    try
    {
        service.Start(new OmpProcessStartRequest(workingRoot, "dotnet", ["--version"], UseWindowsTerminal: false), launchCancellation.Token);
        throw new InvalidOperationException("Canceled process launch was not rejected.");
    }
    catch (OperationCanceledException)
    {
    }
    Assert(gateway.StartCount == startsBeforeCancellation, "Canceled process launch must not reach the process gateway.");
    const string syntheticSecret = "synthetic-token-DO-NOT-LOG https://example.invalid/prices?api_key=synthetic-query-secret&token=synthetic-token&cookie=synthetic-cookie C:\\Users\\Private\\Documents\\secret";
    var credentialStore = new WindowsSiteCredentialStore(dataRoot);
    credentialStore.SaveCredential(new ProviderPriceSwitcher.Core.SiteCredentialRecord
    {
        ProviderId = "omp-launch",
        SiteType = "new-api",
        AuthorizationScheme = "Bearer",
        AccessToken = syntheticSecret,
        CookieHeader = "synthetic-cookie"
    });
    gateway.StartException = new InvalidOperationException(syntheticSecret);
    var failedLaunch = service.Start(new OmpProcessStartRequest(workingRoot, "dotnet", UseWindowsTerminal: false));
    Assert(!failedLaunch.Succeeded && failedLaunch.FailureKind == OmpProcessFailureKind.StartFailed && !failedLaunch.ToString().Contains(syntheticSecret, StringComparison.Ordinal), "Launch exceptions must be safe.");
    var failedStartInfo = gateway.LastStartInfo ?? throw new InvalidOperationException("failed launch did not publish start info");
    var launchMaterial = string.Join(
        "\n",
        new[] { failedStartInfo.FileName, failedStartInfo.WorkingDirectory }
            .Concat(failedStartInfo.ArgumentList)
            .Concat(failedStartInfo.Environment.Select(pair => $"{pair.Key}={pair.Value}")));
    Assert(!launchMaterial.Contains(syntheticSecret, StringComparison.Ordinal)
        && !launchMaterial.Contains("synthetic-query-secret", StringComparison.Ordinal)
        && !launchMaterial.Contains("synthetic-cookie", StringComparison.Ordinal),
        "OMP command line and environment must not contain credential material");
    gateway.StartException = null;
    using var existingOmpProcess = Process.GetCurrentProcess();
    gateway.Processes = [existingOmpProcess];
    var startsBeforeRepeatedLaunches = gateway.StartCount;
    var repeatedSameDirectory = service.Start(new OmpProcessStartRequest(workingRoot, "dotnet", ["--version"], UseWindowsTerminal: false));
    var repeatedOtherDirectory = service.Start(new OmpProcessStartRequest(otherWorkingRoot, "dotnet", ["--version"], UseWindowsTerminal: false));
    Assert(repeatedSameDirectory.Succeeded
        && repeatedOtherDirectory.Succeeded
        && repeatedSameDirectory.ExistingProcess.Exists
        && repeatedOtherDirectory.ExistingProcess.Exists
        && gateway.StartCount == startsBeforeRepeatedLaunches + 2,
        "An existing OMP instance must not block new instances from the same or a different working directory.");
    Assert(gateway.StartInfos[^2].WorkingDirectory == Path.GetFullPath(workingRoot)
        && gateway.StartInfos[^1].WorkingDirectory == Path.GetFullPath(otherWorkingRoot),
        "Each repeated OMP launch must preserve its requested working directory.");
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
    public IReadOnlyList<ProcessStartInfo> StartInfos => startInfos;
    public int StartCount => started.Count;
    public Exception? StartException { get; set; }
    private readonly List<ProcessStartInfo> startInfos = [];
    public IReadOnlyList<Process> GetProcessesByName(string processName) => Processes;
    public Process? Start(ProcessStartInfo startInfo)
    {
        LastStartInfo = startInfo;
        startInfos.Add(startInfo);
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

sealed class TestPaths(string root) : IAppPathDefaults
{
    public string OmpRootDirectory => root;
    public string OmpConfigPath(string ompRootDirectory) => Path.Combine(ompRootDirectory, "config.yml");
    public string OmpModelsPath(string ompRootDirectory) => Path.Combine(ompRootDirectory, "agent", "models.yml");
    public string OmpAgentDirectory(string ompRootDirectory) => Path.Combine(ompRootDirectory, "agent");
}
