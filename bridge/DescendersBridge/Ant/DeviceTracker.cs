using System.Diagnostics;

namespace DescendersBridge.Ant;

/// <summary>
/// Everything heard from one device during a scan: how often, and a live decoded reading so a human can tell devices apart
/// (the watch's heart rate, the cadence sensor that reads rpm only while you spin it).
/// Decoders are pure functions over the 8-byte ANT payload.
/// </summary>
public sealed class DeviceTracker
{
    public DeviceKey Key { get; }
    public long Messages { get; private set; }
    public long FirstTicks { get; }
    public long LastTicks { get; private set; }

    /// <summary>Latest decoded values (null = none yet / invalid sentinel).</summary>
    public int? HeartRateBpm { get; private set; }          // last VALID reading (a 0 from the watch never overwrites it)
    /// <summary>Messages carrying heart rate 0: the radio link is fine but the watch has no reading (strap/skin contact).</summary>
    public long HrZeroMessages { get; private set; }
    public long LastHrValidTicks { get; private set; }      // 0 = never
    public double? SecondsSinceValidHr => LastHrValidTicks == 0 ? null : Stopwatch.GetElapsedTime(LastHrValidTicks).TotalSeconds;
    private double? _cad;
    public double? CadenceRpm { get => _cad; private set { _cad = value; if (value > MaxCadenceRpm) MaxCadenceRpm = value.Value; } }
    /// <summary>Highest cadence seen: a sensor that is being spun stands out from one that is idle.</summary>
    public double MaxCadenceRpm { get; private set; }
    public int? PowerW { get; private set; }
    /// <summary>Trainer (FE-C page 16) speed, m/s: shown in the window, not sent to the mod (the game has its own speed).</summary>
    public double? SpeedMps { get; private set; }
    /// <summary>Who made it: the latest manufacturer page heard, null = none yet (it comes a few times a minute, or never).</summary>
    public DeviceIdentity? Identity { get; private set; }
    /// <summary>Called from <see cref="Update"/> (the radio's thread, the caller's lock held) when the identity is first heard or changes: keep it short.</summary>
    public Action<DeviceKey, DeviceIdentity>? IdentityChanged { get; set; }

    // cadence-sensor state: (event time in 1/1024 s, cumulative revolutions), 16-bit wrapping
    private int _cadTime = -1, _cadRevs;
    private long _cadLastChange;

    public DeviceTracker(DeviceKey key) { Key = key; FirstTicks = LastTicks = Stopwatch.GetTimestamp(); }

    public double SecondsSinceLast => Stopwatch.GetElapsedTime(LastTicks).TotalSeconds;
    public double Rate => Messages <= 1 ? 0 : Messages / Math.Max(0.25, Stopwatch.GetElapsedTime(FirstTicks).TotalSeconds);

    public void Update(ReadOnlySpan<byte> p)
    {
        Messages++;
        LastTicks = Stopwatch.GetTimestamp();
        if (p.Length < 8) return;

        if (DeviceIdentityPages.Decode(Key.DeviceType, p) is { } id && Identity != id)
        {
            Identity = id;
            IdentityChanged?.Invoke(Key, id);
        }

        switch (Key.DeviceType)
        {
            case AntDeviceTypes.HeartRate:
                // every HR page: byte 7 = computed heart rate, 0 = not available
                if (p[7] == 0) HrZeroMessages++;
                else { HeartRateBpm = p[7]; LastHrValidTicks = Stopwatch.GetTimestamp(); }
                break;

            case AntDeviceTypes.BikeCadence:
                // page 0 (and all its variants): bytes 4-5 cadence event time, 6-7 revolution count
                UpdateCadence(p[4] | (p[5] << 8), p[6] | (p[7] << 8));
                break;

            case AntDeviceTypes.BikeSpeedCadence:
                // bytes 0-1 cadence event time, 2-3 revolution count (speed is 4-7)
                UpdateCadence(p[0] | (p[1] << 8), p[2] | (p[3] << 8));
                break;

            case AntDeviceTypes.PowerMeter:
                if (p[0] == 0x10)   // standard power-only page: byte 3 = instantaneous cadence, 6-7 = power
                {
                    CadenceRpm = p[3] == 0xFF ? null : p[3];
                    PowerW = p[6] | (p[7] << 8);
                }
                break;

            case AntDeviceTypes.FitnessEquipment:
                var t = FecParser.Parse(p);
                if (t is null) break;
                if (t.CadenceRpm.HasValue) CadenceRpm = t.CadenceRpm;
                if (t.InstantaneousPowerWatts.HasValue) PowerW = t.InstantaneousPowerWatts;
                if (t.SpeedMetersPerSecond.HasValue) SpeedMps = t.SpeedMetersPerSecond;
                if (t.HeartRateBpm.HasValue && t.HeartRateBpm > 0) { HeartRateBpm = t.HeartRateBpm; LastHrValidTicks = Stopwatch.GetTimestamp(); }
                break;
        }
    }

    private void UpdateCadence(int time, int revs)
    {
        if (_cadTime >= 0 && time != _cadTime)
        {
            int dt = (time - _cadTime) & 0xFFFF;      // 1/1024 s, wraps at 64 s
            int dr = (revs - _cadRevs) & 0xFFFF;
            if (dt > 0 && dt < 8192)                   // < 8 s between events, else stale
                CadenceRpm = dr * 60.0 * 1024.0 / dt;
            _cadLastChange = Stopwatch.GetTimestamp();
        }
        else if (_cadTime >= 0 && Stopwatch.GetElapsedTime(_cadLastChange).TotalSeconds > 3)
        {
            CadenceRpm = 0;                            // no new event for 3 s: not pedalling
        }
        if (_cadTime < 0) _cadLastChange = Stopwatch.GetTimestamp();
        _cadTime = time; _cadRevs = revs;
    }
}
