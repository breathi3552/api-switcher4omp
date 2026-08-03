using ProviderPriceSwitcher.Core;
using Microsoft.Extensions.Logging;

namespace ProviderPriceSwitcher.Application;

public static class OmpSidecarProvider
{
    public const string Id = "provider-price-switcher";
}
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
    IInferenceApiKeyStore? inferenceApiKeyStore = null,
    IActiveRouteController? activeRoute = null)
{
    private static readonly Action<ILogger, string, Exception?> LogLaunchFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "OmpLaunchFailure"), "OMP launch failed after configuration switch: {FailureKind}");

    public async Task<SwitchAndStartOutcome> ExecuteAsync(
        LocalAppSettings settings,
        string providerId,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        using var routeLease = activeRoute is null ? null : await activeRoute.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var configuration = await configurationService.SwitchAsync(settings.OmpRootDirectory, OmpSidecarProvider.Id, cancellationToken).ConfigureAwait(false);
        if (!configuration.Succeeded)
            return new SwitchAndStartOutcome(SwitchAndStartStatus.ConfigurationFailed, settings);
        var directories = settings.OmpWorkingDirectories.Append(workingDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var updated = settingsRepository.Update(current => current with { OmpWorkingDirectories = directories, LastOmpWorkingDirectory = workingDirectory });
        if (routeController is not null && inferenceApiKeyStore is not null)
        {
            var site = settings.Sites.FirstOrDefault(x => string.Equals(x.ProviderId, providerId, StringComparison.Ordinal));
            var key = site is null ? null : inferenceApiKeyStore.Load(providerId);
            if (site is null || !site.Enabled || key is null || !string.Equals(key.BoundGroup, site.CurrentGroup, StringComparison.Ordinal))
            {
                if (activeRoute?.CurrentProviderId is { } active && string.Equals(active, providerId, StringComparison.Ordinal))
                {
                    await routeController.ClearAsync(cancellationToken).ConfigureAwait(false);
                    activeRoute.ClearIfProvider(providerId);
                }
                return new SwitchAndStartOutcome(SwitchAndStartStatus.ConfigurationFailed, settings);
            }
            var snapshot = new ProviderPriceSwitcher.Core.RouteSnapshot(providerId, site.BaseUrl.ToString(), key.KeyHandle);
            await routeController.ApplyAsync(snapshot, cancellationToken).ConfigureAwait(false);
            activeRoute?.Apply(snapshot);
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
