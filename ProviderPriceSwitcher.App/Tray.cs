using System.ComponentModel;
using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ProviderPriceSwitcher.App;

public enum TrayCommand
{
    OpenWindow,
    StartOmp,
    Exit
}

public sealed record TrayStatus(string GatewayText, string RouteText);

public interface ITrayHost : IDisposable
{
    event Action<TrayCommand>? CommandRequested;
    void Update(TrayStatus status);
}

public sealed class TrayApplicationController : IDisposable
{
    private const string ExitConfirmation = "退出将停止本地网关，但不会终止已经启动的 OMP 进程。是否继续？";
    private readonly MainWindow _window;
    private readonly MainViewModel _viewModel;
    private readonly ITrayHost _tray;
    private readonly IUserNotificationService _notifications;
    private readonly Action _requestShutdown;
    private bool _allowWindowClose;
    private bool _disposed;

    public TrayApplicationController(
        MainWindow window,
        MainViewModel viewModel,
        ITrayHost tray,
        IUserNotificationService notifications,
        Action requestShutdown)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(requestShutdown);
        _window = window;
        _viewModel = viewModel;
        _tray = tray;
        _notifications = notifications;
        _requestShutdown = requestShutdown;
        _window.Closing += HandleWindowClosing;
        _viewModel.PropertyChanged += HandleViewModelPropertyChanged;
        _tray.CommandRequested += HandleTrayCommand;
        SyncStatus();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _allowWindowClose = true;
        _window.Closing -= HandleWindowClosing;
        _viewModel.PropertyChanged -= HandleViewModelPropertyChanged;
        _tray.CommandRequested -= HandleTrayCommand;
        _tray.Dispose();
    }

    private void HandleWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowWindowClose) return;
        e.Cancel = true;
        _window.Hide();
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(MainViewModel.GatewayStatusText)
            or nameof(MainViewModel.ActiveRouteStatusText))
            SyncStatus();
    }

    private void HandleTrayCommand(TrayCommand command)
    {
        void Execute()
        {
            if (_disposed) return;
            switch (command)
            {
                case TrayCommand.OpenWindow:
                    _window.Show();
                    if (_window.WindowState == System.Windows.WindowState.Minimized)
                        _window.WindowState = System.Windows.WindowState.Normal;
                    _window.Activate();
                    break;
                case TrayCommand.StartOmp:
                    if (_viewModel.StartOmpCommand.CanExecute(null))
                        _viewModel.StartOmpCommand.Execute(null);
                    break;
                case TrayCommand.Exit:
                    RequestExit();
                    break;
            }
        }

        if (_window.Dispatcher.CheckAccess())
            Execute();
        else
            _ = _window.Dispatcher.BeginInvoke(Execute);
    }

    private void RequestExit()
    {
        if (!_notifications.Confirm(ExitConfirmation, "退出 ProviderPriceSwitcher"))
            return;
        _allowWindowClose = true;
        _requestShutdown();
    }

    private void SyncStatus() => _tray.Update(new TrayStatus(_viewModel.GatewayStatusText, _viewModel.ActiveRouteStatusText));
}

public sealed class WindowsTrayHost : ITrayHost
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _gatewayStatus;
    private readonly Forms.ToolStripMenuItem _routeStatus;
    private readonly Drawing.Icon _icon;
    private readonly Stream _iconStream;
    private readonly EventHandler _doubleClickHandler;
    private bool _disposed;

    public WindowsTrayHost()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("/ProviderPriceSwitcher.App;component/Assets/AppIcon.ico", UriKind.Relative))
            ?? throw new InvalidOperationException("tray_icon_missing");
        _iconStream = resource.Stream;
        _icon = new Drawing.Icon(_iconStream);
        _menu = new Forms.ContextMenuStrip();
        AddCommand("打开主窗口", TrayCommand.OpenWindow);
        AddCommand("启动 OMP", TrayCommand.StartOmp);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _gatewayStatus = AddStatus("网关：检查中");
        _routeStatus = AddStatus("活动路由：检查中");
        _menu.Items.Add(new Forms.ToolStripSeparator());
        AddCommand("退出", TrayCommand.Exit);
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            ContextMenuStrip = _menu,
            Text = "ProviderPriceSwitcher",
            Visible = true
        };
        _doubleClickHandler = (_, _) => CommandRequested?.Invoke(TrayCommand.OpenWindow);
        _notifyIcon.DoubleClick += _doubleClickHandler;
    }

    public event Action<TrayCommand>? CommandRequested;

    public void Update(TrayStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (_disposed) return;
        _gatewayStatus.Text = $"网关：{status.GatewayText}";
        _routeStatus.Text = $"活动路由：{status.RouteText}";
        _notifyIcon.Text = TrimTooltip($"ProviderPriceSwitcher · {status.GatewayText}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.DoubleClick -= _doubleClickHandler;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        _iconStream.Dispose();
    }

    private Forms.ToolStripMenuItem AddStatus(string text)
    {
        var item = new Forms.ToolStripMenuItem(text) { Enabled = false };
        _menu.Items.Add(item);
        return item;
    }

    private void AddCommand(string text, TrayCommand command)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => CommandRequested?.Invoke(command);
        _menu.Items.Add(item);
    }

    private static string TrimTooltip(string text) => text.Length <= 63 ? text : text[..63];
}
