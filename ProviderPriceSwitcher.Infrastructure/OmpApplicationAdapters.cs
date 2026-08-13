using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.Infrastructure;


public sealed class OmpProcessLauncher(
    OmpProcessService processService,
    IAppPathDefaults pathDefaults) : IOmpProcessLauncher
{
    public OmpLaunchResult Launch(OmpLaunchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = processService.Start(new OmpProcessStartRequest(
            request.WorkingDirectory,
            OmpAgentDirectory: pathDefaults.OmpAgentDirectory(request.OmpRootDirectory)), cancellationToken);
        return new OmpLaunchResult(result.Succeeded, result.FailureKind);
    }
}
