using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Infrastructure;

namespace ProviderPriceSwitcher.App;
public sealed partial class SettingsDialog : Window
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
        InitializeComponent();
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
        Owner = System.Windows.Application.Current.MainWindow;

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
