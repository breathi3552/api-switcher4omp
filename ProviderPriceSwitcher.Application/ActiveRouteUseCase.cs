using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Application;

public enum ApplyActiveRouteStatus
{
    Applied,
    NoActiveRoute,
    Cleared,
    ProviderNotFound,
    ProviderDisabled,
    InferenceKeyMissing,
    InferenceKeyUnavailable,
    BindingMismatch,
    SidecarFailed,
    PersistenceFailed,
    RollbackFailed,
}

public sealed record ApplyActiveRouteOutcome(ApplyActiveRouteStatus Status, LocalAppSettings Settings)
{
    public bool Succeeded => Status is ApplyActiveRouteStatus.Applied or ApplyActiveRouteStatus.NoActiveRoute or ApplyActiveRouteStatus.Cleared;
}
public sealed class ApplyActiveRouteUseCase(
    ISettingsRepository settingsRepository,
    IRouteController routeController,
    IInferenceApiKeyStore inferenceApiKeyStore,
    IActiveRouteController activeRoute)
{

    public async Task<ApplyActiveRouteOutcome> ExecuteAsync(
        LocalAppSettings settings,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        using var lease = await activeRoute.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var target = FindTarget(settings, providerId);
        if (target.Status is not null)
            return new ApplyActiveRouteOutcome(target.Status.Value, settings);

        return await CommitAsync(settings, target.Site!, target.Key!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplyActiveRouteOutcome> RestoreAsync(
        LocalAppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var lease = await activeRoute.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.ActiveProviderId))
        {
            var previousProviderId = activeRoute.Current?.ProviderId;
            try
            {
                await routeController.ClearAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return new ApplyActiveRouteOutcome(ApplyActiveRouteStatus.SidecarFailed, settings);
            }

            if (previousProviderId is not null)
                activeRoute.ClearIfProvider(previousProviderId);
            return new ApplyActiveRouteOutcome(ApplyActiveRouteStatus.NoActiveRoute, settings);
        }

        var target = FindTarget(settings, settings.ActiveProviderId);
        if (target.Status is not null)
        {
            var previousProviderId = activeRoute.Current?.ProviderId;
            try
            {
                await routeController.ClearAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return new ApplyActiveRouteOutcome(ApplyActiveRouteStatus.SidecarFailed, settings);
            }

            if (previousProviderId is not null)
                activeRoute.ClearIfProvider(previousProviderId);
            try
            {
                var cleared = settingsRepository.Update(current => current with { ActiveProviderId = null });
                return new ApplyActiveRouteOutcome(ApplyActiveRouteStatus.Cleared, cleared);
            }
            catch (Exception)
            {
                return new ApplyActiveRouteOutcome(ApplyActiveRouteStatus.PersistenceFailed, settings);
            }
        }

        return await RestoreExistingAsync(settings, target.Site!, target.Key!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApplyActiveRouteOutcome> CommitAsync(
        LocalAppSettings settings,
        SiteConfiguration site,
        InferenceApiKeyRecord key,
        CancellationToken cancellationToken)
    {
        var next = new RouteSnapshot(site.ProviderId, site.BaseUrl.ToString(), key.KeyHandle);
        var previous = activeRoute.Current;
        try
        {
            await routeController.ApplyAsync(next, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ApplyActiveRouteOutcome(ApplyActiveRouteStatus.SidecarFailed, settings);
        }

        LocalAppSettings persisted;
        try
        {
            persisted = settingsRepository.Update(current => current with { ActiveProviderId = site.ProviderId });
        }
        catch (Exception)
        {
            if (!await TryRestoreRouteAsync(previous).ConfigureAwait(false))
                return new ApplyActiveRouteOutcome(ApplyActiveRouteStatus.RollbackFailed, settings);

            return new ApplyActiveRouteOutcome(ApplyActiveRouteStatus.PersistenceFailed, settings);
        }

        activeRoute.Apply(next);
        return new ApplyActiveRouteOutcome(ApplyActiveRouteStatus.Applied, persisted);
    }

    private async Task<ApplyActiveRouteOutcome> RestoreExistingAsync(
        LocalAppSettings settings,
        SiteConfiguration site,
        InferenceApiKeyRecord key,
        CancellationToken cancellationToken)
    {
        var next = new RouteSnapshot(site.ProviderId, site.BaseUrl.ToString(), key.KeyHandle);
        try
        {
            await routeController.ApplyAsync(next, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ApplyActiveRouteOutcome(ApplyActiveRouteStatus.SidecarFailed, settings);
        }

        activeRoute.Apply(next);
        return new ApplyActiveRouteOutcome(ApplyActiveRouteStatus.Applied, settings);
    }

    private async Task<bool> TryRestoreRouteAsync(RouteSnapshot? previous)
    {
        try
        {
            if (previous is null)
                await routeController.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            else
                await routeController.ApplyAsync(previous, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private (SiteConfiguration? Site, InferenceApiKeyRecord? Key, ApplyActiveRouteStatus? Status) FindTarget(LocalAppSettings settings, string providerId)
    {
        var site = settings.Sites.FirstOrDefault(x => string.Equals(x.ProviderId, providerId, StringComparison.Ordinal));
        if (site is null)
            return (null, null, ApplyActiveRouteStatus.ProviderNotFound);
        if (!site.Enabled)
            return (null, null, ApplyActiveRouteStatus.ProviderDisabled);

        InferenceApiKeyRecord? key;
        try
        {
            key = inferenceApiKeyStore.Load(providerId);
        }
        catch
        {
            return (null, null, ApplyActiveRouteStatus.InferenceKeyUnavailable);
        }

        if (key is null)
            return (null, null, ApplyActiveRouteStatus.InferenceKeyMissing);
        if (!string.Equals(key.BoundGroup, site.CurrentGroup, StringComparison.Ordinal))
            return (null, null, ApplyActiveRouteStatus.BindingMismatch);
        return (site, key, null);
    }
}
