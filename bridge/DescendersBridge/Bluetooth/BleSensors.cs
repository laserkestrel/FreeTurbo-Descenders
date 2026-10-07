using DescendersBridge.Ant;

namespace DescendersBridge.Bluetooth;

/// <summary>One Heart Rate Measurement. Contact: what the sensor says about skin contact; null = it does not report it.</summary>
public readonly record struct HeartRateMeasurement(int Bpm, bool? Contact);

/// <summary>Crank revolution data: cumulative crank revolutions and the time of the last crank event (1/1024 s); both 16-bit, both wrap.</summary>
public readonly record struct CrankData(ushort Revolutions, ushort EventTime1024);

/// <summary>One CSC Measurement. Crank null = the packet carries no crank data (a speed-only sensor).</summary>
public readonly record struct CscMeasurement(CrankData? Crank);

/// <summary>One Cycling Power Measurement. Crank null = this power meter sends no crank data.</summary>
public readonly record struct PowerMeasurement(int PowerW, CrankData? Crank);

/// <summary>What a sensor connection listens to (one device may fill two roles: a power meter that also gives cadence).</summary>
[Flags]
public enum BleFeed
{
    None = 0,
    /// <summary>Heart Rate Measurement.</summary>
    HeartRate = 1,
    /// <summary>Cycling Power Measurement: power, and cadence from its crank data.</summary>
    Power = 2,
    /// <summary>CSC Measurement: cadence from its crank data.</summary>
    Csc = 4,
}

/// <summary>
/// The three Bluetooth SIG sensor profiles the bridge reads, as far as it needs them. Pure: byte arrays in, no radio
/// (tests\unit\BleSensorTests.cs). All numbers are little endian.
///  - Heart Rate Service 0x180D, Heart Rate Measurement 0x2A37 (notify): flags (uint8), then the heart rate (uint8, or uint16 when flags
///    bit 0 is set). Bits 1-2: sensor contact (bit 2 = the sensor reports contact at all, bit 1 = contact detected).
///  - Cycling Speed and Cadence Service 0x1816, CSC Measurement 0x2A5B (notify): flags (uint8), wheel revolution data when bit 0 is set
///    (uint32 revolutions + uint16 event time = 6 bytes, stepped over), crank revolution data when bit 1 is set (uint16 + uint16).
///  - Cycling Power Service 0x1818, Cycling Power Measurement 0x2A63 (notify): flags (uint16), instantaneous power (sint16, W), then the
///    optional fields in flag order: pedal power balance (bit 0, uint8), accumulated torque (bit 2, uint16), wheel revolution data (bit 4,
///    uint32 + uint16 = 6 bytes), crank revolution data (bit 5, uint16 + uint16). What follows the crank data is not read.
/// </summary>
public static class BleSensors
{
    public static readonly Guid HeartRateService = Ftms.Uuid(0x180D), HeartRateMeasurementChar = Ftms.Uuid(0x2A37);
    public static readonly Guid CscService = Ftms.Uuid(0x1816), CscMeasurementChar = Ftms.Uuid(0x2A5B);
    public static readonly Guid PowerService = Ftms.Uuid(0x1818), PowerMeasurementChar = Ftms.Uuid(0x2A63);

    /// <summary>Null = too short for its flags.</summary>
    public static HeartRateMeasurement? DecodeHeartRate(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) return null;
        int flags = p[0], bpm;
        if ((flags & 0x01) != 0)
        {
            if (p.Length < 3) return null;
            bpm = p[1] | (p[2] << 8);
        }
        else bpm = p[1];
        bool? contact = (flags & 0x04) != 0 ? (flags & 0x02) != 0 : null;
        return new HeartRateMeasurement(bpm, contact);
    }

    /// <summary>A heart rate to show: above zero, and not from a sensor that says it has no skin contact.</summary>
    public static bool Valid(HeartRateMeasurement m) => m.Bpm > 0 && m.Contact != false;

    /// <summary>Null = shorter than its flags say (nothing is taken from it).</summary>
    public static CscMeasurement? DecodeCsc(ReadOnlySpan<byte> p)
    {
        if (p.Length < 1) return null;
        int flags = p[0], at = 1;
        if ((flags & 0x01) != 0 && !Skip(p, ref at, 6)) return null;
        CrankData? crank = null;
        if ((flags & 0x02) != 0 && !Crank(p, ref at, out crank)) return null;
        return new CscMeasurement(crank);
    }

    /// <summary>Null = shorter than its flags say (nothing is taken from it).</summary>
    public static PowerMeasurement? DecodePower(ReadOnlySpan<byte> p)
    {
        if (p.Length < 4) return null;
        int flags = p[0] | (p[1] << 8), at = 4;
        int power = (short)(p[2] | (p[3] << 8));
        if ((flags & (1 << 0)) != 0 && !Skip(p, ref at, 1)) return null;   // pedal power balance
        if ((flags & (1 << 2)) != 0 && !Skip(p, ref at, 2)) return null;   // accumulated torque
        if ((flags & (1 << 4)) != 0 && !Skip(p, ref at, 6)) return null;   // wheel revolution data
        CrankData? crank = null;
        if ((flags & (1 << 5)) != 0 && !Crank(p, ref at, out crank)) return null;
        return new PowerMeasurement(power, crank);
    }

    private static bool Skip(ReadOnlySpan<byte> p, ref int at, int size)
    {
        if (at + size > p.Length) return false;
        at += size;
        return true;
    }

    private static bool Crank(ReadOnlySpan<byte> p, ref int at, out CrankData? crank)
    {
        crank = null;
        if (at + 4 > p.Length) return false;
        crank = new CrankData((ushort)(p[at] | (p[at + 1] << 8)), (ushort)(p[at + 2] | (p[at + 3] << 8)));
        at += 4;
        return true;
    }

    /// <summary>What a connection listens to for a device in this role: cadence from a power meter is its Cycling Power crank data, from
    /// anything else the CSC Measurement. The trainer is not a sensor.</summary>
    public static BleFeed Feed(Role role, byte deviceType) => role switch
    {
        Role.HeartRate => BleFeed.HeartRate,
        Role.Power => BleFeed.Power,
        Role.Cadence => deviceType == AntDeviceTypes.PowerMeter ? BleFeed.Power : BleFeed.Csc,
        _ => BleFeed.None,
    };

    /// <summary>For the log: "heart-rate sensor", "power meter", "cadence sensor".</summary>
    public static string Kind(BleFeed feeds) =>
        (feeds & BleFeed.HeartRate) != 0 ? "heart-rate sensor" : (feeds & BleFeed.Power) != 0 ? "power meter" : "cadence sensor";

    /// <summary>
    /// Why a Bluetooth heart-rate sensor shows no heart rate, for the console's status line; "" = nothing to explain (there is a reading).
    /// </summary>
    /// <param name="everConnected">it was connected earlier in this ride.</param>
    /// <param name="notFound">a connection attempt has failed since it was last connected.</param>
    /// <param name="contact">what the sensor last said about skin contact; null = it does not say.</param>
    public static string HeartRateWords(bool connected, bool everConnected, bool notFound, bool hasReading, bool? contact)
    {
        if (!connected) return everConnected ? "Bluetooth sensor lost: reconnecting" : notFound ? "not found: riding without it, still looking" : "Bluetooth sensor not found yet";
        if (hasReading) return "";
        return contact == false ? "connected, NO CONTACT: check the strap" : "connected, no reading yet";
    }
}

/// <summary>
/// Cadence from crank revolution data (CSC Measurement or Cycling Power Measurement): the change in cumulative crank
/// revolutions over the change in the last crank event time (1/1024 s). Both counters are 16-bit and wrap; the differences are taken
/// modulo 65536. A packet that repeats the last counters is "no new crank event": after <see cref="StopSeconds"/> of those the cadence is
/// 0, and <see cref="AbsentSeconds"/> after the last packet of any kind it is absent (null). Pure: the clock is passed in, in seconds.
/// </summary>
public sealed class CrankCadence
{
    public const double StopSeconds = 3.0, AbsentSeconds = 5.0;
    /// <summary>Above this a difference is a glitch (a sensor restarting its counters), not pedalling.</summary>
    public const double MaxRpm = 250.0;
    /// <summary>The event time wraps every 64 s: after a longer pause the first new event only sets the counters again.</summary>
    public const double RebaseSeconds = 30.0;

    private bool _first, _second;
    private ushort _revs, _time;
    private double _rpm, _heardAt = double.NegativeInfinity, _eventAt = double.NegativeInfinity, _countersAt;

    public void Add(CrankData c, double now)
    {
        _heardAt = now;
        if (!_first)
        {
            _first = true;
            _revs = c.Revolutions; _time = c.EventTime1024; _countersAt = now;
            return;
        }
        _second = true;
        int revs = (ushort)(c.Revolutions - _revs), time = (ushort)(c.EventTime1024 - _time);
        if (revs == 0) return;   // no new crank event
        bool longPause = now - _countersAt > RebaseSeconds;
        _revs = c.Revolutions; _time = c.EventTime1024; _countersAt = now;
        if (longPause || time == 0) return;
        double rpm = revs * 60.0 * 1024.0 / time;
        if (rpm > MaxRpm) return;
        _rpm = rpm;
        _eventAt = now;
    }

    /// <summary>Null = not known: nothing heard for <see cref="AbsentSeconds"/>, or only one packet so far (a rate needs two).</summary>
    public double? Rpm(double now)
    {
        if (!_second || now - _heardAt > AbsentSeconds) return null;
        return now - _eventAt <= StopSeconds ? _rpm : 0.0;
    }
}
