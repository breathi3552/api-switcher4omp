using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Infrastructure;

namespace ProviderPriceSwitcher.App;
public sealed partial class SitesDialog : Window
{
    private readonly JsonSettingsRepository _settingsRepository;
    private readonly PricingRefreshService _refreshService;
    private readonly IPricingAdapterRegistry _adapterRegistry;
    private readonly ISiteCredentialStore _credentialStore;
    private readonly IUserNotificationService _notifications;
    private readonly SiteManagementUseCase _siteManagement;
    private readonly string? _currentProvider;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly Button _edit = new() { Content = "编辑" };
    private readonly Button _toggle = new() { Content = "启用/禁用" };
    private readonly Button _delete = new() { Content = "删除" };

    public LocalAppSettings Settings { get; private set; }

    public SitesDialog(LocalAppSettings settings, JsonSettingsRepository settingsRepository, PricingRefreshService refreshService, IPricingAdapterRegistry adapterRegistry, ISiteCredentialStore credentialStore, IUserNotificationService notifications, string? currentProvider)
    {
        InitializeComponent();
        Settings = settings;
        _settingsRepository = settingsRepository;
        _refreshService = refreshService;
        _adapterRegistry = adapterRegistry;
        _credentialStore = credentialStore;
        _notifications = notifications;
        _siteManagement = new SiteManagementUseCase(settingsRepository, refreshService.SnapshotRepository);
        _currentProvider = currentProvider;
        Title = "管理站点";
        Width = 1120;
        Height = 560;
        MinWidth = 900;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = System.Windows.Application.Current.MainWindow;

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
        var dialog = new SiteEditorDialog(null, Settings, _refreshService, _adapterRegistry, _credentialStore, _notifications) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Site is null) return;
        SaveSite(dialog.Site, null);
    }

    private void EditSelected()
    {
        var selected = Selected;
        if (selected is null) return;
        var original = Settings.Sites.First(x => string.Equals(x.ProviderId, selected.ProviderId, StringComparison.Ordinal));
        var dialog = new SiteEditorDialog(original, Settings, _refreshService, _adapterRegistry, _credentialStore, _notifications) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Site is null) return;
        SaveSite(dialog.Site, original.ProviderId);
    }

    private void SaveSite(SiteConfiguration site, string? originalProviderId)
    {
        Settings = _siteManagement.SaveSite(Settings, site, originalProviderId);
        RefreshRows(site.ProviderId);
    }

    private void ToggleSelected()
    {
        var selected = Selected;
        if (selected is null) return;
        Settings = _siteManagement.SetEnabled(Settings, selected.ProviderId, !selected.Enabled);
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
        Settings = _siteManagement.DeleteSite(Settings, selected.ProviderId);
        RefreshRows();
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



