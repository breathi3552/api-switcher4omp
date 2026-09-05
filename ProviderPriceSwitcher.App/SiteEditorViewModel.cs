using System.Collections.ObjectModel;
using System.ComponentModel;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.App;

public sealed class SiteEditorViewModel : ObservableObject
{
    public SiteEditorViewModel(SupplierEditorSession session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Session.PropertyChanged += OnSessionPropertyChanged;
        SaveCommand = new RelayCommand(Save, () => CanSave);
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
        set => Session.CurrentGroup = value;
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

    public void UpdateCredentialStatus() => Session.UpdateCredentialStatus();

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
        if (string.IsNullOrEmpty(e.PropertyName)) return;
        OnPropertyChanged(e.PropertyName);
        if (e.PropertyName == nameof(SupplierEditorSession.CanSave))
        {
            SaveCommand.RaiseCanExecuteChanged();
        }
    }
}
