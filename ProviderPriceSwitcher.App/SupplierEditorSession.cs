using System.Collections.ObjectModel;
using System.Globalization;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.App;

public enum SupplierEditorProbeState { Idle, Probing, Succeeded, Canceled, Failed }
public sealed class SupplierEditorSession : ObservableObject
{
    private readonly PricingProbeUseCase? _probe;
    private readonly int _requestTimeoutSeconds;
    private readonly IUserNotificationService? _notifications;

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

    private CancellationTokenSource? _cancel;
    private Task? _probeTask;
    private bool _isProbing;
    private SupplierEditorProbeState _probeState = SupplierEditorProbeState.Idle;
    private string _probeMessage = string.Empty;
    private IReadOnlyDictionary<string, decimal>? _probedGroupRatios;

    public SupplierEditorSession(
        PricingProbeUseCase probe,
        IPricingAdapterRegistry registry,
        LocalAppSettings settings,
        SiteConfiguration? original = null,
        IUserNotificationService? notifications = null)
        : this(
            probe ?? throw new ArgumentNullException(nameof(probe)),
            (registry ?? throw new ArgumentNullException(nameof(registry))).Descriptors,
            (settings ?? throw new ArgumentNullException(nameof(settings))).Model,
            settings.RequestTimeoutSeconds,
            original,
            notifications)
    {
    }

    public SupplierEditorSession(
        IReadOnlyList<PricingAdapterDescriptor> descriptors,
        string defaultModel,
        SiteConfiguration? original = null)
        : this(null, descriptors, defaultModel, 10, original, null)
    {
    }

    public SupplierEditorSession(
        PricingProbeUseCase? probe,
        IReadOnlyList<PricingAdapterDescriptor> descriptors,
        string defaultModel,
        int requestTimeoutSeconds = 10,
        SiteConfiguration? original = null,
        IUserNotificationService? notifications = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestTimeoutSeconds);
        _probe = probe;
        _requestTimeoutSeconds = requestTimeoutSeconds;
        _notifications = notifications;

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

        if (!string.IsNullOrWhiteSpace(_currentGroup))
        {
            GroupOptions.Add(_currentGroup);
        }

        ProbeCommand = new AsyncCommand(() => ProbeAsync(), HandleError, () => CanProbe);
        CancelProbeCommand = new RelayCommand(CancelProbe, () => IsProbing);
    }

    public SiteConfiguration? Original { get; }
    public IReadOnlyList<PricingAdapterDescriptor> Descriptors { get; }
    public ObservableCollection<string> GroupOptions { get; } = [];
    public IReadOnlyDictionary<string, decimal>? ProbedGroupRatios => _probedGroupRatios;

    public AsyncCommand ProbeCommand { get; }
    public RelayCommand CancelProbeCommand { get; }

    public bool IsProbing
    {
        get => _isProbing;
        private set
        {
            if (SetProperty(ref _isProbing, value))
                NotifyDraftChanged();
        }
    }

    public SupplierEditorProbeState ProbeState
    {
        get => _probeState;
        private set => SetProperty(ref _probeState, value);
    }

    public string ProbeMessage
    {
        get => _probeMessage;
        private set => SetProperty(ref _probeMessage, value);
    }

    public bool CanSave => !IsProbing && TryBuild(out _);
    public bool CanProbe => _probe is not null && !IsProbing && TryBuild(out _);

    public PricingAdapterDescriptor? Descriptor
    {
        get => _descriptor;
        set
        {
            if (!SetProperty(ref _descriptor, value)) return;
            AuthenticationMode = value?.AuthenticationModes.Count > 0 ? value.AuthenticationModes[0] : null;
            OnPropertyChanged(nameof(AuthenticationModes));
            OnPropertyChanged(nameof(CredentialVisible));
            NotifyDraftChanged();
        }
    }

    public IReadOnlyList<string> AuthenticationModes => Descriptor?.AuthenticationModes ?? [];

    public string? AuthenticationMode
    {
        get => _authenticationMode;
        set
        {
            if (SetProperty(ref _authenticationMode, value))
                NotifyDraftChanged();
        }
    }

    public bool CredentialVisible => Descriptor?.RequiresCredential == true;

    public string ProviderId
    {
        get => _providerId;
        set
        {
            if (SetProperty(ref _providerId, value))
                NotifyDraftChanged();
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
                NotifyDraftChanged();
        }
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (SetProperty(ref _baseUrl, value))
                NotifyDraftChanged();
        }
    }

    public string ConfigurationApiAddress
    {
        get => _configurationApiAddress;
        set
        {
            if (SetProperty(ref _configurationApiAddress, value))
                NotifyDraftChanged();
        }
    }

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value))
                NotifyDraftChanged();
        }
    }

    public string CurrentGroup
    {
        get => _currentGroup;
        set
        {
            if (SetProperty(ref _currentGroup, value))
            {
                ApplyProbedGroupRatio(_currentGroup);
                if (!string.IsNullOrWhiteSpace(_currentGroup) && !GroupOptions.Contains(_currentGroup, StringComparer.OrdinalIgnoreCase))
                {
                    GroupOptions.Add(_currentGroup);
                }
                NotifyDraftChanged();
            }
        }
    }

    public string CurrentGroupRatio
    {
        get => _currentGroupRatio;
        set
        {
            if (SetProperty(ref _currentGroupRatio, value))
                NotifyDraftChanged();
        }
    }

    public string Currency
    {
        get => _currency;
        set
        {
            if (SetProperty(ref _currency, value))
                NotifyDraftChanged();
        }
    }

    public string CnyConversionRate
    {
        get => _cnyConversionRate;
        set
        {
            if (SetProperty(ref _cnyConversionRate, value))
                NotifyDraftChanged();
        }
    }

    public void CancelProbe()
    {
        _cancel?.Cancel();
    }

    public async Task CloseAsync()
    {
        _cancel?.Cancel();
        if (_probeTask is not null)
        {
            try
            {
                await _probeTask.ConfigureAwait(false);
            }
            catch
            {
                // Captured within probe core
            }
        }
    }

    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (_probe is null || !TryBuild(out var site) || _isProbing) return false;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancel = linked;
        _probeTask = ProbeCoreAsync(site, linked.Token);
        try
        {
            await _probeTask;
            return _probeState == SupplierEditorProbeState.Succeeded;
        }
        finally
        {
            _cancel = null;
            _probeTask = null;
            linked.Dispose();
        }
    }

    private async Task ProbeCoreAsync(SiteConfiguration site, CancellationToken cancellationToken)
    {
        IsProbing = true;
        ProbeState = SupplierEditorProbeState.Probing;
        ProbeMessage = "正在查询…";
        try
        {
            var result = await _probe!.ExecuteAsync(site, _requestTimeoutSeconds, cancellationToken);
            var boundGroup = CurrentGroup;
            _probedGroupRatios = result.GroupRatios;
            var desiredGroups = result.GroupRatios.Keys
                .Append(boundGroup)
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var group in desiredGroups)
            {
                if (!GroupOptions.Contains(group, StringComparer.OrdinalIgnoreCase))
                    GroupOptions.Add(group);
            }
            for (var index = GroupOptions.Count - 1; index >= 0; index--)
            {
                if (!desiredGroups.Contains(GroupOptions[index], StringComparer.OrdinalIgnoreCase))
                    GroupOptions.RemoveAt(index);
            }

            ApplyProbedGroupRatio(boundGroup);
            ProbeState = SupplierEditorProbeState.Succeeded;
            if (result.GroupRatios.TryGetValue(boundGroup, out var actualRatio) && actualRatio > 0)
            {
                ProbeMessage = $"成功：当前组倍率 {actualRatio:0.####}；最低组 {result.MinimumValidGroup}（{result.MinimumGroupRatio:0.####}）。";
            }
            else if (decimal.TryParse(CurrentGroupRatio, NumberStyles.Number, CultureInfo.InvariantCulture, out var retainedRatio))
            {
                ProbeMessage = $"成功：响应未包含当前组，已保留现有倍率 {retainedRatio:0.####}；最低组 {result.MinimumValidGroup}（{result.MinimumGroupRatio:0.####}）。";
            }
            else
            {
                ProbeMessage = $"成功：最低组 {result.MinimumValidGroup}（{result.MinimumGroupRatio:0.####}）。";
            }
        }
        catch (OperationCanceledException)
        {
            ProbeState = SupplierEditorProbeState.Canceled;
            ProbeMessage = "已取消价格查询。";
        }
        catch (PricingAdapterException ex)
        {
            ProbeState = SupplierEditorProbeState.Failed;
            ProbeMessage = UserErrorMessages.ForProbeFailure(ex.Failure);
        }
        catch (Exception)
        {
            ProbeState = SupplierEditorProbeState.Failed;
            ProbeMessage = UserErrorMessages.Unexpected;
            _notifications?.ShowError(UserErrorMessages.Unexpected, "价格查询");
        }
        finally
        {
            IsProbing = false;
        }
    }
    private void ApplyProbedGroupRatio(string? group)
    {
        if (!string.IsNullOrWhiteSpace(group) &&
            _probedGroupRatios is not null &&
            _probedGroupRatios.TryGetValue(group, out var ratio) &&
            ratio > 0)
        {
            CurrentGroupRatio = ratio.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void HandleError(Exception ex)
    {
        if (ex is not OperationCanceledException)
            _notifications?.ShowError(UserErrorMessages.Unexpected, "站点编辑");
    }

    private void NotifyDraftChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanProbe));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        ProbeCommand?.RaiseCanExecuteChanged();
        CancelProbeCommand?.RaiseCanExecuteChanged();
    }

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
