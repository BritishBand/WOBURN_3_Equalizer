using System.Drawing;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace WoburnEQ;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MainWindow? _popup;
    private BleService? _ble;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _ble = new BleService();

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) =>
        {
            _ble?.Dispose();
            _trayIcon?.Dispose();
            Shutdown();
        };

        _trayIcon = new TaskbarIcon
        {
            Icon = CreateIcon(),
            ToolTipText = "WOBURN III EQ",
            ContextMenu = new System.Windows.Controls.ContextMenu
            {
                Items = { exitItem }
            }
        };
        _trayIcon.TrayLeftMouseUp += TrayIcon_Click;
    }

    private void TrayIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_popup == null || !_popup.IsLoaded)
            _popup = new MainWindow(_ble!);

        if (_popup.IsVisible)
        {
            _popup.Hide();
            return;
        }

        var pos = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(pos);
        var workArea = screen.WorkingArea;

        _popup.Left = Math.Min(pos.X - 140, workArea.Right - 280);
        _popup.Top = workArea.Bottom - 150;
        _popup.Show();
        _popup.Activate();
    }

private static Icon CreateIcon()
    {
        var uri = new Uri("pack://application:,,,/marshall.ico");
        using var stream = System.Windows.Application.GetResourceStream(uri).Stream;
        return new Icon(stream, new System.Drawing.Size(32, 32));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _ble?.Dispose();
        base.OnExit(e);
    }
}
