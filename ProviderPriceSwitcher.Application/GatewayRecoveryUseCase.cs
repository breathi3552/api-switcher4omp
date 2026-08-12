namespace ProviderPriceSwitcher.Application;

public enum GatewayRecoveryStatus
{
    Recovered,
    NoActiveRoute,
    Failed
}

public sealed record GatewayRecoveryOutcome(
    GatewayRecoveryStatus Status,
    ApplyActiveRouteStatus? RouteStatus = null)
{
    public bool Succeeded => Status is GatewayRecoveryStatus.Recovered or GatewayRecoveryStatus.NoActiveRoute;
}

public sealed class GatewayRecoveryUseCase(
    ISidecarLifecycle sidecar,
    ApplyActiveRouteUseCase activeRoute)
{
    public async Task<GatewayRecoveryOutcome> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(activeRoute);
        try
        {
            await sidecar.StartAsync(cancellationToken).ConfigureAwait(false);
            var route = await activeRoute.RestoreAsync(cancellationToken).ConfigureAwait(false);
            return route.Status switch
            {
                ApplyActiveRouteStatus.Applied => new GatewayRecoveryOutcome(GatewayRecoveryStatus.Recovered, route.Status),
                ApplyActiveRouteStatus.NoActiveRoute or ApplyActiveRouteStatus.Cleared => new GatewayRecoveryOutcome(GatewayRecoveryStatus.NoActiveRoute, route.Status),
                _ => new GatewayRecoveryOutcome(GatewayRecoveryStatus.Failed, route.Status)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new GatewayRecoveryOutcome(GatewayRecoveryStatus.Failed);
        }
    }
}
