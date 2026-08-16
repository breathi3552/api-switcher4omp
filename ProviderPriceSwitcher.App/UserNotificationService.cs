using System.Windows;

namespace ProviderPriceSwitcher.App;

public interface IUserNotificationService
{
    void ShowWarning(string message, string title);
    void ShowError(string message, string title);
    bool Confirm(string message, string title);
}

public static class UserErrorMessages
{
    public const string Unexpected = "操作失败，请查看日志或稍后重试。";
    public const string ConfigurationReadFailed = "暂时无法读取 OMP 配置，请检查配置后重试。";
    public const string ApplicationStartupFailed = "应用启动失败：无法加载本地设置或初始化服务。";
    public const string GatewayPortUnavailable = "本地网关目标端口已被占用，请在设置中更改端口后重新启动应用。";
    public const string SettingsRecoveryFailed = "设置未保存，请检查文件权限后重新启动应用。";
    public const string Unhandled = "发生未处理错误，请查看日志或重新启动应用。";

    public static string ForPricingFailure(ProviderPriceSwitcher.Application.PricingRefreshFailureKind? kind) => kind switch
    {
        ProviderPriceSwitcher.Application.PricingRefreshFailureKind.UnknownSiteType => "当前站点类型不受支持。",
        ProviderPriceSwitcher.Application.PricingRefreshFailureKind.Timeout => "请求超时，请稍后重试。",
        ProviderPriceSwitcher.Application.PricingRefreshFailureKind.Authentication => "需要重新绑定凭据。",
        ProviderPriceSwitcher.Application.PricingRefreshFailureKind.Adapter => "价格服务返回无效结果，请稍后重试。",
        ProviderPriceSwitcher.Application.PricingRefreshFailureKind.Unexpected => Unexpected,
        _ => Unexpected
    };

    public static string ForOmpLaunchStatus(ProviderPriceSwitcher.Application.OmpLaunchStatus status) => status switch
    {
        ProviderPriceSwitcher.Application.OmpLaunchStatus.Started => "已创建新的 OMP 实例；当前供应商未改变。",
        ProviderPriceSwitcher.Application.OmpLaunchStatus.SettingsPersistenceFailed => "OMP 未启动：无法保存工作目录设置。",
        ProviderPriceSwitcher.Application.OmpLaunchStatus.LaunchFailed => "OMP 启动失败；当前供应商未改变。",
        _ => Unexpected
    };

    public static string ForOmpStartupOutcome(ProviderPriceSwitcher.Application.OmpStartupOutcome outcome) => outcome.Status switch
    {
        ProviderPriceSwitcher.Application.OmpStartupStatus.SettingsPersistenceFailed => "应用启动失败：无法保存 OMP 端口设置。",
        _ => ApplicationStartupFailed
    };

    public static string ForOmpLaunchStatus(ProviderPriceSwitcher.Application.OmpLaunchOutcome outcome) =>
        ForOmpLaunchStatus(outcome.Status);

    public static string ForApplyRouteStatus(ProviderPriceSwitcher.Application.ApplyActiveRouteStatus status) => status switch
    {
        ProviderPriceSwitcher.Application.ApplyActiveRouteStatus.Applied => "供应商已应用；只影响后续新请求。",
        ProviderPriceSwitcher.Application.ApplyActiveRouteStatus.NoActiveRoute => "当前没有已应用供应商。",
        ProviderPriceSwitcher.Application.ApplyActiveRouteStatus.Cleared => "当前供应商已清除。",
        ProviderPriceSwitcher.Application.ApplyActiveRouteStatus.ProviderNotFound => "目标供应商不存在。",
        ProviderPriceSwitcher.Application.ApplyActiveRouteStatus.ProviderDisabled => "已禁用的供应商不能应用。",
        ProviderPriceSwitcher.Application.ApplyActiveRouteStatus.InferenceKeyMissing => "目标供应商没有模型推理 API key。",
        ProviderPriceSwitcher.Application.ApplyActiveRouteStatus.InferenceKeyUnavailable => "暂时无法读取目标供应商的模型推理 API key。",
        ProviderPriceSwitcher.Application.ApplyActiveRouteStatus.BindingMismatch => "模型推理 API key 与当前分组不一致。",
        ProviderPriceSwitcher.Application.ApplyActiveRouteStatus.SidecarFailed => "当前供应商未应用，私有路由服务不可用。",
        ProviderPriceSwitcher.Application.ApplyActiveRouteStatus.PersistenceFailed => "当前供应商未提交到本地设置，请重试。",
        ProviderPriceSwitcher.Application.ApplyActiveRouteStatus.RollbackFailed => "当前供应商回滚失败，当前路由状态需要重新检查。",
        _ => Unexpected
    };

    public static string ForProbeFailure(ProviderPriceSwitcher.Application.PricingAdapterFailure failure) => failure switch
    {
        ProviderPriceSwitcher.Application.PricingAdapterFailure.Authentication => "需要重新绑定凭据。",
        ProviderPriceSwitcher.Application.PricingAdapterFailure.Request => "网络请求失败，请稍后重试。",
        ProviderPriceSwitcher.Application.PricingAdapterFailure.Timeout => "请求超时，请稍后重试。",
        ProviderPriceSwitcher.Application.PricingAdapterFailure.InvalidResponse => "价格服务返回无效结果，请检查站点配置。",
        _ => Unexpected
    };
}

public sealed class WpfUserNotificationService : IUserNotificationService
{
    public void ShowWarning(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
