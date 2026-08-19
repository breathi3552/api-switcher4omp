using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.App;

public sealed class HomepageWorkflow : IDisposable
{
    private readonly PricingCheckUseCase _pricingCheck;
    private readonly SettingsUseCase _settingsUseCase;
    private readonly ApplyActiveRouteUseCase _applyActiveRoute;
    private readonly OmpLaunchUseCase _ompLaunch;
    private readonly OmpConfigurationReplacementUseCase? _ompReplacement;
    private readonly IActiveRouteController _activeRoute;
    private readonly IPricingSnapshotQuery _snapshotQuery;
    private readonly IUserNotificationService? _notifications;
    private readonly ISidecarStatus? _sidecarStatus;
    private readonly ILogger _logger;
    private readonly CancellationToken _lifetimeCancellationToken;

    private static readonly Action<ILogger, string, Exception?> LogFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(201, "WorkflowFailure"), "Workflow operation failed: {FailureKind}");

    private LocalAppSettings _settings;
    private CancellationTokenSource? _checkCancellation;
    private bool _disposed;

    public event Action<HomepageState>? StateChanged;

    public HomepageWorkflow(
        PricingCheckUseCase pricingCheck,
        SettingsUseCase settingsUseCase,
        ApplyActiveRouteUseCase applyActiveRoute,
        OmpLaunchUseCase ompLaunch,
        IActiveRouteController activeRoute,
        IPricingSnapshotQuery snapshotQuery,
        LocalAppSettings initialSettings,
        ILogger? logger = null,
        ISidecarStatus? sidecarStatus = null,
        OmpConfigurationReplacementUseCase? ompReplacement = null,
        IUserNotificationService? notifications = null,
        CancellationToken lifetimeCancellationToken = default)
    {
        _pricingCheck = pricingCheck ?? throw new ArgumentNullException(nameof(pricingCheck));
        _settingsUseCase = settingsUseCase ?? throw new ArgumentNullException(nameof(settingsUseCase));
        _applyActiveRoute = applyActiveRoute ?? throw new ArgumentNullException(nameof(applyActiveRoute));
        _ompLaunch = ompLaunch ?? throw new ArgumentNullException(nameof(ompLaunch));
        _activeRoute = activeRoute ?? throw new ArgumentNullException(nameof(activeRoute));
        _snapshotQuery = snapshotQuery ?? throw new ArgumentNullException(nameof(snapshotQuery));
        _settings = initialSettings ?? throw new ArgumentNullException(nameof(initialSettings));
        _logger = logger ?? NullLogger.Instance;
        _sidecarStatus = sidecarStatus;
        _ompReplacement = ompReplacement;
        _notifications = notifications;
        _lifetimeCancellationToken = lifetimeCancellationToken;

        if (_sidecarStatus is not null)
            _sidecarStatus.Changed += HandleSidecarStatusChanged;
        _activeRoute.Changed += HandleActiveRouteChanged;

        State = HomepageStateProjector.ProjectInitial(
            _settings,
            _activeRoute.CurrentProviderId,
            _sidecarStatus?.Current,
            null);
    }

    public HomepageState State { get; private set; }

    public LocalAppSettings Settings => _settings;

    public bool CanCheckPrices => _checkCancellation is null && !State.IsCheckingPrices;

    public bool CanCancelPriceCheck => _checkCancellation is not null;

    public bool CanApplyRoute => !State.IsApplyingRoute && State.SelectedProvider is not null;

    public bool CanStartOmp => !State.IsStartingOmp;

    public bool CanReplaceOmpGptProvider => _ompReplacement is not null && !State.IsReplacingOmpGptProvider && State.SelectedOmpConfigurationTarget is not null;

    public async Task<HomepageState> InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationToken, cancellationToken);
        var restored = await _applyActiveRoute.RestoreAsync(linked.Token);
        _settings = restored.Settings with { CurrentGatewayPort = _settings.CurrentGatewayPort };
        var snapshotResult = _snapshotQuery.Load();
        var snapshots = snapshotResult.IsSuccess ? snapshotResult.Snapshots : null;

        var newState = HomepageStateProjector.ProjectInitial(
            _settings,
            _activeRoute.CurrentProviderId,
            _sidecarStatus?.Current,
            snapshots,
            snapshotResult.IsSuccess);

        if (!restored.Succeeded)
        {
            newState = newState with { StatusText = UserErrorMessages.ForApplyRouteStatus(restored.Status) };
        }

        if (!snapshotResult.IsSuccess)
        {
            LogFailure(_logger, "SnapshotReadFailed", null);
        }

        SetState(newState);
        return State;
    }

    public async Task<HomepageState> CheckPricesAsync(CancellationToken cancellationToken = default)
    {
        if (_checkCancellation is not null || State.IsCheckingPrices)
            return State;

        var checkCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationToken, cancellationToken);
        _checkCancellation = checkCts;

        SetState(HomepageStateProjector.ProjectPriceCheckStarted(State));

        try
        {
            var outcome = await _pricingCheck.ExecuteAsync(_settings, _activeRoute.CurrentProviderId, checkCts.Token);
            _settings = outcome.Settings;
            _checkCancellation = null;
            checkCts.Dispose();
            SetState(HomepageStateProjector.ProjectPriceCheckCompleted(
                State,
                _settings,
                outcome.RefreshResult,
                _activeRoute.CurrentProviderId,
                LocalAppSettings.DefaultUsageProfile));
        }
        catch (OperationCanceledException)
        {
            _checkCancellation = null;
            checkCts.Dispose();
            SetState(HomepageStateProjector.ProjectPriceCheckCanceled(State));
        }
        catch (Exception ex)
        {
            _checkCancellation = null;
            checkCts.Dispose();
            SetState(State with
            {
                IsCheckingPrices = false,
                StatusText = UserErrorMessages.Unexpected
            });
            LogFailure(_logger, "PriceCheckUnexpected", ex);
        }
        finally
        {
            if (_checkCancellation is not null)
            {
                _checkCancellation = null;
                checkCts.Dispose();
            }
            if (State.IsCheckingPrices)
            {
                SetState(State with { IsCheckingPrices = false });
            }
        }

        return State;
    }

    public void CancelPriceCheck()
    {
        _checkCancellation?.Cancel();
    }

    public async Task<HomepageState> ApplyActiveRouteAsync(string? providerId = null, CancellationToken cancellationToken = default)
    {
        var target = providerId ?? State.SelectedProvider?.ProviderId;
        if (string.IsNullOrEmpty(target) || State.IsApplyingRoute)
            return State;

        SetState(HomepageStateProjector.ProjectApplyingRouteStarted(State));

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationToken, cancellationToken);
            var outcome = await _applyActiveRoute.ExecuteAsync(_settings, target, linked.Token);
            _settings = outcome.Settings;
            SetState(HomepageStateProjector.ProjectApplyingRouteCompleted(
                State,
                outcome,
                _activeRoute.CurrentProviderId,
                _sidecarStatus?.Current));
        }
        catch (OperationCanceledException)
        {
            SetState(State with { IsApplyingRoute = false, StatusText = "已取消应用供应商。" });
        }
        catch (Exception ex)
        {
            SetState(State with { IsApplyingRoute = false, StatusText = UserErrorMessages.Unexpected });
            LogFailure(_logger, "ApplyRouteUnexpected", ex);
        }
        finally
        {
            if (State.IsApplyingRoute)
            {
                SetState(State with { IsApplyingRoute = false });
            }
        }

        return State;
    }

    public async Task<HomepageState> StartOmpAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        if (State.IsStartingOmp)
            return State;

        SetState(HomepageStateProjector.ProjectStartingOmpStarted(State));

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationToken, cancellationToken);
            var dir = workingDirectory ?? State.SelectedOmpWorkingDirectory ?? _settings.OmpRootDirectory;
            var outcome = await _ompLaunch.LaunchAsync(_settings, dir, linked.Token);
            _settings = outcome.Settings;
            SetState(HomepageStateProjector.ProjectStartingOmpCompleted(State, outcome, _settings));
        }
        catch (OperationCanceledException)
        {
            SetState(State with { IsStartingOmp = false, StatusText = "已取消启动 OMP。" });
        }
        catch (Exception ex)
        {
            SetState(State with { IsStartingOmp = false, StatusText = UserErrorMessages.Unexpected });
            LogFailure(_logger, "StartOmpUnexpected", ex);
        }
        finally
        {
            if (State.IsStartingOmp)
            {
                SetState(State with { IsStartingOmp = false });
            }
        }

        return State;
    }

    public async Task<HomepageState> ReplaceOmpGptProviderAsync(
        string? targetProviderId = null,
        CancellationToken cancellationToken = default)
    {
        if (_ompReplacement is null || State.IsReplacingOmpGptProvider)
            return State;

        var target = targetProviderId ?? State.SelectedOmpConfigurationTarget?.ProviderId;
        if (string.IsNullOrEmpty(target))
            return State;

        SetState(HomepageStateProjector.ProjectReplacingOmpStarted(State));

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationToken, cancellationToken);
            var preview = await _ompReplacement.PreviewAsync(_settings, target, linked.Token);
            if (!preview.Succeeded || !preview.HasChanges)
            {
                SetState(HomepageStateProjector.ProjectReplacingOmpPreview(State, preview, false));
                return State;
            }

            var rows = string.Join(
                Environment.NewLine,
                preview.Changes.Select(change =>
                    $"{change.RolePath}: {change.OriginalProvider} -> {change.TargetProvider}，ModelId={change.ModelId}"));
            var modelsText = preview.ModelsChangeKind switch
            {
                OmpModelsProviderChangeKind.Added => Environment.NewLine + "同时新增本地 provider-price-switcher 定义。",
                OmpModelsProviderChangeKind.Updated => Environment.NewLine + "同时更新本地 provider-price-switcher 定义。",
                _ => string.Empty
            };

            var message = $"将替换以下 OMP GPT 路由到 {target}：{Environment.NewLine}{rows}{modelsText}{Environment.NewLine}{Environment.NewLine}确认写入 OMP 配置吗？";
            var title = "预览 OMP GPT 供应商替换";

            var confirmed = _notifications?.Confirm(message, title) ?? false;

            if (!confirmed)
            {
                SetState(HomepageStateProjector.ProjectReplacingOmpPreview(State, preview, false, "已取消 OMP 配置替换，未写入文件。"));
                return State;
            }

            var result = await _ompReplacement.ExecuteAsync(_settings, preview, linked.Token);
            SetState(HomepageStateProjector.ProjectReplacingOmpCompleted(State, result));
        }
        catch (OperationCanceledException)
        {
            SetState(State with { IsReplacingOmpGptProvider = false, StatusText = "已取消 OMP 配置替换，未写入文件。" });
        }
        catch (Exception ex)
        {
            SetState(State with
            {
                IsReplacingOmpGptProvider = false,
                OmpConfigurationStatus = "配置替换失败",
                StatusText = UserErrorMessages.Unexpected
            });
            LogFailure(_logger, "ReplaceOmpUnexpected", ex);
        }
        finally
        {
            if (State.IsReplacingOmpGptProvider)
            {
                SetState(State with { IsReplacingOmpGptProvider = false });
            }
        }

        return State;
    }

    public void SelectProvider(ProviderChoice? choice)
    {
        SetState(HomepageStateProjector.ProjectSelectedProvider(State, choice));
    }

    public void SelectProviderId(string? providerId)
    {
        var choice = State.ProviderChoices.FirstOrDefault(c => string.Equals(c.ProviderId, providerId, StringComparison.Ordinal));
        SetState(HomepageStateProjector.ProjectSelectedProvider(State, choice));
    }

    public void SelectOmpConfigurationTarget(OmpConfigurationTargetChoice? choice)
    {
        SetState(HomepageStateProjector.ProjectSelectedOmpTarget(State, choice));
    }

    public void SelectOmpConfigurationTargetId(string? targetId)
    {
        var choice = State.OmpConfigurationTargetChoices.FirstOrDefault(c => string.Equals(c.ProviderId, targetId, StringComparison.Ordinal));
        SetState(HomepageStateProjector.ProjectSelectedOmpTarget(State, choice));
    }

    public void SelectOmpWorkingDirectory(string? path)
    {
        SetState(HomepageStateProjector.ProjectSelectedWorkingDirectory(State, path));
    }

    public void UpdateSettings(LocalAppSettings newSettings, string? preferredProvider = null)
    {
        ArgumentNullException.ThrowIfNull(newSettings);
        _settings = newSettings;
        var snapshotResult = _snapshotQuery.Load();
        var snapshots = snapshotResult.IsSuccess ? snapshotResult.Snapshots : null;

        SetState(HomepageStateProjector.ProjectSettingsUpdated(
            State,
            _settings,
            _activeRoute.CurrentProviderId,
            snapshots,
            snapshotResult.IsSuccess,
            preferredProvider ?? State.SelectedProvider?.ProviderId));
    }

    public void SaveSettings(LocalAppSettings newSettings, string? preferredProvider = null)
    {
        ArgumentNullException.ThrowIfNull(newSettings);
        var saved = _settingsUseCase.Save(newSettings) with { CurrentGatewayPort = _settings.CurrentGatewayPort };
        UpdateSettings(saved, preferredProvider);
    }

    public void UpdateGatewayStatus(SidecarStatus status)
    {
        SetState(HomepageStateProjector.ProjectGatewayStatus(
            State,
            status,
            _activeRoute.CurrentProviderId,
            _settings.CurrentGatewayPort,
            _settings.GatewayPort));
    }

    public void UpdateActiveRoute()
    {
        SetState(HomepageStateProjector.ProjectActiveRoute(
            State,
            _activeRoute.CurrentProviderId,
            _sidecarStatus?.Current));
    }

    private void HandleSidecarStatusChanged(SidecarStatus status)
    {
        UpdateGatewayStatus(status);
    }

    private void HandleActiveRouteChanged()
    {
        UpdateActiveRoute();
    }

    private void SetState(HomepageState newState)
    {
        State = newState;
        StateChanged?.Invoke(newState);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sidecarStatus is not null)
            _sidecarStatus.Changed -= HandleSidecarStatusChanged;
        _activeRoute.Changed -= HandleActiveRouteChanged;

        _checkCancellation?.Cancel();
        _checkCancellation?.Dispose();
        _checkCancellation = null;
    }
}
