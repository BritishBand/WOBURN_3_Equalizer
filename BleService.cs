using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

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

    private void Log(string message, bool showInUi = true)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
        if (showInUi)
            StatusUpdated?.Invoke(message);
    }

    private static void TrimOldLog()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var firstLine = File.ReadLines(LogPath).FirstOrDefault();
            if (firstLine == null) return;
            // parse "yyyy-MM-dd HH:mm:ss.fff ..."
            if (DateTime.TryParse(firstLine.AsSpan(0, 23), out var firstDate)
                && DateTime.Now - firstDate > TimeSpan.FromDays(7))
            {
                File.WriteAllText(LogPath, "");
            }
        }
        catch { }
    }

    public async Task ConnectAsync()
    {
        TrimOldLog();
        try
        {
            await ConnectInternalAsync();
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            Dispose();
            throw;
        }
    }

    private async Task ConnectInternalAsync()
    {
        Log("Scanning...");
        var (address, addressType) = await ScanForDeviceAsync();

        Log("Connecting...");
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType);
        if (_device == null)
            throw new Exception($"{DeviceName} not found");

        Log($"Found {_device.Name} @ {address:X12}", showInUi: false);

        // Diagnostic probe: try GetGattServicesAsync BEFORE pairing to see if speaker
        // exposes services without a bond. Result is only logged, logic is unchanged.
        try
        {
            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var probe = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached)
                .AsTask().WaitAsync(probeCts.Token);
            var hasEq = probe.Status == GattCommunicationStatus.Success
                && probe.Services.Any(s => s.Uuid == EqServiceUuid);
            Log($"Pre-pair probe: {probe.Status}, {probe.Services.Count} svc, EQ={hasEq}", showInUi: false);
            foreach (var s in probe.Services)
                s.Dispose();
        }
        catch (OperationCanceledException)
        {
            Log("Pre-pair probe: timeout", showInUi: false);
        }
        catch (Exception ex)
        {
            Log($"Pre-pair probe: {ex.Message}", showInUi: false);
        }

        // Step 1: Pair BEFORE creating GattSession (session locks security level)
        var pairing = _device.DeviceInformation.Pairing;
        Log($"Paired: {pairing.IsPaired}, CanPair: {pairing.CanPair}", showInUi: false);
        bool justPaired = false;

        if (!pairing.IsPaired && pairing.CanPair)
        {
            justPaired = await TryPairAsync(pairing);
        }

        // Step 2: If we just paired, dispose and reconnect to get encrypted connection
        if (justPaired)
        {
            Log("Reconnecting with encryption...", showInUi: false);
            _device.Dispose();
            _device = null;
            await Task.Delay(500);

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType);
            if (_device == null)
                throw new Exception($"{DeviceName} reconnect failed after pairing");
        }

        // Step 3: NOW create GattSession on the (encrypted) connection
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;

        // Step 4: Discover services with retry + auto re-pair on Unreachable
        GattDeviceService? service = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var allServices = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached)
                    .AsTask().WaitAsync(cts.Token);
                Log($"#{attempt}: {allServices.Status}, {allServices.Services.Count} svc", showInUi: false);

                if (allServices.Status == GattCommunicationStatus.Success)
                {
                    foreach (var s in allServices.Services)
                    {
                        if (s.Uuid == EqServiceUuid)
                            service = s;
                    }
                }
                else if (allServices.Status == GattCommunicationStatus.Unreachable
                         && (attempt == 1 || attempt == 3))
                {
                    await ReconnectWithRepairAsync(address, addressType);
                }
            }
            catch (OperationCanceledException)
            {
                Log($"#{attempt}: timeout", showInUi: false);
            }

            if (service != null) break;
            await Task.Delay(1000);
        }

        if (service == null)
            throw new Exception("EQ service not found");

        _service = service;

        Log("Reading characteristics...", showInUi: false);
        var allChars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        Log($"Chars: {allChars.Status}, {allChars.Characteristics.Count} total", showInUi: false);
        foreach (var c in allChars.Characteristics)
            Log($"  char: {c.Uuid} [{string.Join(",", c.CharacteristicProperties)}]", showInUi: false);

        _eqChar = allChars.Characteristics.FirstOrDefault(c => c.Uuid == EqCharUuid);
        if (_eqChar == null)
            throw new Exception("EQ characteristic not found");

        var eqNotify = await _eqChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (eqNotify == GattCommunicationStatus.Success)
            _eqChar.ValueChanged += OnEqValueChanged;
        Log("EQ notify: " + eqNotify, showInUi: false);

        _volumeChar = allChars.Characteristics.FirstOrDefault(c => c.Uuid == VolumeCharUuid);
        if (_volumeChar != null)
        {
            var volNotify = await _volumeChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (volNotify == GattCommunicationStatus.Success)
                _volumeChar.ValueChanged += OnVolumeValueChanged;
            Log("Vol notify: " + volNotify, showInUi: false);
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
        {
            Log($"Read volume failed: {result.Status}");
            throw new Exception("Failed to read volume");
        }

        return result.Value.ToArray()[0];
    }

    public async Task WriteVolumeAsync(int volume)
    {
        if (_volumeChar == null) throw new InvalidOperationException("Not connected");

        volume = Math.Clamp(volume, 0, 31);
        var data = new byte[] { (byte)volume };
        Log($"WriteVol: {volume}", showInUi: false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await _volumeChar.WriteValueWithResultAsync(data.AsBuffer()).AsTask().WaitAsync(cts.Token);
            Log($"WriteVol result: {result.Status}, ProtocolError: {result.ProtocolError}", showInUi: false);
            if (result.Status != GattCommunicationStatus.Success)
            {
                Log($"Vol write failed: {result.Status} ({result.ProtocolError})");
                if (IsEncryptionError(result.ProtocolError) && await TryRecoverEncryptionAsync())
                    await WriteVolumeAsync(volume);
            }
        }
        catch (Exception ex)
        {
            Log($"WriteVol exception: {ex.Message}");
            throw;
        }
    }

    public async Task<(int Bass, int Treble)> ReadEqAsync()
    {
        if (_eqChar == null) throw new InvalidOperationException("Not connected");

        var result = await _eqChar.ReadValueAsync();
        if (result.Status != GattCommunicationStatus.Success)
        {
            Log($"Read EQ failed: {result.Status}");
            throw new Exception("Failed to read EQ");
        }

        var data = result.Value.ToArray();
        return (data[0], data[4]);
    }

    public async Task WriteEqAsync(int bass, int treble)
    {
        if (_eqChar == null) throw new InvalidOperationException("Not connected");

        bass = Math.Clamp(bass, 0, 10);
        treble = Math.Clamp(treble, 0, 10);

        var data = new byte[] { (byte)bass, 0xFF, 0xFF, 0xFF, (byte)treble };
        Log($"WriteEQ: bass={bass} treble={treble}", showInUi: false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await _eqChar.WriteValueWithResultAsync(data.AsBuffer()).AsTask().WaitAsync(cts.Token);
            Log($"WriteEQ result: {result.Status}, ProtocolError: {result.ProtocolError}", showInUi: false);
            if (result.Status != GattCommunicationStatus.Success)
            {
                Log($"EQ write failed: {result.Status} ({result.ProtocolError})");
                if (IsEncryptionError(result.ProtocolError) && await TryRecoverEncryptionAsync())
                    await WriteEqAsync(bass, treble);
            }
        }
        catch (Exception ex)
        {
            Log($"WriteEQ exception: {ex.Message}");
            throw;
        }
    }

    // ATT 0x05 InsufficientAuthentication / 0x0F InsufficientEncryption — the
    // link is unencrypted because the bond is missing or stale (e.g. the
    // speaker evicted us after another device paired).
    private static bool IsEncryptionError(byte? protocolError) => protocolError is 5 or 15;

    private DateTime _lastRecovery = DateTime.MinValue;

    private async Task<bool> TryRecoverEncryptionAsync()
    {
        if (DateTime.Now - _lastRecovery < TimeSpan.FromSeconds(30))
            return false;
        _lastRecovery = DateTime.Now;

        Log("Insufficient encryption, re-pairing...");
        try
        {
            var pairing = _device?.DeviceInformation.Pairing;
            if (pairing?.IsPaired == true)
            {
                var unpair = await pairing.UnpairAsync();
                Log($"Unpair: {unpair.Status}", showInUi: false);
                await Task.Delay(1000);
            }

            Dispose();
            await ConnectAsync();
            return _connected;
        }
        catch (Exception ex)
        {
            Log($"Recovery failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TryPairAsync(DeviceInformationPairing pairing)
    {
        // Plain PairAsync has no consent broker in a desktop app and fails with
        // a generic Failed status. Custom pairing with an auto-accepting
        // ConfirmOnly handler is required for Just Works bonding to complete.
        var custom = pairing.Custom;
        custom.PairingRequested += OnPairingRequested;

        DevicePairingProtectionLevel[] levels =
        [
            DevicePairingProtectionLevel.Encryption,
            DevicePairingProtectionLevel.Default,
            DevicePairingProtectionLevel.EncryptionAndAuthentication,
        ];

        try
        {
            foreach (var level in levels)
            {
                try
                {
                    // AsTask(token) cancels the underlying WinRT operation on
                    // timeout — otherwise it stays pending and later attempts
                    // fail with OperationAlreadyInProgress. Right after an
                    // unpair the speaker can take >10s to complete bonding.
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly, level)
                        .AsTask(cts.Token);
                    Log($"Pair ({level}): {result.Status}", showInUi: false);

                    if (result.Status == DevicePairingResultStatus.Paired
                        || result.Status == DevicePairingResultStatus.AlreadyPaired)
                        return true;
                }
                catch (OperationCanceledException)
                {
                    Log($"Pair ({level}): timeout", showInUi: false);
                }

                if (pairing.IsPaired)
                {
                    Log($"Pair ({level}): completed late, IsPaired=true", showInUi: false);
                    return true;
                }
            }
        }
        finally
        {
            custom.PairingRequested -= OnPairingRequested;
        }

        Log("All pairing attempts failed", showInUi: false);
        return false;
    }

    private static void OnPairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
    {
        args.Accept();
    }

    private async Task ReconnectWithRepairAsync(ulong address, BluetoothAddressType addressType)
    {
        var pairing = _device!.DeviceInformation.Pairing;
        if (pairing.IsPaired)
        {
            Log("Unreachable, unpairing...", showInUi: false);
            await pairing.UnpairAsync();
            await Task.Delay(1000);
        }

        // Dispose old connection
        _session?.Dispose();
        _session = null;
        _device?.Dispose();
        _device = null;

        await Task.Delay(500);

        // Reconnect
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType);
        if (_device == null)
            throw new Exception($"{DeviceName} reconnect failed");

        // Re-pair
        var newPairing = _device.DeviceInformation.Pairing;
        if (!newPairing.IsPaired && newPairing.CanPair)
            await TryPairAsync(newPairing);

        // Dispose and reconnect again to get encrypted session
        _device.Dispose();
        _device = null;
        await Task.Delay(500);

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType);
        if (_device == null)
            throw new Exception($"{DeviceName} reconnect failed after re-pair");

        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;
        Log("Reconnected after re-pair", showInUi: false);
    }

    private async Task<(ulong Address, BluetoothAddressType AddressType)> ScanForDeviceAsync()
    {
        // The speaker runs two advertising sets under the same name: the vendor
        // identity (advertises the aa00 EQ service) and a Google Fast Pair
        // identity with a different address. Only the vendor identity accepts
        // standard bonding, so prefer the advertisement carrying aa00 and fall
        // back to a name-only match if it never shows up.
        var tcs = new TaskCompletionSource<(ulong, BluetoothAddressType)>();
        (ulong Address, BluetoothAddressType AddressType)? fallback = null;

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, args) =>
        {
            if (args.Advertisement.ServiceUuids.Contains(EqServiceUuid))
                tcs.TrySetResult((args.BluetoothAddress, args.BluetoothAddressType));
            else if (args.Advertisement.LocalName?.Contains(DeviceName, StringComparison.OrdinalIgnoreCase) == true)
                fallback ??= (args.BluetoothAddress, args.BluetoothAddressType);
        };

        watcher.Start();

        var timeout = Task.Delay(TimeSpan.FromSeconds(10));
        var completed = await Task.WhenAny(tcs.Task, timeout);

        watcher.Stop();

        if (completed != timeout)
            return tcs.Task.Result;

        if (fallback is { } fb)
        {
            Log($"EQ adv not seen, using name match @ {fb.Address:X12}", showInUi: false);
            return fb;
        }

        throw new Exception($"{DeviceName} not found. Make sure it's on and nearby.");
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
