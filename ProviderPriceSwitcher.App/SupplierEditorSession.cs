using System.Globalization;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.App;

public sealed class SupplierEditorSession : ObservableObject
{
    private PricingAdapterDescriptor? _descriptor;
    private string? _authenticationMode;
    private string _providerId = string.Empty;
    private string _displayName = string.Empty;
    private string _baseUrl = string.Empty;
    private string _configurationApiAddress = string.Empty;
    private string _model = string.Empty;
    private string _currentGroup = string.Empty;
    private string _currentGroupRatio = string.Empty;
    private string _currency = string.Empty;
    private string _cnyConversionRate = string.Empty;

    public SupplierEditorSession(IPricingAdapterRegistry registry, LocalAppSettings settings, SiteConfiguration? original = null)
        : this(registry?.Descriptors ?? [], settings?.Model ?? string.Empty, original)
    {
    }

    public SupplierEditorSession(IReadOnlyList<PricingAdapterDescriptor> descriptors, string defaultModel, SiteConfiguration? original = null)
    {
        Original = original;
        Descriptors = descriptors ?? [];
        _descriptor = Descriptors.FirstOrDefault(x => string.Equals(x.SiteType, original?.SiteType, StringComparison.Ordinal))
            ?? (Descriptors.Count > 0 ? Descriptors[0] : null);
        _authenticationMode = original?.AuthenticationMode
            ?? (_descriptor?.AuthenticationModes.Count > 0 ? _descriptor.AuthenticationModes[0] : null);
        _providerId = original?.ProviderId ?? string.Empty;
        _displayName = original?.DisplayName ?? string.Empty;
        _baseUrl = original?.BaseUrl?.ToString() ?? string.Empty;
        _configurationApiAddress = string.Equals(original?.ConfigurationApiAddress, "/keys", StringComparison.Ordinal)
            ? string.Empty
            : original?.ConfigurationApiAddress ?? string.Empty;
        _model = original?.Model ?? defaultModel ?? string.Empty;
        _currentGroup = original?.CurrentGroup ?? string.Empty;
        _currentGroupRatio = original?.CurrentGroupRatio?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _currency = original?.Currency ?? string.Empty;
        _cnyConversionRate = original?.CnyConversionRate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public SiteConfiguration? Original { get; }
    public IReadOnlyList<PricingAdapterDescriptor> Descriptors { get; }

    public PricingAdapterDescriptor? Descriptor
    {
        get => _descriptor;
        set
        {
            if (!SetProperty(ref _descriptor, value)) return;
            AuthenticationMode = value?.AuthenticationModes.Count > 0 ? value.AuthenticationModes[0] : null;
            OnPropertyChanged(nameof(AuthenticationModes));
            OnPropertyChanged(nameof(CredentialVisible));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public IReadOnlyList<string> AuthenticationModes => Descriptor?.AuthenticationModes ?? [];

    public string? AuthenticationMode
    {
        get => _authenticationMode;
        set
        {
            if (SetProperty(ref _authenticationMode, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool CredentialVisible => Descriptor?.RequiresCredential == true;

    public string ProviderId
    {
        get => _providerId;
        set
        {
            if (SetProperty(ref _providerId, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (SetProperty(ref _baseUrl, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public string ConfigurationApiAddress
    {
        get => _configurationApiAddress;
        set
        {
            if (SetProperty(ref _configurationApiAddress, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public string CurrentGroup
    {
        get => _currentGroup;
        set
        {
            if (SetProperty(ref _currentGroup, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public string CurrentGroupRatio
    {
        get => _currentGroupRatio;
        set
        {
            if (SetProperty(ref _currentGroupRatio, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public string Currency
    {
        get => _currency;
        set
        {
            if (SetProperty(ref _currency, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public string CnyConversionRate
    {
        get => _cnyConversionRate;
        set
        {
            if (SetProperty(ref _cnyConversionRate, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool CanSave => TryBuild(out _);

    public bool TryBuild(out SiteConfiguration site)
    {
        site = null!;
        if (string.IsNullOrWhiteSpace(ProviderId) ||
            string.IsNullOrWhiteSpace(Model) ||
            string.IsNullOrWhiteSpace(CurrentGroup) ||
            Descriptor is null ||
            !Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !decimal.TryParse(CurrentGroupRatio, NumberStyles.Number, CultureInfo.InvariantCulture, out var ratio) ||
            ratio <= 0)
            return false;

        decimal? conversion = decimal.TryParse(CnyConversionRate, NumberStyles.Number, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : null;
        uri = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        var configAddress = string.IsNullOrWhiteSpace(ConfigurationApiAddress) ? "/keys" : ConfigurationApiAddress.Trim();
        site = new SiteConfiguration
        {
            ProviderId = ProviderId.Trim(),
            DisplayName = DisplayName.Trim(),
            ConfigurationApiAddress = configAddress,
            BaseUrl = uri,
            SiteType = Descriptor.SiteType,
            Enabled = Original?.Enabled ?? true,
            Model = Model.Trim(),
            CurrentGroup = CurrentGroup.Trim(),
            CurrentGroupRatio = ratio,
            GroupRatioSource = "手动",
            AuthenticationMode = AuthenticationMode ?? "无需认证",
            Currency = Currency.Trim(),
            CnyConversionRate = conversion,
            ConfigurationKey = SiteConfigurationKey.Create(ProviderId.Trim(), Descriptor.SiteType, uri, Model.Trim(), CurrentGroup.Trim())
        };
        return true;
    }
}
