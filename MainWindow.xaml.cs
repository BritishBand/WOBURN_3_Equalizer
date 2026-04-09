using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WoburnEQ;

public partial class MainWindow : Window
{
    private readonly BleService _ble;
    private bool _suppressEvents;

    public MainWindow(BleService ble)
    {
        InitializeComponent();
        _ble = ble;
        _ble.EqChanged += OnEqChanged;
        _ble.VolumeChanged += OnVolumeChanged;
        _ble.StatusUpdated += OnStatusUpdated;
        Loaded += OnLoaded;
        Closed += OnClosed;

        BassSlider.PreviewMouseWheel += Slider_MouseWheel;
        TrebleSlider.PreviewMouseWheel += Slider_MouseWheel;
        VolumeSlider.PreviewMouseWheel += Slider_MouseWheel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_ble.IsConnected)
                await _ble.ConnectAsync();

            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x44, 0xBB, 0x44));
            StatusText.Text = "Connected";

            var (bass, treble) = await _ble.ReadEqAsync();
            _suppressEvents = true;
            BassSlider.Value = bass;
            TrebleSlider.Value = treble;
            BassValue.Text = bass.ToString();
            TrebleValue.Text = treble.ToString();

            var volume = await _ble.ReadVolumeAsync();
            VolumeSlider.Value = volume;
            VolumeValue.Text = volume.ToString();
            _suppressEvents = false;
        }
        catch (Exception ex)
        {
            _ble.Dispose();
            StatusText.Text = ex.Message;
        }
    }

    private void OnEqChanged(int bass, int treble)
    {
        Dispatcher.Invoke(() =>
        {
            _suppressEvents = true;
            BassSlider.Value = bass;
            TrebleSlider.Value = treble;
            BassValue.Text = bass.ToString();
            TrebleValue.Text = treble.ToString();
            _suppressEvents = false;
        });
    }

    private void OnVolumeChanged(int volume)
    {
        Dispatcher.Invoke(() =>
        {
            _suppressEvents = true;
            VolumeSlider.Value = volume;
            VolumeValue.Text = volume.ToString();
            _suppressEvents = false;
        });
    }

    private async void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var val = (int)e.NewValue;
        VolumeValue.Text = val.ToString();
        if (_suppressEvents) return;

        try
        {
            await _ble.WriteVolumeAsync(val);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Vol write: {ex.Message}";
        }
    }

    private async void BassSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var val = (int)e.NewValue;
        BassValue.Text = val.ToString();
        if (_suppressEvents) return;

        try
        {
            await _ble.WriteEqAsync(val, (int)TrebleSlider.Value);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"EQ write: {ex.Message}";
        }
    }

    private async void TrebleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var val = (int)e.NewValue;
        TrebleValue.Text = val.ToString();
        if (_suppressEvents) return;

        try
        {
            await _ble.WriteEqAsync((int)BassSlider.Value, val);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"EQ write: {ex.Message}";
        }
    }

    private void Slider_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var slider = (Slider)sender;
        slider.Value += e.Delta > 0 ? 1 : -1;
        e.Handled = true;
    }

    private void OnStatusUpdated(string status)
    {
        Dispatcher.Invoke(() => StatusText.Text = status);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ble.EqChanged -= OnEqChanged;
        _ble.VolumeChanged -= OnVolumeChanged;
        _ble.StatusUpdated -= OnStatusUpdated;
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        Hide();
    }
}
