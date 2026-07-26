using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed class OmpConfigurationService(
    OmpConfigurationSwitcher switcher,
    IAppPathDefaults pathDefaults) : IOmpConfigurationService
{
    public async Task<OmpConfigurationOperationResult> SwitchAsync(
        string ompRootDirectory,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await switcher.SwitchFileAsync(
                pathDefaults.OmpConfigPath(ompRootDirectory),
                providerId,
                cancellationToken).ConfigureAwait(false);
            return new OmpConfigurationOperationResult(result.Succeeded, result.Error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }
}

public sealed class OmpProcessLauncher(OmpProcessService processService) : IOmpProcessLauncher
{
    public OmpLaunchResult Launch(string workingDirectory)
    {
        var result = processService.Start(new OmpProcessStartRequest(workingDirectory));
        return new OmpLaunchResult(result.Succeeded, result.ExistingProcess.Exists, result.ErrorMessage);
    }
}
