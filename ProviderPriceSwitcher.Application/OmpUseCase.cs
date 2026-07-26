using Microsoft.Extensions.Logging;

namespace ProviderPriceSwitcher.Application;

public sealed record OmpConfigurationOperationResult(bool Succeeded, string? Error);
public sealed record OmpLaunchResult(bool Succeeded, bool ExistingProcess, string? Error);

public interface IOmpConfigurationService
{
    Task<OmpConfigurationOperationResult> SwitchAsync(string ompRootDirectory, string providerId, CancellationToken cancellationToken = default);
}

public interface IOmpProcessLauncher
{
    OmpLaunchResult Launch(string workingDirectory);
}

public enum SwitchAndStartStatus
{
    ConfigurationFailed,
    Started,
    StartedWithExistingProcess,
    LaunchFailedAfterSwitch
}

public sealed record SwitchAndStartOutcome(SwitchAndStartStatus Status, LocalAppSettings Settings, string? Error);

public sealed class SwitchAndStartUseCase(
    ISettingsRepository settingsRepository,
    IOmpConfigurationService configurationService,
    IOmpProcessLauncher processLauncher,
    ILogger<SwitchAndStartUseCase> logger)
{
    private static readonly Action<ILogger, string?, Exception?> LogLaunchFailure =
        LoggerMessage.Define<string?>(LogLevel.Error, new EventId(1, "OmpLaunchFailure"), "OMP launch failed after configuration switch: {Error}");

    public async Task<SwitchAndStartOutcome> ExecuteAsync(
        LocalAppSettings settings,
        string providerId,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurationService.SwitchAsync(settings.OmpRootDirectory, providerId, cancellationToken).ConfigureAwait(false);
        if (!configuration.Succeeded)
            return new SwitchAndStartOutcome(SwitchAndStartStatus.ConfigurationFailed, settings, configuration.Error);

        var directories = settings.OmpWorkingDirectories.Append(workingDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var updated = settings with { OmpWorkingDirectories = directories, LastOmpWorkingDirectory = workingDirectory };
        settingsRepository.Save(updated);

        var launch = processLauncher.Launch(workingDirectory);
        if (!launch.Succeeded)
        {
            LogLaunchFailure(logger, launch.Error, null);
            return new SwitchAndStartOutcome(SwitchAndStartStatus.LaunchFailedAfterSwitch, updated, launch.Error);
        }

        return new SwitchAndStartOutcome(
            launch.ExistingProcess ? SwitchAndStartStatus.StartedWithExistingProcess : SwitchAndStartStatus.Started,
            updated,
            null);
    }
}
