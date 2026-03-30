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
        _popup.Top = workArea.Bottom - 200;
        _popup.Show();
        _popup.Activate();
    }

private static Icon CreateIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.FillEllipse(Brushes.DodgerBlue, 1, 1, 14, 14);
        g.DrawString("W", new Font("Segoe UI", 8, System.Drawing.FontStyle.Bold),
            Brushes.White, 1, 1);
        var handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _ble?.Dispose();
        base.OnExit(e);
    }
}
