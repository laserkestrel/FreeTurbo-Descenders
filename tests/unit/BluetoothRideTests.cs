using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DescendersBridge.Ant;
using DescendersBridge.Bluetooth;

namespace DescendersBridge;

/// <summary>
/// A fake FTMS smart trainer on the Bluetooth seam: it behaves as the Fitness Machine Service specification says a trainer
/// does. Control must be requested first (else "control not permitted"), every command is answered with a response indication, Reset
/// clears the targets and takes control back, and it sends Indoor Bike Data (speed + cadence + power) every 50 ms while connected. Like an
/// ERG trainer, the power it reports is the target while one is held. The trainer keeps its state across connections; <see cref="Drop"/>
/// is the rider switching it off (or walking the laptop out of range), <see cref="PowerCycle"/> what it has forgotten when it is back.
/// </summary>
internal sealed class FakeFtmsTrainer
{
    private readonly object _gate = new();
    private Link? _link;

    public bool HasControl, WasReset, Silent, NoErg;
    public double? GradePercent;
    public int? TargetW;
    public int RiderW = 150;
    public double CadenceRpm = 85;
    /// <summary>The Supported Power Range value; null = the trainer does not have the characteristic.</summary>
    public byte[]? PowerRange = { 0x00, 0x00, 0xD0, 0x07, 0x01, 0x00 };
    public readonly List<byte[]> Writes = new();
    public int Disconnects;

    public int Count(byte op) { lock (_gate) return Writes.Count(w => w[0] == op); }
    public byte[][] WritesNow() { lock (_gate) return Writes.ToArray(); }

    public IBleConnection Connect()
    {
        var l = new Link(this);
        lock (_gate) _link = l;
        return l;
    }

    /// <summary>The trainer goes away: the connection in use is dead.</summary>
    public void Drop() { Link? l; lock (_gate) l = _link; l?.Lose(); }

    /// <summary>Switched off and on: no control, no target.</summary>
    public void PowerCycle() { lock (_gate) { HasControl = false; GradePercent = null; TargetW = null; } }

    private byte[] IndoorBikeData()
    {
        int w; double rpm;
        lock (_gate) { w = TargetW ?? RiderW; rpm = CadenceRpm; }
        int cad = (int)Math.Round(rpm * 2);
        return new byte[] { 0x44, 0x00, 0xB8, 0x0B, (byte)(cad & 0xFF), (byte)(cad >> 8), (byte)(w & 0xFF), (byte)((w >> 8) & 0xFF) };
    }

    /// <summary>One control-point write; the answer is the response indication (null = the trainer stays silent).</summary>
    private byte[]? Handle(byte[] cmd)
    {
        lock (_gate)
        {
            Writes.Add(cmd);
            if (Silent) return null;
            byte op = cmd[0];
            FtmsResult r;
            if (op == Ftms.OpRequestControl) { HasControl = true; r = FtmsResult.Success; }
            else if (!HasControl) r = FtmsResult.ControlNotPermitted;
            else if (op == Ftms.OpReset) { HasControl = false; GradePercent = null; TargetW = null; WasReset = true; r = FtmsResult.Success; }
            else if (op == Ftms.OpSetTargetPower && cmd.Length == 3)
            {
                if (NoErg) r = FtmsResult.NotSupported;
                else { TargetW = (short)(cmd[1] | (cmd[2] << 8)); GradePercent = null; r = FtmsResult.Success; }
            }
            else if (op == Ftms.OpSetIndoorBikeSimulation && cmd.Length == 7) { GradePercent = (short)(cmd[3] | (cmd[4] << 8)) * 0.01; TargetW = null; r = FtmsResult.Success; }
            else if (op is Ftms.OpSetTargetPower or Ftms.OpSetIndoorBikeSimulation) r = FtmsResult.InvalidParameter;
            else r = FtmsResult.NotSupported;
            return new byte[] { Ftms.OpResponse, op, (byte)r };
        }
    }

    private sealed class Link : IBleConnection
    {
        private readonly FakeFtmsTrainer _t;
        private readonly Dictionary<Guid, Action<byte[]>> _subs = new();
        private readonly Timer _data;
        private volatile bool _up = true;

        public Link(FakeFtmsTrainer t)
        {
            _t = t;
            _data = new Timer(_ =>
            {
                Action<byte[]>? to;
                lock (_subs) _subs.TryGetValue(Ftms.IndoorBikeDataChar, out to);
                if (_up) to?.Invoke(_t.IndoorBikeData());
            }, null, 20, 50);
        }

        public bool Connected => _up;
        public void Lose() { if (_up) { _up = false; Interlocked.Increment(ref _t.Disconnects); } }

        public Task<byte[]?> ReadAsync(Guid service, Guid characteristic, CancellationToken ct)
        {
            if (!_up) throw new InvalidOperationException("the trainer did not answer (Unreachable)");
            return Task.FromResult(service == Ftms.Service && characteristic == Ftms.SupportedPowerRangeChar ? _t.PowerRange : null);
        }

        public Task SubscribeAsync(Guid service, Guid characteristic, bool indicate, Action<byte[]> onValue, CancellationToken ct)
        {
            if (!_up) throw new InvalidOperationException("the trainer did not answer (Unreachable)");
            if (service != Ftms.Service) throw new InvalidOperationException("no such service");
            if (indicate != (characteristic == Ftms.ControlPointChar)) throw new InvalidOperationException("the control point indicates, the data notifies");
            lock (_subs) _subs[characteristic] = onValue;
            return Task.CompletedTask;
        }

        public Task<bool> WriteAsync(Guid service, Guid characteristic, byte[] value, CancellationToken ct)
        {
            if (!_up || service != Ftms.Service || characteristic != Ftms.ControlPointChar) return Task.FromResult(false);
            byte[]? answer = _t.Handle(value);
            Action<byte[]>? to;
            lock (_subs) _subs.TryGetValue(Ftms.ControlPointChar, out to);
            if (answer is not null) to?.Invoke(answer);   // the indication can arrive before the write itself returns
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            Lose();
            _data.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The fake radio: one <see cref="FakeFtmsTrainer"/> that is in range or not, plus a device that is not a trainer.</summary>
internal sealed class FakeFtmsRadio : IBleRadio
{
    public const ulong TrainerAddress = 0xC123456789AB;
    public const string TrainerName = "FAKE FTMS 0001";

    public BleAdapterInfo Adapter = new(true, true, true, true, "fake radio");
    public readonly FakeFtmsTrainer Trainer = new();
    public volatile bool InRange = true;
    public int Connects, Attempts;

    public Task<BleAdapterInfo> AdapterAsync() => Task.FromResult(Adapter);

    public async Task<IReadOnlyList<BleAdvert>> ScanAsync(TimeSpan duration, CancellationToken ct)
    {
        await Task.Delay(duration, ct);
        var list = new List<BleAdvert> { new(0x0000A1B2C3D4, "Headphones", -48, 31, Array.Empty<Guid>()) };
        if (InRange) list.Add(new(TrainerAddress, TrainerName, -61, 12, new[] { Ftms.Service }));
        return list;
    }

    public Task<IBleConnection> ConnectAsync(ulong address, CancellationToken ct)
    {
        Interlocked.Increment(ref Attempts);
        if (!InRange || address != TrainerAddress) throw new InvalidOperationException("not heard (is the trainer switched on and awake?)");
        Interlocked.Increment(ref Connects);
        return Task.FromResult(Trainer.Connect());
    }
}

/// <summary>
/// The real <see cref="RideLoop"/> rides the fake FTMS trainer through the real <see cref="RideSource"/>, <see cref="FtmsSession"/>
/// and <see cref="FtmsTrainerLink"/>: freeride gradient writes, "control not permitted" then recovered, ERG, a short workout, the trainer
/// lost and back mid-workout (the session survives, the last target is written again), and the release at the end. No radio, no stick, no
/// game: a real <see cref="ModLink"/> on ephemeral UDP ports (never 9871 / 9872) with the test as the game. Run by `turbotrainer-bridge selftest`.
/// </summary>
public static class BluetoothRideTests
{
    private const string Zwo = "<workout_file><name>Bluetooth test</name><workout><SteadyState Duration='4' Power='0.75'/></workout></workout_file>";
    private static readonly byte[] Flat = { 0x11, 0x00, 0x00, 0x00, 0x00, 0x28, 0x33 };

    private static bool Wait(Func<bool> until, double seconds = 5)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds) { if (until()) return true; Thread.Sleep(5); }
        return until();
    }

    private static bool Near(double? a, double b) => a is { } x && Math.Abs(x - b) < 0.005;

    public static void Run(Action<bool, string> check)
    {
        LinkChecks(check);
        RideChecks(check);
    }

    /// <summary>The link alone: what a write returns.</summary>
    private static void LinkChecks(Action<bool, string> check)
    {
        Console.WriteLine("Bluetooth trainer: a control write is true only when the trainer's response says success");
        var log = new List<string>();
        var t = new FakeFtmsTrainer();
        var link = new FtmsTrainerLink(t.Connect(), log.Add, responseSeconds: 0.2);
        link.OpenAsync().GetAwaiter().GetResult();
        check(t.HasControl && t.Count(Ftms.OpRequestControl) == 1 && t.WritesNow()[0].SequenceEqual(new byte[] { 0x00 }), "Request Control is sent once after connecting");
        check(link.PowerRange == new FtmsPowerRange(0, 2000, 1), "the trainer's power range is read");
        check(link.SetTrackResistanceAsync(5.0, double.NaN).Result && Near(t.GradePercent, 5.0) && t.WritesNow()[^1].SequenceEqual(new byte[] { 0x11, 0x00, 0x00, 0xF4, 0x01, 0x28, 0x33 }),
            "a 5 % gradient is the simulation command, acknowledged");
        check(link.SetTargetPowerAsync(180.4).Result && t.TargetW == 180 && link.LastTargetPowerW == 180, "an ERG target is Set Target Power, acknowledged");
        check(link.SetTargetPowerAsync(5000).Result && t.TargetW == 2000, "a target above the trainer's range is clamped to it (5000 -> 2000 W)");
        check(Wait(() => link.PowerW == 2000 && Near(link.CadenceRpm, 85) && link.SpeedMps is not null && link.Messages > 0), "its Indoor Bike Data gives power, cadence and speed");

        t.NoErg = true;
        check(!link.SetTargetPowerAsync(200).Result && t.TargetW == 2000 && log.Exists(l => l.Contains("target power") && l.Contains("not supported")),
            "\"not supported\" is false, and logged: the trainer still holds what it held");
        int lines = log.Count;
        check(!link.SetTargetPowerAsync(200).Result && log.Count == lines, "the same failure again is not logged again (a write repeats every second)");
        t.NoErg = false;
        t.Silent = true;
        var sw = Stopwatch.StartNew();
        check(!link.SetTrackResistanceAsync(7.0, double.NaN).Result && sw.Elapsed.TotalSeconds < 2 && !Near(t.GradePercent, 7.0), "a write the trainer does not answer is false after the response wait, never true");
        t.Silent = false;

        t.HasControl = false;   // the trainer took control back (another app, or its own reset)
        int asked = t.Count(Ftms.OpRequestControl);
        check(link.SetTrackResistanceAsync(6.0, double.NaN).Result && Near(t.GradePercent, 6.0) && t.Count(Ftms.OpRequestControl) == asked + 1,
            "\"control not permitted\": control is requested again and the write sent once more, then true");
        var last = t.WritesNow();
        check(last.Length >= 3 && last[^3][0] == 0x11 && last[^2][0] == 0x00 && last[^1][0] == 0x11, "in that order: the write, Request Control, the write again");

        var r = link.ReleaseAsync(TimeSpan.FromSeconds(1)).Result;
        last = t.WritesNow();
        check(r.Outcome == ReleaseOutcome.Released && Near(r.HeldGradePercent, 6.0) && last[^2].SequenceEqual(Flat) && last[^1].SequenceEqual(new byte[] { 0x01 }) && t.WasReset && t.GradePercent is null,
            "release: simulation grade 0, then Reset; it reports what the trainer was holding (6 %)");
        t.Drop();
        check(!link.SetTrackResistanceAsync(2.0, double.NaN).Result && link.ReleaseAsync(TimeSpan.FromSeconds(1)).Result.Outcome == ReleaseOutcome.LinkAlreadyDead,
            "a trainer that has gone: writes are false, and a release says the link is dead instead of claiming success");
    }

    private static void RideChecks(Action<bool, string> check)
    {
        Console.WriteLine("Bluetooth trainer: the real ride loop rides a fake FTMS trainer (no stick, no key, no radio)");
        string dir = Path.Combine(Path.GetTempPath(), "descenders_btride_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string zwo = Path.Combine(dir, "bt.zwo");
            File.WriteAllText(zwo, Zwo);
            using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var mod = new ModLink(telemetryPort: 0, commandPort: ((IPEndPoint)game.Client.LocalEndPoint!).Port);
            var radio = new FakeFtmsRadio();
            var t = radio.Trainer;
            var log = new List<string>();
            void Log(string s) { lock (log) log.Add(s); }
            bool Logged(string part) { lock (log) return log.Exists(l => l.Contains(part)); }

            var prefs = new DevicePreferences();
            prefs.Set(Role.Trainer, DeviceKey.Bluetooth(AntDeviceTypes.FitnessEquipment, FakeFtmsRadio.TrainerAddress), name: FakeFtmsRadio.TrainerName);
            using var src = Ride.OpenSourceAsync(prefs, _ => throw new AntStickException("the stick must not be opened", null), () => radio, Log, bluetoothRetrySeconds: 0.1).GetAwaiter().GetResult();

            // the game: fresh telemetry ten times a second, on the road gradient the test sets
            double gradient = 0.05;
            using var gameStop = new CancellationTokenSource();
            var gameTask = Task.Run(async () =>
            {
                while (!gameStop.IsCancellationRequested)
                {
                    byte[] b = Encoding.UTF8.GetBytes("{\"type\":\"telemetry\",\"playerFound\":true,\"riding\":true,\"fitness\":{\"active\":true,\"roadGradient\":"
                        + Volatile.Read(ref gradient).ToString("0.###", CultureInfo.InvariantCulture) + "}}");
                    try { game.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, mod.ListenPort)); } catch { return; }
                    try { await Task.Delay(100, gameStop.Token); } catch { return; }
                }
            });

            var cfg = new ResistanceConfig { WriteIntervalMs = 250 };
            var training = new TrainingConfig { TrainerDifficulty = 1 };   // the road gradient reaches the trainer as is
            var loop = new RideLoop(src, mod, cfg, null, training, new Session(SessionConfig.From(training, cfg), () => 200, Log));
            using var cts = new CancellationTokenSource();
            Task run = Task.Run(() => loop.RunAsync(cts.Token));

            check(!src.TrainerPaired && src.PowerW is null && src.Control is not null && !src.LinkLost, "before the trainer is found: not paired, no reading (absent is never zero), nothing reported lost");
            src.BluetoothSide!.Start();
            check(Wait(() => src.TrainerPaired && t.HasControl) && radio.Connects == 1, "the trainer is found and connected, control requested");
            check(Wait(() => src.PowerW == 150 && Near(src.CadenceRpm, 85)), "its power and cadence are the ride's (150 W, 85 rpm): the trainer supplies them when no other sensor is chosen");
            check(src.HeartRateBpm is null && src.Link(Role.Trainer) is { Messages: > 0 } li && li.Key.IsBluetooth && src.Link(Role.Power)?.Key == li.Key && src.Link(Role.HeartRate) is null,
                "the window's lights: trainer and power are the Bluetooth trainer, heart rate has no device");

            check(Wait(() => Near(t.GradePercent, 5.0)), "freeride: the road's 5 % reaches the trainer as its simulation grade");
            Volatile.Write(ref gradient, 0.08);
            check(Wait(() => Near(t.GradePercent, 8.0)), "the road steepens to 8 %: written");

            t.HasControl = false;
            int asked = t.Count(Ftms.OpRequestControl);
            Volatile.Write(ref gradient, 0.03);
            check(Wait(() => Near(t.GradePercent, 3.0)) && t.Count(Ftms.OpRequestControl) == asked + 1 && Wait(() => Near(loop.LastSentGrade, 3.0)),
                "\"control not permitted\" mid-ride: control is asked for again and the 3 % lands");

            var erg = loop.Post((s, now) => { s.Apply(new Intent("erg_toggle"), now); return new MenuResult(true, "erg"); });
            check(erg.Wait(3000) && loop.Session.Erg && loop.Session.ErgTargetW is { } manual && Wait(() => t.TargetW == (int)Math.Round(manual) && t.GradePercent is null),
                $"ERG from the game's mode button: the target ({loop.Session.ErgTargetW:0} W) is Set Target Power");
            check(Wait(() => src.PowerW == t.TargetW), "and the trainer's power follows it");

            var started = loop.Post((s, now) => Menu.StartWorkout(s, zwo, now));
            check(started.Wait(3000) && started.Result.Ok && Wait(() => t.TargetW == 150), "a workout started from the window: its 150 W step (0.75 x FTP 200) is the target");

            // lost mid-workout: out of range, and it has forgotten everything when it is back
            radio.InRange = false;
            t.Drop();
            check(Wait(() => !src.TrainerPaired && src.PowerW is null), "the trainer disconnects mid-workout: not paired, no readings");
            int attempts = radio.Attempts;
            check(Wait(() => radio.Attempts >= attempts + 2) && !run.IsCompleted && !src.LinkLost, "it is looked for again and again; the ride loop carries on");
            check(loop.Session.Run?.W.Name == "Bluetooth test" && loop.Session.Erg && Logged("trainer lost"), "the session, its mode and its workout survive, and the loss is logged");
            float heldAt = loop.Session.Run?.Elapsed ?? -1f;
            t.PowerCycle();
            radio.InRange = true;
            check(Wait(() => src.TrainerPaired && radio.Connects == 2 && src.BluetoothSide.Connections == 2), "back in range: reconnected under the running ride");
            check(Wait(() => t.TargetW == 150 && t.HasControl), "control is requested again and the last target (150 W) written again");
            check(loop.Session.Run is { } still && still.W.Name == "Bluetooth test" && still.Elapsed >= heldAt && ReferenceEquals(RideLoop.Current, loop) && Logged("trainer back after"),
                "same loop, same workout, its clock carried on from where it was");

            check(Wait(() => loop.Session.Run is null && !loop.Session.Erg, 12), "the workout runs to its end: back to freeride");
            check(Wait(() => t.TargetW is null && Near(t.GradePercent, 3.0)), "and the trainer is back on the road's gradient (ERG released)");

            cts.Cancel();
            check(run.Wait(5000), "Stop ends the loop");
            Volatile.Write(ref gradient, 0.03);
            Ride.ReleaseAsync(src, Log).GetAwaiter().GetResult();
            var w = t.WritesNow();
            check(Logged("Resistance released.") && w[^2].SequenceEqual(Flat) && w[^1].SequenceEqual(new byte[] { 0x01 }) && t.WasReset && t.GradePercent is null && t.TargetW is null,
                "the end of the ride: simulation grade 0, then Reset, and \"Resistance released.\" in the log");
            src.Dispose();
            check(t.Disconnects == 2 && !src.TrainerPaired, "then the trainer is disconnected");
            check(!Logged("ANT+") && !Logged("stick"), "nothing about ANT+ or a stick was said in the whole ride");
            gameStop.Cancel();
            gameTask.Wait(1000);

            // a ride that stops while the trainer is away
            var away = new FakeFtmsRadio { InRange = false };
            using var src2 = Ride.OpenSourceAsync(prefs, _ => throw new AntStickException("no", null), () => away, Log, bluetoothRetrySeconds: 0.05).GetAwaiter().GetResult();
            src2.BluetoothSide!.Start();
            check(Wait(() => away.Attempts >= 2) && !src2.TrainerPaired && Logged("trainer not connected after"), "a trainer still switched off at the start is not an error: it is searched for");
            Ride.ReleaseAsync(src2, Log).GetAwaiter().GetResult();
            check(Logged("The Bluetooth trainer was never connected in this ride") && !Logged("WARNING: the Bluetooth trainer was not connected when the ride stopped"),
                "stopping then says it was never connected: not \"Resistance released.\", and no WARNING about a load the bridge never set");
        }
        catch (SocketException ex) { check(false, "could not open a UDP port for the test: " + ex.Message); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
