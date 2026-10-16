namespace DescendersBridge;

/// <summary>One second of an activity. A reading that is absent (no strap, a sensor that dropped out) is null and is written as the
/// format's "invalid" value, never 0. <paramref name="DistanceM"/> = metres ridden since the start.</summary>
public readonly record struct FitRecord(DateTime TimeUtc, int? PowerW, double? CadenceRpm, int? HeartRateBpm, double? SpeedMps, double DistanceM);

/// <summary>The totals of an activity, as written to its lap and session (<see cref="FitActivity.Totals"/>). An average or maximum is null
/// when no record has that reading.</summary>
public readonly record struct FitTotals(double ElapsedS, double TimerS, double DistanceM, double AvgSpeedMps, double? MaxSpeedMps,
    double? AvgPowerW, int? MaxPowerW, double? AvgCadenceRpm, double? MaxCadenceRpm, double? AvgHeartRateBpm, int? MaxHeartRateBpm);

/// <summary>
/// What goes into an activity file: the records and the timer's stops and starts, in the order they happened. The timer runs
/// from <see cref="StartUtc"/>; <see cref="Timer"/> stops and starts it (the game paused), so paused time is not riding time. Plain data,
/// no file and no clock here: <see cref="ActivityRecorder"/> fills it during a workout, <see cref="Fit.Encode"/> turns it into bytes.
/// </summary>
public sealed class FitActivity
{
    internal readonly record struct Item(bool IsTimer, bool Start, FitRecord Record);

    private readonly List<Item> _items = new();

    public FitActivity(DateTime startUtc) { StartUtc = EndUtc = startUtc; }

    public DateTime StartUtc { get; }
    public DateTime EndUtc { get; private set; }
    /// <summary>The timer is running (not stopped by <see cref="Timer"/>).</summary>
    public bool Running { get; private set; } = true;
    public int Records { get; private set; }
    internal IReadOnlyList<Item> Items => _items;

    public void Add(FitRecord r)
    {
        _items.Add(new Item(false, false, r));
        Records++;
        if (r.TimeUtc > EndUtc) EndUtc = r.TimeUtc;
    }

    /// <summary>Stop (<paramref name="start"/> false) or start the timer; a stop while stopped or a start while running changes nothing.</summary>
    public void Timer(DateTime utc, bool start)
    {
        if (start == Running) return;
        Running = start;
        _items.Add(new Item(true, start, new FitRecord(utc, null, null, null, null, 0)));
        if (utc > EndUtc) EndUtc = utc;
    }

    /// <summary>The activity ends at <paramref name="utc"/> (never before its last record).</summary>
    public void End(DateTime utc) { if (utc > EndUtc) EndUtc = utc; }

    /// <summary>Seconds the timer has run up to <paramref name="asOf"/>: the time between the start and it, less every stop.</summary>
    public double TimerSeconds(DateTime asOf)
    {
        double sum = 0;
        DateTime from = StartUtc;
        bool running = true;
        foreach (var i in _items)
        {
            if (!i.IsTimer || i.Start == running) continue;
            if (running) sum += (i.Record.TimeUtc - from).TotalSeconds; else from = i.Record.TimeUtc;
            running = i.Start;
        }
        if (running && asOf > from) sum += (asOf - from).TotalSeconds;
        return Math.Max(0, sum);
    }

    /// <summary>Totals and averages: an average is over the records that have the reading; the average speed is distance / timer time.</summary>
    public FitTotals Totals()
    {
        double timer = TimerSeconds(EndUtc), distance = 0;
        double powerSum = 0, cadenceSum = 0, hrSum = 0;
        int powerN = 0, cadenceN = 0, hrN = 0;
        int? maxPower = null, maxHr = null;
        double? maxCadence = null, maxSpeed = null;
        foreach (var i in _items)
        {
            if (i.IsTimer) continue;
            var r = i.Record;
            distance = Math.Max(distance, r.DistanceM);
            if (r.PowerW is { } p) { powerSum += p; powerN++; maxPower = Math.Max(maxPower ?? p, p); }
            if (r.CadenceRpm is { } c) { cadenceSum += c; cadenceN++; maxCadence = Math.Max(maxCadence ?? c, c); }
            if (r.HeartRateBpm is { } h) { hrSum += h; hrN++; maxHr = Math.Max(maxHr ?? h, h); }
            if (r.SpeedMps is { } v) maxSpeed = Math.Max(maxSpeed ?? v, v);
        }
        return new FitTotals((EndUtc - StartUtc).TotalSeconds, timer, distance, timer > 0 ? distance / timer : 0, maxSpeed,
            powerN > 0 ? powerSum / powerN : null, maxPower, cadenceN > 0 ? cadenceSum / cadenceN : null, maxCadence,
            hrN > 0 ? hrSum / hrN : null, maxHr);
    }
}

/// <summary>
/// The FIT activity file, our own encoder from the public format description (no SDK, no library). Pure: an activity in, bytes
/// out, no file (tests\unit\FitTests.cs decodes them again). All numbers are little endian.
///  - File: a 14-byte header (its size, protocol version, profile version, the size of the data, ".FIT", CRC-16 of the 12 bytes before),
///    the data records, then the CRC-16 of everything before it.
///  - A record is a definition message (header 0x40 | local type, reserved 0, architecture 0, global message number, field count, then
///    field number / size / base type per field) or a data message (header = local type, then the fields as defined). Each kind of
///    message is defined once, before its first data message.
///  - Messages written: file_id (0), event (21, timer start / stop / stop_all), record (20), lap (19), session (18), activity (34).
///    No altitude and no position. Sport cycling, sub-sport virtual activity.
///  - Times are seconds since 1989-12-31 00:00 UTC. Scales: speed 0.001 m/s, distance 0.01 m, durations 0.001 s.
///  - An absent reading is the base type's invalid value (0xFF, 0xFFFF, 0xFFFFFFFF).
/// </summary>
public static class Fit
{
    public const int HeaderSize = 14;
    public static readonly DateTime Epoch = new(1989, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    // base types
    public const byte Enum = 0x00, UInt8 = 0x02, UInt16 = 0x84, UInt32 = 0x86, UInt32Z = 0x8C;
    public const uint InvalidU8 = 0xFF, InvalidU16 = 0xFFFF, InvalidU32 = 0xFFFFFFFF;

    // global message numbers
    public const ushort MesgFileId = 0, MesgSession = 18, MesgLap = 19, MesgRecord = 20, MesgEvent = 21, MesgActivity = 34;

    public const byte FileTypeActivity = 4, SportCycling = 2, SubSportVirtualActivity = 58;
    public const ushort ManufacturerDevelopment = 255;
    public const byte EventTimer = 0, EventSession = 8, EventLap = 9, EventActivity = 26;
    public const byte EventStart = 0, EventStop = 1, EventStopAll = 4;

    private const byte FieldTimestamp = 253, FieldMessageIndex = 254;
    private const byte LapTriggerSessionEnd = 7, SessionTriggerActivityEnd = 0, ActivityManual = 0;

    private static readonly ushort[] CrcTable =
    {
        0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401, 0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400,
    };

    /// <summary>The format's CRC-16 of <paramref name="count"/> bytes from <paramref name="offset"/>, half a byte at a time.</summary>
    public static ushort Crc(byte[] data, int offset, int count)
    {
        ushort crc = 0;
        for (int i = offset; i < offset + count; i++)
        {
            byte b = data[i];
            ushort tmp = CrcTable[crc & 0xF];
            crc = (ushort)((crc >> 4) & 0x0FFF);
            crc = (ushort)(crc ^ tmp ^ CrcTable[b & 0xF]);
            tmp = CrcTable[crc & 0xF];
            crc = (ushort)((crc >> 4) & 0x0FFF);
            crc = (ushort)(crc ^ tmp ^ CrcTable[(b >> 4) & 0xF]);
        }
        return crc;
    }

    /// <summary>Whole seconds since the format's epoch (1989-12-31 00:00 UTC); a time before it is 0.</summary>
    public static uint Timestamp(DateTime utc) => (uint)Math.Clamp(Math.Floor((utc.ToUniversalTime() - Epoch).TotalSeconds), 0, InvalidU32 - 1.0);

    /// <summary>A reading times <paramref name="scale"/>, rounded and held below <paramref name="invalid"/>; absent or not a number = <paramref name="invalid"/>.</summary>
    public static uint Scaled(double? value, double scale, uint invalid) =>
        value is { } v && !double.IsNaN(v) ? (uint)Math.Clamp(Math.Round(v * scale), 0, invalid - 1.0) : invalid;

    private readonly record struct Field(byte Num, byte Type, uint Value);

    private sealed class Writer
    {
        public readonly MemoryStream Data = new();
        private readonly bool[] _defined = new bool[16];

        public void Message(byte local, ushort global, params Field[] fields)
        {
            if (!_defined[local])
            {
                _defined[local] = true;
                Data.WriteByte((byte)(0x40 | local));
                Data.WriteByte(0);   // reserved
                Data.WriteByte(0);   // little endian
                Data.WriteByte((byte)global); Data.WriteByte((byte)(global >> 8));
                Data.WriteByte((byte)fields.Length);
                foreach (var f in fields) { Data.WriteByte(f.Num); Data.WriteByte((byte)Size(f.Type)); Data.WriteByte(f.Type); }
            }
            Data.WriteByte(local);
            foreach (var f in fields)
                for (int i = 0, n = Size(f.Type); i < n; i++) Data.WriteByte((byte)(f.Value >> (8 * i)));
        }
    }

    /// <summary>Bytes of a base type's value.</summary>
    public static int Size(byte baseType) => baseType switch { UInt16 => 2, UInt32 or UInt32Z => 4, _ => 1 };

    private static Field E(byte num, byte v) => new(num, Enum, v);
    private static Field U8(byte num, double? v) => new(num, UInt8, Scaled(v, 1, InvalidU8));
    private static Field U16(byte num, double? v, double scale = 1) => new(num, UInt16, Scaled(v, scale, InvalidU16));
    private static Field U32(byte num, double? v, double scale = 1) => new(num, UInt32, Scaled(v, scale, InvalidU32));
    private static Field Time(byte num, DateTime utc) => new(num, UInt32, Timestamp(utc));

    /// <summary>The whole file for <paramref name="a"/>. <paramref name="createdUtc"/> = the file's creation time (file_id).</summary>
    public static byte[] Encode(FitActivity a, DateTime createdUtc)
    {
        var w = new Writer();
        var t = a.Totals();
        const byte fileId = 0, ev = 1, record = 2, lap = 3, session = 4, activity = 5;   // local message types

        w.Message(fileId, MesgFileId, E(0, FileTypeActivity), U16(1, ManufacturerDevelopment), U16(2, 0), new Field(3, UInt32Z, 1), Time(4, createdUtc));
        void TimerEvent(DateTime utc, byte type) => w.Message(ev, MesgEvent, Time(FieldTimestamp, utc), E(0, EventTimer), E(1, type));

        TimerEvent(a.StartUtc, EventStart);
        foreach (var i in a.Items)
        {
            var r = i.Record;
            if (i.IsTimer) TimerEvent(r.TimeUtc, i.Start ? EventStart : EventStop);
            else
                w.Message(record, MesgRecord, Time(FieldTimestamp, r.TimeUtc), U8(3, r.HeartRateBpm), U8(4, r.CadenceRpm),
                    U32(5, r.DistanceM, 100), U16(6, r.SpeedMps, 1000), U16(7, r.PowerW));
        }
        TimerEvent(a.EndUtc, EventStopAll);

        w.Message(lap, MesgLap, new Field(FieldMessageIndex, UInt16, 0), Time(FieldTimestamp, a.EndUtc), E(0, EventLap), E(1, EventStop), Time(2, a.StartUtc),
            U32(7, t.ElapsedS, 1000), U32(8, t.TimerS, 1000), U32(9, t.DistanceM, 100), U16(13, t.AvgSpeedMps, 1000), U16(14, t.MaxSpeedMps, 1000),
            U8(15, t.AvgHeartRateBpm), U8(16, t.MaxHeartRateBpm), U8(17, t.AvgCadenceRpm), U8(18, t.MaxCadenceRpm), U16(19, t.AvgPowerW), U16(20, t.MaxPowerW),
            E(24, LapTriggerSessionEnd), E(25, SportCycling), E(39, SubSportVirtualActivity));

        w.Message(session, MesgSession, new Field(FieldMessageIndex, UInt16, 0), Time(FieldTimestamp, a.EndUtc), E(0, EventSession), E(1, EventStop), Time(2, a.StartUtc),
            E(5, SportCycling), E(6, SubSportVirtualActivity), U32(7, t.ElapsedS, 1000), U32(8, t.TimerS, 1000), U32(9, t.DistanceM, 100),
            U16(14, t.AvgSpeedMps, 1000), U16(15, t.MaxSpeedMps, 1000), U8(16, t.AvgHeartRateBpm), U8(17, t.MaxHeartRateBpm), U8(18, t.AvgCadenceRpm),
            U8(19, t.MaxCadenceRpm), U16(20, t.AvgPowerW), U16(21, t.MaxPowerW), U16(25, 0), U16(26, 1), E(28, SessionTriggerActivityEnd));

        // local_timestamp: the same instant as the rider's clock showed it
        var local = DateTime.SpecifyKind(a.EndUtc.ToUniversalTime() + TimeZoneInfo.Local.GetUtcOffset(a.EndUtc.ToUniversalTime()), DateTimeKind.Utc);
        w.Message(activity, MesgActivity, Time(FieldTimestamp, a.EndUtc), U32(0, t.TimerS, 1000), U16(1, 1), E(2, ActivityManual), E(3, EventActivity), E(4, EventStop),
            Time(5, local));

        byte[] data = w.Data.ToArray();
        byte[] file = new byte[HeaderSize + data.Length + 2];
        file[0] = HeaderSize;
        file[1] = 0x10;                       // protocol version 1.0
        file[2] = 0x54; file[3] = 0x08;       // profile version 21.32 (2132)
        for (int i = 0; i < 4; i++) file[4 + i] = (byte)(data.Length >> (8 * i));
        file[8] = (byte)'.'; file[9] = (byte)'F'; file[10] = (byte)'I'; file[11] = (byte)'T';
        ushort head = Crc(file, 0, 12);
        file[12] = (byte)head; file[13] = (byte)(head >> 8);
        Buffer.BlockCopy(data, 0, file, HeaderSize, data.Length);
        ushort crc = Crc(file, 0, HeaderSize + data.Length);
        file[^2] = (byte)crc; file[^1] = (byte)(crc >> 8);
        return file;
    }
}
