namespace ProviderPriceSwitcher.Application;

public static class OmpConfigurationReplacementTargets
{
    public const string LocalProviderId = OmpSidecarProvider.Id;
    public const string OfficialOAuthProviderId = "openai-codex";

    public static IReadOnlyList<string> All { get; } =
        [LocalProviderId, OfficialOAuthProviderId];

    public static bool IsSupported(string? providerId) =>
        providerId is not null && All.Contains(providerId, StringComparer.Ordinal);
}

public enum OmpConfigurationReplacementFailureKind
{
    None,
    InvalidTarget,
    InvalidGatewayPort,
    ConfigurationMissing,
    ConfigurationReadFailed,
    ConfigurationInvalid,
    ModelsReadFailed,
    StalePreview,
    ConfigurationWriteFailed,
    ModelsWriteFailed,
    RollbackFailed,
    Unexpected
}

public enum OmpModelsProviderChangeKind
{
    None,
    Added,
    Updated
}

public sealed record OmpConfigurationReplacementRequest(
    string OmpRootDirectory,
    string TargetProvider,
    int GatewayPort);

public sealed record OmpConfigurationReplacementChange(
    string RolePath,
    string OriginalProvider,
    string TargetProvider,
    string ModelId,
    string OriginalReference,
    string NewReference)
{
    public string Path => RolePath;
}

public sealed record OmpConfigurationReplacementPreview(
    bool Succeeded,
    OmpConfigurationReplacementRequest Request,
    IReadOnlyList<OmpConfigurationReplacementChange> Changes,
    OmpModelsProviderChangeKind ModelsChangeKind = OmpModelsProviderChangeKind.None,
    string? ConfigurationVersion = null,
    string? ModelsVersion = null,
    OmpConfigurationReplacementFailureKind FailureKind = OmpConfigurationReplacementFailureKind.None,
    string? ErrorMessage = null)
{
    public bool IsValid => Succeeded;
    public bool HasRouteChanges => Changes.Count != 0;
    public bool HasChanges => HasRouteChanges || ModelsChangeKind != OmpModelsProviderChangeKind.None;
    public bool IsNoOp => Succeeded && !HasChanges;
}

public sealed record OmpConfigurationReplacementResult(
    bool Succeeded,
    OmpConfigurationReplacementPreview Preview,
    bool ConfigurationChanged = false,
    bool ModelsChanged = false,
    string? ConfigurationBackupPath = null,
    string? ModelsBackupPath = null,
    bool BackupRetentionSucceeded = true,
    OmpConfigurationReplacementFailureKind FailureKind = OmpConfigurationReplacementFailureKind.None,
    string? ErrorMessage = null)
{
    public bool Success => Succeeded;
}

public interface IOmpConfigurationReplacementPort
{
    Task<OmpConfigurationReplacementPreview> PreviewAsync(
        OmpConfigurationReplacementRequest request,
        CancellationToken cancellationToken = default);

    Task<OmpConfigurationReplacementResult> ExecuteAsync(
        OmpConfigurationReplacementRequest request,
        OmpConfigurationReplacementPreview preview,
        CancellationToken cancellationToken = default);
}

public sealed class OmpConfigurationReplacementUseCase(
    IOmpConfigurationReplacementPort replacementPort)
{
    public async Task<OmpConfigurationReplacementPreview> PreviewAsync(
        LocalAppSettings settings,
        string targetProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var request = BuildRequest(settings, targetProvider);
        if (!OmpConfigurationReplacementTargets.IsSupported(targetProvider))
            return InvalidPreview(request, OmpConfigurationReplacementFailureKind.InvalidTarget, "不支持的 OMP Provider 目标。");
        if (request.GatewayPort is < 1 or > 65535)
            return InvalidPreview(request, OmpConfigurationReplacementFailureKind.InvalidGatewayPort, "本地网关端口无效。");
        if (string.IsNullOrWhiteSpace(request.OmpRootDirectory))
            return InvalidPreview(request, OmpConfigurationReplacementFailureKind.ConfigurationMissing, "未配置 OMP 根目录。");

        return await replacementPort.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OmpConfigurationReplacementResult> ExecuteAsync(
        LocalAppSettings settings,
        OmpConfigurationReplacementPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(preview);
        var request = BuildRequest(settings, preview.Request.TargetProvider);
        if (!OmpConfigurationReplacementTargets.IsSupported(request.TargetProvider)
            || !RequestsMatch(request, preview.Request))
        {
            return new(
                false,
                preview,
                FailureKind: OmpConfigurationReplacementFailureKind.StalePreview,
                ErrorMessage: "配置预览已失效，请重新预览后重试。");
        }
        if (!preview.Succeeded)
        {
            return new(
                false,
                preview,
                FailureKind: preview.FailureKind,
                ErrorMessage: preview.ErrorMessage);
        }

        return await replacementPort.ExecuteAsync(request, preview, cancellationToken).ConfigureAwait(false);
    }

    private static OmpConfigurationReplacementRequest BuildRequest(LocalAppSettings settings, string? targetProvider) =>
        new(
            settings.OmpRootDirectory ?? string.Empty,
            targetProvider ?? string.Empty,
            settings.CurrentGatewayPort);

    private static bool RequestsMatch(
        OmpConfigurationReplacementRequest current,
        OmpConfigurationReplacementRequest preview) =>
        string.Equals(current.OmpRootDirectory, preview.OmpRootDirectory, StringComparison.Ordinal)
        && string.Equals(current.TargetProvider, preview.TargetProvider, StringComparison.Ordinal)
        && current.GatewayPort == preview.GatewayPort;

    private static OmpConfigurationReplacementPreview InvalidPreview(
        OmpConfigurationReplacementRequest request,
        OmpConfigurationReplacementFailureKind failureKind,
        string message) =>
        new(false, request, Array.Empty<OmpConfigurationReplacementChange>(), FailureKind: failureKind, ErrorMessage: message);
}
