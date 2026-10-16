using System.Text;

namespace DescendersBridge;

/// <summary>
/// A small FIT decoder for the tests only: the header, both CRCs (checked with a bit-by-bit CRC-16, not the encoder's
/// table) and every data message as field number -> raw value, read through the definition messages in the file. It reads what
/// <see cref="Fit.Encode"/> writes (little endian, fields of 1, 2 or 4 bytes, no developer fields) and gives an error for anything else.
/// </summary>
internal sealed class FitReader
{
    public readonly List<(ushort Mesg, Dictionary<byte, uint> F)> Messages = new();
    public int HeaderSize, DataSize, ProtocolVersion, ProfileVersion;
    public string Tag = "";
    public bool HeaderCrcOk, FileCrcOk;
    public string? Error;

    public bool Ok => Error is null && HeaderCrcOk && FileCrcOk;
    public IEnumerable<Dictionary<byte, uint>> Of(ushort mesg) => Messages.Where(m => m.Mesg == mesg).Select(m => m.F);

    /// <summary>CRC-16 with the reflected polynomial 0xA001 and start value 0, one bit at a time.</summary>
    public static ushort Crc16(byte[] b, int offset, int count)
    {
        ushort crc = 0;
        for (int i = offset; i < offset + count; i++)
        {
            crc ^= b[i];
            for (int k = 0; k < 8; k++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        return crc;
    }

    public static FitReader Read(byte[] b)
    {
        var r = new FitReader();
        if (b.Length < 16) { r.Error = "shorter than a header and a CRC"; return r; }
        r.HeaderSize = b[0];
        r.ProtocolVersion = b[1];
        r.ProfileVersion = b[2] | (b[3] << 8);
        r.DataSize = b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24);
        r.Tag = Encoding.ASCII.GetString(b, 8, 4);
        if (r.HeaderSize != 14 || r.HeaderSize + r.DataSize + 2 != b.Length) { r.Error = "the header's sizes do not match the file"; return r; }
        r.HeaderCrcOk = Crc16(b, 0, 12) == (b[12] | (b[13] << 8));
        r.FileCrcOk = Crc16(b, 0, b.Length - 2) == (b[^2] | (b[^1] << 8));

        var defs = new Dictionary<int, (ushort Mesg, List<(byte Num, int Size)> Fields)>();
        int at = r.HeaderSize, end = r.HeaderSize + r.DataSize;
        while (at < end)
        {
            byte head = b[at++];
            if ((head & 0x80) != 0 || (head & 0x20) != 0) { r.Error = "a compressed-timestamp header or developer fields"; return r; }
            int local = head & 0x0F;
            if ((head & 0x40) != 0)
            {
                if (at + 5 > end || b[at + 1] != 0) { r.Error = "a definition that is cut off or big endian"; return r; }
                ushort mesg = (ushort)(b[at + 2] | (b[at + 3] << 8));
                int n = b[at + 4];
                at += 5;
                var fields = new List<(byte, int)>();
                for (int i = 0; i < n; i++, at += 3)
                {
                    if (at + 3 > end || b[at + 1] != Fit.Size(b[at + 2])) { r.Error = "a field whose size is not its base type's"; return r; }
                    fields.Add((b[at], b[at + 1]));
                }
                defs[local] = (mesg, fields);
                continue;
            }
            if (!defs.TryGetValue(local, out var d)) { r.Error = "a data message before its definition"; return r; }
            var f = new Dictionary<byte, uint>();
            foreach (var (num, size) in d.Fields)
            {
                if (at + size > end) { r.Error = "a data message that is cut off"; return r; }
                uint v = 0;
                for (int i = 0; i < size; i++) v |= (uint)b[at + i] << (8 * i);
                f[num] = v;
                at += size;
            }
            r.Messages.Add((d.Mesg, f));
        }
        return r;
    }
}

/// <summary>
/// The .fit activity file's bytes (<see cref="Fit"/>), checked by decoding them again with <see cref="FitReader"/>: header,
/// CRC, timestamps from the format's epoch, scaled fields, totals, a pause as timer stop / start left out of the timer time, an absent
/// reading as the invalid value; and the file's name and folder (<see cref="ActivityFiles"/>). No file, no clock.
/// </summary>
public static class FitTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 18, 30, 0, DateTimeKind.Utc);

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Activity file: the FIT bytes");
        byte[] digits = Encoding.ASCII.GetBytes("123456789");
        check(Fit.Crc(digits, 0, 9) == 0xBB3D && FitReader.Crc16(digits, 0, 9) == 0xBB3D, $"CRC-16 of \"123456789\" is 0xBB3D (got 0x{Fit.Crc(digits, 0, 9):X4})");
        check((Fit.Epoch - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds == 631065600 && Fit.Timestamp(Fit.Epoch) == 0
            && Fit.Timestamp(T0) == 1160591400u && Fit.Timestamp(T0.AddSeconds(1.9)) == 1160591401u,
            $"timestamps are whole seconds since 1989-12-31 00:00 UTC (2026-10-10 18:30:00 UTC = {Fit.Timestamp(T0)})");
        check(Fit.Scaled(8.5, 1000, Fit.InvalidU16) == 8500 && Fit.Scaled(null, 1000, Fit.InvalidU16) == 0xFFFF && Fit.Scaled(double.NaN, 1, Fit.InvalidU8) == 0xFF
            && Fit.Scaled(400, 1, Fit.InvalidU8) == 0xFE && Fit.Scaled(-3, 1, Fit.InvalidU16) == 0,
            "a reading is scaled and rounded, held below the invalid value, and absent = the invalid value");

        // 5 s of riding, the game paused for 60 s, 5 s more; no heart rate before the pause
        var a = new FitActivity(T0);
        for (int k = 0; k < 5; k++) a.Add(new FitRecord(T0.AddSeconds(k), 200, 90.4, null, 8.5, 8.5 * k));
        a.Timer(T0.AddSeconds(5), start: false);
        a.Timer(T0.AddSeconds(6), start: false);   // a second stop while stopped: nothing
        a.Timer(T0.AddSeconds(65), start: true);
        for (int k = 0; k < 5; k++) a.Add(new FitRecord(T0.AddSeconds(65 + k), 300, 80, 150 + 2 * k, 9.0, 34 + 8.5 * (k + 1)));
        a.End(T0.AddSeconds(70));
        var t = a.Totals();
        check(Math.Abs(t.ElapsedS - 70) < 1e-9 && Math.Abs(t.TimerS - 10) < 1e-9 && Math.Abs(a.TimerSeconds(T0.AddSeconds(30)) - 5) < 1e-9,
            $"the pause is left out of the timer time: 70 s from start to end, 10 s of riding (got {t.TimerS:0.###} s)");
        check(Math.Abs(t.DistanceM - 76.5) < 1e-9 && Math.Abs(t.AvgSpeedMps - 7.65) < 1e-9 && t.MaxSpeedMps == 9.0 && t.AvgPowerW == 250 && t.MaxPowerW == 300
            && Math.Abs(t.AvgCadenceRpm!.Value - 85.2) < 1e-9 && t.AvgHeartRateBpm == 154 && t.MaxHeartRateBpm == 158,
            "totals: distance, average speed = distance / riding time, average and maximum power, cadence and heart rate (heart rate over the records that have one)");

        DateTime created = T0.AddSeconds(71);
        byte[] file = Fit.Encode(a, created);
        var r = FitReader.Read(file);
        check(r.Error is null, "the bytes decode again: every data message has its definition before it" + (r.Error is { } e ? " (" + e + ")" : ""));
        check(r.HeaderSize == 14 && file[0] == 14 && r.Tag == ".FIT" && r.DataSize == file.Length - 16 && r.ProtocolVersion == 0x10 && r.ProfileVersion > 0,
            $"header: 14 bytes, \".FIT\", the data size is the file less the header and the CRC ({r.DataSize} of {file.Length} bytes)");
        check(r.HeaderCrcOk && r.FileCrcOk, "the header's CRC and the file's CRC are right (checked bit by bit)");
        byte[] broken = (byte[])file.Clone();
        broken[40] ^= 0x01;
        check(!FitReader.Read(broken).FileCrcOk, "one changed bit in the data and the file's CRC no longer matches");

        var order = r.Messages.Select(m => m.Mesg).ToList();
        check(order.Count > 0 && order[0] == Fit.MesgFileId && order.Count(m => m == Fit.MesgFileId) == 1 && order[^3] == Fit.MesgLap && order[^2] == Fit.MesgSession && order[^1] == Fit.MesgActivity
            && order.Count(m => m == Fit.MesgLap) == 1 && order.Count(m => m == Fit.MesgSession) == 1 && order.Count(m => m == Fit.MesgActivity) == 1,
            "file_id first, then the events and records, then one lap, one session, one activity");
        var id = r.Of(Fit.MesgFileId).FirstOrDefault() ?? new();
        check(id.GetValueOrDefault((byte)0, 99u) == Fit.FileTypeActivity && id.GetValueOrDefault((byte)1) == 255 && id.GetValueOrDefault((byte)4) == Fit.Timestamp(created),
            "file_id: type activity (4), manufacturer 255 (development), the creation time");

        uint ts0 = Fit.Timestamp(T0);
        var events = r.Of(Fit.MesgEvent).Select(f => (At: f[253] - ts0, Event: f[0], Type: f[1])).ToList();
        check(events.SequenceEqual(new[] { (0u, 0u, 0u), (5u, 0u, 1u), (65u, 0u, 0u), (70u, 0u, 4u) }),
            "timer events: start at 0 s, stop at 5 s, start at 65 s, stop_all at 70 s (got " + string.Join(", ", events.Select(x => x.At + " s type " + x.Type)) + ")");
        int stopAt = r.Messages.FindIndex(m => m.Mesg == Fit.MesgEvent && m.F[1] == 1), firstAfter = r.Messages.FindIndex(m => m.Mesg == Fit.MesgRecord && m.F[253] == ts0 + 65);
        check(stopAt == 7 && firstAfter == 9, "the stop and the start sit between the records, where they happened");

        var recs = r.Of(Fit.MesgRecord).ToList();
        check(recs.Count == 10 && recs.Select(f => f[253] - ts0).SequenceEqual(new uint[] { 0, 1, 2, 3, 4, 65, 66, 67, 68, 69 }),
            "ten records, one per second of riding, none during the pause");
        check(recs.Count == 10 && recs[2][7] == 200 && recs[2][4] == 90 && recs[2][6] == 8500 && recs[2][5] == 1700 && recs[9][7] == 300 && recs[9][6] == 9000 && recs[9][5] == 7650,
            "record fields: power in W, cadence in rpm (rounded), speed x 1000 (m/s), distance x 100 (m)");
        check(recs.Take(5).All(f => f[3] == 0xFF) && recs.Skip(5).Select(f => f[3]).SequenceEqual(new uint[] { 150, 152, 154, 156, 158 }),
            "no heart rate (no strap) is the invalid value 0xFF, not 0; with a strap it is the bpm");
        check(recs.All(f => !f.ContainsKey(0) && !f.ContainsKey(1) && !f.ContainsKey(2) && f.Count == 6), "no position and no altitude in any record");

        foreach (var (name, mesg, off) in new[] { ("lap", Fit.MesgLap, 0), ("session", Fit.MesgSession, 1) })
        {
            var f = r.Of(mesg).FirstOrDefault() ?? new();
            byte B(int n) => (byte)(n + off);   // session's averages are one field number after lap's
            check(f.Count > 0 && f[253] == ts0 + 70 && f[2] == ts0 && f[7] == 70000 && f[8] == 10000 && f[9] == 7650,
                $"{name}: start time, total elapsed time 70 s and total timer time 10 s (x 1000), total distance 76.5 m (x 100)");
            check(f.Count > 0 && f[B(13)] == 7650 && f[B(14)] == 9000 && f[B(15)] == 154 && f[B(16)] == 158 && f[B(17)] == 85 && f[B(18)] == 90 && f[B(19)] == 250 && f[B(20)] == 300,
                $"{name}: average / maximum speed, heart rate, cadence and power");
            check(f.Count > 0 && f[mesg == Fit.MesgLap ? (byte)25 : (byte)5] == Fit.SportCycling && f[mesg == Fit.MesgLap ? (byte)39 : (byte)6] == Fit.SubSportVirtualActivity,
                $"{name}: sport cycling (2), sub-sport virtual activity (58)");
        }
        var ses = r.Of(Fit.MesgSession).FirstOrDefault() ?? new();
        var act = r.Of(Fit.MesgActivity).FirstOrDefault() ?? new();
        check(ses.GetValueOrDefault((byte)26) == 1 && ses.GetValueOrDefault((byte)25, 9u) == 0 && act.GetValueOrDefault((byte)1) == 1 && act.GetValueOrDefault((byte)0) == 10000
            && act.GetValueOrDefault((byte)253) == ts0 + 70,
            "one lap in the session, one session in the activity, whose timer time is the riding time too");

        Console.WriteLine("Activity file: every reading absent");
        var none = new FitActivity(T0);
        none.Add(new FitRecord(T0, null, null, null, null, 0));
        none.Add(new FitRecord(T0.AddSeconds(1), null, null, null, null, 0));
        var rn = FitReader.Read(Fit.Encode(none, T0));
        var rec = rn.Of(Fit.MesgRecord).FirstOrDefault() ?? new();
        var sn = rn.Of(Fit.MesgSession).FirstOrDefault() ?? new();
        check(rn.Ok && rec.Count == 6 && rec[7] == 0xFFFF && rec[4] == 0xFF && rec[3] == 0xFF && rec[6] == 0xFFFF && rec[5] == 0,
            "a record with no power, cadence, heart rate or speed: each is its invalid value; the distance stays a number");
        check(sn.Count > 0 && sn[16] == 0xFF && sn[17] == 0xFF && sn[20] == 0xFFFF && sn[21] == 0xFFFF && sn[18] == 0xFF && sn[15] == 0xFFFF && sn[14] == 0,
            "its session: averages and maximums of readings nobody had are invalid, not 0");

        Console.WriteLine("Activity file: name and folder");
        var local = new DateTime(2026, 10, 10, 18, 30, 0, DateTimeKind.Local);
        check(ActivityFiles.FileName(local, "Sweet Spot 3x10") == "2026-10-10_1830_Sweet-Spot-3x10.fit", "named with the date, the time and the workout's name: " + ActivityFiles.FileName(local, "Sweet Spot 3x10"));
        check(ActivityFiles.SafeName("VO2: 5x3 / hard?  *<go>*") == "VO2-5x3-hard-go" && ActivityFiles.SafeName("  ") == "workout" && ActivityFiles.SafeName(null) == "workout"
            && ActivityFiles.SafeName("..\\..\\evil") == "evil" && ActivityFiles.SafeName(new string('x', 200)).Length == 60,
            "characters not allowed in a file name are taken out (got '" + ActivityFiles.SafeName("VO2: 5x3 / hard?  *<go>*") + "'); no name = \"workout\"; a long name is cut");
        string dir = Path.Combine(Path.GetTempPath(), "bridge-cfg");
        check(ActivityFiles.DirFor(Path.Combine(dir, "TrainerBridgeConfig.json")) == Path.Combine(dir, "activities"), "the folder is activities\\ beside the settings file (also a --config file)");
        string first = Path.Combine(dir, "2026-10-10_1830_Ramp.fit");
        check(ActivityFiles.NewPath(dir, local, "Ramp", _ => false) == first
            && ActivityFiles.NewPath(dir, local, "Ramp", p => p == first) == Path.Combine(dir, "2026-10-10_1830_Ramp_2.fit")
            && ActivityFiles.NewPath(dir, local, "Ramp", p => p == first || p.EndsWith("_2.fit")) == Path.Combine(dir, "2026-10-10_1830_Ramp_3.fit"),
            "the same workout twice in a minute: _2, _3 are added, a file is never overwritten");
        check(ActivityFiles.Keep(true, 20, 20) && ActivityFiles.Keep(true, 6000, 6000), "a workout that reached its end gets a file");
        check(ActivityFiles.Keep(false, 216, 216) && ActivityFiles.Keep(false, 0.25, 1), "one that ended early gets a file after any riding, however short (3:36 of riding gave none)");
        check(!ActivityFiles.Keep(false, 0, 0) && !ActivityFiles.Keep(true, 0, 0) && !ActivityFiles.Keep(false, 0, 1) && !ActivityFiles.Keep(false, 30, 0),
            "no riding = no file: nothing recorded, or no riding time, never an empty file");
        check(ActivityFiles.FileName(local, ActivityFiles.FreeRideName) == "2026-10-10_1830_Free-ride.fit", "a ride with no workout is named Free-ride: " + ActivityFiles.FileName(local, ActivityFiles.FreeRideName));
    }
}
