using ProviderPriceSwitcher.Core;
using Microsoft.Extensions.Logging;

namespace ProviderPriceSwitcher.Application;

public sealed record OmpConfigurationOperationResult(bool Succeeded);
public sealed record OmpLaunchResult(bool Succeeded, bool ExistingProcess);

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

public sealed record SwitchAndStartOutcome(SwitchAndStartStatus Status, LocalAppSettings Settings);

public sealed class SwitchAndStartUseCase(
    ISettingsRepository settingsRepository,
    IOmpConfigurationService configurationService,
    IOmpProcessLauncher processLauncher,
    ILogger<SwitchAndStartUseCase> logger,
    IRouteController? routeController = null,
    IInferenceApiKeyStore? inferenceApiKeyStore = null)
{
    private static readonly Action<ILogger, string, Exception?> LogLaunchFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "OmpLaunchFailure"), "OMP launch failed after configuration switch: {FailureKind}");

    public async Task<SwitchAndStartOutcome> ExecuteAsync(
        LocalAppSettings settings,
        string providerId,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurationService.SwitchAsync(settings.OmpRootDirectory, providerId, cancellationToken).ConfigureAwait(false);
        if (!configuration.Succeeded)
            return new SwitchAndStartOutcome(SwitchAndStartStatus.ConfigurationFailed, settings);

        var directories = settings.OmpWorkingDirectories.Append(workingDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var updated = settings with { OmpWorkingDirectories = directories, LastOmpWorkingDirectory = workingDirectory };
        settingsRepository.Save(updated);
        if (routeController is not null && inferenceApiKeyStore is not null)
        {
            var site = settings.Sites.FirstOrDefault(x => string.Equals(x.ProviderId, providerId, StringComparison.Ordinal));
            var key = site is null ? null : inferenceApiKeyStore.Load(providerId);
            if (site is null || key is null)
                return new SwitchAndStartOutcome(SwitchAndStartStatus.ConfigurationFailed, settings);
            await routeController.ApplyAsync(new ProviderPriceSwitcher.Core.RouteSnapshot(providerId, site.BaseUrl.ToString(), key.KeyHandle), cancellationToken).ConfigureAwait(false);
        }

        var launch = processLauncher.Launch(workingDirectory);
        if (!launch.Succeeded)
        {
            LogLaunchFailure(logger, SwitchAndStartStatus.LaunchFailedAfterSwitch.ToString(), null);
            return new SwitchAndStartOutcome(SwitchAndStartStatus.LaunchFailedAfterSwitch, updated);
        }

        return new SwitchAndStartOutcome(
            launch.ExistingProcess ? SwitchAndStartStatus.StartedWithExistingProcess : SwitchAndStartStatus.Started,
            updated);
    }
}
