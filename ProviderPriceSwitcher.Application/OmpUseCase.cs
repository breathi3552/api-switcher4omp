using Microsoft.Extensions.Logging;

namespace ProviderPriceSwitcher.Application;

public static class OmpSidecarProvider
{
    public const string Id = "provider-price-switcher";
    public const string Host = "127.0.0.1";
    public const int DefaultPort = 15722;
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
    SettingsPersistenceFailed
}

public sealed record OmpStartupOutcome(
    OmpStartupStatus Status,
    LocalAppSettings Settings)
{
    public bool Succeeded => Status == OmpStartupStatus.Ready;
}

public sealed class OmpStartupUseCase
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly ILogger<OmpStartupUseCase> _logger;
    private static readonly Action<ILogger, string, Exception?> LogStartupFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "OmpStartupFailure"), "OMP startup preparation failed: {FailureKind}");

    public OmpStartupUseCase(
        ISettingsRepository settingsRepository,
        ILogger<OmpStartupUseCase> logger)
    {
        _settingsRepository = settingsRepository;
        _logger = logger;
    }

    public OmpStartupOutcome Initialize(LocalAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.CurrentGatewayPort == settings.GatewayPort)
            return new(OmpStartupStatus.Ready, settings);

        try
        {
            var updated = _settingsRepository.Update(current => current with
            {
                CurrentGatewayPort = settings.GatewayPort
            });
            return new(OmpStartupStatus.Ready, updated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStartupFailure(_logger, OmpStartupStatus.SettingsPersistenceFailed.ToString(), exception);
            return new(OmpStartupStatus.SettingsPersistenceFailed, settings);
        }
    }

    public Task<OmpStartupOutcome> InitializeAsync(
        LocalAppSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Initialize(settings));
    }
}

public enum OmpLaunchStatus
{
    Started,
    SettingsPersistenceFailed,
    LaunchFailed
}

public sealed record OmpLaunchOutcome(
    OmpLaunchStatus Status,
    LocalAppSettings Settings,
    OmpProcessFailureKind? ProcessFailureKind = null)
{
    public bool Succeeded => Status == OmpLaunchStatus.Started;
}

public sealed class OmpLaunchUseCase
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IOmpProcessLauncher _processLauncher;
    private readonly ILogger<OmpLaunchUseCase> _logger;
    private static readonly Action<ILogger, string, Exception?> LogLaunchFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "OmpLaunchFailure"), "OMP launch failed: {FailureKind}");

    public OmpLaunchUseCase(
        ISettingsRepository settingsRepository,
        IOmpProcessLauncher processLauncher,
        ILogger<OmpLaunchUseCase> logger)
    {
        _settingsRepository = settingsRepository;
        _processLauncher = processLauncher;
        _logger = logger;
    }

    public Task<OmpLaunchOutcome> LaunchAsync(
        LocalAppSettings settings,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Task.FromResult(LaunchProcess(settings, workingDirectory, cancellationToken));
    }

    private OmpLaunchOutcome LaunchProcess(
        LocalAppSettings settings,
        string workingDirectory,
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
            updated = _settingsRepository.Update(current => current with
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
            LogLaunchFailure(_logger, OmpLaunchStatus.SettingsPersistenceFailed.ToString(), exception);
            return new(OmpLaunchStatus.SettingsPersistenceFailed, settings);
        }

        var launch = _processLauncher.Launch(new OmpLaunchRequest(workingDirectory, settings.OmpRootDirectory), cancellationToken);
        if (!launch.Succeeded)
        {
            LogLaunchFailure(_logger, launch.FailureKind?.ToString() ?? OmpLaunchStatus.LaunchFailed.ToString(), null);
            return new(
                OmpLaunchStatus.LaunchFailed,
                updated,
                ProcessFailureKind: launch.FailureKind);
        }

        return new(OmpLaunchStatus.Started, updated);
    }
}
