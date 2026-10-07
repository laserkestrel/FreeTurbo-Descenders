using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Security.Cryptography;

namespace DescendersBridge.Bluetooth;

// CA1416 (API newer than the oldest Windows the exe starts on): AdapterAsync checks the Windows version first and every caller asks it
// before scanning or connecting, so nothing below runs on a Windows without the API.
#pragma warning disable CA1416

/// <summary>
/// The real <see cref="IBleRadio"/>: the Bluetooth Low Energy API built into Windows 10 / 11. No NuGet package, no driver, no
/// pairing in Windows' settings. The ONLY file that names a Windows.Devices type: nothing here runs (or is even loaded) until Bluetooth is
/// asked for, and every failure of Windows itself (no radio, an older Windows without the API) comes back as "no Bluetooth", never as a crash.
/// </summary>
public sealed class WindowsBleRadio : IBleRadio
{
    /// <summary>How long a connect listens for the trainer's advertisement when Windows does not know the address yet.</summary>
    public const double FindSeconds = 8;

    /// <summary>The Windows version is checked before anything touches the API: on an older Windows the answer is "no Bluetooth", and the
    /// other methods are never reached (every caller asks this first).</summary>
    public Task<BleAdapterInfo> AdapterAsync() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
        ? AdapterCoreAsync()
        : Task.FromResult(new BleAdapterInfo(false, Detail: "Bluetooth needs Windows 10 version 2004 or newer"));

    private static async Task<BleAdapterInfo> AdapterCoreAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null) return new BleAdapterInfo(false, Detail: "Windows reports no Bluetooth adapter");
            bool? on = null;
            string detail = "adapter " + BleAddress.Format(adapter.BluetoothAddress);
            try
            {
                var radio = await adapter.GetRadioAsync();
                if (radio is not null) on = radio.State == RadioState.On;
                detail += radio is null ? ", radio state unknown" : $", radio \"{radio.Name}\" {radio.State}";
            }
            // Windows refuses the radio's on / off state to a 32-bit program on 64-bit Windows (this exe is x86 for the ANT+ library):
            // unknown is not "off"; a radio that IS off makes the scan or the connect fail with its own message
            catch (Exception ex) { detail += ", radio state not readable (" + ex.GetType().Name + (ex.Message.Length > 0 ? ": " + ex.Message : "") + ")"; }
            return new BleAdapterInfo(true, adapter.IsLowEnergySupported, adapter.IsCentralRoleSupported, on, detail);
        }
        catch (Exception ex) { return new BleAdapterInfo(false, Detail: ex.GetType().Name + ": " + ex.Message); }
    }

    private sealed class Heard
    {
        public string Name = "";
        public int Rssi = -127, Count;
        public readonly HashSet<Guid> Services = new();
    }

    public async Task<IReadOnlyList<BleAdvert>> ScanAsync(TimeSpan duration, CancellationToken ct)
    {
        var heard = new ConcurrentDictionary<ulong, Heard>();
        // active: also asks for the scan response, where most trainers put their name
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, a) =>
        {
            var h = heard.GetOrAdd(a.BluetoothAddress, _ => new Heard());
            lock (h)
            {
                h.Count++;
                h.Rssi = a.RawSignalStrengthInDBm;
                if (!string.IsNullOrEmpty(a.Advertisement.LocalName)) h.Name = a.Advertisement.LocalName;
                foreach (var u in a.Advertisement.ServiceUuids) h.Services.Add(u);
                // a fitness machine also announces itself in a "service data" section (0x16: the service's 16-bit
                // number, then its data); a trainer that leaves 0x1826 out of its service list is still a trainer by this
                try
                {
                    foreach (var section in a.Advertisement.GetSectionsByType(0x16))
                    {
                        CryptographicBuffer.CopyToByteArray(section.Data, out byte[] data);
                        if (data is { Length: >= 2 }) h.Services.Add(Ftms.Uuid((ushort)(data[0] | (data[1] << 8))));
                    }
                }
                catch { /* an odd packet must not take the scan down */ }
            }
        };
        BluetoothError stopped = BluetoothError.Success;   // a watcher Windows stops by itself says why (the radio is off, say)
        watcher.Stopped += (_, a) => stopped = a.Error;
        watcher.Start();
        try { await Task.Delay(duration, ct); }
        finally { watcher.Stop(); }
        if (stopped != BluetoothError.Success && heard.IsEmpty)
            throw new InvalidOperationException(stopped == BluetoothError.RadioNotAvailable ? BleRules.RadioOff : "Windows stopped the Bluetooth scan (" + stopped + ")");
        var list = new List<BleAdvert>();
        foreach (var (address, h) in heard)
            lock (h) list.Add(new BleAdvert(address, h.Name, h.Rssi, h.Count, h.Services.ToList()));
        return list;
    }

    public async Task<IBleConnection> ConnectAsync(ulong address, CancellationToken ct)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct);
        if (device is null)
        {
            // Windows only knows an address it has heard: listen for the trainer's advertisement, then ask again
            if (!await HeardAsync(address, TimeSpan.FromSeconds(FindSeconds), ct)) throw new InvalidOperationException("not heard (is it switched on and awake?)");
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct);
            if (device is null) throw new InvalidOperationException("heard, but Windows could not open it");
        }
        return new Connection(device);
    }

    private static async Task<bool> HeardAsync(ulong address, TimeSpan wait, CancellationToken ct)
    {
        var found = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, a) => { if (a.BluetoothAddress == address) found.TrySetResult(true); };
        watcher.Start();
        try { return await found.Task.WaitAsync(wait, ct); }
        catch (TimeoutException) { return false; }
        finally { watcher.Stop(); }
    }

    /// <summary>One device. Windows makes the radio connection at the first GATT request and keeps it while a service object is alive.</summary>
    private sealed class Connection : IBleConnection
    {
        private readonly BluetoothLEDevice _device;
        private readonly Dictionary<Guid, GattDeviceService> _services = new();
        private readonly Dictionary<(Guid, Guid), GattCharacteristic> _chars = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile bool _everConnected, _lost, _disposed;

        public Connection(BluetoothLEDevice device)
        {
            _device = device;
            _device.ConnectionStatusChanged += OnStatus;
        }

        private void OnStatus(BluetoothLEDevice d, object _)
        {
            try
            {
                if (d.ConnectionStatus == BluetoothConnectionStatus.Connected) _everConnected = true;
                else if (_everConnected) _lost = true;
            }
            catch { _lost = true; }
        }

        /// <summary>Until the first GATT request has connected it, the connection counts as up (a request that fails throws anyway).</summary>
        public bool Connected => !_disposed && !_lost;

        private async Task<GattCharacteristic?> FindAsync(Guid service, Guid characteristic, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_chars.TryGetValue((service, characteristic), out var known)) return known;
                if (!_services.TryGetValue(service, out var svc))
                {
                    var s = await _device.GetGattServicesForUuidAsync(service, BluetoothCacheMode.Uncached).AsTask(ct);
                    if (s.Status != GattCommunicationStatus.Success) throw new InvalidOperationException("the device did not answer (" + s.Status + ")");
                    if (s.Services.Count == 0) return null;
                    _everConnected = true;
                    _services[service] = svc = s.Services[0];
                }
                var c = await svc.GetCharacteristicsForUuidAsync(characteristic, BluetoothCacheMode.Uncached).AsTask(ct);
                if (c.Status != GattCommunicationStatus.Success) throw new InvalidOperationException("the device did not answer (" + c.Status + ")");
                if (c.Characteristics.Count == 0) return null;
                return _chars[(service, characteristic)] = c.Characteristics[0];
            }
            finally { _gate.Release(); }
        }

        public async Task<byte[]?> ReadAsync(Guid service, Guid characteristic, CancellationToken ct)
        {
            if (await FindAsync(service, characteristic, ct) is not { } ch) return null;
            var r = await ch.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct);
            if (r.Status != GattCommunicationStatus.Success) throw new InvalidOperationException("read failed (" + r.Status + ")");
            CryptographicBuffer.CopyToByteArray(r.Value, out byte[] bytes);
            return bytes ?? Array.Empty<byte>();
        }

        public async Task SubscribeAsync(Guid service, Guid characteristic, bool indicate, Action<byte[]> onValue, CancellationToken ct)
        {
            var ch = await FindAsync(service, characteristic, ct)
                ?? throw new InvalidOperationException(service == Ftms.Service
                    ? "this device has no Fitness Machine Service and no Tacx trainer service (it is not a trainer the bridge can control, or another app holds it)"
                    : "this device does not have the service asked for (" + BleRules.ServiceName(service) + "): it is not that kind of sensor");
            ch.ValueChanged += (_, a) =>
            {
                try
                {
                    CryptographicBuffer.CopyToByteArray(a.CharacteristicValue, out byte[] bytes);
                    if (bytes is not null) onValue(bytes);
                }
                catch { /* a bad packet must not take the radio's thread down */ }
            };
            var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                indicate ? GattClientCharacteristicConfigurationDescriptorValue.Indicate : GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
            if (status != GattCommunicationStatus.Success) throw new InvalidOperationException("could not subscribe (" + status + ")");
        }

        public async Task<bool> WriteAsync(Guid service, Guid characteristic, byte[] value, CancellationToken ct)
        {
            if (await FindAsync(service, characteristic, ct) is not { } ch) return false;
            // with response wherever the characteristic allows it; one that only takes writes without response (a maker's own service may) gets that
            var props = ch.CharacteristicProperties;
            var option = props.HasFlag(GattCharacteristicProperties.Write) || !props.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
            var r = await ch.WriteValueWithResultAsync(CryptographicBuffer.CreateFromByteArray(value), option).AsTask(ct);
            return r.Status == GattCommunicationStatus.Success;
        }

        public async Task<IReadOnlyList<Guid>> ServicesAsync(CancellationToken ct)
        {
            var s = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);
            if (s.Status != GattCommunicationStatus.Success) throw new InvalidOperationException("the device did not answer (" + s.Status + ")");
            var list = s.Services.Select(x => x.Uuid).ToList();
            foreach (var x in s.Services) try { x.Dispose(); } catch { }
            return list;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            try { _device.ConnectionStatusChanged -= OnStatus; } catch { }
            // Windows drops the radio connection once every service object and the device are closed
            foreach (var s in _services.Values) try { s.Dispose(); } catch { }
            try { _device.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}
