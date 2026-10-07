using System.Collections.Concurrent;
using ANT_Managed_Library;

namespace DescendersBridge.Ant;

/// <summary>
/// Continuous ANT+ scan on 2457 MHz: hears every ANT+ device in range, whatever its type, and keeps a
/// <see cref="DeviceTracker"/> for each. A wildcard slave channel can only ever report the first device that answers;
/// scan mode plus extended messages (which append each sender's channel ID) is what makes "list everything" possible.
/// Scan mode takes the whole radio, so it must be closed before dedicated device channels are opened.
/// </summary>
public sealed class AntScanner : IDisposable
{
    private const uint Wait = 500;
    private const byte RfFrequency = 57;   // 2457 MHz, all ANT+ sport devices

    private readonly byte[] _networkKey;
    private readonly ConcurrentDictionary<DeviceKey, DeviceTracker> _devices = new();
    private ANT_Device? _device;
    private ANT_Channel? _channel;
    private long _messages, _noExt;

    public event Action<string>? Status;
    public long Messages => Interlocked.Read(ref _messages);
    public long MessagesWithoutId => Interlocked.Read(ref _noExt);
    public IReadOnlyCollection<DeviceTracker> Devices => _devices.Values.ToArray();

    public AntScanner(byte[] networkKey) { _networkKey = networkKey; }

    public void Start()
    {
        _device = new ANT_Device();   // throws if the stick is absent / driver not bound
        Status?.Invoke($"ANT stick #{_device.getSerialNumber()} {_device.getDeviceUSBInfo().printProductDescription().Trim()}");

        Check(_device.setNetworkKey(0, _networkKey, Wait), "setNetworkKey");
        Check(_device.enableRxExtendedMessages(true, Wait), "enableRxExtendedMessages (stick lacks extended messages?)");

        _channel = _device.getChannel(0);
        _channel.channelResponse += OnResponse;
        Check(_channel.assignChannel(ANT_ReferenceLibrary.ChannelType.BASE_Slave_Receive_0x00, 0, Wait), "assignChannel");
        Check(_channel.setChannelID(0, false, 0, 0, Wait), "setChannelID (wildcard)");
        Check(_channel.setChannelFreq(RfFrequency, Wait), "setChannelFreq");
        Check(_device.openRxScanMode(Wait), "openRxScanMode (stick lacks continuous scan?)");
        Status?.Invoke("scan mode open: listening for every ANT+ device");
    }

    private void OnResponse(ANT_Response r)
    {
        try
        {
            // 0x4E broadcast, 0x5D extended broadcast; acknowledged/burst are not sensors' periodic data
            if (r.responseID != 0x4E && r.responseID != 0x5D) return;
            Interlocked.Increment(ref _messages);
            if (!r.isExtended()) { Interlocked.Increment(ref _noExt); return; }

            var id = r.getDeviceIDfromExt();
            var key = new DeviceKey(id.deviceTypeID, id.deviceNumber, id.transmissionTypeID);
            var t = _devices.GetOrAdd(key, k => new DeviceTracker(k));
            lock (t) t.Update(r.getDataPayload());
        }
        catch (Exception ex) { Status?.Invoke($"response handler error: {ex.Message}"); }
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException($"{what} failed.");
    }

    public void Dispose()
    {
        try { _channel?.closeChannel(Wait); } catch { }
        try { _device?.Dispose(); } catch { }
        _device = null; _channel = null;
    }
}
