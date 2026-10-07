using System.Diagnostics;
using ANT_Managed_Library;

namespace DescendersBridge.Ant;

/// <summary>
/// The ride-time ANT+ side: one stick, one channel per distinct chosen device (trainer on channel 0 with control,
/// the rest receive-only). Decoded values are read per ROLE, so a role can be filled by any device the user picked
/// (e.g. heart rate from the watch, cadence from a separate sensor rather than the trainer's built-in one).
/// A value older than <see cref="StaleSeconds"/> reads as null: absent is never zero.
/// </summary>
public sealed class AntSession : IDisposable, ITrainerSource
{
    ITrainerControl? ITrainerSource.Control => Trainer;
    bool ITrainerSource.TrainerPaired => Trainer?.IsPaired ?? false;
    bool ITrainerSource.Muted => false;
    bool ITrainerSource.LinkLost => _stickLost;
    string ITrainerSource.LinkLostText => _stickLost ? RecoveringSource.StickLostText : "";

    public const double StaleSeconds = 3.0;
    /// <summary>Heart rate moves slowly and the watch broadcast drops packets when three channels share the radio (ANT then re-searches for 5-7 s), so it is held longer.</summary>
    public const double HeartRateStaleSeconds = 10.0;

    private readonly ANT_Device _device;
    private readonly DevicePreferences _prefs;
    private readonly Dictionary<DeviceKey, DeviceTracker> _trackers = new();
    private readonly List<AntSensorChannel> _sensors = new();
    private volatile AntSensorChannel[] _sensorList = Array.Empty<AntSensorChannel>();   // the same channels, read without the lock (the window)
    private volatile bool _disposed;
    private const uint Wait = 500;

    public AntTrainerLink? Trainer { get; private set; }
    public event Action<string>? Status;
    /// <summary>The library reported a critical serial error (the stick was unplugged): this session is dead, open a new one.</summary>
    public bool StickLost => _stickLost;
    private volatile bool _stickLost;

    public AntSession(byte[] networkKey, DevicePreferences prefs)
    {
        _prefs = prefs;
        _device = new ANT_Device();   // throws if the stick is absent / driver not bound
        Status?.Invoke($"ANT stick #{_device.getSerialNumber()} {_device.getDeviceUSBInfo().printProductDescription().Trim()}");
        _device.serialError += OnSerialError;
        if (!_device.setNetworkKey(0, networkKey, Wait)) throw new InvalidOperationException("setNetworkKey failed.");
        _device.enableRxExtendedMessages(false, Wait);   // scan mode may have left this on; run-time channels want plain messages
    }

    /// <summary>Open a channel for every distinct device the preferences name. Trainer (if chosen) gets channel 0 and control.</summary>
    public async Task OpenAsync(RiderConfig rider)
    {
        byte next = 0;
        var trainerKey = _prefs.Get(Role.Trainer);
        if (trainerKey is { } tk)
        {
            var tr = Track(tk);
            Trainer = new AntTrainerLink(_device, next++, tk, tr);
            Trainer.Status += m => Status?.Invoke("trainer: " + m);
            Trainer.LinkStateChanged += async up =>
            {
                if (up) { await Trainer.SendUserConfigAsync(rider); }   // after every (re)pair
            };
            await Trainer.ConnectAsync();
        }

        // Sensors search one at a time in the background, after the trainer has paired. Two channels searching at once
        // starved each other on the ANTUSB-m (heart rate never paired while the trainer channel was open; found in testing).
        var order = new List<DeviceKey>();
        foreach (Role role in new[] { Role.HeartRate, Role.Cadence, Role.Power })
            if (_prefs.Get(role) is { } k && !_trackers.ContainsKey(k) && !order.Contains(k)) order.Add(k);
        var sensors = new List<AntSensorChannel>();
        foreach (var k in order)
        {
            if (next >= 8) { Status?.Invoke("out of ANT channels (8)"); break; }
            var ch = new AntSensorChannel(_device, next++, k, Track(k));
            ch.Status += m => Status?.Invoke(m);
            sensors.Add(ch);
        }
        lock (_sensors) _sensors.AddRange(sensors);
        _sensorList = sensors.ToArray();
        if (sensors.Count > 0) _ = Task.Run(RunSensorsAsync);
    }

    /// <summary>
    /// A sensor that is not there must not harm the trainer channel. Each sensor channel searches for a short time, one at a
    /// time, and is closed when nothing answers; it is opened again at intervals for the whole ride, so a sensor switched on later is
    /// still picked up. No sensor searches while the trainer is (re-)pairing. The rules are <see cref="SensorSearch"/>.
    /// </summary>
    private async Task RunSensorsAsync()
    {
        try
        {
            var clock = Stopwatch.StartNew();
            double? unpairedSince = null;
            bool waitSaid = false;
            while (!_disposed && !_stickLost)
            {
                double now = clock.Elapsed.TotalSeconds;
                bool paired = Trainer?.IsPaired ?? false;
                if (paired) unpairedSince = null; else unpairedSince ??= now;
                bool holds = SensorSearchRules.TrainerHolds(Trainer is not null, paired, now - (unpairedSince ?? now));
                if (Trainer is not null && !paired && !holds && !waitSaid)
                {
                    waitSaid = true;
                    Status?.Invoke($"trainer not heard after {SensorSearchRules.TrainerHoldSeconds:0} s (still searching): looking for the other sensors meanwhile");
                }
                if (paired) waitSaid = false;
                lock (_sensors)
                {
                    if (_disposed) return;
                    foreach (var s in _sensors)
                    {
                        bool another = false;
                        foreach (var o in _sensors) if (!ReferenceEquals(o, s) && o.Searching) another = true;
                        switch (s.Next(another, holds))
                        {
                            case SensorStep.Open: s.Open(); break;
                            case SensorStep.Close: s.Close(miss: !holds); break;
                        }
                    }
                }
                await Task.Delay(250);
            }
        }
        catch (Exception ex) { Status?.Invoke("sensor search stopped: " + ex.Message); }
    }

    private AntSensorChannel? SensorFor(DeviceKey k)
    {
        foreach (var s in _sensorList) if (s.Key == k) return s;
        return null;
    }

    private void OnSerialError(ANT_Device sender, ANT_Device.serialErrorCode error, bool isCritical)
    {
        if (isCritical) _stickLost = true;
        Status?.Invoke($"stick error {error}" + (isCritical ? ": connection to the stick lost" : ""));
    }

    private DeviceTracker Track(DeviceKey k)
    {
        if (!_trackers.TryGetValue(k, out var t)) _trackers[k] = t = new DeviceTracker(k) { IdentityChanged = OnIdentity };
        return t;
    }

    /// <summary>A device said who made it (the ride channels pass every page to the tracker anyway): logged once per device, and
    /// handed on off the radio's thread so the ride can remember it in the settings file.</summary>
    private void OnIdentity(DeviceKey k, DeviceIdentity id)
    {
        Task.Run(() =>
        {
            try
            {
                Status?.Invoke($"{k}: {id.LogText}");
                Identified?.Invoke(k, id);
            }
            catch (Exception ex) { Status?.Invoke("device identity not handled: " + ex.Message); }
        });
    }

    /// <summary>A chosen device's manufacturer page was heard (first time in this session, or changed). Raised on a pool thread.</summary>
    public event Action<DeviceKey, DeviceIdentity>? Identified;

    // ---- values by role (null = no fresh reading) ----

    private DeviceKey? KeyFor(Role role) => _prefs.Get(role) ?? (role is Role.Power or Role.Cadence ? _prefs.Get(Role.Trainer) : null);

    private T? Fresh<T>(Role role, double staleSeconds, Func<DeviceTracker, T?> read) where T : struct
    {
        if (KeyFor(role) is not { } k || !_trackers.TryGetValue(k, out var t)) return null;
        lock (t) return t.SecondsSinceLast > staleSeconds ? null : read(t);
    }

    public int? PowerW => Fresh(Role.Power, StaleSeconds, t => t.PowerW);
    public double? CadenceRpm => Fresh(Role.Cadence, StaleSeconds, t => t.CadenceRpm);
    public double? SpeedMps => Fresh(Role.Trainer, StaleSeconds, t => t.SpeedMps);
    /// <summary>Last valid heart rate, held for HeartRateStaleSeconds (measured from the last VALID reading, not the last message).</summary>
    public int? HeartRateBpm
    {
        get
        {
            if (KeyFor(Role.HeartRate) is not { } k || !_trackers.TryGetValue(k, out var t)) return null;
            lock (t) return t.SecondsSinceValidHr is { } s && s <= HeartRateStaleSeconds ? t.HeartRateBpm : null;
        }
    }

    /// <summary>Why heart rate is missing: no radio signal vs the watch broadcasting but reporting no reading. Empty when there is nothing to explain.</summary>
    public string HeartRateDiagnosis
    {
        get
        {
            if (KeyFor(Role.HeartRate) is not { } k || !_trackers.TryGetValue(k, out var t)) return "";
            bool notFound = SensorFor(k)?.NotFound ?? false;
            lock (t)
            {
                if (t.Messages == 0) return notFound ? "not found: riding without it, still looking" : "watch not found yet";
                if (t.SecondsSinceLast > StaleSeconds) return $"NO RADIO from the watch for {t.SecondsSinceLast:0} s";
                if (t.SecondsSinceValidHr is not { } s || s > HeartRateStaleSeconds) return $"radio ok, watch reports NO READING ({t.HrZeroMessages} zero msgs): strap contact";
                return "";
            }
        }
    }

    /// <summary>Seconds since anything was heard from the device filling this role (null = no such device chosen).</summary>
    public double? AgeSeconds(Role role)
    {
        if (KeyFor(role) is not { } k || !_trackers.TryGetValue(k, out var t)) return null;
        lock (t) return t.SecondsSinceLast;
    }

    /// <summary>
    /// The radio link to the device filling this role, for the window's health lights: null = no device fills it (the trainer counts for power
    /// and cadence). A chosen device whose channel is not open yet reads as 0 messages. NotFound: a sensor searched for and not there
    /// (the ride carries on without it and it is looked for again).
    /// </summary>
    public LinkInfo? Link(Role role)
    {
        if (KeyFor(role) is not { } k) return null;
        if (!_trackers.TryGetValue(k, out var t)) return new LinkInfo(k, 0, double.PositiveInfinity);
        bool notFound = SensorFor(k)?.NotFound ?? false;
        lock (t) return new LinkInfo(k, t.Messages, t.Messages == 0 ? double.PositiveInfinity : t.SecondsSinceLast, notFound, t.Identity?.ManufacturerId);
    }

    public void Dispose()
    {
        _disposed = true;
        try { Trainer?.DisposeAsync().AsTask().Wait(3000); } catch { }
        lock (_sensors) foreach (var s in _sensors) s.Dispose();
        try { _device.Dispose(); } catch { }
    }
}
