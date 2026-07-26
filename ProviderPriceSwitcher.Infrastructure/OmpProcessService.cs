using System.Diagnostics;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed class OmpProcessService
{
    private readonly IOmpProcessGateway _processGateway;

    public OmpProcessService(IOmpProcessGateway? processGateway = null)
    {
        _processGateway = processGateway ?? new SystemOmpProcessGateway();
    }

    public OmpProcessStartResult Start(OmpProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        OmpExistingProcessHint existingProcess;
        try
        {
            existingProcess = new OmpExistingProcessHint(
                _processGateway.GetProcessesByName("omp").Count);
        }
        catch
        {
            // Process detection is advisory and must never prevent an attempted launch.
            existingProcess = new OmpExistingProcessHint(0);
        }

        try
        {
            var workingDirectory = WorkingDirectoryValidator.Validate(request.WorkingDirectory);
            var executable = ValidateExecutable(request.Executable);
            var startInfo = request.UseWindowsTerminal
                ? CreateWindowsTerminalStartInfo(workingDirectory, executable, request.EffectiveArguments)
                : CreateStartInfo(workingDirectory, executable, request.EffectiveArguments);
            var process = _processGateway.Start(startInfo);
            if (process is null)
            {
                return OmpProcessStartResult.Failure(
                    existingProcess,
                    "OMP process could not be started: the process gateway returned no process.");
            }

            var startTime = process.StartTime.ToUniversalTime();
            return OmpProcessStartResult.Success(existingProcess, process.Id, new DateTimeOffset(startTime));
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return OmpProcessStartResult.Failure(
                existingProcess,
                $"OMP process could not be started: {exception.Message}");
        }
        catch (Exception exception)
        {
            return OmpProcessStartResult.Failure(
                existingProcess,
                $"OMP process could not be started: {exception.Message}");
        }
    }

    public static ProcessStartInfo CreateStartInfo(
        string workingDirectory,
        string executable,
        IEnumerable<string>? arguments = null)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };

        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
        }

        return info;
    }

    public static ProcessStartInfo CreateWindowsTerminalStartInfo(
        string workingDirectory,
        string executable,
        IEnumerable<string>? arguments = null)
    {
        var info = new ProcessStartInfo
        {
            FileName = "wt.exe",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        info.ArgumentList.Add("-w");
        info.ArgumentList.Add("new");
        info.ArgumentList.Add("new-tab");
        info.ArgumentList.Add("--startingDirectory");
        info.ArgumentList.Add(workingDirectory);
        info.ArgumentList.Add(executable);
        if (arguments is not null)
        {
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
        }
        return info;
    }
    private static string ValidateExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new ArgumentException("An executable command is required.", nameof(executable));
        }

        if (Path.IsPathRooted(executable) && !File.Exists(executable))
        {
            throw new FileNotFoundException($"The executable does not exist: '{executable}'.", executable);
        }

        return executable;
    }
}
