namespace ProviderPriceSwitcher.Infrastructure;

public sealed record OmpProcessStartRequest(
    string WorkingDirectory,
    string Executable = "omp",
    IReadOnlyList<string>? Arguments = null,
    bool UseWindowsTerminal = true)
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
    string? ErrorMessage)
{
    public static OmpProcessStartResult Failure(OmpExistingProcessHint existingProcess, string errorMessage) =>
        new(false, null, null, existingProcess, errorMessage);

    public static OmpProcessStartResult Success(
        OmpExistingProcessHint existingProcess,
        int processId,
        DateTimeOffset startTime) =>
        new(true, processId, startTime, existingProcess, null);
}
