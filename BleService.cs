using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace WoburnEQ;

public class BleService : IDisposable
{
    private const string DeviceName = "WOBURN III";
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "woburn.log");
    private static readonly Guid EqServiceUuid = Guid.Parse("0000aa00-0000-1000-8000-00805f9b34fb");
    private static readonly Guid EqCharUuid = Guid.Parse("0000aa16-0000-1000-8000-00805f9b34fb");
    private static readonly Guid VolumeCharUuid = Guid.Parse("0000aa08-0000-1000-8000-00805f9b34fb");

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _service;
    private GattCharacteristic? _eqChar;
    private GattCharacteristic? _volumeChar;
    private bool _connected;

    public bool IsConnected => _connected;

    public event Action<int, int>? EqChanged;
    public event Action<int>? VolumeChanged;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? StatusUpdated;

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
        StatusUpdated?.Invoke(message);
    }

    public async Task ConnectAsync()
    {
        try
        {
            await ConnectInternalAsync();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private async Task ConnectInternalAsync()
    {
        Log("Scanning...");
        var (address, addressType) = await ScanForDeviceAsync();

        Log($"Connecting ({addressType})...");
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType);
        if (_device == null)
            throw new Exception($"{DeviceName} not found");

        Log($"Found {_device.Name}");

        Log("Requesting access...");
        var access = await _device.RequestAccessAsync();
        Log($"Access: {access}");

        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;

        var pairing = _device.DeviceInformation.Pairing;
        Log($"Paired: {pairing.IsPaired}, CanPair: {pairing.CanPair}");
        if (!pairing.IsPaired && pairing.CanPair)
        {
            var custom = pairing.Custom;
            custom.PairingRequested += (sender, args) =>
            {
                Log($"PairingRequested: {args.PairingKind}");
                args.Accept();
            };

            using var pairCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var pairResult = await custom.PairAsync(
                    Windows.Devices.Enumeration.DevicePairingKinds.ConfirmOnly,
                    Windows.Devices.Enumeration.DevicePairingProtectionLevel.Default
                ).AsTask().WaitAsync(pairCts.Token);
                Log($"Pair: {pairResult.Status}");

                if (pairResult.Status != Windows.Devices.Enumeration.DevicePairingResultStatus.Paired
                    && pairResult.Status != Windows.Devices.Enumeration.DevicePairingResultStatus.AlreadyPaired)
                {
                    Log("Retrying pair with None protection...");
                    pairResult = await custom.PairAsync(
                        Windows.Devices.Enumeration.DevicePairingKinds.ConfirmOnly,
                        Windows.Devices.Enumeration.DevicePairingProtectionLevel.None
                    ).AsTask().WaitAsync(pairCts.Token);
                    Log($"Pair retry: {pairResult.Status}");
                }
            }
            catch (OperationCanceledException)
            {
                Log("Pair: timeout");
            }

            Log($"Paired after: {pairing.IsPaired}");
        }

        Log("Reading GATT services...");

        GattDeviceService? service = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var task = _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask();
                var allServices = await task.WaitAsync(cts.Token);
                Log($"#{attempt}: {allServices.Status}, {allServices.Services.Count} svc");

                if (allServices.Status == GattCommunicationStatus.Success)
                {
                    foreach (var s in allServices.Services)
                    {
                        Log($"  svc: {s.Uuid}");
                        if (s.Uuid == EqServiceUuid)
                            service = s;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log($"#{attempt}: timeout");
            }

            if (service != null) break;
            await Task.Delay(2000);
        }

        if (service == null)
            throw new Exception("EQ service not found");

        _service = service;

        Log("Reading characteristics...");
        var allChars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        Log($"Chars: {allChars.Status}, {allChars.Characteristics.Count} total");
        foreach (var c in allChars.Characteristics)
            Log($"  char: {c.Uuid} [{string.Join(",", c.CharacteristicProperties)}]");

        _eqChar = allChars.Characteristics.FirstOrDefault(c => c.Uuid == EqCharUuid);
        if (_eqChar == null)
            throw new Exception("EQ characteristic not found");

        var eqNotify = await _eqChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (eqNotify == GattCommunicationStatus.Success)
            _eqChar.ValueChanged += OnEqValueChanged;
        Log("EQ notify: " + eqNotify);

        _volumeChar = allChars.Characteristics.FirstOrDefault(c => c.Uuid == VolumeCharUuid);
        if (_volumeChar != null)
        {
            var volNotify = await _volumeChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (volNotify == GattCommunicationStatus.Success)
                _volumeChar.ValueChanged += OnVolumeValueChanged;
            Log("Vol notify: " + volNotify);
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
        Log($"WriteVol: {volume}");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await _volumeChar.WriteValueWithResultAsync(data.AsBuffer()).AsTask().WaitAsync(cts.Token);
        Log($"WriteVol result: {result.Status}, ProtocolError: {result.ProtocolError}");
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
        Log($"WriteEQ: bass={bass} treble={treble}");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await _eqChar.WriteValueWithResultAsync(data.AsBuffer()).AsTask().WaitAsync(cts.Token);
        Log($"WriteEQ result: {result.Status}, ProtocolError: {result.ProtocolError}");
    }

    private static async Task<(ulong Address, BluetoothAddressType AddressType)> ScanForDeviceAsync()
    {
        var tcs = new TaskCompletionSource<(ulong, BluetoothAddressType)>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, args) =>
        {
            if (args.Advertisement.LocalName?.Contains(DeviceName, StringComparison.OrdinalIgnoreCase) == true)
                tcs.TrySetResult((args.BluetoothAddress, args.BluetoothAddressType));
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
        if (_eqChar != null)
        {
            _eqChar.ValueChanged -= OnEqValueChanged;
            _eqChar = null;
        }

        if (_volumeChar != null)
        {
            _volumeChar.ValueChanged -= OnVolumeValueChanged;
            _volumeChar = null;
        }

        _service?.Dispose();
        _service = null;

        _session?.Dispose();
        _session = null;

        _device?.Dispose();
        _device = null;

        _connected = false;
        ConnectionChanged?.Invoke(false);
    }
}
