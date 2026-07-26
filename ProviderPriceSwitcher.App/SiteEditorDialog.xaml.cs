using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Infrastructure;

namespace ProviderPriceSwitcher.App;
public sealed partial class SiteEditorDialog : Window
{
    private readonly SiteConfiguration? _original;
    private readonly LocalAppSettings _settings;
    private readonly PricingRefreshService _refreshService;
    private readonly IPricingAdapterRegistry _adapterRegistry;
    private readonly PricingProbeUseCase _pricingProbe;
    private readonly TextBox _displayName = new(), _provider = new(), _url = new(), _model = new(), _group = new(), _ratio = new(), _currency = new(), _conversion = new(), _cookieHeader = new();
    private readonly PasswordBox _token = new();
    private readonly ComboBox _siteType = new() { IsReadOnly = true, DisplayMemberPath = nameof(PricingAdapterDescriptor.DisplayName) };
    private readonly ComboBox _authentication = new() { IsReadOnly = true };
    private readonly WindowsSiteCredentialStore _credentialStore = new();
    private readonly TextBlock _credentialStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly TextBlock _probeResult = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly Button _save = new() { Content = "保存", IsDefault = true };
    public SiteConfiguration? Site { get; private set; }

    public SiteEditorDialog(SiteConfiguration? original, LocalAppSettings settings, PricingRefreshService refreshService, IPricingAdapterRegistry adapterRegistry)
    {
        InitializeComponent();
        _original = original;
        _settings = settings;
        _refreshService = refreshService;
        _adapterRegistry = adapterRegistry;
        _siteType.ItemsSource = adapterRegistry.Descriptors;
        _pricingProbe = new PricingProbeUseCase(adapterRegistry);
        Title = original is null ? "新增站点" : "编辑站点";
        Width = 720;
        Height = 760;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _displayName.Text = original?.DisplayName ?? string.Empty;
        _provider.Text = original?.ProviderId ?? string.Empty;
        _siteType.SelectedItem = adapterRegistry.Descriptors.FirstOrDefault(x => string.Equals(x.SiteType, original?.SiteType, StringComparison.Ordinal)) ?? (adapterRegistry.Descriptors.Count > 0 ? adapterRegistry.Descriptors[0] : null);
        _url.Text = original?.BaseUrl.ToString() ?? string.Empty;
        var initialDescriptor = _siteType.SelectedItem as PricingAdapterDescriptor;
        _authentication.ItemsSource = initialDescriptor?.AuthenticationModes ?? [];
        _authentication.SelectedItem = original?.AuthenticationMode ?? (initialDescriptor?.AuthenticationModes.Count > 0 ? initialDescriptor.AuthenticationModes[0] : null);
        _model.Text = original?.Model ?? settings.Model;
        _group.Text = original?.CurrentGroup ?? string.Empty;
        _ratio.Text = (original?.CurrentGroupRatio ?? refreshService.LoadSnapshots().GetValueOrDefault(original?.ProviderId ?? string.Empty)?.CurrentGroupRatio)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _currency.Text = original?.Currency ?? string.Empty;
        _conversion.Text = original?.CnyConversionRate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var existingCredential = string.IsNullOrWhiteSpace(original?.ProviderId) ? null : _credentialStore.LoadCredential(original.ProviderId);
        _cookieHeader.Text = existingCredential?.CookieHeader ?? string.Empty;

        var form = new StackPanel { Margin = new Thickness(20) };
        form.Children.Add(Section("基本信息"));
        form.Children.Add(Field("显示名称（预留）", _displayName));
        form.Children.Add(Field("ProviderId（对应 OMP Provider ID）", _provider));
        form.Children.Add(Field("站点类型", _siteType));
        form.Children.Add(Section("连接配置"));
        form.Children.Add(Field("Base URL", _url));
        form.Children.Add(Field("认证方式", _authentication));
        form.Children.Add(_credentialStatus);
        form.Children.Add(Field("访问令牌", _token));
        form.Children.Add(Field("浏览器会话 Cookie（形如 refresh_token=...）", _cookieHeader));
        var credentialButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var bindCredential = new Button { Content = "绑定/更新令牌" };
        bindCredential.Click += (_, _) => SaveCredential();
        var clearCredential = new Button { Content = "清除凭据" };
        clearCredential.Click += (_, _) => ClearCredential();
        credentialButtons.Children.Add(bindCredential);
        credentialButtons.Children.Add(clearCredential);
        form.Children.Add(credentialButtons);
        form.Children.Add(Section("价格查询"));
        form.Children.Add(Field("目标模型", _model));
        form.Children.Add(Field("当前分组", _group));
        form.Children.Add(Field("当前分组倍率（正数）", _ratio));
        form.Children.Add(Field("计价币种（预留，暂未用于推荐）", _currency));
        form.Children.Add(Field("人民币换算率（预留，暂未用于推荐）", _conversion));
        var probe = new Button { Content = "测试价格查询" };
        probe.Click += async (_, _) => await ProbeAsync(probe);
        form.Children.Add(probe);
        form.Children.Add(_probeResult);
        form.Children.Add(Section("账户能力（预留）"));
        form.Children.Add(new TextBlock { Text = "余额查询：暂未接入    日志查询：暂未接入    缓存命中率：—", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 14) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _save.Click += (_, _) => Save();
        buttons.Children.Add(_save);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true });
        form.Children.Add(buttons);
        Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        UpdateCredentialStatus();
    }

    private void UpdateCredentialStatus()
    {
        var provider = _provider.Text.Trim();
        var descriptor = _siteType.SelectedItem as PricingAdapterDescriptor;
        var summary = string.IsNullOrWhiteSpace(provider)
            ? new SiteCredentialSummary { ProviderId = string.Empty, Status = SiteCredentialStatus.NotConfigured, StatusText = descriptor?.RequiresCredential == true ? "请先填写 ProviderId，再绑定令牌。" : "当前站点无需凭据。" }
            : _credentialStore.GetSummary(provider);
        var suffix = summary.ExpiresAt is DateTimeOffset expiresAt ? $"；到期 {expiresAt.LocalDateTime:yyyy-MM-dd HH:mm}" : string.Empty;
        _credentialStatus.Text = $"凭据状态：{summary.StatusText}{suffix}";
    }

    private void SaveCredential()
    {
        var provider = _provider.Text.Trim();
        var descriptor = _siteType.SelectedItem as PricingAdapterDescriptor;
        var siteType = descriptor?.SiteType ?? string.Empty;
        if (string.IsNullOrWhiteSpace(provider) || descriptor?.RequiresCredential != true)
        {
            MessageBox.Show("当前站点类型不支持绑定令牌，请先填写 ProviderId 并选择需要凭据的站点类型。", "无法绑定", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_token.Password))
        {
            MessageBox.Show("访问令牌不能为空。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_cookieHeader.Text))
        {
            MessageBox.Show("浏览器会话 Cookie 不能为空。请从成功的 SevnX 价格请求中复制 Cookie header。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _credentialStore.SaveCredential(new SiteCredentialRecord
        {
            ProviderId = provider,
            SiteType = siteType,
            AuthorizationScheme = "Bearer",
            AccessToken = _token.Password.Trim(),
            CookieHeader = _cookieHeader.Text.Trim()
        });
        _token.Password = string.Empty;
        _cookieHeader.Text = string.Empty;
        UpdateCredentialStatus();
        _probeResult.Text = "访问令牌与浏览器会话 Cookie 已保存；价格查询会使用已验证的浏览器请求指纹。";
    }

    private void ClearCredential()
    {
        var provider = _provider.Text.Trim();
        if (string.IsNullOrWhiteSpace(provider)) return;
        _credentialStore.ClearCredential(provider);
        _token.Password = string.Empty;
        _cookieHeader.Text = string.Empty;
        UpdateCredentialStatus();
        _probeResult.Text = "已清除本地凭据。";
    }

    private static TextBlock Section(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 10) };
    private static FrameworkElement Field(string label, Control control)
    {
        control.Margin = new Thickness(0, 3, 0, 10);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(control);
        return panel;
    }

    private bool TryBuild(out SiteConfiguration site, bool validateDuplicate = true)
    {
        site = null!;
        var provider = _provider.Text.Trim();
        var model = _model.Text.Trim();
        var group = _group.Text.Trim();
        var type = (_siteType.SelectedItem as PricingAdapterDescriptor)?.SiteType ?? string.Empty;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(type) ||
            !Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show("ProviderId、站点类型、有效的 http(s) Base URL、目标模型和当前分组均为必填项。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (validateDuplicate && _settings.Sites.Any(x => !ReferenceEquals(x, _original) && string.Equals(x.ProviderId, provider, StringComparison.Ordinal)))
        {
            MessageBox.Show("ProviderId 不能重复。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!decimal.TryParse(_ratio.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var ratio) || ratio <= 0)
        {
            MessageBox.Show("当前分组倍率必须是大于 0 的数字。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        decimal? conversion = null;
        if (!string.IsNullOrWhiteSpace(_conversion.Text) && (!decimal.TryParse(_conversion.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0))
        {
            MessageBox.Show("人民币换算率留空或填写大于 0 的数字。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        else if (!string.IsNullOrWhiteSpace(_conversion.Text)) conversion = decimal.Parse(_conversion.Text, CultureInfo.InvariantCulture);
        uri = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        var key = SiteConfigurationKey.Create(provider, type, uri, model, group);
        site = new SiteConfiguration
        {
            ProviderId = provider,
            DisplayName = _displayName.Text.Trim(),
            BaseUrl = uri,
            SiteType = type,
            Enabled = _original?.Enabled ?? true,
            Model = model,
            CurrentGroup = group,
            CurrentGroupRatio = ratio,
            GroupRatioSource = "手动",
            AuthenticationMode = (_adapterRegistry.Descriptors.FirstOrDefault(x => string.Equals(x.SiteType, type, StringComparison.Ordinal)) is { AuthenticationModes.Count: > 0 } descriptor ? descriptor.AuthenticationModes[0] : (_authentication.SelectedItem as string ?? "无需认证")),
            Currency = _currency.Text.Trim(),
            CnyConversionRate = conversion,
            ConfigurationKey = key
        };
        return true;
    }

    private async Task ProbeAsync(Button button)
    {
        if (!TryBuild(out var draft, false)) return;
        button.IsEnabled = false;
        _save.IsEnabled = false;
        _probeResult.Text = "正在查询…";
        try
        {
            var result = await _pricingProbe.ExecuteAsync(draft, _settings.RequestTimeoutSeconds);
            var prices = result.Prices;
            _probeResult.Text = $"成功：模型 {draft.Model}；当前组倍率 {result.Snapshot.CurrentGroupRatio:0.####}；最低组 {result.MinimumValidGroup}（{result.MinimumGroupRatio:0.####}）；输入/缓存/输出单价 {prices.InputPerMillion:0.####} / {prices.CachedInputPerMillion:0.####} / {prices.OutputPerMillion:0.####}";
        }
        catch (Exception ex)
        {
            _probeResult.Text = "查询失败：" + ex.Message;
        }
        finally
        {
            UpdateCredentialStatus();
            button.IsEnabled = true;
            _save.IsEnabled = true;
        }
    }

    private void Save()
    {
        if (!TryBuild(out var site)) return;
        Site = site;
        DialogResult = true;
    }
}

