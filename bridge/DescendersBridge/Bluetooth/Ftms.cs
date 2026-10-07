namespace DescendersBridge.Bluetooth;

/// <summary>What one Indoor Bike Data notification carried. A field the trainer left out is null: absent is never zero.</summary>
public readonly record struct IndoorBikeData(int? PowerW, double? CadenceRpm, double? SpeedMps);

/// <summary>The trainer's answer to a control-point write (the result byte of the 0x80 response indication).</summary>
public enum FtmsResult : byte
{
    Success = 0x01,
    NotSupported = 0x02,
    InvalidParameter = 0x03,
    Failed = 0x04,
    ControlNotPermitted = 0x05,
}

/// <summary>A control-point response indication: which request it answers and how.</summary>
public readonly record struct FtmsResponse(byte RequestOpCode, FtmsResult Result);

/// <summary>The trainer's Supported Power Range (0x2AD8), watts. An ERG target is clamped to it and rounded to its step.</summary>
public readonly record struct FtmsPowerRange(int MinW, int MaxW, int StepW)
{
    public int Clamp(double watts)
    {
        double w = Math.Clamp(double.IsNaN(watts) ? MinW : watts, MinW, MaxW);
        if (StepW > 1) w = MinW + Math.Round((w - MinW) / StepW) * StepW;
        return (int)Math.Round(Math.Min(w, MaxW));
    }
}

/// <summary>
/// The Bluetooth SIG Fitness Machine Service 1.0 (FTMS, service 0x1826), as far as a smart trainer needs it. Pure: byte arrays
/// in and out, no radio (tests\unit\FtmsTests.cs). All numbers are little endian.
///  - Indoor Bike Data (0x2AD2, notify): a 16-bit flags field, then the fields the flags announce, in the order of <see cref="DecodeIndoorBikeData"/>.
///  - Fitness Machine Control Point (0x2AD9, write with response + indicate): one op code byte, then its parameters. The trainer answers
///    every write with an indication {0x80, the request's op code, result}.
///  - Supported Power Range (0x2AD8, read): minimum (sint16), maximum (sint16), minimum increment (uint16), watts.
/// </summary>
public static class Ftms
{
    public static readonly Guid Service = Uuid(0x1826);
    /// <summary>Fitness Machine Feature (read): every fitness machine has it, so a device that answers it has the service.</summary>
    public static readonly Guid FeatureChar = Uuid(0x2ACC);
    public static readonly Guid IndoorBikeDataChar = Uuid(0x2AD2);
    public static readonly Guid SupportedPowerRangeChar = Uuid(0x2AD8);
    public static readonly Guid ControlPointChar = Uuid(0x2AD9);

    /// <summary>A 16-bit Bluetooth SIG number as the full 128-bit UUID (0000xxxx-0000-1000-8000-00805F9B34FB).</summary>
    public static Guid Uuid(ushort shortId) => new(shortId, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB);

    public const byte OpRequestControl = 0x00, OpReset = 0x01, OpSetTargetPower = 0x05, OpSetIndoorBikeSimulation = 0x11, OpResponse = 0x80;

    /// <summary>What the FE-C path leaves to the trainer (rolling resistance 0xFF and no wind page = the FE-C defaults); FTMS has no
    /// "use your default" value, so the same numbers are sent: Crr 0.004, wind resistance coefficient 0.51 kg/m.</summary>
    public const double DefaultCrr = 0.004, DefaultWindCoefficientKgPerM = 0.51;

    // Indoor Bike Data flags. Bit 0 is the odd one: set = "more data", the instantaneous speed is NOT in this packet.
    private const int FlagMoreData = 1 << 0, FlagAvgSpeed = 1 << 1, FlagCadence = 1 << 2, FlagAvgCadence = 1 << 3, FlagDistance = 1 << 4,
        FlagResistance = 1 << 5, FlagPower = 1 << 6, FlagAvgPower = 1 << 7, FlagEnergy = 1 << 8, FlagHeartRate = 1 << 9, FlagMet = 1 << 10,
        FlagElapsed = 1 << 11, FlagRemaining = 1 << 12;

    /// <summary>
    /// One Indoor Bike Data value. Field order and sizes: instantaneous speed (uint16, 0.01 km/h; there unless "more data"), average speed
    /// (uint16), instantaneous cadence (uint16, 0.5 rpm), average cadence (uint16), total distance (uint24), resistance level (sint16),
    /// instantaneous power (sint16, W), average power (sint16), expended energy (uint16 + uint16 + uint8), heart rate (uint8), metabolic
    /// equivalent (uint8), elapsed time (uint16), remaining time (uint16). Null = the packet is shorter than its flags say (nothing is taken from it).
    /// </summary>
    public static IndoorBikeData? DecodeIndoorBikeData(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) return null;
        int flags = p[0] | (p[1] << 8);
        int at = 2;
        int? power = null; double? cadence = null, speed = null;

        if ((flags & FlagMoreData) == 0)
        {
            if (!Take(p, ref at, 2, out int raw)) return null;
            speed = raw * 0.01 / 3.6;
        }
        if ((flags & FlagAvgSpeed) != 0 && !Take(p, ref at, 2, out _)) return null;
        if ((flags & FlagCadence) != 0)
        {
            if (!Take(p, ref at, 2, out int raw)) return null;
            cadence = raw * 0.5;
        }
        if ((flags & FlagAvgCadence) != 0 && !Take(p, ref at, 2, out _)) return null;
        if ((flags & FlagDistance) != 0 && !Take(p, ref at, 3, out _)) return null;
        if ((flags & FlagResistance) != 0 && !Take(p, ref at, 2, out _)) return null;
        if ((flags & FlagPower) != 0)
        {
            if (!Take(p, ref at, 2, out int raw)) return null;
            power = (short)raw;
        }
        if ((flags & FlagAvgPower) != 0 && !Take(p, ref at, 2, out _)) return null;
        if ((flags & FlagEnergy) != 0 && !Take(p, ref at, 5, out _)) return null;
        if ((flags & FlagHeartRate) != 0 && !Take(p, ref at, 1, out _)) return null;
        if ((flags & FlagMet) != 0 && !Take(p, ref at, 1, out _)) return null;
        if ((flags & FlagElapsed) != 0 && !Take(p, ref at, 2, out _)) return null;
        if ((flags & FlagRemaining) != 0 && !Take(p, ref at, 2, out _)) return null;
        return new IndoorBikeData(power, cadence, speed);
    }

    /// <summary>Step over <paramref name="size"/> bytes; <paramref name="value"/> = their first two as an unsigned number. False = not there.</summary>
    private static bool Take(ReadOnlySpan<byte> p, ref int at, int size, out int value)
    {
        value = 0;
        if (at + size > p.Length) return false;
        value = size >= 2 ? p[at] | (p[at + 1] << 8) : p[at];
        at += size;
        return true;
    }

    public static byte[] RequestControl() => new[] { OpRequestControl };

    public static byte[] Reset() => new[] { OpReset };

    /// <summary>Set Target Power (0x05): sint16 watts.</summary>
    public static byte[] SetTargetPower(int watts)
    {
        short w = (short)Math.Clamp(watts, short.MinValue, short.MaxValue);
        return new[] { OpSetTargetPower, (byte)(w & 0xFF), (byte)((w >> 8) & 0xFF) };
    }

    /// <summary>
    /// Set Indoor Bike Simulation Parameters (0x11): wind speed (sint16, 0.001 m/s), grade (sint16, 0.01 %), rolling resistance (uint8,
    /// 0.0001), wind resistance coefficient (uint8, 0.01 kg/m). NaN for the last two = <see cref="DefaultCrr"/> / <see cref="DefaultWindCoefficientKgPerM"/>.
    /// </summary>
    public static byte[] SetIndoorBikeSimulation(double gradePercent, double crr = double.NaN, double windCoefficientKgPerM = double.NaN, double windSpeedMps = 0.0)
    {
        short wind = (short)Math.Clamp(Math.Round(Finite(windSpeedMps, 0.0) / 0.001), short.MinValue, short.MaxValue);
        short grade = (short)Math.Clamp(Math.Round(Finite(gradePercent, 0.0) / 0.01), short.MinValue, short.MaxValue);
        byte c = (byte)Math.Clamp(Math.Round(Finite(crr, DefaultCrr) / 0.0001), 0, 255);
        byte cw = (byte)Math.Clamp(Math.Round(Finite(windCoefficientKgPerM, DefaultWindCoefficientKgPerM) / 0.01), 0, 255);
        return new[] { OpSetIndoorBikeSimulation, (byte)(wind & 0xFF), (byte)((wind >> 8) & 0xFF), (byte)(grade & 0xFF), (byte)((grade >> 8) & 0xFF), c, cw };
    }

    private static double Finite(double v, double fallback) => double.IsNaN(v) || double.IsInfinity(v) ? fallback : v;

    /// <summary>A control-point indication; null = not a response (too short, another op code, or a result byte the specification does not have).</summary>
    public static FtmsResponse? DecodeResponse(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3 || p[0] != OpResponse) return null;
        if (p[2] < (byte)FtmsResult.Success || p[2] > (byte)FtmsResult.ControlNotPermitted) return null;
        return new FtmsResponse(p[1], (FtmsResult)p[2]);
    }

    /// <summary>Supported Power Range; null = not published properly (too short, or maximum below minimum): no clamp is applied then.</summary>
    public static FtmsPowerRange? DecodePowerRange(ReadOnlySpan<byte> p)
    {
        if (p.Length < 6) return null;
        int min = (short)(p[0] | (p[1] << 8)), max = (short)(p[2] | (p[3] << 8)), step = p[4] | (p[5] << 8);
        return max < min ? null : new FtmsPowerRange(min, max, step);
    }

    /// <summary>The watts that go into Set Target Power: inside the trainer's range when it has one, never negative.</summary>
    public static int TargetWatts(double watts, FtmsPowerRange? range) =>
        range is { } r ? r.Clamp(watts) : (int)Math.Clamp(Math.Round(double.IsNaN(watts) ? 0 : watts), 0, short.MaxValue);

    /// <summary>For the log.</summary>
    public static string Words(FtmsResult r) => r switch
    {
        FtmsResult.Success => "success",
        FtmsResult.NotSupported => "not supported by this trainer",
        FtmsResult.InvalidParameter => "invalid parameter",
        FtmsResult.Failed => "operation failed",
        _ => "control not permitted",
    };
}
