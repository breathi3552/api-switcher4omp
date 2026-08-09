using Microsoft.Extensions.Logging;

namespace ProviderPriceSwitcher.Application;

public static class OmpSidecarProvider
{
    public const string Id = "provider-price-switcher";
    public const string Host = "127.0.0.1";
    public const int DefaultPort = 15722;
}

public enum OmpTakeoverStatus
{
    TakenOver,
    NotTakenOver,
    ReadFailed
}

public sealed record OmpTakeoverCheckResult(OmpTakeoverStatus Status, string? CurrentProviderId = null, int? CurrentGatewayPort = null);
public enum OmpTakeoverFailureKind
{
    None,
    InvalidPort,
    Configuration,
    RollbackFailed
}
public sealed record OmpTakeoverOperationResult(
    bool Succeeded,
    OmpTakeoverFailureKind FailureKind = OmpTakeoverFailureKind.None,
    bool BackupRetentionSucceeded = true);

public interface IOmpTakeoverService
{
    Task<OmpTakeoverCheckResult> CheckAsync(string ompRootDirectory, CancellationToken cancellationToken = default);
    Task<OmpTakeoverOperationResult> TakeOverAsync(string ompRootDirectory, int gatewayPort, CancellationToken cancellationToken = default);
}

public enum OmpProcessFailureKind
{
    InvalidRequest,
    WorkingDirectoryUnavailable,
    ExecutableUnavailable,
    AccessDenied,
    StartFailed,
    Unexpected
}

public sealed record OmpLaunchResult(
    bool Succeeded,
    OmpProcessFailureKind? FailureKind = null);
public sealed record OmpLaunchRequest(
    string WorkingDirectory,
    string OmpRootDirectory);


public interface IOmpProcessLauncher
{
    OmpLaunchResult Launch(OmpLaunchRequest request, CancellationToken cancellationToken = default);
}
public enum OmpStartupStatus
{
    Ready,
    TakeoverReadFailed,
    PortMigrationFailed,
    SettingsPersistenceFailed
}

public sealed record OmpStartupOutcome(
    OmpStartupStatus Status,
    LocalAppSettings Settings,
    OmpTakeoverFailureKind TakeoverFailureKind = OmpTakeoverFailureKind.None,
    bool BackupRetentionSucceeded = true)
{
    public bool Succeeded => Status == OmpStartupStatus.Ready;
}

public sealed class OmpStartupUseCase(
    ISettingsRepository settingsRepository,
    IOmpTakeoverService takeoverService,
    ILogger<OmpStartupUseCase> logger)
{
    private static readonly Action<ILogger, string, Exception?> LogStartupFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "OmpStartupFailure"), "OMP startup preparation failed: {FailureKind}");

    public async Task<OmpStartupOutcome> InitializeAsync(
        LocalAppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var takeover = await takeoverService.CheckAsync(settings.OmpRootDirectory, cancellationToken).ConfigureAwait(false);
        if (takeover.Status == OmpTakeoverStatus.ReadFailed)
            return new(OmpStartupStatus.TakeoverReadFailed, settings);

        var targetPort = settings.GatewayPort;
        var observedPort = takeover.CurrentGatewayPort ?? settings.CurrentGatewayPort;
        var effectivePort = targetPort;
        var backupRetentionSucceeded = true;
        if (takeover.Status == OmpTakeoverStatus.TakenOver)
        {
            effectivePort = observedPort;
            if (observedPort != targetPort)
            {
                var migration = await takeoverService.TakeOverAsync(
                    settings.OmpRootDirectory,
                    targetPort,
                    cancellationToken).ConfigureAwait(false);
                if (!migration.Succeeded)
                    return new(
                        OmpStartupStatus.PortMigrationFailed,
                        settings,
                        migration.FailureKind,
                        migration.BackupRetentionSucceeded);
                effectivePort = targetPort;
                backupRetentionSucceeded = migration.BackupRetentionSucceeded;
            }
        }

        if (settings.CurrentGatewayPort == effectivePort)
            return new(OmpStartupStatus.Ready, settings, BackupRetentionSucceeded: backupRetentionSucceeded);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updated = settingsRepository.Update(current => current with { CurrentGatewayPort = effectivePort });
            return new(OmpStartupStatus.Ready, updated, BackupRetentionSucceeded: backupRetentionSucceeded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogStartupFailure(logger, OmpStartupStatus.SettingsPersistenceFailed.ToString(), exception);
            return new(OmpStartupStatus.SettingsPersistenceFailed, settings, BackupRetentionSucceeded: backupRetentionSucceeded);
        }
    }
}


public enum OmpLaunchStatus
{
    Started,
    TakeoverRequired,
    TakeoverFailed,
    TakeoverReadFailed,
    SettingsPersistenceFailed,
    LaunchFailed
}

public sealed record OmpLaunchOutcome(
    OmpLaunchStatus Status,
    LocalAppSettings Settings,
    OmpTakeoverFailureKind TakeoverFailureKind = OmpTakeoverFailureKind.None,
    bool BackupRetentionSucceeded = true,
    OmpProcessFailureKind? ProcessFailureKind = null)
{
    public bool Succeeded => Status == OmpLaunchStatus.Started;
}

public sealed class OmpLaunchUseCase(
    ISettingsRepository settingsRepository,
    IOmpTakeoverService takeoverService,
    IOmpProcessLauncher processLauncher,
    ILogger<OmpLaunchUseCase> logger)
{
    private static readonly Action<ILogger, string, Exception?> LogLaunchFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "OmpLaunchFailure"), "OMP launch failed: {FailureKind}");

    public Task<OmpTakeoverCheckResult> CheckTakeoverAsync(
        LocalAppSettings settings,
        CancellationToken cancellationToken = default) =>
        takeoverService.CheckAsync(settings.OmpRootDirectory, cancellationToken);

    public async Task<OmpLaunchOutcome> LaunchAsync(
        LocalAppSettings settings,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var takeover = await takeoverService.CheckAsync(settings.OmpRootDirectory, cancellationToken).ConfigureAwait(false);
        if (takeover.Status == OmpTakeoverStatus.NotTakenOver)
            return new(OmpLaunchStatus.TakeoverRequired, settings);
        if (takeover.Status == OmpTakeoverStatus.ReadFailed)
            return new(OmpLaunchStatus.TakeoverReadFailed, settings);
        return LaunchProcess(settings, workingDirectory, backupRetentionSucceeded: true, cancellationToken);
    }

    public async Task<OmpLaunchOutcome> TakeOverAndLaunchAsync(
        LocalAppSettings settings,
        string workingDirectory,
        int gatewayPort,
        CancellationToken cancellationToken = default)
    {
        var takeover = await takeoverService.TakeOverAsync(settings.OmpRootDirectory, gatewayPort, cancellationToken).ConfigureAwait(false);
        if (!takeover.Succeeded)
            return new(OmpLaunchStatus.TakeoverFailed, settings, takeover.FailureKind, takeover.BackupRetentionSucceeded);
        LocalAppSettings takeoverSettings;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            takeoverSettings = settingsRepository.Update(current => current with { CurrentGatewayPort = gatewayPort });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogLaunchFailure(logger, OmpLaunchStatus.SettingsPersistenceFailed.ToString(), exception);
            return new(OmpLaunchStatus.SettingsPersistenceFailed, settings, BackupRetentionSucceeded: takeover.BackupRetentionSucceeded);
        }
        return LaunchProcess(
            takeoverSettings,
            workingDirectory,
            takeover.BackupRetentionSucceeded,
            cancellationToken);
    }

    private OmpLaunchOutcome LaunchProcess(
        LocalAppSettings settings,
        string workingDirectory,
        bool backupRetentionSucceeded,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directories = settings.OmpWorkingDirectories
            .Append(workingDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        LocalAppSettings updated;
        try
        {
            updated = settingsRepository.Update(current => current with
            {
                OmpWorkingDirectories = directories,
                LastOmpWorkingDirectory = workingDirectory
            });
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogLaunchFailure(logger, OmpLaunchStatus.SettingsPersistenceFailed.ToString(), exception);
            return new(OmpLaunchStatus.SettingsPersistenceFailed, settings, BackupRetentionSucceeded: backupRetentionSucceeded);
        }

        var launch = processLauncher.Launch(new OmpLaunchRequest(workingDirectory, settings.OmpRootDirectory), cancellationToken);
        if (!launch.Succeeded)
        {
            LogLaunchFailure(logger, launch.FailureKind?.ToString() ?? OmpLaunchStatus.LaunchFailed.ToString(), null);
            return new(
                OmpLaunchStatus.LaunchFailed,
                updated,
                BackupRetentionSucceeded: backupRetentionSucceeded,
                ProcessFailureKind: launch.FailureKind);
        }

        return new(OmpLaunchStatus.Started, updated, BackupRetentionSucceeded: backupRetentionSucceeded);
    }
}
