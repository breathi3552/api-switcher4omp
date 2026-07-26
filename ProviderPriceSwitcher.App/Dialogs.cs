using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Infrastructure;

namespace ProviderPriceSwitcher.App;

public sealed class SitesDialog : Window
{
    private readonly JsonSettingsRepository _settingsRepository;
    private readonly PricingRefreshService _refreshService;
    private readonly string? _currentProvider;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly Button _edit = new() { Content = "编辑" };
    private readonly Button _toggle = new() { Content = "启用/禁用" };
    private readonly Button _delete = new() { Content = "删除" };

    public LocalAppSettings Settings { get; private set; }

    public SitesDialog(LocalAppSettings settings, JsonSettingsRepository settingsRepository, PricingRefreshService refreshService, string? currentProvider)
    {
        Settings = settings;
        _settingsRepository = settingsRepository;
        _refreshService = refreshService;
        _currentProvider = currentProvider;
        Title = "管理站点";
        Width = 1120;
        Height = 560;
        MinWidth = 900;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.MainWindow;

        _grid.Columns.Add(new DataGridTextColumn { Header = "站点名称", Binding = new System.Windows.Data.Binding("DisplayName"), Width = new DataGridLength(150) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "ProviderId", Binding = new System.Windows.Data.Binding("ProviderId"), Width = new DataGridLength(130) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new System.Windows.Data.Binding("SiteType"), Width = new DataGridLength(100) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Base URL", Binding = new System.Windows.Data.Binding("BaseUrl"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "当前组", Binding = new System.Windows.Data.Binding("CurrentGroup"), Width = new DataGridLength(150) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "当前倍率", Binding = new System.Windows.Data.Binding("RatioDisplay"), Width = new DataGridLength(95) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new System.Windows.Data.Binding("EnabledDisplay"), Width = new DataGridLength(75) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "最近检查", Binding = new System.Windows.Data.Binding("CheckedAt"), Width = new DataGridLength(135) });
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.MouseDoubleClick += (_, _) => EditSelected();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var add = new Button { Content = "新增" };
        add.Click += (_, _) => AddSite();
        _edit.Click += (_, _) => EditSelected();
        _toggle.Click += (_, _) => ToggleSelected();
        _delete.Click += (_, _) => DeleteSelected();
        var close = new Button { Content = "关闭", IsCancel = true };
        buttons.Children.Add(add);
        buttons.Children.Add(_edit);
        buttons.Children.Add(_toggle);
        buttons.Children.Add(_delete);
        buttons.Children.Add(close);

        var panel = new Grid { Margin = new Thickness(16) };
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(_grid);
        Grid.SetRow(buttons, 1);
        panel.Children.Add(buttons);
        Content = panel;
        RefreshRows();
    }

    private SiteListRow? Selected => _grid.SelectedItem as SiteListRow;

    private void RefreshRows(string? selectProvider = null)
    {
        var snapshots = _refreshService.LoadSnapshots();
        var rows = Settings.Sites.Select(site => new SiteListRow(site, snapshots.GetValueOrDefault(site.ProviderId))).ToList();
        _grid.ItemsSource = rows;
        _grid.SelectedItem = rows.FirstOrDefault(x => string.Equals(x.ProviderId, selectProvider, StringComparison.Ordinal)) ?? rows.FirstOrDefault();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var selected = Selected;
        _edit.IsEnabled = selected is not null;
        _toggle.IsEnabled = selected is not null;
        _delete.IsEnabled = selected is not null;
        _toggle.Content = selected?.Enabled == true ? "禁用" : "启用";
    }

    private void AddSite()
    {
        var dialog = new SiteEditorDialog(null, Settings, _refreshService) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Site is null) return;
        SaveSite(dialog.Site, null);
    }

    private void EditSelected()
    {
        var selected = Selected;
        if (selected is null) return;
        var original = Settings.Sites.First(x => string.Equals(x.ProviderId, selected.ProviderId, StringComparison.Ordinal));
        var dialog = new SiteEditorDialog(original, Settings, _refreshService) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Site is null) return;
        SaveSite(dialog.Site, original.ProviderId);
    }

    private void SaveSite(SiteConfiguration site, string? originalProviderId)
    {
        var sites = Settings.Sites.ToList();
        if (originalProviderId is null) sites.Add(site);
        else sites[sites.FindIndex(x => string.Equals(x.ProviderId, originalProviderId, StringComparison.Ordinal))] = site;
        if (originalProviderId is not null && !string.Equals(originalProviderId, site.ProviderId, StringComparison.Ordinal))
            _refreshService.DeleteSnapshot(originalProviderId);
        Settings = Settings with { Sites = sites };
        var snapshot = _refreshService.LoadSnapshots().GetValueOrDefault(site.ProviderId);
        if (snapshot is not null && snapshot.Matches(site) && site.CurrentGroupRatio is > 0 && snapshot.BasePrices is not null)
            _refreshService.SaveSnapshot(snapshot.WithCurrentRatio(site.CurrentGroupRatio.Value, site.GroupRatioSource));
        _settingsRepository.Save(Settings);
        RefreshRows(site.ProviderId);
    }

    private void ToggleSelected()
    {
        var selected = Selected;
        if (selected is null) return;
        var sites = Settings.Sites.Select(site => string.Equals(site.ProviderId, selected.ProviderId, StringComparison.Ordinal) ? site with { Enabled = !site.Enabled } : site).ToList();
        Settings = Settings with { Sites = sites };
        _settingsRepository.Save(Settings);
        RefreshRows(selected.ProviderId);
    }

    private void DeleteSelected()
    {
        var selected = Selected;
        if (selected is null) return;
        var currentWarning = string.Equals(selected.ProviderId, _currentProvider, StringComparison.Ordinal)
            ? "\n\n该站点是当前 OMP Provider。删除不会修改 OMP 配置。"
            : string.Empty;
        if (MessageBox.Show($"确认删除站点“{selected.ProviderId}”及其本地价格快照？{currentWarning}", "删除站点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Settings = Settings with { Sites = Settings.Sites.Where(x => !string.Equals(x.ProviderId, selected.ProviderId, StringComparison.Ordinal)).ToList() };
        _settingsRepository.Save(Settings);
        _refreshService.DeleteSnapshot(selected.ProviderId);
        RefreshRows();
    }
}

public sealed class SiteEditorDialog : Window
{
    private static readonly string[] SiteTypes = ["new-api", "pawsai", "sub2api"];
    private readonly SiteConfiguration? _original;
    private readonly LocalAppSettings _settings;
    private readonly PricingRefreshService _refreshService;
    private readonly TextBox _displayName = new(), _provider = new(), _url = new(), _model = new(), _group = new(), _ratio = new(), _currency = new(), _conversion = new(), _cookieHeader = new();
    private readonly PasswordBox _token = new();
    private readonly ComboBox _siteType = new() { ItemsSource = SiteTypes, IsReadOnly = true };
    private readonly ComboBox _authentication = new() { ItemsSource = new[] { "无需认证", "导入令牌", "账户登录" }, IsReadOnly = true };
    private readonly WindowsSiteCredentialStore _credentialStore = new();
    private readonly TextBlock _credentialStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly TextBlock _probeResult = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly Button _save = new() { Content = "保存", IsDefault = true };
    public SiteConfiguration? Site { get; private set; }

    public SiteEditorDialog(SiteConfiguration? original, LocalAppSettings settings, PricingRefreshService refreshService)
    {
        _original = original;
        _settings = settings;
        _refreshService = refreshService;
        Title = original is null ? "新增站点" : "编辑站点";
        Width = 720;
        Height = 760;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _displayName.Text = original?.DisplayName ?? string.Empty;
        _provider.Text = original?.ProviderId ?? string.Empty;
        _siteType.SelectedItem = original?.SiteType ?? "new-api";
        _url.Text = original?.BaseUrl.ToString() ?? string.Empty;
        _authentication.SelectedItem = original?.AuthenticationMode ?? ((original?.SiteType ?? "new-api") == "sub2api" ? "导入令牌" : "无需认证");
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
        var siteType = _siteType.SelectedItem as string ?? "new-api";
        var summary = string.IsNullOrWhiteSpace(provider)
            ? new SiteCredentialSummary { ProviderId = string.Empty, Status = SiteCredentialStatus.NotConfigured, StatusText = siteType == "sub2api" ? "请先填写 ProviderId，再绑定令牌。" : "当前站点无需凭据。" }
            : _credentialStore.GetSummary(provider, siteType);
        var suffix = summary.ExpiresAt is DateTimeOffset expiresAt ? $"；到期 {expiresAt.LocalDateTime:yyyy-MM-dd HH:mm}" : string.Empty;
        _credentialStatus.Text = $"凭据状态：{summary.StatusText}{suffix}";
    }

    private void SaveCredential()
    {
        var provider = _provider.Text.Trim();
        var siteType = _siteType.SelectedItem as string ?? string.Empty;
        if (string.IsNullOrWhiteSpace(provider) || !string.Equals(siteType, "sub2api", StringComparison.Ordinal))
        {
            MessageBox.Show("仅 sub2api 站点支持绑定令牌，请先填写 ProviderId 并选择站点类型。", "无法绑定", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var type = _siteType.SelectedItem as string ?? string.Empty;
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
            AuthenticationMode = type == "sub2api" ? "导入令牌" : (_authentication.SelectedItem as string ?? "无需认证"),
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
            var result = await _refreshService.ProbeAsync(draft, _settings.RequestTimeoutSeconds);
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

internal sealed class SiteListRow
{
    public SiteListRow(SiteConfiguration site, PricingSnapshot? snapshot)
    {
        ProviderId = site.ProviderId;
        DisplayName = string.IsNullOrWhiteSpace(site.DisplayName) ? site.ProviderId : site.DisplayName;
        SiteType = site.SiteType;
        BaseUrl = site.BaseUrl;
        CurrentGroup = site.CurrentGroup;
        RatioDisplay = (site.CurrentGroupRatio ?? snapshot?.CurrentGroupRatio)?.ToString("0.####", CultureInfo.InvariantCulture) ?? "—";
        Enabled = site.Enabled;
        EnabledDisplay = site.Enabled ? "启用" : "禁用";
        CheckedAt = snapshot?.RefreshedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";
    }
    public string ProviderId { get; }
    public string DisplayName { get; }
    public string SiteType { get; }
    public Uri BaseUrl { get; }
    public string CurrentGroup { get; }
    public string RatioDisplay { get; }
    public bool Enabled { get; }
    public string EnabledDisplay { get; }
    public string CheckedAt { get; }
}



public sealed class SettingsDialog : Window
{
    private readonly LocalAppSettings _source;
    private readonly TextBox _timeout = new(), _ompRoot = new();
    private readonly ListBox _directoryList = new() { MinHeight = 140, DisplayMemberPath = nameof(WorkingDirectoryRow.Display) };
    private readonly Button _deleteDirectory = new() { Content = "删除" };
    private readonly Button _setDefaultDirectory = new() { Content = "设为默认" };
    private readonly List<string> _directories;
    private string? _defaultDirectory;

    public LocalAppSettings Settings { get; private set; }

    public SettingsDialog(LocalAppSettings settings)
    {
        _source = settings;
        Settings = settings;
        _directories = settings.OmpWorkingDirectories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _defaultDirectory = _directories.FirstOrDefault(x => string.Equals(x, settings.LastOmpWorkingDirectory, StringComparison.OrdinalIgnoreCase))
            ?? _directories.FirstOrDefault();

        Title = "设置";
        Width = 720;
        Height = 500;
        MinWidth = 620;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.MainWindow;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition());

        var timeoutRow = Row("请求超时（秒）", _timeout, settings.RequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        content.Children.Add(timeoutRow);

        var rootRow = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        rootRow.Children.Add(new TextBlock { Text = "OMP 配置目录" });
        var rootInput = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        rootInput.ColumnDefinitions.Add(new ColumnDefinition());
        rootInput.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _ompRoot.Text = settings.OmpRootDirectory;
        rootInput.Children.Add(_ompRoot);
        var browseRoot = new Button { Content = "浏览", Margin = new Thickness(8, 0, 0, 0) };
        browseRoot.Click += (_, _) => BrowseInto(_ompRoot, "选择 OMP 配置目录");
        Grid.SetColumn(browseRoot, 1);
        rootInput.Children.Add(browseRoot);
        rootRow.Children.Add(rootInput);
        Grid.SetRow(rootRow, 1);
        content.Children.Add(rootRow);

        var directoriesPanel = new Grid();
        directoriesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        directoriesPanel.RowDefinitions.Add(new RowDefinition());
        directoriesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        directoriesPanel.Children.Add(new TextBlock { Text = "常用工作目录", Margin = new Thickness(0, 0, 0, 3) });
        Grid.SetRow(_directoryList, 1);
        directoriesPanel.Children.Add(_directoryList);
        var directoryButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var addDirectory = new Button { Content = "添加" };
        addDirectory.Click += (_, _) => AddDirectory();
        _deleteDirectory.Click += (_, _) => DeleteDirectory();
        _setDefaultDirectory.Click += (_, _) => SetDefaultDirectory();
        directoryButtons.Children.Add(addDirectory);
        directoryButtons.Children.Add(_deleteDirectory);
        directoryButtons.Children.Add(_setDefaultDirectory);
        Grid.SetRow(directoryButtons, 2);
        directoriesPanel.Children.Add(directoryButtons);
        Grid.SetRow(directoriesPanel, 2);
        content.Children.Add(directoriesPanel);
        root.Children.Add(content);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var save = new Button { Content = "保存", IsDefault = true };
        save.Click += (_, _) => Save();
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0) });
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        Content = root;

        _directoryList.SelectionChanged += (_, _) => UpdateDirectoryButtons();
        RefreshDirectoryList(_defaultDirectory);
    }

    private static FrameworkElement Row(string label, TextBox box, string value)
    {
        box.Text = value;
        box.Margin = new Thickness(0, 3, 0, 12);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        return panel;
    }

    private void BrowseInto(TextBox target, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (Directory.Exists(target.Text.Trim())) dialog.InitialDirectory = target.Text.Trim();
        if (dialog.ShowDialog(this) == true) target.Text = Path.GetFullPath(dialog.FolderName);
    }

    private void AddDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "添加常用工作目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var path = Path.GetFullPath(dialog.FolderName);
        var existing = _directories.FirstOrDefault(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _directories.Add(path);
            existing = path;
            _defaultDirectory ??= path;
        }
        RefreshDirectoryList(existing);
    }

    private void DeleteDirectory()
    {
        if (_directoryList.SelectedItem is not WorkingDirectoryRow selected) return;
        _directories.RemoveAll(x => string.Equals(x, selected.Path, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(_defaultDirectory, selected.Path, StringComparison.OrdinalIgnoreCase))
            _defaultDirectory = _directories.FirstOrDefault();
        RefreshDirectoryList(_defaultDirectory);
    }

    private void SetDefaultDirectory()
    {
        if (_directoryList.SelectedItem is not WorkingDirectoryRow selected) return;
        _defaultDirectory = selected.Path;
        RefreshDirectoryList(selected.Path);
    }

    private void RefreshDirectoryList(string? selectedPath)
    {
        var rows = _directories.Select(path => new WorkingDirectoryRow(path, string.Equals(path, _defaultDirectory, StringComparison.OrdinalIgnoreCase))).ToList();
        _directoryList.ItemsSource = rows;
        _directoryList.SelectedItem = rows.FirstOrDefault(x => string.Equals(x.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
        UpdateDirectoryButtons();
    }

    private void UpdateDirectoryButtons()
    {
        var selected = _directoryList.SelectedItem as WorkingDirectoryRow;
        _deleteDirectory.IsEnabled = selected is not null;
        _setDefaultDirectory.IsEnabled = selected is not null && !selected.IsDefault;
    }

    private void Save()
    {
        if (!int.TryParse(_timeout.Text, out var timeout) || timeout < 0)
        {
            MessageBox.Show("超时必须为非负整数。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Settings = _source with
        {
            RequestTimeoutSeconds = timeout,
            OmpRootDirectory = _ompRoot.Text.Trim(),
            OmpWorkingDirectories = _directories.ToList(),
            LastOmpWorkingDirectory = _defaultDirectory
        };
        DialogResult = true;
    }

    private sealed record WorkingDirectoryRow(string Path, bool IsDefault)
    {
        public string Display => IsDefault ? Path + "    默认" : Path;
    }
}
