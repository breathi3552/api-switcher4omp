using System.Collections.ObjectModel;
using System.ComponentModel;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.App;

public sealed class SiteEditorViewModel : ObservableObject
{
    private readonly IUserNotificationService _notifications;
    private readonly LocalAppSettings _settings;

    public SiteEditorViewModel(
        SupplierEditorSession session,
        IUserNotificationService notifications,
        LocalAppSettings settings)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _notifications = notifications;
        _settings = settings;

        Session.PropertyChanged += OnSessionPropertyChanged;

        SaveCommand = new RelayCommand(Save, () => CanSave);
    }

    public SiteEditorViewModel(
        PricingProbeUseCase probe,
        IPricingAdapterRegistry registry,
        ISiteAccessCredentialStore credentials,
        IUserNotificationService notifications,
        LocalAppSettings settings,
        SiteConfiguration? original = null)
        : this(new SupplierEditorSession(probe, registry, credentials, settings, original, notifications), notifications, settings)
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
    public SiteCredentialSummary CredentialSummary => Session.CredentialSummary;
    public InferenceApiKeySummary? InferenceKeySummary => Session.InferenceKeySummary;
    public string InferenceKeyDisplayText => Session.InferenceKeyDisplayText;
    public string KeyActionMessage => Session.KeyActionMessage;
    public bool IsDeletingInferenceKey => Session.IsDeletingInferenceKey;

    public bool SaveInferenceKey(string apiKey) => Session.SaveInferenceKey(apiKey);
    public void ReportInferenceKeyDeleteFailure() => Session.ReportInferenceKeyDeleteFailure();
    public Task<bool> DeleteInferenceKeyAsync(CancellationToken cancellationToken = default) => Session.DeleteInferenceKeyAsync(cancellationToken);
    public void UpdateInferenceKeyStatus() => Session.UpdateInferenceKeyStatus();

    public bool SaveCredential(string token, string cookie) => Session.SaveCredential(token, cookie);

    public bool ClearCredential() => Session.ClearCredential();

    public void UpdateCredentialStatus()
    {
        Session.UpdateCredentialStatus();
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
                OnPropertyChanged(nameof(ProviderId));
                OnPropertyChanged(nameof(CredentialSummary));
                OnPropertyChanged(nameof(InferenceKeySummary));
                OnPropertyChanged(nameof(InferenceKeyDisplayText));
                RaiseCommands();
                break;
            case nameof(SupplierEditorSession.InferenceKeySummary):
                OnPropertyChanged(nameof(InferenceKeySummary));
                OnPropertyChanged(nameof(InferenceKeyDisplayText));
                break;
            case nameof(SupplierEditorSession.InferenceKeyDisplayText):
                OnPropertyChanged(nameof(InferenceKeyDisplayText));
                break;
            case nameof(SupplierEditorSession.KeyActionMessage):
                OnPropertyChanged(nameof(KeyActionMessage));
                break;
            case nameof(SupplierEditorSession.IsDeletingInferenceKey):
                OnPropertyChanged(nameof(IsDeletingInferenceKey));
                break;
            case nameof(SupplierEditorSession.Descriptor):
                OnPropertyChanged(nameof(Descriptor));
                OnPropertyChanged(nameof(AuthenticationModes));
                OnPropertyChanged(nameof(CredentialVisible));
                OnPropertyChanged(nameof(CredentialSummary));
                RaiseCommands();
                break;
            case nameof(SupplierEditorSession.CredentialSummary):
                OnPropertyChanged(nameof(CredentialSummary));
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
