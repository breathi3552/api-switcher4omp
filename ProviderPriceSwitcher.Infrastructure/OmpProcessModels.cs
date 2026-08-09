using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed record OmpProcessStartRequest(
    string WorkingDirectory,
    string Executable = "omp",
    IReadOnlyList<string>? Arguments = null,
    bool UseWindowsTerminal = true,
    string? OmpAgentDirectory = null)
{
    public IReadOnlyList<string> EffectiveArguments => Arguments ?? Array.Empty<string>();
}

public sealed record OmpExistingProcessHint(int Count)
{
    public bool Exists => Count > 0;
}


public sealed record OmpProcessStartResult(
    bool Succeeded,
    int? ProcessId,
    DateTimeOffset? StartTime,
    OmpExistingProcessHint ExistingProcess,
    OmpProcessFailureKind? FailureKind)
{
    public static OmpProcessStartResult Failure(OmpExistingProcessHint existingProcess, OmpProcessFailureKind failureKind) =>
        new(false, null, null, existingProcess, failureKind);

    public static OmpProcessStartResult Success(
        OmpExistingProcessHint existingProcess,
        int processId,
        DateTimeOffset startTime) =>
        new(true, processId, startTime, existingProcess, null);
}
