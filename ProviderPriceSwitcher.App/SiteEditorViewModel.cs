using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.App;

public sealed class SiteEditorViewModel : ObservableObject
{
    private readonly IPricingAdapterRegistry _registry;
    private PricingAdapterDescriptor? _descriptor;
    private string? _authenticationMode;
    private bool _isProbing;

    public SiteEditorViewModel(IPricingAdapterRegistry registry, SiteConfiguration? original = null)
    {
        _registry = registry;
        Descriptors = registry.Descriptors;
        Descriptor = Descriptors.FirstOrDefault(x => string.Equals(x.SiteType, original?.SiteType, StringComparison.Ordinal))
            ?? (Descriptors.Count > 0 ? Descriptors[0] : null);
        AuthenticationMode = original is not null && string.Equals(original.SiteType, Descriptor?.SiteType, StringComparison.Ordinal)
            ? original.AuthenticationMode
            : (Descriptor?.AuthenticationModes.Count > 0 ? Descriptor.AuthenticationModes[0] : null);
        ProviderId = original?.ProviderId ?? string.Empty;
    }

    public IReadOnlyList<PricingAdapterDescriptor> Descriptors { get; }
    public string ProviderId { get; set; }
    public PricingAdapterDescriptor? Descriptor
    {
        get => _descriptor;
        set
        {
            if (!SetProperty(ref _descriptor, value)) return;
            AuthenticationMode = value?.AuthenticationModes.Count > 0 ? value.AuthenticationModes[0] : null;
            OnPropertyChanged(nameof(AuthenticationModes));
            OnPropertyChanged(nameof(CredentialVisible));
        }
    }
    public IReadOnlyList<string> AuthenticationModes => Descriptor?.AuthenticationModes ?? [];
    public string? AuthenticationMode { get => _authenticationMode; set => SetProperty(ref _authenticationMode, value); }
    public bool CredentialVisible => Descriptor?.RequiresCredential == true;
    public bool IsProbing { get => _isProbing; set { if (SetProperty(ref _isProbing, value)) OnPropertyChanged(nameof(CanSave)); } }
    public bool CanSave => !IsProbing;

    public SiteConfiguration ApplySiteType(SiteConfiguration site) => site with
    {
        SiteType = Descriptor?.SiteType ?? throw new InvalidOperationException("未选择站点类型。"),
        AuthenticationMode = AuthenticationMode ?? string.Empty
    };
}
