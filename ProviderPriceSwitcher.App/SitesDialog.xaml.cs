using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.App;

public interface ISitesDialogFactory
{
    SitesDialog Create(LocalAppSettings settings, string? currentProvider);
}

public sealed class SitesDialogFactory(Func<LocalAppSettings, string?, SitesDialog> create) : ISitesDialogFactory
{
    public SitesDialog Create(LocalAppSettings settings, string? currentProvider) => create(settings, currentProvider);
}

public sealed partial class SitesDialog : Window
{
    private readonly IPricingSnapshotQuery _snapshotQuery;
    private readonly ISiteEditorDialogFactory _editorFactory;
    private readonly SiteManagementUseCase _siteManagement;
    private readonly string? _currentProvider;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false };
    private readonly Button _edit = new() { Content = "编辑" };
    private readonly Button _toggle = new() { Content = "启用/禁用" };
    private readonly Button _delete = new() { Content = "删除" };
    public LocalAppSettings Settings { get; private set; }
    public SitesDialog(LocalAppSettings settings, SiteManagementUseCase siteManagement, IPricingSnapshotQuery snapshotQuery, ISiteEditorDialogFactory editorFactory, string? currentProvider)
    {
        InitializeComponent(); Settings = settings; _siteManagement = siteManagement; _snapshotQuery = snapshotQuery; _editorFactory = editorFactory; _currentProvider = currentProvider;
        Title = "管理站点"; Width = 1120; Height = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner; Owner = System.Windows.Application.Current.MainWindow;
        foreach (var column in new[] { ("站点名称", "DisplayName"), ("ProviderId", "ProviderId"), ("类型", "SiteType"), ("Base URL", "BaseUrl") }) _grid.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new System.Windows.Data.Binding(column.Item2) });
        _grid.SelectionChanged += (_, _) => UpdateButtons(); _grid.MouseDoubleClick += (_, _) => EditSelected();
        var add = new Button { Content = "新增" }; add.Click += (_, _) => AddSite(); _edit.Click += (_, _) => EditSelected(); _toggle.Click += (_, _) => ToggleSelected(); _delete.Click += (_, _) => DeleteSelected();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(add); buttons.Children.Add(_edit); buttons.Children.Add(_toggle); buttons.Children.Add(_delete); buttons.Children.Add(new Button { Content = "关闭", IsCancel = true });
        var panel = new Grid { Margin = new Thickness(16) }; panel.RowDefinitions.Add(new RowDefinition()); panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.Children.Add(_grid); Grid.SetRow(buttons, 1); panel.Children.Add(buttons); Content = panel; RefreshRows();
    }
    private SiteListRow? Selected => _grid.SelectedItem as SiteListRow;
    private void RefreshRows(string? selectProvider = null) { var result = _snapshotQuery.Load(); var snapshots = result.IsSuccess ? result.Snapshots : new Dictionary<string, PricingSnapshot>(StringComparer.Ordinal); var rows = Settings.Sites.Select(site => new SiteListRow(site, snapshots.GetValueOrDefault(site.ProviderId))).ToList(); _grid.ItemsSource = rows; _grid.SelectedItem = rows.FirstOrDefault(x => string.Equals(x.ProviderId, selectProvider, StringComparison.Ordinal)) ?? rows.FirstOrDefault(); UpdateButtons(); }
    private void UpdateButtons() { var selected = Selected; _edit.IsEnabled = selected is not null; _toggle.IsEnabled = selected is not null; _delete.IsEnabled = selected is not null; _toggle.Content = selected?.Enabled == true ? "禁用" : "启用"; }
    private void AddSite() { var dialog = _editorFactory.Create(null, Settings, this); if (dialog.ShowDialog() == true && dialog.Site is not null) SaveSite(dialog.Site, null); }
    private void EditSelected() { var selected = Selected; if (selected is null) return; var original = Settings.Sites.First(x => string.Equals(x.ProviderId, selected.ProviderId, StringComparison.Ordinal)); var dialog = _editorFactory.Create(original, Settings, this); if (dialog.ShowDialog() == true && dialog.Site is not null) SaveSite(dialog.Site, original.ProviderId); }
    private void SaveSite(SiteConfiguration site, string? originalProviderId) { Settings = _siteManagement.SaveSite(Settings, site, originalProviderId); RefreshRows(site.ProviderId); }
    private void ToggleSelected() { if (Selected is { } selected) { Settings = _siteManagement.SetEnabled(Settings, selected.ProviderId, !selected.Enabled); RefreshRows(selected.ProviderId); } }
    private void DeleteSelected() { if (Selected is not { } selected) return; if (MessageBox.Show($"确认删除站点“{selected.ProviderId}”及其本地价格快照？", "删除站点", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { Settings = _siteManagement.DeleteSite(Settings, selected.ProviderId); RefreshRows(); } }
}

internal sealed class SiteListRow
{
    public SiteListRow(SiteConfiguration site, PricingSnapshot? snapshot) { ProviderId = site.ProviderId; DisplayName = string.IsNullOrWhiteSpace(site.DisplayName) ? site.ProviderId : site.DisplayName; SiteType = site.SiteType; BaseUrl = site.BaseUrl; CurrentGroup = site.CurrentGroup; RatioDisplay = (site.CurrentGroupRatio ?? snapshot?.CurrentGroupRatio)?.ToString("0.####", CultureInfo.InvariantCulture) ?? "—"; Enabled = site.Enabled; EnabledDisplay = site.Enabled ? "启用" : "禁用"; CheckedAt = snapshot?.RefreshedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—"; }
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
