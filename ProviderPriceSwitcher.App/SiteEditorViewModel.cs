using System.Globalization;
using System.Windows.Input;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.App;

public enum SiteEditorProbeState { Idle, Probing, Succeeded, Canceled, Failed }

public sealed class SiteEditorViewModel : ObservableObject
{
    private readonly PricingProbeUseCase _probe;
    private readonly ISiteAccessCredentialStore _credentials;
    private readonly IInferenceApiKeyStore _inferenceKeys;
    private readonly IUserNotificationService _notifications;
    private readonly LocalAppSettings _settings;
    private readonly SiteConfiguration? _original;
    private CancellationTokenSource? _cancel;
    private Task? _probeTask;
    private PricingAdapterDescriptor? _descriptor;
    private string? _authenticationMode;
    private bool _isProbing;
    private SiteEditorProbeState _probeState;
    private string _probeMessage = string.Empty;
    private string _providerId = string.Empty, _displayName = string.Empty, _baseUrl = string.Empty, _model = string.Empty, _group = string.Empty, _ratio = string.Empty;

    public SiteEditorViewModel(PricingProbeUseCase probe, IPricingAdapterRegistry registry, ISiteAccessCredentialStore credentials, IUserNotificationService notifications, LocalAppSettings settings, SiteConfiguration? original = null, IInferenceApiKeyStore? inferenceKeys = null)
    {
        _probe = probe; _credentials = credentials; _inferenceKeys = inferenceKeys ?? new NullInferenceApiKeyStore(); _notifications = notifications; _settings = settings; _original = original; Descriptors = registry.Descriptors;
        Descriptor = Descriptors.FirstOrDefault(x => string.Equals(x.SiteType, original?.SiteType, StringComparison.Ordinal)) ?? (Descriptors.Count > 0 ? Descriptors[0] : null); AuthenticationMode = original?.AuthenticationMode ?? (Descriptor?.AuthenticationModes.Count > 0 ? Descriptor.AuthenticationModes[0] : null);
        ProviderId = original?.ProviderId ?? string.Empty; DisplayName = original?.DisplayName ?? string.Empty; BaseUrl = original?.BaseUrl.ToString() ?? string.Empty; ConfigurationApiAddress = original?.ConfigurationApiAddress ?? "/keys"; Model = original?.Model ?? settings.Model; CurrentGroup = original?.CurrentGroup ?? string.Empty; CurrentGroupRatio = original?.CurrentGroupRatio?.ToString(CultureInfo.InvariantCulture) ?? string.Empty; Currency = original?.Currency ?? string.Empty; CnyConversionRate = original?.CnyConversionRate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty; UpdateCredentialStatus(); UpdateInferenceKeyStatus();
        ProbeCommand = new AsyncCommand(ProbeAsync, HandleError, () => CanProbe); CancelProbeCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsProbing); SaveCommand = new RelayCommand(Save, () => CanSave);
    }
    public IReadOnlyList<PricingAdapterDescriptor> Descriptors { get; }
    public string ProviderId { get => _providerId; set { if (SetProperty(ref _providerId, value)) { UpdateCredentialStatus(); RaiseCommands(); } } }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public string BaseUrl { get => _baseUrl; set { if (SetProperty(ref _baseUrl, value)) RaiseCommands(); } }
    public string ConfigurationApiAddress { get; set; } = "/keys";
    public string Model { get => _model; set { if (SetProperty(ref _model, value)) RaiseCommands(); } }
    public string CurrentGroup { get => _group; set { if (SetProperty(ref _group, value)) RaiseCommands(); } }
    public string CurrentGroupRatio { get => _ratio; set { if (SetProperty(ref _ratio, value)) RaiseCommands(); } }
    public string Currency { get; set; } = string.Empty;
    public string CnyConversionRate { get; set; } = string.Empty;
    public PricingAdapterDescriptor? Descriptor { get => _descriptor; set { if (!SetProperty(ref _descriptor, value)) return; AuthenticationMode = value?.AuthenticationModes.Count > 0 ? value.AuthenticationModes[0] : null; OnPropertyChanged(nameof(AuthenticationModes)); OnPropertyChanged(nameof(CredentialVisible)); UpdateCredentialStatus(); RaiseCommands(); } }
    public IReadOnlyList<string> AuthenticationModes => Descriptor?.AuthenticationModes ?? [];
    public string? AuthenticationMode { get => _authenticationMode; set => SetProperty(ref _authenticationMode, value); }
    public bool CredentialVisible => Descriptor?.RequiresCredential == true;
    public bool IsProbing { get => _isProbing; private set { if (SetProperty(ref _isProbing, value)) { OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanProbe)); RaiseCommands(); } } }
    public bool CanSave => !IsProbing && TryBuild(out _);
    public bool CanProbe => !IsProbing && TryBuild(out _);
    public SiteEditorProbeState ProbeState { get => _probeState; private set => SetProperty(ref _probeState, value); }
    public string ProbeMessage { get => _probeMessage; private set => SetProperty(ref _probeMessage, value); }
    public SiteConfiguration? SavedSite { get; private set; }
    public AsyncCommand ProbeCommand { get; }
    public RelayCommand CancelProbeCommand { get; }
    public RelayCommand SaveCommand { get; }
    public event EventHandler? Saved;
    public SiteCredentialSummary CredentialSummary { get; private set; } = new() { ProviderId = string.Empty, Status = SiteCredentialStatus.NotConfigured };
    public InferenceApiKeySummary? InferenceKeySummary { get; private set; }
    public string InferenceBoundGroup { get; set; } = string.Empty;
    public bool SaveInferenceKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(InferenceBoundGroup)) return false;
        new InferenceApiKeyUseCase(_inferenceKeys).Save(ProviderId, apiKey, InferenceBoundGroup);
        UpdateInferenceKeyStatus();
        return true;
    }
    public bool DeleteInferenceKey() { if (string.IsNullOrWhiteSpace(ProviderId)) return false; new InferenceApiKeyUseCase(_inferenceKeys).Delete(ProviderId); UpdateInferenceKeyStatus(); return true; }
    public void UpdateInferenceKeyStatus() { InferenceKeySummary = string.IsNullOrWhiteSpace(ProviderId) ? null : _inferenceKeys.GetSummary(ProviderId); InferenceBoundGroup = InferenceKeySummary?.BoundGroup ?? string.Empty; OnPropertyChanged(nameof(InferenceKeySummary)); OnPropertyChanged(nameof(InferenceBoundGroup)); }
    public void UpdateCredentialStatus() { CredentialSummary = string.IsNullOrWhiteSpace(ProviderId) ? new SiteCredentialSummary { ProviderId = string.Empty, Status = SiteCredentialStatus.NotConfigured, StatusText = "请先填写 ProviderId。" } : _credentials.GetSummary(ProviderId.Trim()); OnPropertyChanged(nameof(CredentialSummary)); }
    public bool SaveCredential(string token, string cookie) { if (!CredentialVisible || string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(cookie)) { _notifications.ShowWarning("访问令牌和 Cookie 不能为空。", "校验失败"); return false; } _credentials.SaveCredential(new SiteCredentialRecord { ProviderId = ProviderId.Trim(), SiteType = Descriptor!.SiteType, AuthorizationScheme = "Bearer", AccessToken = token.Trim(), CookieHeader = cookie.Trim() }); UpdateCredentialStatus(); return true; }
    public bool ClearCredential() { if (!CredentialVisible || string.IsNullOrWhiteSpace(ProviderId) || !_notifications.Confirm("确定清除本地凭据吗？", "清除凭据")) return false; _credentials.ClearCredential(ProviderId.Trim()); UpdateCredentialStatus(); return true; }
    public async Task CloseAsync() { _cancel?.Cancel(); if (_probeTask is not null) await _probeTask.ConfigureAwait(true); }
    private async Task ProbeAsync()
    {
        _probeTask = ProbeCoreAsync(); await _probeTask; _probeTask = null;
    }
    private async Task ProbeCoreAsync()
    {
        if (!TryBuild(out var site)) return; IsProbing = true; ProbeState = SiteEditorProbeState.Probing; ProbeMessage = "正在查询…"; _cancel = new CancellationTokenSource();
        try { var result = await _probe.ExecuteAsync(site, _settings.RequestTimeoutSeconds, _cancel.Token); ProbeState = SiteEditorProbeState.Succeeded; ProbeMessage = $"成功：当前组倍率 {result.Snapshot.CurrentGroupRatio:0.####}；最低组 {result.MinimumValidGroup}（{result.MinimumGroupRatio:0.####}）。"; }
        catch (OperationCanceledException) { ProbeState = SiteEditorProbeState.Canceled; ProbeMessage = "已取消价格查询。"; }
        catch (PricingAdapterException ex) { ProbeState = SiteEditorProbeState.Failed; ProbeMessage = UserErrorMessages.ForProbeFailure(ex.Failure); }
        catch { ProbeState = SiteEditorProbeState.Failed; ProbeMessage = UserErrorMessages.Unexpected; _notifications.ShowError(UserErrorMessages.Unexpected, "价格查询"); }
        finally { _cancel.Dispose(); _cancel = null; IsProbing = false; }
    }
    private void Save() { if (TryBuild(out var site)) { SavedSite = site; Saved?.Invoke(this, EventArgs.Empty); } }
    private bool TryBuild(out SiteConfiguration site) { site = null!; if (string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(CurrentGroup) || Descriptor is null || !Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !decimal.TryParse(CurrentGroupRatio, NumberStyles.Number, CultureInfo.InvariantCulture, out var ratio) || ratio <= 0) return false; decimal? conversion = decimal.TryParse(CnyConversionRate, NumberStyles.Number, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : null; uri = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"); site = new SiteConfiguration { ProviderId = ProviderId.Trim(), DisplayName = DisplayName.Trim(), ConfigurationApiAddress = string.IsNullOrWhiteSpace(ConfigurationApiAddress) ? "/keys" : ConfigurationApiAddress.Trim(), BaseUrl = uri, SiteType = Descriptor.SiteType, Enabled = _original?.Enabled ?? true, Model = Model.Trim(), CurrentGroup = CurrentGroup.Trim(), CurrentGroupRatio = ratio, GroupRatioSource = "手动", AuthenticationMode = AuthenticationMode ?? "无需认证", Currency = Currency.Trim(), CnyConversionRate = conversion, ConfigurationKey = SiteConfigurationKey.Create(ProviderId.Trim(), Descriptor.SiteType, uri, Model.Trim(), CurrentGroup.Trim()) }; return true; }
    private void HandleError(Exception ex) { if (ex is not OperationCanceledException) _notifications.ShowError(UserErrorMessages.Unexpected, "站点编辑"); }
    private void RaiseCommands() { ProbeCommand?.RaiseCanExecuteChanged(); SaveCommand?.RaiseCanExecuteChanged(); CancelProbeCommand?.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanProbe)); }
}

file sealed class NullInferenceApiKeyStore : IInferenceApiKeyStore
{
    public InferenceApiKeyRecord? Load(string providerId) => null;
    public void Save(InferenceApiKeyRecord record) => throw new InvalidOperationException("推理 key 存储未装配。");
    public void Clear(string providerId) { }
    public InferenceApiKeySummary? GetSummary(string providerId) => null;
}
