namespace ProviderPriceSwitcher.Application;

public enum GatewayRecoveryStatus
{
    Recovered,
    NoActiveRoute,
    Failed
}

public enum GatewayRecoveryFailureKind
{
    None,
    GatewayUnavailable,
    ExecutableUnavailable,
    Protocol,
    AccessDenied,
    Unexpected
}

public sealed record GatewayRecoveryOutcome(
    GatewayRecoveryStatus Status,
    ApplyActiveRouteStatus? RouteStatus = null,
    GatewayRecoveryFailureKind FailureKind = GatewayRecoveryFailureKind.None)
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
                _ => new GatewayRecoveryOutcome(
                    GatewayRecoveryStatus.Failed,
                    route.Status,
                    route.SidecarFailure switch
                    {
                        SidecarFailureKind.GatewayUnavailable => GatewayRecoveryFailureKind.GatewayUnavailable,
                        SidecarFailureKind.Protocol => GatewayRecoveryFailureKind.Protocol,
                        SidecarFailureKind.AccessDenied => GatewayRecoveryFailureKind.AccessDenied,
                        SidecarFailureKind.ExecutableUnavailable => GatewayRecoveryFailureKind.ExecutableUnavailable,
                        _ => GatewayRecoveryFailureKind.Unexpected
                    })
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SidecarLifecycleException exception)
        {
            return new GatewayRecoveryOutcome(
                GatewayRecoveryStatus.Failed,
                FailureKind: exception.FailureKind switch
                {
                    SidecarFailureKind.GatewayUnavailable => GatewayRecoveryFailureKind.GatewayUnavailable,
                    SidecarFailureKind.ExecutableUnavailable => GatewayRecoveryFailureKind.ExecutableUnavailable,
                    SidecarFailureKind.Protocol => GatewayRecoveryFailureKind.Protocol,
                    SidecarFailureKind.AccessDenied => GatewayRecoveryFailureKind.AccessDenied,
                    _ => GatewayRecoveryFailureKind.Unexpected
                });
        }
        catch
        {
            return new GatewayRecoveryOutcome(GatewayRecoveryStatus.Failed, FailureKind: GatewayRecoveryFailureKind.Unexpected);
        }
    }
}
