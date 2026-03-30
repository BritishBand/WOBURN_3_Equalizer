using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace WoburnEQ;

public class BleService : IDisposable
{
    private const string DeviceName = "WOBURN III";
    private static readonly Guid EqServiceUuid = Guid.Parse("0000aa00-0000-1000-8000-00805f9b34fb");
    private static readonly Guid EqCharUuid = Guid.Parse("0000aa16-0000-1000-8000-00805f9b34fb");
    private static readonly Guid VolumeCharUuid = Guid.Parse("0000aa08-0000-1000-8000-00805f9b34fb");

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _eqChar;
    private GattCharacteristic? _volumeChar;
    private bool _connected;

    public bool IsConnected => _connected;

    public event Action<int, int>? EqChanged;
    public event Action<int>? VolumeChanged;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? StatusUpdated;

    public async Task ConnectAsync()
    {
        StatusUpdated?.Invoke("Scanning...");
        var address = await ScanForDeviceAsync();

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (_device == null)
            throw new Exception($"{DeviceName} not found");

        StatusUpdated?.Invoke($"Found {_device.Name}");

        var pairing = _device.DeviceInformation.Pairing;
        if (!pairing.IsPaired)
            await pairing.PairAsync();

        var servicesResult = await _device.GetGattServicesForUuidAsync(
            EqServiceUuid, BluetoothCacheMode.Uncached);
        if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            throw new Exception("EQ service not found");

        var service = servicesResult.Services[0];
        var charsResult = await service.GetCharacteristicsForUuidAsync(
            EqCharUuid, BluetoothCacheMode.Uncached);
        if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
            throw new Exception("EQ characteristic not found");

        _eqChar = charsResult.Characteristics[0];

        var eqNotify = await _eqChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (eqNotify == GattCommunicationStatus.Success)
            _eqChar.ValueChanged += OnEqValueChanged;

        var volCharsResult = await service.GetCharacteristicsForUuidAsync(
            VolumeCharUuid, BluetoothCacheMode.Uncached);
        if (volCharsResult.Status == GattCommunicationStatus.Success && volCharsResult.Characteristics.Count > 0)
        {
            _volumeChar = volCharsResult.Characteristics[0];
            var volNotify = await _volumeChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (volNotify == GattCommunicationStatus.Success)
                _volumeChar.ValueChanged += OnVolumeValueChanged;
        }

        _connected = true;
        ConnectionChanged?.Invoke(true);
    }

    private void OnEqValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = args.CharacteristicValue.ToArray();
        if (data.Length >= 5)
            EqChanged?.Invoke(data[0], data[4]);
    }

    private void OnVolumeValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = args.CharacteristicValue.ToArray();
        if (data.Length >= 1)
            VolumeChanged?.Invoke(data[0]);
    }

    public async Task<int> ReadVolumeAsync()
    {
        if (_volumeChar == null) throw new InvalidOperationException("Not connected");

        var result = await _volumeChar.ReadValueAsync();
        if (result.Status != GattCommunicationStatus.Success)
            throw new Exception("Failed to read volume");

        return result.Value.ToArray()[0];
    }

    public async Task WriteVolumeAsync(int volume)
    {
        if (_volumeChar == null) throw new InvalidOperationException("Not connected");

        volume = Math.Clamp(volume, 0, 31);
        var data = new byte[] { (byte)volume };
        var result = await _volumeChar.WriteValueAsync(data.AsBuffer());

        if (result != GattCommunicationStatus.Success)
            throw new Exception("Failed to write volume");
    }

    public async Task<(int Bass, int Treble)> ReadEqAsync()
    {
        if (_eqChar == null) throw new InvalidOperationException("Not connected");

        var result = await _eqChar.ReadValueAsync();
        if (result.Status != GattCommunicationStatus.Success)
            throw new Exception("Failed to read EQ");

        var data = result.Value.ToArray();
        return (data[0], data[4]);
    }

    public async Task WriteEqAsync(int bass, int treble)
    {
        if (_eqChar == null) throw new InvalidOperationException("Not connected");

        bass = Math.Clamp(bass, 0, 10);
        treble = Math.Clamp(treble, 0, 10);

        var data = new byte[] { (byte)bass, 0xFF, 0xFF, 0xFF, (byte)treble };
        var result = await _eqChar.WriteValueAsync(data.AsBuffer());

        if (result != GattCommunicationStatus.Success)
            throw new Exception("Failed to write EQ");
    }

    private static async Task<ulong> ScanForDeviceAsync()
    {
        var tcs = new TaskCompletionSource<ulong>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, args) =>
        {
            if (args.Advertisement.LocalName?.Contains(DeviceName, StringComparison.OrdinalIgnoreCase) == true)
                tcs.TrySetResult(args.BluetoothAddress);
        };

        watcher.Start();

        var timeout = Task.Delay(TimeSpan.FromSeconds(10));
        var completed = await Task.WhenAny(tcs.Task, timeout);

        watcher.Stop();

        if (completed == timeout)
            throw new Exception($"{DeviceName} not found. Make sure it's on and nearby.");

        return tcs.Task.Result;
    }

    public void Dispose()
    {
        _device?.Dispose();
        _device = null;
        _connected = false;
    }
}
