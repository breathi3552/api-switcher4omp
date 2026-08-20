using System.Diagnostics;
using System.Windows;
namespace ProviderPriceSwitcher.App;

public interface IExternalUriLauncher
{
    void Launch(Uri uri);
}

public sealed class ShellUriLauncher : IExternalUriLauncher
{
    public void Launch(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

public partial class MainWindow : Window
{
    private readonly IExternalUriLauncher _uriLauncher;

    public MainWindow(MainViewModel viewModel, IExternalUriLauncher? uriLauncher = null)
    {
        InitializeComponent();
        _uriLauncher = uriLauncher ?? new ShellUriLauncher();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.InitializeCommand.Execute(null);
    }

    private void PriceGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid)
            TryLaunchPriceRow(grid, e.OriginalSource, e.ChangedButton);
    }

    internal bool TryLaunchPriceRow(System.Windows.Controls.DataGrid grid, object? originalSource, System.Windows.Input.MouseButton button)
    {
        if (button != System.Windows.Input.MouseButton.Left || !TryGetHitPriceRow(grid, originalSource, out var row) || row is not { IsSiteFirstRow: true, KeysUri: not null })
            return false;

        _uriLauncher.Launch(row.KeysUri);
        return true;
    }

    internal static bool TryGetHitPriceRow(System.Windows.Controls.DataGrid grid, object? originalSource, out PriceRow? row)
    {
        row = null;
        if (originalSource is not System.Windows.DependencyObject source)
            return false;

        var hitRow = System.Windows.Controls.ItemsControl.ContainerFromElement(grid, source) as System.Windows.Controls.DataGridRow;
        row = hitRow?.DataContext as PriceRow;
        return row is not null;
    }
}
