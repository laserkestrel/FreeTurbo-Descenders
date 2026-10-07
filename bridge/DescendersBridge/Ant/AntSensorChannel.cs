using System.Diagnostics;
using ANT_Managed_Library;

namespace DescendersBridge.Ant;

/// <summary>
/// Receive-only slave channel paired to ONE chosen sensor (heart rate, cadence, power meter) on the shared ANT device.
/// Every broadcast page goes into the sensor's <see cref="DeviceTracker"/>; the session reads decoded values from there.
/// The channel never searches for ever: a search is short and low priority, the channel closes when it finds nothing
/// and the session opens it again later (<see cref="SensorSearch"/> holds the rules; <see cref="Next"/> / <see cref="Open"/> / <see cref="Close"/>).
/// </summary>
public sealed class AntSensorChannel : IDisposable
{
    private const uint Wait = 500;
    private const byte RfFrequency = 57;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly ANT_Device _device;
    private readonly byte _channelNumber;
    private readonly DeviceKey _key;
    private readonly DeviceTracker _tracker;
    private readonly SensorSearch _search = new();   // locked for every use: the radio's thread and the session's both touch it
    private ANT_Channel? _channel;
    private bool _everOpened;

    /// <summary>Also log routine channel events (missed slots, collisions). Set env DESCENDERS_BRIDGE_VERBOSE=1.</summary>
    public static bool Verbose { get; } = Environment.GetEnvironmentVariable("DESCENDERS_BRIDGE_VERBOSE") == "1";

    public event Action<string>? Status;

    public AntSensorChannel(ANT_Device device, byte channelNumber, DeviceKey key, DeviceTracker tracker)
    {
        _device = device; _channelNumber = channelNumber; _key = key; _tracker = tracker;
    }

    public DeviceKey Key => _key;
    public bool Searching { get { lock (_search) return _search.State == SensorState.Searching; } }
    /// <summary>Chosen but not there: a search ended without it and it is not being received now.</summary>
    public bool NotFound { get { lock (_search) return _search.NotFound; } }

    private static double Now => Clock.Elapsed.TotalSeconds;

    /// <summary>Channel period in 1/32768 s for each ANT+ profile (from the device profile specs).</summary>
    public static ushort PeriodFor(byte deviceType) => deviceType switch
    {
        AntDeviceTypes.HeartRate => 8070,        // ~4.06 Hz
        AntDeviceTypes.BikeCadence => 8102,      // ~4.04 Hz
        AntDeviceTypes.BikeSpeedCadence => 8086, // ~4.05 Hz
        AntDeviceTypes.PowerMeter => 8182,       // ~4.01 Hz
        AntDeviceTypes.FitnessEquipment => 8192, // ~4 Hz
        _ => 8192,
    };

    /// <summary>What the session should do with this channel now.</summary>
    public SensorStep Next(bool anotherSearching, bool trainerHolds)
    {
        lock (_search) return _search.Next(Now, anotherSearching, trainerHolds);
    }

    /// <summary>Start a search for the sensor (the first call also sets the channel up).</summary>
    public void Open()
    {
        try
        {
            var ch = _channel ?? Configure();
            lock (_search) _search.Opened(Now);   // before the radio call: the first message may arrive at once
            Check(ch.openChannel(Wait), "openChannel");
            if (!_everOpened || Verbose) Status?.Invoke($"channel {_channelNumber} open: searching for {_key}");
            _everOpened = true;
        }
        catch (Exception ex)
        {
            lock (_search) _search.OpenFailed(Now);
            Status?.Invoke($"{_key}: could not start a search ({ex.Message}), trying again in {SensorSearchRules.RetrySeconds:0} s");
        }
    }

    private ANT_Channel Configure()
    {
        var ch = _device.getChannel(_channelNumber);
        Check(ch.assignChannel(ANT_ReferenceLibrary.ChannelType.BASE_Slave_Receive_0x00, 0, Wait), "assignChannel");
        try
        {
            Check(ch.setChannelID(_key.DeviceNumber, false, _key.DeviceType, _key.TransmissionType, Wait), "setChannelID");
            Check(ch.setChannelFreq(RfFrequency, Wait), "setChannelFreq");
            Check(ch.setChannelPeriod(PeriodFor(_key.DeviceType), Wait), "setChannelPeriod");

            // Low-priority search only ("will not interrupt other open channels"), then the stick closes the channel. Not every stick has it:
            // without it the search is the ordinary one, which does interrupt the trainer, so it is kept short and the pauses are long.
            bool low;
            try { low = ch.setLowPrioritySearchTimeout(SensorSearchRules.TimeoutUnits(SensorSearchRules.SearchSeconds), Wait); }
            catch { low = false; }
            lock (_search) _search.LowPriority = low;
            if (low)
            {
                Check(ch.setChannelSearchTimeout(0, Wait), "setChannelSearchTimeout");   // 0 = no high-priority search after it
            }
            else
            {
                Check(ch.setChannelSearchTimeout(SensorSearchRules.TimeoutUnits(SensorSearchRules.FallbackSearchSeconds), Wait), "setChannelSearchTimeout");
                Status?.Invoke($"{_key}: this stick has no low-priority search: searching {SensorSearchRules.FallbackSearchSeconds:0} s at a time, " +
                               "the trainer may miss a write while it does");
            }
        }
        catch
        {
            try { ch.unassignChannel(Wait); } catch { }   // so the next try can set it up from the start
            throw;
        }
        ch.channelResponse += OnResponse;
        _channel = ch;
        return ch;
    }

    /// <summary>
    /// Stop the search. <paramref name="miss"/>: it had its full time and found nothing (false: cut short for the trainer).
    /// </summary>
    public void Close(bool miss)
    {
        bool changed;
        lock (_search) changed = _search.Closed(Now, miss);   // before the radio call, so its "closed" event is not counted again
        if (changed && miss) ReportMiss();
        try { _channel?.closeChannel(Wait); }
        catch (Exception ex) { Status?.Invoke($"{_key}: closing the channel failed: {ex.Message}"); }
    }

    private void ReportMiss()
    {
        int misses; bool low, heard;
        lock (_search) { misses = _search.Misses; low = _search.LowPriority; heard = _search.HeardBefore; }
        if (Verbose || SensorSearchRules.ReportMiss(misses)) Status?.Invoke(SensorSearchRules.MissText(_key, misses, low, heard));
    }

    private void OnResponse(ANT_Response r)
    {
        try
        {
            if (r.responseID == 0x40)           // channel event: [channel, msgId, eventCode]
            {
                var c = r.messageContents;
                if (c.Length < 3) return;
                bool routine = false;
                switch ((AntChannelEvent)c[2])
                {
                    // RX_FAIL (one missed 4 Hz slot) and CHANNEL_COLLISION (two devices in the same instant) are routine with several channels
                    case AntChannelEvent.RxFail:
                    case AntChannelEvent.ChannelCollision:
                        routine = true;
                        break;
                    case AntChannelEvent.RxFailGoToSearch:
                        lock (_search) _search.WentToSearch(Now);
                        break;
                    // the search ran out: the stick closes the channel, the session opens it again later
                    case AntChannelEvent.RxSearchTimeout:
                        routine = true;
                        break;
                    case AntChannelEvent.ChannelClosed:
                        bool changed;
                        lock (_search) changed = _search.Closed(Now, miss: true);
                        if (changed) ReportMiss();
                        routine = true;
                        break;
                }
                if (Verbose || !routine) Status?.Invoke($"{_key}: channel event {AntChannelEvents.Name(c[2])}");
                return;
            }
            if (r.responseID != 0x4E) return;   // broadcast data
            bool first = _tracker.Messages == 0, found;
            lock (_search) found = _search.Data(Now);
            if (first) Status?.Invoke($"{_key}: first data");
            else if (found) Status?.Invoke($"{_key}: heard again");
            lock (_tracker) _tracker.Update(r.getDataPayload());
        }
        catch (Exception ex) { Status?.Invoke($"response handler error: {ex.Message}"); }
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException($"{what} failed.");
    }

    public void Dispose()
    {
        try
        {
            if (_channel is null) return;
            _channel.channelResponse -= OnResponse;
            _channel.closeChannel(Wait);
            _channel.Dispose();
        }
        catch { /* best effort on shutdown */ }
        _channel = null;
    }
}
