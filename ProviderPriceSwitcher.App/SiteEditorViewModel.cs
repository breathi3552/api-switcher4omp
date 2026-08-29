using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.App;

public sealed class SiteEditorViewModel : ObservableObject
{
    private readonly ISiteAccessCredentialStore _credentials;
    private readonly IInferenceApiKeyUseCase _inferenceKeyUseCase;
    private readonly IUserNotificationService _notifications;
    private readonly LocalAppSettings _settings;
    private string? _lastKeyActionMessage;

    public SiteEditorViewModel(
        SupplierEditorSession session,
        ISiteAccessCredentialStore credentials,
        IUserNotificationService notifications,
        LocalAppSettings settings,
        IInferenceApiKeyUseCase? inferenceKeyUseCase = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _credentials = credentials;
        _inferenceKeyUseCase = inferenceKeyUseCase ?? new NullInferenceApiKeyUseCase();
        _notifications = notifications;
        _settings = settings;

        Session.PropertyChanged += OnSessionPropertyChanged;

        UpdateCredentialStatus();
        UpdateInferenceKeyStatus();

        SaveCommand = new RelayCommand(Save, () => CanSave);
    }

    public SiteEditorViewModel(
        PricingProbeUseCase probe,
        IPricingAdapterRegistry registry,
        ISiteAccessCredentialStore credentials,
        IUserNotificationService notifications,
        LocalAppSettings settings,
        SiteConfiguration? original = null,
        IInferenceApiKeyUseCase? inferenceKeyUseCase = null)
        : this(new SupplierEditorSession(probe, registry, settings, original, notifications), credentials, notifications, settings, inferenceKeyUseCase)
    {
    }
    public SupplierEditorSession Session { get; }

    public IReadOnlyList<PricingAdapterDescriptor> Descriptors => Session.Descriptors;

    public string ProviderId
    {
        get => Session.ProviderId;
        set => Session.ProviderId = value;
    }

    public string DisplayName
    {
        get => Session.DisplayName;
        set => Session.DisplayName = value;
    }

    public string BaseUrl
    {
        get => Session.BaseUrl;
        set => Session.BaseUrl = value;
    }

    public string ConfigurationApiAddress
    {
        get => Session.ConfigurationApiAddress;
        set => Session.ConfigurationApiAddress = value;
    }

    public string CurrentGroup
    {
        get => Session.CurrentGroup;
        set
        {
            if (Session.CurrentGroup == value) return;
            Session.CurrentGroup = value;
        }
    }

    public string Model
    {
        get => Session.Model;
        set => Session.Model = value;
    }

    public string CurrentGroupRatio
    {
        get => Session.CurrentGroupRatio;
        set => Session.CurrentGroupRatio = value;
    }

    public string Currency
    {
        get => Session.Currency;
        set => Session.Currency = value;
    }

    public string CnyConversionRate
    {
        get => Session.CnyConversionRate;
        set => Session.CnyConversionRate = value;
    }

    public PricingAdapterDescriptor? Descriptor
    {
        get => Session.Descriptor;
        set => Session.Descriptor = value;
    }

    public IReadOnlyList<string> AuthenticationModes => Session.AuthenticationModes;

    public string? AuthenticationMode
    {
        get => Session.AuthenticationMode;
        set => Session.AuthenticationMode = value;
    }

    public bool CredentialVisible => Session.CredentialVisible;

    public bool IsProbing => Session.IsProbing;
    public bool CanSave => Session.CanSave;
    public bool CanProbe => Session.CanProbe;
    public SupplierEditorProbeState ProbeState => Session.ProbeState;
    public string ProbeMessage => Session.ProbeMessage;
    public ObservableCollection<string> GroupOptions => Session.GroupOptions;
    public AsyncCommand ProbeCommand => Session.ProbeCommand;
    public RelayCommand CancelProbeCommand => Session.CancelProbeCommand;

    public SiteConfiguration? SavedSite { get; private set; }
    public RelayCommand SaveCommand { get; }
    public event EventHandler? Saved;
    public SiteCredentialSummary CredentialSummary { get; private set; } = new() { ProviderId = string.Empty, Status = SiteCredentialStatus.NotConfigured };
    public InferenceApiKeySummary? InferenceKeySummary { get; private set; }
    public string InferenceKeyDisplayText => InferenceKeySummary?.MaskedKey ?? "未配置";
    public string KeyActionMessage { get => _lastKeyActionMessage ?? string.Empty; private set => SetProperty(ref _lastKeyActionMessage, value); }

    public bool SaveInferenceKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(CurrentGroup))
        {
            KeyActionMessage = "请先填写当前分组和 API key。";
            return false;
        }
        _inferenceKeyUseCase.Save(ProviderId, apiKey, CurrentGroup);
        UpdateInferenceKeyStatus();
        KeyActionMessage = "API key 已更新并安全保存。";
        return true;
    }

    public void ReportInferenceKeyDeleteFailure() => KeyActionMessage = "API key 删除失败，请重试。";

    public async Task<bool> DeleteInferenceKeyAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ProviderId)) return false;
        if (!_notifications.Confirm("确定删除当前供应商的模型推理 API key 吗？", "删除 API key")) return false;
        await _inferenceKeyUseCase.DeleteAsync(ProviderId, cancellationToken);
        UpdateInferenceKeyStatus();
        KeyActionMessage = "API key 已删除。";
        return true;
    }

    public void UpdateInferenceKeyStatus()
    {
        InferenceKeySummary = string.IsNullOrWhiteSpace(ProviderId) ? null : _inferenceKeyUseCase.GetSummary(ProviderId);
        OnPropertyChanged(nameof(InferenceKeySummary));
        OnPropertyChanged(nameof(InferenceKeyDisplayText));
    }

    public bool SaveCredential(string token, string cookie)
    {
        if (!CredentialVisible || string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(cookie))
        {
            _notifications.ShowWarning("访问令牌和 Cookie 不能为空。", "校验失败");
            return false;
        }
        _credentials.SaveCredential(new SiteCredentialRecord
        {
            ProviderId = ProviderId.Trim(),
            SiteType = Descriptor!.SiteType,
            AuthorizationScheme = "Bearer",
            AccessToken = token.Trim(),
            CookieHeader = cookie.Trim()
        });
        UpdateCredentialStatus();
        return true;
    }

    public bool ClearCredential()
    {
        if (!CredentialVisible || string.IsNullOrWhiteSpace(ProviderId) || !_notifications.Confirm("确定清除本地凭据吗？", "清除凭据"))
            return false;
        _credentials.ClearCredential(ProviderId.Trim());
        UpdateCredentialStatus();
        return true;
    }

    public void UpdateCredentialStatus()
    {
        CredentialSummary = string.IsNullOrWhiteSpace(ProviderId)
            ? new SiteCredentialSummary { ProviderId = string.Empty, Status = SiteCredentialStatus.NotConfigured, StatusText = "请先填写 ProviderId。" }
            : _credentials.GetSummary(ProviderId.Trim());
        OnPropertyChanged(nameof(CredentialSummary));
    }

    public Task CloseAsync() => Session.CloseAsync();

    private void Save()
    {
        if (TryBuild(out var site))
        {
            SavedSite = site;
            Saved?.Invoke(this, EventArgs.Empty);
        }
    }
    public bool TryBuild(out SiteConfiguration site) => Session.TryBuild(out site);
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SupplierEditorSession.ProviderId):
                UpdateCredentialStatus();
                UpdateInferenceKeyStatus();
                OnPropertyChanged(nameof(ProviderId));
                RaiseCommands();
                break;
            case nameof(SupplierEditorSession.Descriptor):
                UpdateCredentialStatus();
                OnPropertyChanged(nameof(Descriptor));
                OnPropertyChanged(nameof(AuthenticationModes));
                OnPropertyChanged(nameof(CredentialVisible));
                RaiseCommands();
                break;
            case nameof(SupplierEditorSession.AuthenticationMode):
                OnPropertyChanged(nameof(AuthenticationMode));
                RaiseCommands();
                break;
            case nameof(SupplierEditorSession.CurrentGroup):
                OnPropertyChanged(nameof(CurrentGroup));
                RaiseCommands();
                break;
            case nameof(SupplierEditorSession.CurrentGroupRatio):
                OnPropertyChanged(nameof(CurrentGroupRatio));
                RaiseCommands();
                break;
            case nameof(SupplierEditorSession.IsProbing):
                OnPropertyChanged(nameof(IsProbing));
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(CanProbe));
                RaiseCommands();
                break;
            case nameof(SupplierEditorSession.ProbeState):
                OnPropertyChanged(nameof(ProbeState));
                break;
            case nameof(SupplierEditorSession.ProbeMessage):
                OnPropertyChanged(nameof(ProbeMessage));
                break;
            case nameof(SupplierEditorSession.CanSave):
                OnPropertyChanged(nameof(CanSave));
                RaiseCommands();
                break;
            case nameof(SupplierEditorSession.CanProbe):
                OnPropertyChanged(nameof(CanProbe));
                break;
            default:
                if (!string.IsNullOrEmpty(e.PropertyName))
                    OnPropertyChanged(e.PropertyName);
                break;
        }
    }

    private void RaiseCommands()
    {
        SaveCommand?.RaiseCanExecuteChanged();
    }
}

file sealed class NullInferenceApiKeyUseCase : IInferenceApiKeyUseCase
{
    public InferenceApiKeySummary? GetSummary(string providerId) => null;
    public InferenceApiKeySummary Save(string providerId, string apiKey, string boundGroup) => throw new InvalidOperationException("推理 key 用例未装配。");
    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
