using System.Diagnostics;
using DescendersBridge.Ant;
using DescendersBridge.Bluetooth;

namespace DescendersBridge;

/// <summary>
/// A fake Tacx trainer that has NO Fitness Machine Service, only Tacx's own trainer service (what the garage's Tacx Neo 2 turned out to
/// be): it sends FE-C pages 16 and 25 in ANT frames every 50 ms and takes framed control pages. Like an ERG trainer, the power it reports
/// is the target while one is held.
/// </summary>
internal sealed class FakeTacxTrainer
{
    private readonly object _gate = new();
    private Link? _link;

    public double? GradePercent;
    public double? TargetW;
    public int RiderW = 160;
    public int CadenceRpm = 88;
    public bool RefuseWrites;
    public readonly List<byte[]> Frames = new();
    public int Disconnects;

    public byte[][] FramesNow() { lock (_gate) return Frames.ToArray(); }

    public IBleConnection Connect()
    {
        var l = new Link(this);
        lock (_gate) _link = l;
        return l;
    }

    public void Drop() { Link? l; lock (_gate) l = _link; l?.Lose(); }

    private static byte[] Framed(byte[] page)
    {
        byte[] f = TacxFec.Frame(page);
        f[2] = TacxFec.Broadcast;
        f[12] = 0;
        for (int i = 0; i < 12; i++) f[12] ^= f[i];
        return f;
    }

    private byte[] Page25()
    {
        int w; int rpm;
        lock (_gate) { w = (int)(TargetW ?? RiderW); rpm = CadenceRpm; }
        return Framed(new byte[] { 0x19, 1, (byte)rpm, 0, 0, (byte)(w & 0xFF), (byte)((w >> 8) & 0x0F), 0x20 });
    }

    private static byte[] Page16() => Framed(new byte[] { 0x10, 25, 0, 0, 0x10, 0x27, 0xFF, 0x20 });   // 10.000 m/s

    private bool Handle(byte[] frame)
    {
        lock (_gate)
        {
            if (RefuseWrites) return false;
            Frames.Add(frame);
            if (TacxFec.Unframe(frame) is not { } p) return true;
            if (p[0] == FecControlPages.TrackResistancePage) { GradePercent = (p[5] | (p[6] << 8)) * 0.01 - 200.0; TargetW = null; }
            else if (p[0] == FecControlPages.TargetPowerPage) { TargetW = (p[6] | (p[7] << 8)) * 0.25; GradePercent = null; }
            return true;
        }
    }

    private sealed class Link : IBleConnection
    {
        private readonly FakeTacxTrainer _t;
        private Action<byte[]>? _sub;
        private readonly Timer _data;
        private volatile bool _up = true;
        private int _n;

        public Link(FakeTacxTrainer t)
        {
            _t = t;
            _data = new Timer(_ => { if (_up) Volatile.Read(ref _sub)?.Invoke(Interlocked.Increment(ref _n) % 2 == 0 ? _t.Page25() : Page16()); }, null, 20, 50);
        }

        public bool Connected => _up;
        public void Lose() { if (_up) { _up = false; Interlocked.Increment(ref _t.Disconnects); } }

        public Task<byte[]?> ReadAsync(Guid service, Guid characteristic, CancellationToken ct) => Task.FromResult<byte[]?>(null);

        public Task SubscribeAsync(Guid service, Guid characteristic, bool indicate, Action<byte[]> onValue, CancellationToken ct)
        {
            if (!_up) throw new InvalidOperationException("the device did not answer (Unreachable)");
            if (service != TacxFec.Service || characteristic != TacxFec.ReadChar || indicate)
                throw new InvalidOperationException("this device has no Fitness Machine Service and no Tacx trainer service");
            Volatile.Write(ref _sub, onValue);
            return Task.CompletedTask;
        }

        public Task<bool> WriteAsync(Guid service, Guid characteristic, byte[] value, CancellationToken ct) =>
            Task.FromResult(_up && service == TacxFec.Service && characteristic == TacxFec.WriteChar && _t.Handle(value));

        public Task<IReadOnlyList<Guid>> ServicesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>(new[] { BleSensors.PowerService, TacxFec.Service });

        public ValueTask DisposeAsync()
        {
            Lose();
            _data.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The fake radio for <see cref="FakeTacxTrainer"/>, plus a power meter that is no trainer at all.</summary>
internal sealed class FakeTacxRadio : IBleRadio
{
    public const ulong TrainerAddress = 0xFBC196246B68, PedalsAddress = 0xC1234567E002;
    public readonly FakeTacxTrainer Trainer = new();
    public int Connects;

    public Task<BleAdapterInfo> AdapterAsync() => Task.FromResult(new BleAdapterInfo(true, true, true, null, "fake radio"));
    public Task<IReadOnlyList<BleAdvert>> ScanAsync(TimeSpan duration, CancellationToken ct) => Task.FromResult<IReadOnlyList<BleAdvert>>(Array.Empty<BleAdvert>());

    public Task<IBleConnection> ConnectAsync(ulong address, CancellationToken ct)
    {
        if (address == PedalsAddress) return Task.FromResult<IBleConnection>(new Pedals());
        if (address != TrainerAddress) throw new InvalidOperationException("not heard (is it switched on and awake?)");
        Interlocked.Increment(ref Connects);
        return Task.FromResult(Trainer.Connect());
    }

    private sealed class Pedals : IBleConnection
    {
        public bool Connected => true;
        public Task<byte[]?> ReadAsync(Guid service, Guid characteristic, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task SubscribeAsync(Guid service, Guid characteristic, bool indicate, Action<byte[]> onValue, CancellationToken ct) =>
            throw new InvalidOperationException("this device has no Fitness Machine Service and no Tacx trainer service");
        public Task<bool> WriteAsync(Guid service, Guid characteristic, byte[] value, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<Guid>> ServicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>(new[] { BleSensors.PowerService });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// After the garage test of 2026-10-05: a Tacx Neo 2 chosen as the Bluetooth trainer (and, by the same address,
/// as cadence and power) answered "no Fitness Machine Service" and never connected. A trainer without that service is now ridden through
/// Tacx's own service: the frame bytes, the link, and the garage's saved devices through the real <see cref="Ride.OpenSourceAsync"/>.
/// </summary>
public static class TacxFecTests
{
    private static bool Wait(Func<bool> until, double seconds = 5)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds) { if (until()) return true; Thread.Sleep(5); }
        return until();
    }

    private static bool Near(double? a, double b) => a is { } x && Math.Abs(x - b) < 0.005;

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Tacx trainer over its own Bluetooth service: a trainer with no Fitness Machine Service is ridden");

        // ---- the bytes ----
        byte[] frame = TacxFec.Frame(FecControlPages.TrackResistance(5.0, double.NaN));
        check(frame.SequenceEqual(new byte[] { 0xA4, 0x09, 0x4F, 0x05, 0x33, 0xFF, 0xFF, 0xFF, 0xFF, 0x14, 0x50, 0xFF, 0x6F }),
            "a 5 % gradient is page 0x33 in an ANT frame: sync A4, length 9, acknowledged 4F, channel 5, the page, the XOR checksum");
        check(TacxFec.Unframe(frame) is { } back && back.SequenceEqual(FecControlPages.TrackResistance(5.0, double.NaN)), "and the frame gives the page back");
        byte[] bad = (byte[])frame.Clone(); bad[12] ^= 1;
        check(TacxFec.Unframe(bad) is null && TacxFec.Unframe(new byte[] { 0xA4, 0x09, 0x4E }) is null && TacxFec.Unframe(null) is null
            && TacxFec.Unframe(new byte[] { 0x44, 0x00, 0xB8, 0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0 }) is null,
            "a wrong checksum, a short packet and bytes that are no frame are not a page");

        // ---- the link alone ----
        var log = new List<string>();
        var t = new FakeTacxTrainer();
        var link = new TacxFecTrainerLink(t.Connect(), log.Add);
        link.OpenAsync().GetAwaiter().GetResult();
        check(Wait(() => link.PowerW == 160 && Near(link.CadenceRpm, 88) && Near(link.SpeedMps, 10.0) && link.Messages > 0), "its FE-C pages give power, cadence and speed (160 W, 88 rpm, 10 m/s)");
        check(link.SetTrackResistanceAsync(-3.5, double.NaN).Result && Near(t.GradePercent, -3.5) && Near(link.HeldGradePercent, -3.5), "a gradient is written as page 0x33 (-3.5 %)");
        check(link.SetTargetPowerAsync(180.3).Result && Near(t.TargetW, 180.25) && Near(link.LastTargetPowerW, 180.25) && t.GradePercent is null && Wait(() => link.PowerW == 180),
            "an ERG target is page 0x31 in steps of 0.25 W, and the trainer's power follows it");
        t.RefuseWrites = true;
        check(!link.SetTargetPowerAsync(200).Result && Near(link.LastTargetPowerW, 180.25) && log.Count(l => l.Contains("WARNING: target power")) == 1
            && !link.SetTargetPowerAsync(200).Result && log.Count == 1, "a write the trainer does not accept is false, and logged once");
        t.RefuseWrites = false;
        var r = link.ReleaseAsync(TimeSpan.FromSeconds(1)).Result;
        check(r.Outcome == ReleaseOutcome.Released && Near(t.GradePercent, 0.0) && t.TargetW is null, "release: gradient 0, which also ends the ERG target");
        t.Drop();
        check(!link.SetTrackResistanceAsync(2.0, double.NaN).Result && link.ReleaseAsync(TimeSpan.FromSeconds(1)).Result.Outcome == ReleaseOutcome.LinkAlreadyDead,
            "a trainer that has gone: writes are false, and a release says the link is dead");

        // ---- the garage's saved devices: the Tacx as trainer, cadence and power (Bluetooth), an ANT+ strap, no stick ----
        var radio = new FakeTacxRadio();
        var tacx = radio.Trainer;
        var said = new List<string>();
        void Say(string s) { lock (said) said.Add(s); }
        bool Said(string part) { lock (said) return said.Exists(l => l.Contains(part)); }
        var prefs = new DevicePreferences();
        prefs.Set(Role.Trainer, DeviceKey.Bluetooth(AntDeviceTypes.FitnessEquipment, FakeTacxRadio.TrainerAddress), name: "Tacx Neo 2 44226");
        prefs.Set(Role.Cadence, DeviceKey.Bluetooth(AntDeviceTypes.PowerMeter, FakeTacxRadio.TrainerAddress), name: "Tacx Neo 2 44226");
        prefs.Set(Role.Power, DeviceKey.Bluetooth(AntDeviceTypes.PowerMeter, FakeTacxRadio.TrainerAddress), name: "Tacx Neo 2 44226");
        prefs.Set(Role.HeartRate, new DeviceKey(AntDeviceTypes.HeartRate, 7000, 1));
        static ITrainerSource NoStick(DevicePreferences _) => throw new AntStickException("ANT+ stick not available: test", null);
        using (var src = Ride.OpenSourceAsync(prefs, NoStick, () => radio, Say, bluetoothRetrySeconds: 0.05).GetAwaiter().GetResult())
        {
            check(src.BluetoothSide is not null && !src.BluetoothSensors.Any(), "cadence and power saved as the trainer's own address are not separate connections: the trainer supplies them");
            src.StartBluetooth();
            check(Wait(() => src.TrainerPaired) && radio.Connects == 1 && src.BluetoothSide!.Link is TacxFecTrainerLink && Said("connected through Tacx's own trainer service"),
                "the trainer has no Fitness Machine Service: it is connected through Tacx's own service, and the log says so");
            check(Wait(() => src.PowerW == 160 && Near(src.CadenceRpm, 88) && Near(src.SpeedMps, 10.0)), "the ride's power, cadence and speed are the trainer's");
            check(src.Link(Role.Trainer) is { Messages: > 0 } li && src.Link(Role.Power)?.Key == li.Key && src.Link(Role.Cadence)?.Key == li.Key,
                "the window's lights for trainer, power and cadence are the Bluetooth trainer's");
            check(src.Control!.SetTrackResistanceAsync(4.0, double.NaN).Result && Near(tacx.GradePercent, 4.0), "the ride's gradient write reaches it");
            check(src.Control.SetTargetPowerAsync(150).Result && Near(tacx.TargetW, 150), "and its ERG target");

            tacx.Drop();
            check(Wait(() => !src.TrainerPaired && src.PowerW is null), "the trainer disconnects: not paired, no readings");
            check(Wait(() => src.TrainerPaired && radio.Connects == 2) && Said("trainer back after"), "and it is reconnected under the running ride");

            Ride.ReleaseAsync(src, Say).GetAwaiter().GetResult();
            check(Said("Resistance released.") && Near(tacx.GradePercent, 0.0), "the end of the ride: gradient 0 and \"Resistance released.\"");
        }
        check(tacx.Disconnects == 2, "then the trainer is disconnected");

        // ---- third rework (the garage log of 2026-10-05 20:17): what the log says when the trainer is not there ----
        check(BleRules.Why(new InvalidOperationException("not heard (is it switched on and awake?)")) == "not heard (is it switched on and awake?)"
            && BleRules.Why(new System.Runtime.InteropServices.COMException("", unchecked((int)0x80070490))) == "COMException 0x80070490 (Windows gave no reason)",
            "an error with words is logged in its words; one of Windows' with none is logged by its type and number, never as nothing");
        var lostLog = new List<string>();
        var lostRadio = new FakeTacxRadio();
        using (var src = Ride.OpenSourceAsync(prefs, NoStick, () => lostRadio, s => { lock (lostLog) lostLog.Add(s); }, bluetoothRetrySeconds: 30).GetAwaiter().GetResult())
        {
            src.StartBluetooth();
            bool up = Wait(() => src.TrainerPaired);
            lostRadio.Trainer.Drop();
            bool down = Wait(() => !src.TrainerPaired);
            Ride.ReleaseAsync(src, s => { lock (lostLog) lostLog.Add(s); }).GetAwaiter().GetResult();
            check(up && down && lostLog.Exists(l => l.StartsWith("WARNING: the Bluetooth trainer was not connected when the ride stopped", StringComparison.Ordinal)),
                "a trainer that was connected and is gone when the ride stops: the WARNING that its resistance could not be released");
        }

        // ---- a device that is no trainer at all ----
        var wrong = new DevicePreferences();
        wrong.Set(Role.Trainer, DeviceKey.Bluetooth(AntDeviceTypes.FitnessEquipment, FakeTacxRadio.PedalsAddress), name: "Pedals");
        var words = new List<string>();
        using (var src = Ride.OpenSourceAsync(wrong, NoStick, () => new FakeTacxRadio(), s => { lock (words) words.Add(s); }, bluetoothRetrySeconds: 0.05).GetAwaiter().GetResult())
        {
            src.StartBluetooth();
            bool told = Wait(() => { lock (words) return words.Exists(l => l.Contains("trainer not connected after") && l.Contains("no Fitness Machine Service") && l.EndsWith("its services: 1818", StringComparison.Ordinal)); });
            check(told && !src.TrainerPaired, "a device with neither trainer service is not connected, and the log names the services it does have");
            Ride.ReleaseAsync(src, s => { lock (words) words.Add(s); }).GetAwaiter().GetResult();
            lock (words)
                check(words.Exists(l => l.StartsWith("The Bluetooth trainer was never connected in this ride", StringComparison.Ordinal)) && !words.Exists(l => l.Contains("could not be released")),
                    "stopping a ride whose trainer never connected: one plain line, no WARNING about a resistance the bridge never set");
        }

        // ---- the FTMS trainer is still an FTMS trainer ----
        var ftmsPrefs = new DevicePreferences();
        ftmsPrefs.Set(Role.Trainer, DeviceKey.Bluetooth(AntDeviceTypes.FitnessEquipment, FakeFtmsRadio.TrainerAddress), name: FakeFtmsRadio.TrainerName);
        var ftmsLog = new List<string>();
        using (var src = Ride.OpenSourceAsync(ftmsPrefs, NoStick, () => new FakeFtmsRadio(), s => { lock (ftmsLog) ftmsLog.Add(s); }, bluetoothRetrySeconds: 0.05).GetAwaiter().GetResult())
        {
            src.StartBluetooth();
            check(Wait(() => src.TrainerPaired) && src.BluetoothSide!.Link is FtmsTrainerLink && Wait(() => { lock (ftmsLog) return ftmsLog.Exists(l => l.Contains("connected through the Fitness Machine Service")); }),
                "a trainer that has the Fitness Machine Service is still ridden through it");
        }
    }
}
