using DescendersBridge.Ant;
using DescendersCyclingPOC;

namespace DescendersBridge;

/// <summary>
/// `turbotrainer-bridge selftest`: drives the real <see cref="RideLoop.Step"/> against a fake trainer on a simulated clock (250 ms ticks,
/// like the live loop). No radio, no mod, no game. Covers the bridge half of docs/REQUIREMENTS.md TE5 / TE6 and the page 0x31 encoding.
/// </summary>
public static class SelfTest
{
    private sealed class FakeTrainer : ITrainerControl
    {
        public readonly List<(string Kind, double Value, DateTime At)> Writes = new();
        public bool Ack = true;
        public DateTime Now;
        public Task<bool> SetTrackResistanceAsync(double g, double crr, CancellationToken ct = default) { Writes.Add(("grade", g, Now)); return Task.FromResult(Ack); }
        public Task<bool> SetTargetPowerAsync(double w, CancellationToken ct = default) { Writes.Add(("erg", w, Now)); return Task.FromResult(Ack); }
    }

    private static int _fail, _pass;
    private static void Check(bool ok, string what) { if (ok) _pass++; else { _fail++; Console.WriteLine("  FAIL: " + what); } }

    private static readonly DateTime T0 = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Rig
    {
        public readonly FakeTrainer Tr = new();
        public readonly RideLoop Loop;
        public readonly ResistanceConfig Cfg = new();
        public DateTime Now = T0;
        public bool Paired = true;
        // difficulty 1: the grades below are what reaches the trainer (tests\unit\TrainingTests.cs covers the default 0.35)
        public Rig(double? fixedErg = null) { Loop = new RideLoop(Cfg, fixedErg, new TrainingConfig { TrainerDifficulty = 1 }); }

        /// <summary>The mod's telemetry plus the session's ERG target (null = freeride) for one tick.</summary>
        public readonly record struct In(ModState M, double? ErgW);

        public static In Erg(double? w, double age = 0.1, bool player = true, double grade = 0.05) => new(new ModState(true, age, player, true, grade), w);
        public static In Free(double grade = 0.05, double age = 0.1) => new(new ModState(true, age, true, true, grade), null);

        /// <summary>Run <paramref name="ticks"/> 250 ms ticks with the same state; returns the writes made during them.</summary>
        public List<(string Kind, double Value, DateTime At)> Run(In s, int ticks)
        {
            int before = Tr.Writes.Count;
            for (int i = 0; i < ticks; i++)
            {
                Now = Now.AddMilliseconds(250); Tr.Now = Now;
                Loop.Step(Tr, s.M, s.ErgW, Paired, Now, CancellationToken.None);
            }
            return Tr.Writes.Skip(before).ToList();
        }
    }

    public static int Run()
    {
        Console.WriteLine("Page 0x31 encoding");
        Check(FecControlPages.TargetPower(150).SequenceEqual(new byte[] { 0x31, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x58, 0x02 }), "150 W = 600 x 0.25 W = 0x0258");
        Check(FecControlPages.TargetPower(400)[6] == 0x40 && FecControlPages.TargetPower(400)[7] == 0x06, "400 W = 0x0640");
        Check(FecControlPages.TargetPower(-5)[6] == 0 && FecControlPages.TargetPower(-5)[7] == 0, "negative clamps to 0");
        Check(FecControlPages.TargetPower(150.1)[6] == 0x58 && FecControlPages.TargetPower(150.1)[7] == 0x02, "150.1 W rounds to 600 raw");

        Console.WriteLine("Enter ERG from freeride: first write goes straight to the target, then a refresh every second");
        {
            var r = new Rig();
            r.Run(Rig.Free(0.06), 6);   // freeride, loaded
            var w = r.Run(Rig.Erg(120), 8);   // 2 s
            Check(w.Count > 0 && w[0].Kind == "erg" && w[0].Value == 120, "first ERG write is exactly 120 W (no ramp from 0)");
            Check(w.All(x => x.Kind == "erg" && x.Value == 120), "only 120 W erg writes while the target is unchanged");
            Check(w.Count == 2, $"1 write on entry + 1 refresh in 2 s (got {w.Count})");
            Check(w.Count == 2 && Math.Abs((w[1].At - w[0].At).TotalMilliseconds - 1000) < 1, "refresh is 1000 ms after the first write");
            Check(r.Loop.ErgApplied == 120, "ErgApplied echoes 120");
        }

        Console.WriteLine("Step 120 -> 320 W: ramp at 100 W/s, no overshoot, then hold");
        {
            var r = new Rig();
            r.Run(Rig.Erg(120), 4);
            var w = r.Run(Rig.Erg(320), 12);
            var ergs = w.Where(x => x.Kind == "erg").Select(x => x.Value).ToList();
            Check(ergs.Count >= 8, $"one write per tick while ramping (got {ergs.Count} in 3 s)");
            Check(ergs.Zip(ergs.Skip(1), (a, b) => b >= a).All(x => x), "monotonic rising");
            Check(ergs.Max() == 320, "reaches exactly 320, never above");
            Check(ergs.Count > 0 && Math.Abs(ergs[0] - 145) < 0.01, $"first ramp step is +25 W (got {(ergs.Count > 0 ? ergs[0] : double.NaN)})");
            int reach = ergs.FindIndex(v => v == 320) + 1;
            Check(reach == 8, $"320 W reached on tick 8, i.e. 2 s (got {reach})");
            Check(r.Loop.ErgApplied == 320, "ErgApplied = 320 at the end");
            var after = r.Run(Rig.Erg(320), 4);
            Check(after.Count == 1 && after[0].Value == 320, "then only the 1/s refresh");
        }

        Console.WriteLine("Step down 300 -> 100 W");
        {
            var r = new Rig();
            r.Run(Rig.Erg(300), 4);
            var ergs = r.Run(Rig.Erg(100), 12).Select(x => x.Value).ToList();
            Check(ergs.Zip(ergs.Skip(1), (a, b) => b <= a).All(x => x) && ergs.Min() == 100 && ergs.Max() < 300, "monotonic falling to exactly 100");
        }

        Console.WriteLine("Session asks for more than the bridge maximum");
        {
            var r = new Rig();
            var w = r.Run(Rig.Erg(900), 12);
            Check(w.All(x => x.Value <= r.Cfg.ErgMaxW), $"never above ErgMaxW {r.Cfg.ErgMaxW}");
        }

        Console.WriteLine("ERG -> freeride (TE5): released at once");
        {
            var r = new Rig();
            r.Run(Rig.Erg(200), 5);   // just wrote / refreshed <1 s ago
            var w = r.Run(Rig.Free(0.06), 1);
            Check(w.Count == 1 && w[0].Kind == "grade", "the very next tick writes a resistance page (ignores the 1/s limit)");
            Check(w.Count == 1 && Math.Abs(w[0].Value - 6.0) < 1e-9, "at the mod's gradient (6 %)");
            Check(r.Loop.ErgApplied is null, "no longer ERG");
            var later = r.Run(Rig.Free(0.06), 8);
            Check(later.All(x => x.Kind == "grade") && later.Count <= 1, "no more ERG writes; deadband stops repeats");
        }

        Console.WriteLine("Failsafe (TE6, bridge side): stale, silent, no player, or an unusable target -> flat");
        foreach (var (name, m) in new (string, Rig.In)[]
        {
            ("telemetry older than StaleSeconds", Rig.Erg(200, age: 2.0)),
            ("mod never heard", new Rig.In(new ModState(false, double.PositiveInfinity, false, false, null), 200)),
            ("player not found", Rig.Erg(200, player: false)),
            ("target NaN", Rig.Erg(double.NaN)),
            ("target 0", Rig.Erg(0)),
        })
        {
            var r = new Rig();
            r.Run(Rig.Erg(200), 5);
            var w = r.Run(m, 1);
            Check(w.Count == 1 && w[0].Kind == "grade" && w[0].Value == 0.0, $"{name}: flat (0 %) written on the next tick");
            Check(r.Loop.ErgApplied is null, $"{name}: ErgApplied cleared");
            Check(r.Run(m, 12).All(x => x.Kind == "grade" && x.Value == 0.0), $"{name}: never an ERG write afterwards");
        }

        Console.WriteLine("Recovery: ERG resumes when telemetry returns");
        {
            var r = new Rig();
            r.Run(Rig.Erg(200), 4);
            r.Run(Rig.Erg(200, age: 3.0), 4);
            var w = r.Run(Rig.Erg(200), 1);
            Check(w.Count == 1 && w[0].Kind == "erg" && w[0].Value == 200, "straight back to 200 W (no ramp from 0)");
        }

        Console.WriteLine("Trainer link lost / re-paired");
        {
            var r = new Rig();
            r.Run(Rig.Erg(200), 4);
            r.Paired = false;
            Check(r.Run(Rig.Erg(200), 8).Count == 0, "no writes while unpaired");
            Check(r.Loop.ErgApplied is null, "ErgApplied cleared while unpaired");
            r.Paired = true;
            var w = r.Run(Rig.Erg(200), 1);
            Check(w.Count == 1 && w[0].Value == 200, "first write after re-pair goes out at once");
        }

        Console.WriteLine("Unacknowledged writes");
        {
            var r = new Rig();
            r.Tr.Ack = false;
            var w = r.Run(Rig.Erg(200), 4);
            Check(w.Count == 4 && w.All(x => x.Value == 200), "an unacknowledged ERG write is retried every tick");
            Check(r.Loop.ErgApplied is null, "and is not reported as applied (echo stays freeride)");
            r.Tr.Ack = true;
            r.Run(Rig.Erg(200), 1);
            Check(r.Loop.ErgApplied == 200, "applied once acknowledged");

            // A flat release that is not acknowledged must be retried, not forgotten.
            r.Tr.Ack = false;
            var rel = r.Run(Rig.Free(0.0), 6);   // 1.5 s
            Check(rel.Count >= 2 && rel.All(x => x.Kind == "grade" && x.Value == 0.0), $"failed release is retried within the write interval (writes: {rel.Count})");
            r.Tr.Ack = true;
            var ok = r.Run(Rig.Free(0.0), 5);
            Check(ok.Count >= 1, "and lands once the trainer acknowledges");
            Check(r.Loop.LastSentGrade == 0.0, "LastSentGrade = 0 after the acknowledged release");
        }

        Console.WriteLine("Fixed ERG (`run --erg 150`), no mod");
        {
            var r = new Rig(150);
            var absent = new Rig.In(new ModState(false, double.PositiveInfinity, false, false, null), null);
            var w = r.Run(absent, 8);
            Check(w.Count > 0 && w.All(x => x.Kind == "erg" && x.Value == 150), "holds 150 W with the mod absent");
            var w2 = new Rig(9999).Run(absent, 4);
            Check(w2.All(x => x.Value <= new ResistanceConfig().ErgMaxW), "fixed target is clamped to ErgMaxW too");
        }

        Console.WriteLine("Freeride is unchanged (regression)");
        {
            var r = new Rig();
            var w = r.Run(Rig.Free(0.06), 8);
            Check(w.Count == 1 && w[0].Kind == "grade" && Math.Abs(w[0].Value - 6.0) < 1e-9, "one write at 6 %, then the deadband holds it");
            var w2 = r.Run(Rig.Free(0.20), 4);
            Check(w2.Count == 1 && w2[0].Value == r.Cfg.MaxGradePercent, $"clamped to MaxGradePercent {r.Cfg.MaxGradePercent}");
        }

        RiderProfileTests();
        GuiTests();
        WorkoutTests();
        PickerTests();
        PlanTests();
        ProgressTests();
        LibraryDataTests();
        DumpFilesTests.Run(Check);
        PortProbeTests.Run(Check);
        IlReaderTests.Run(Check);
        SessionTests.Run(Check);
        MenuTests.Run(Check, FindFixture);   // workouts and plans chosen in the bridge window, plan progress, the library setting; no menu in the game
        BuildStampTests.Run(Check);
        TrainingTests.Run(Check);   // the training section, trainer difficulty, ERG limits, the mod's rider wait
        SpeedModelTests.Run(Check);   // the shared speed integrator and the bike-type presets
        HubTests.Run(Check);          // hello, game presence, ride log end / reopen, launch
        RideLoopTests.Run(Check);     // a slow, failing trainer write never delays the game-facing tick
        StickRecoveryTests.Run(Check); // A lost ANT+ stick is reopened under the running ride, the workout survives
        SensorSearchTests.Run(Check); // a sensor that is not there searches briefly and is retried, the trainer has priority
        KnownDevicesTests.Run(Check); // a device set to none stays known and on offer
        DeviceIdentityTests.Run(Check); // the manufacturer of ANT+ devices (page decode, table, the window's words, the settings file)
        LogsFolderTests.Run(Check);   // the logs folder is the standard one unless another is chosen on the Logs tab
        GraphHistoryTests.Run(Check); // the graphs show the last graphs.historySeconds of the settings file, 300 s by default
        AccentColourTests.Run(Check); // the window's accent is teal, window.accentColour of the settings file changes it
        WorkoutsTabTests.Run(Check);  // the left / right picker, the up / down focus order, the effort graph and its cache
        ConnectRulesTests.Run(Check); // the window connects by itself; when it does, and the words while it is not connected
        NoStickLogTests.Run(Check);   // no ANT+ stick = one plain ERROR line in the log, no stack trace
        StatusStripTests.Run(Check);// the Ride tab's status strip (Trainer, Game), the ride state in the log, one size of light
        PadNavTests.Run(Check);       // the Xbox pad in the bridge window (presses, D-pad repeat, when it is listened to)
        FtmsTests.Run(Check);         // the Bluetooth FTMS bytes (Indoor Bike Data, control point, responses, power range)
        BluetoothSettingsTests.Run(Check); // transport in the settings file; only Bluetooth chosen = no stick, no key
        BluetoothRideTests.Run(Check); // the real ride loop on a fake FTMS trainer (control, ERG, workout, lost and back, release)
        TacxFecTests.Run(Check);      // a Tacx with no Fitness Machine Service is ridden through Tacx's own Bluetooth service
        BleSensorTests.Run(Check);    // the Bluetooth sensor bytes (heart rate, CSC, cycling power), cadence from crank data, setup rules
        BleSensorRideTests.Run(Check); // fake sensors on the seam; a strap dropping out and back mid-ride, which reading wins, no radio
        DeviceSearchTests.Run(Check); // one search for ANT+ and Bluetooth (the four cases), the rows, the Bluetooth trainer's strip words
        WindowDesignTests.Run(Check); // the window's palette and rules, and the real window built and painted off-screen

        Console.WriteLine($"\n{_pass} checks passed, {_fail} failed.");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// TrainerBridgeConfig.json (rider profile + devices) create / migrate / clamp / save / dump, and the `rider` message.
    /// Uses a temp folder, never the real %APPDATA% file.
    /// </summary>
    private static void RiderProfileTests()
    {
        Console.WriteLine("Bridge config (TrainerBridgeConfig.json: rider profile + devices)");
        string dir = Path.Combine(Path.GetTempPath(), "descenders_rider_" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new List<string>();
            UserConfig Load(string sub, Func<RiderConfig?> legacy) =>
                TrainerBridgeConfig.Load(Path.Combine(dir, sub, TrainerBridgeConfig.FileName), Path.Combine(dir, sub, "rider.json"), Path.Combine(dir, sub, "old", "devices.json"), legacy, log.Add);

            string p1 = Path.Combine(dir, "a", TrainerBridgeConfig.FileName);
            var d = Load("a", () => null);
            Check(File.Exists(p1) && d.Rider == new RiderConfig() && d.Devices.Roles.Count == 0, "missing file, nothing to migrate: created with the default rider and no devices");
            Check(d.Rider.WheelDiameterM == 0.7 && d.Rider.GearRatio == 1.0 && d.Rider.FtpW == 180, "defaults: wheel 0.7 m, gear 1.0, FTP 180 W");
            string text = File.ReadAllText(p1);
            Check(text.Contains("\"rider\"") && text.Contains("\"devices\"") && text.Contains("\"riderWeightKg\"") && text.Contains("\"ftpW\"") && !text.Contains("networkKey", StringComparison.OrdinalIgnoreCase),
                "one file with rider and devices sections, camelCase keys, and no network key");
            Check(Load("a", () => throw new Exception("must not be asked")).Rider == d.Rider, "an existing file is read back as written, config.json is not consulted");
            Check(log.Exists(l => l.StartsWith("Bridge config " + p1 + ":") && l.Contains("\"riderWeightKg\": 75")), "every load prints the file path and its values");

            log.Clear();
            string p2 = Path.Combine(dir, "b", TrainerBridgeConfig.FileName);
            var m = Load("b", () => new RiderConfig { RiderWeightKg = 82, BikeWeightKg = 11, WheelDiameterM = 0.68, GearRatio = 2 });
            Check(m.Rider.RiderWeightKg == 82 && m.Rider.BikeWeightKg == 11 && m.Rider.WheelDiameterM == 0.68 && m.Rider.GearRatio == 2 && m.Rider.FtpW == 180, "migration: seeded from the config.json rider block (FTP default)");
            Check(File.Exists(p2) && log.Exists(l => l.Contains("rider block in config.json")), "migration writes the file and logs it");
            Check(Load("b", () => new RiderConfig { RiderWeightKg = 60 }).Rider.RiderWeightKg == 82, "after migration the config.json block is ignored");

            log.Clear();
            Directory.CreateDirectory(Path.Combine(dir, "m", "old"));
            File.WriteAllText(Path.Combine(dir, "m", "rider.json"), "{ \"riderWeightKg\": 68, \"bikeWeightKg\": 8, \"ftpW\": 240 }");
            File.WriteAllText(Path.Combine(dir, "m", "old", "devices.json"), "{ \"Roles\": { \"Trainer\": { \"DeviceType\": 17, \"DeviceNumber\": 4242, \"TransmissionType\": 5, \"Name\": \"FE-C\" } } }");
            var o = Load("m", () => new RiderConfig { RiderWeightKg = 99 });
            Check(o.Rider.RiderWeightKg == 68 && o.Rider.FtpW == 240, "migration: rider.json wins over the config.json block");
            Check(o.Devices.Get(Role.Trainer) == new DeviceKey(17, 4242, 5) && o.Devices.Get(Role.HeartRate) is null, "migration: devices.json copied (trainer 17/4242/5)");
            Check(log.Exists(l => l.Contains("rider.json")) && log.Exists(l => l.Contains("devices.json")), "both copies are logged");
            File.Delete(Path.Combine(dir, "m", "old", "devices.json"));
            Check(Load("m", () => null).Devices.Get(Role.Trainer) == new DeviceKey(17, 4242, 5), "after migration the devices are read from the new file");

            log.Clear();
            string p4 = Path.Combine(dir, "s", TrainerBridgeConfig.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(p4)!);
            File.WriteAllText(p4, "{ // comment\n \"rider\": { \"ftpW\": 260 }, \"devices\": { \"trainer\": { \"deviceType\": 17, \"deviceNumber\": 7, \"transmissionType\": 1 } } }");
            var s = Load("s", () => null);
            Check(s.Rider.FtpW == 260 && s.Devices.Get(Role.Trainer) == new DeviceKey(17, 7, 1), "hand-edited file: comments allowed, role names case-insensitive");
            s.Devices.Set(Role.HeartRate, new DeviceKey(120, 99, 1));
            TrainerBridgeConfig.SaveDevices(p4, s.Devices);
            var s2 = Load("s", () => null);
            Check(s2.Rider.FtpW == 260 && s2.Devices.Get(Role.HeartRate) == new DeviceKey(120, 99, 1) && s2.Devices.Get(Role.Trainer) == new DeviceKey(17, 7, 1),
                "saving devices keeps the rider section");
            File.WriteAllText(p4, "{ broken");
            bool threw = false;
            try { TrainerBridgeConfig.SaveDevices(p4, s2.Devices); } catch (InvalidOperationException) { threw = true; }
            Check(threw && File.ReadAllText(p4) == "{ broken", "saving devices into an unreadable file refuses and leaves it alone");

            log.Clear();
            string p3 = Path.Combine(dir, "c", TrainerBridgeConfig.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(p3)!);
            File.WriteAllText(p3, "{ \"RIDER\": { \"RIDERWEIGHTKG\": 250, \"bikeWeightKg\": 1, \"ftpW\": 700, \"gearRatio\": 0 } }");
            var c = Load("c", () => null).Rider;
            Check(c.RiderWeightKg == 200 && c.BikeWeightKg == 3 && c.FtpW == 600 && c.GearRatio == 0 && c.WheelDiameterM == 0.7, "out-of-range values clamped (200 kg, 3 kg, 600 W), gear 0 kept, a missing field takes its default");
            Check(log.Exists(l => l.Contains("clamped")), "clamping is logged");

            File.WriteAllText(p3, "{ not json");
            var u = Load("c", () => null);
            Check(u.Rider == new RiderConfig() && u.Devices.Roles.Count == 0 && File.ReadAllText(p3) == "{ not json", "unreadable file: defaults, and the file is left alone");

            Check(RiderProfile.Message(new RiderConfig { RiderWeightKg = 75, BikeWeightKg = 9.5, FtpW = 250 }) == "{\"type\":\"rider\",\"riderWeightKg\":75,\"bikeWeightKg\":9.5,\"ftpW\":250,\"bikeType\":\"road\",\"cdA\":0.32,\"crr\":0.004}",
                "the rider message matches docs/IPC.md (with the default road bike preset)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The window's pure logic (health lights, 30 s history, plain-language messages) and the settings it writes.</summary>
    private static void GuiTests()
    {
        Console.WriteLine("Window: health lights, history, messages, rider settings");
        var k = new DeviceKey(17, 1, 5);
        Check(Gui.HealthRules.Classify(null, null) == Gui.Health.NotChosen, "no device for the role = not set up");
        Check(Gui.HealthRules.Classify(new LinkInfo(k, 0, double.PositiveInfinity), null) == Gui.Health.Searching, "chosen but never heard = searching");
        Check(Gui.HealthRules.Classify(new LinkInfo(k, 50, 0.2), 4.0) == Gui.Health.Good, "4 msg/s, heard 0.2 s ago = connected");
        Check(Gui.HealthRules.Classify(new LinkInfo(k, 50, 0.2), null) == Gui.Health.Good, "rate not measured yet but heard just now = connected");
        Check(Gui.HealthRules.Classify(new LinkInfo(k, 50, 0.2), 1.5) == Gui.Health.Weak, "1.5 msg/s = weak signal");
        Check(Gui.HealthRules.Classify(new LinkInfo(k, 50, 2.0), 4.0) == Gui.Health.Weak, "a 2 s gap = weak signal");
        Check(Gui.HealthRules.Classify(new LinkInfo(k, 50, AntSession.StaleSeconds + 0.1), 4.0) == Gui.Health.Lost, "silent longer than the readings stay valid = signal lost");
        Check(Enum.GetValues<Gui.Health>().All(h => Gui.HealthRules.Text(h).Length > 0 && Gui.HealthRules.Advice(h).Length > 0), "every health state has words and advice");

        var rm = new Gui.RateMeter();
        Check(rm.Add(0, 0) is null && rm.Add(1, 4) is null, "rate: nothing until 1.5 s of samples");
        double? rate = null;
        for (int i = 2; i <= 10; i++) rate = rm.Add(i, i * 4);
        Check(rate is { } r1 && Math.Abs(r1 - 4.0) < 1e-9, $"rate: 4 msg/s measured (got {rate})");
        for (int i = 11; i <= 16; i++) rate = rm.Add(i, 40 + (i - 10));
        Check(rate is { } r2 && Math.Abs(r2 - 1.0) < 1e-9, $"rate: only the last 4 s count, so a drop shows within seconds (got {rate})");
        Check(rm.Add(17, 3) is null, "rate: a count that goes backwards (new ride) starts again");

        var hist = new Gui.RollingHistory { WindowSeconds = 30 };
        for (int i = 0; i <= 40; i++) hist.Add(i, i == 35 ? null : i * 10.0);
        Check(hist.Points[0].T == 10 && hist.Points[^1].T == 40, "history keeps exactly the last 30 s");
        Check(hist.Points.Any(p => p.V is null) && hist.Min == 100 && hist.Max == 400, "a missing reading is a gap, not a 0 (min 100, max 400)");
        Check(hist.Summary() == "last 30 s: 30 readings, 100 to 400", $"summary for UI Automation (got '{hist.Summary()}')");
        Check(new Gui.RollingHistory().Summary() == "last 300 s: no readings" && new Gui.RollingHistory().Max is null, "empty history (5 min unless the settings file says otherwise)");

        Check(Gui.ReadingText.Whole(null) == "--" && Gui.ReadingText.Whole(199.6) == "200" && Gui.ReadingText.SpeedKmh(10) == "36.0" && Gui.ReadingText.SpeedKmh(null) == "--",
            "readings: '--' when absent (never 0), watts whole, speed in km/h with one decimal");

        Check(Gui.FriendlyText.For(new System.Net.Sockets.SocketException(10048)) == Gui.FriendlyText.AlreadyRunning, "port in use = another bridge is running");
        Check(Gui.FriendlyText.For(new AntStickException("ANT+ stick not available: x", null)) == Gui.FriendlyText.NoStick, "stick exception = plug the stick in");
        Check(Gui.FriendlyText.For(new InvalidOperationException("No ANT+ network key: this build has none")) == Gui.FriendlyText.NoKey, "no key = get the official release");
        Check(Gui.FriendlyText.For(new Exception("outer", new AntStickException("inner", null))) == Gui.FriendlyText.NoStick, "inner exceptions are looked at too");
        var generic = Gui.FriendlyText.For(new NullReferenceException("Object reference not set"));
        Check(generic.Title == "Something went wrong." && !generic.Text.Contains("Object reference"), "anything else: a generic message without the technical text");
        var all = typeof(Gui.FriendlyText).GetFields().Where(f => f.FieldType == typeof(Gui.FriendlyMessage)).Select(f => (Gui.FriendlyMessage)f.GetValue(null)!).Append(generic).ToList();
        string[] jargon = { "exception", "json", "udp", "port 9871", "stack", "null", "config.json", "fe-c", "%appdata%", "channel" };
        Check(all.Count >= 9 && all.All(m => !jargon.Any(j => m.Text.Contains(j, StringComparison.OrdinalIgnoreCase))), "no message uses technical words");
        Check(all.All(m => m.Title.Length > 0 && m.Advice.Length > 0), "every message says what happened and what to do");

        string dir = Path.Combine(Path.GetTempPath(), "descenders_gui_" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new List<string>();
            string p = Path.Combine(dir, TrainerBridgeConfig.FileName);
            UserConfig Load() => TrainerBridgeConfig.Load(p, Path.Combine(dir, "rider.json"), Path.Combine(dir, "devices.json"), () => null, log.Add);

            var saved = TrainerBridgeConfig.SaveRider(p, new RiderConfig { RiderWeightKg = 81.5, BikeWeightKg = 8.2, FtpW = 230, WheelDiameterM = 0.68, GearRatio = 0 });
            Check(File.Exists(p) && Load().Rider == saved && saved.RiderWeightKg == 81.5 && saved.FtpW == 230, "saving the rider creates the file and reads back");
            var prefs = Load().Devices; prefs.Set(Role.Trainer, k); TrainerBridgeConfig.SaveDevices(p, prefs);
            TrainerBridgeConfig.SaveRider(p, saved with { FtpW = 250 });
            var both = Load();
            Check(both.Rider.FtpW == 250 && both.Devices.Get(Role.Trainer) == k, "saving the rider keeps the devices (and saving devices keeps the rider)");
            Check(TrainerBridgeConfig.SaveRider(p, new RiderConfig { RiderWeightKg = 500, FtpW = 10 }) is { RiderWeightKg: 200, FtpW: 50 } && Load().Rider.RiderWeightKg == 200,
                "the rider is clamped before it is stored");

            Check(TrainerBridgeConfig.IsReadable(p, out _) && TrainerBridgeConfig.IsReadable(Path.Combine(dir, "missing.json"), out _), "a good file and a missing file are both usable");
            File.WriteAllText(p, "{ \"rider\": { \"ftpW\": ");
            Check(!TrainerBridgeConfig.IsReadable(p, out string why) && why.Length > 0, "a broken file is reported");
            bool threw = false;
            try { TrainerBridgeConfig.SaveRider(p, new RiderConfig()); } catch (InvalidOperationException) { threw = true; }
            Check(threw && File.ReadAllText(p).StartsWith("{ \"rider\": { \"ftpW\": "), "saving the rider into a broken file refuses and leaves it alone");
            string backup = TrainerBridgeConfig.ResetBroken(p);
            Check(File.Exists(backup) && File.ReadAllText(backup).StartsWith("{ \"rider\"") && TrainerBridgeConfig.IsReadable(p, out _) && Load().Rider == new RiderConfig(),
                "\"Fix it for me\": the broken file is kept as a backup and a fresh one has the defaults");

            string over = Path.Combine(dir, "override", "test.json");
            TrainerBridgeConfig.PathOverride = over;
            try
            {
                Check(TrainerBridgeConfig.Path == over, "--config replaces the settings file path");
                var o = TrainerBridgeConfig.Load();
                Check(File.Exists(over) && o.Rider == new RiderConfig() && o.Devices.Roles.Count == 0, "--config file is created with defaults, never seeded from the real old files");
            }
            finally { TrainerBridgeConfig.PathOverride = null; }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static bool Near(float a, float b, float tol = 0.01f) => Math.Abs(a - b) <= tol;

    private static string? FindWorkout(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "workouts", file);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Phase 2 (docs/REQUIREMENTS.md V2.12-V2.18, TE8): the .zwo parser and the workout runner, on the real src\Workout.cs.</summary>
    private static void WorkoutTests()
    {
        const float Ftp = 180f;

        Console.WriteLine("TE8: workouts/workout30.zwo at FTP 180");
        string? p30 = FindWorkout("workout30.zwo");
        Check(p30 != null, "workouts/workout30.zwo found");
        if (p30 != null)
        {
            var w = WorkoutParser.Load(p30, Ftp);
            Check(w.Ok, "parses: " + w.Error);
            Check(w.Name == "workout30", $"empty <name> falls back to the file name without its extension (got '{w.Name}')");
            Check(Near(w.TotalS, 1950f), $"total 1950 s (got {w.TotalS})");
            Check(w.Steps.Count == 20, $"warm-up + 9 x (on, off) + cool-down = 20 steps (got {w.Steps.Count})");
            Check(Near(w.WattsAt(0f), 45f), "45 W at t=0");
            Check(Near(w.WattsAt(150f), 90f), "90 W half way up the warm-up");
            Check(Near(w.WattsAt(299.99f), 135f, 0.1f), "warm-up reaches 135 W at t=300");
            bool ok = true;
            for (int k = 0; k < 9; k++)
            {
                float s = 300f + 150f * k;
                ok &= Near(w.WattsAt(s), 180f) && Near(w.WattsAt(s + 29.9f), 180f) && Near(w.WattsAt(s + 30f), 90f) && Near(w.WattsAt(s + 149.9f), 90f);
            }
            Check(ok, "180 W for 30 s from t = 300 + 150k, 90 W for the other 120 s, k = 0..8");
            Check(Near(w.WattsAt(1650f), 135f), "cool-down starts at 135 W at t=1650");
            Check(Near(w.WattsAt(1949.9f), 45f, 0.1f), "and falls to 45 W at t=1950");
            Check(float.IsNaN(w.WattsAt(1950f)), "no target past the end");
            Check(w.Steps[1].Label == "Interval 1 of 9" && w.Steps[2].Label == "Recovery 1 of 9" && w.Steps[17].Label == "Interval 9 of 9", "step labels");
            Check(w.Warnings.Count == 1 && w.Warnings[0] == "ignored attributes: pace", $"one ignored-attribute line (got: {string.Join(" | ", w.Warnings)})");
        }

        Console.WriteLine("workouts/workout60.zwo (second file, picker later)");
        string? p60 = FindWorkout("workout60.zwo");
        if (p60 != null)
        {
            var w = WorkoutParser.Load(p60, Ftp);
            Check(w.Ok && Near(w.TotalS, 3750f) && w.Steps.Count == 44 && w.Steps[41].Label == "Interval 21 of 21", $"3750 s, 44 steps, 21 efforts (got {w.TotalS} s, {w.Steps.Count} steps)");
        }
        else Check(false, "workouts/workout60.zwo found");

        Console.WriteLine("workouts/test2min.zwo (the time-compressed in-game test)");
        Workout t2;
        string? pt = FindWorkout("test2min.zwo");
        if (pt == null) { Check(false, "workouts/test2min.zwo found"); return; }
        t2 = WorkoutParser.Load(pt, Ftp);
        Check(t2.Ok && t2.Name == "Test 2 min" && Near(t2.TotalS, 120f) && t2.Steps.Count == 8, $"120 s, 8 steps, named (got {t2.TotalS} s, {t2.Steps.Count} steps, '{t2.Name}')");
        Check(Near(t2.WattsAt(0), 45f) && Near(t2.WattsAt(20f), 180f) && Near(t2.WattsAt(30f), 90f) && Near(t2.WattsAt(50f), 180f) && Near(t2.WattsAt(110f), 135f), "target schedule 45 / 180 / 90 / 180 / 135 W");
        Check(t2.Texts.Count == 3 && Near(t2.Texts[0].AtS, 2f) && t2.Texts[0].Message == "Test workout: warm-up" && Near(t2.Texts[1].AtS, 10f) && Near(t2.Texts[2].AtS, 14f), "textevents at 2, 10 and 14 s (all in the warm-up)");

        Console.WriteLine("Parser robustness");
        {
            var w = WorkoutParser.Parse("<workout_file><name>X</name><workout><MaxEffort Duration='5'/><SteadyState Duration='60' Power='0.5' Cadence='90'/><FreeRide Duration='30'/><SteadyState Power='1'/></workout></workout_file>", "x.zwo", Ftp);
            Check(w.Ok && w.Steps.Count == 2 && Near(w.TotalS, 90f), "unsupported element and a step without Duration are skipped, the rest still loads");
            Check(w.Warnings.Exists(s => s.Contains("MaxEffort")) && w.Warnings.Exists(s => s.Contains("Cadence")), "each ignored item is named in a warning");
            Check(w.Steps[0].StartW == 90f && w.Steps[1].FreeRide, "SteadyState 0.5 x 180 = 90 W, FreeRide has no power");
            Check(!WorkoutParser.Parse("<not xml", "bad.zwo", Ftp).Ok, "malformed XML does not throw, Ok = false");
            Check(!WorkoutParser.Parse("<workout_file><workout/></workout_file>", "e.zwo", Ftp).Ok, "no steps = not ok");
            Check(!WorkoutParser.Load(Path.Combine(AppContext.BaseDirectory, "nope.zwo"), Ftp).Ok, "missing file = not ok");
            var ramp = WorkoutParser.Parse("<workout_file><workout><Ramp Duration='100' PowerLow='0.5' PowerHigh='1.0'/><Warmup Duration='10' PowerLow='.25' PowerHigh='.75'/></workout></workout_file>", "r.zwo", 200f);
            Check(ramp.Ok && Near(ramp.WattsAt(0), 100f) && Near(ramp.WattsAt(50f), 150f) && Near(ramp.WattsAt(100f), 50f) , "Ramp interpolates start -> end; decimals without a leading zero parse");
        }

        Console.WriteLine("Runner: armed until the first pedal stroke, then time advances");
        {
            var r = new WorkoutRunner(t2, 5f, 3f);
            for (int i = 0; i < 40; i++) r.Tick(0.25f, false);
            Check(!r.Started && r.Elapsed == 0f && Near(r.TargetW, 45f), "not pedalling: does not start, target is the first step's 45 W");
            for (int i = 0; i < 80; i++) r.Tick(0.25f, true);
            Check(r.Started && Near(r.Elapsed, 20f) && r.Step.Label == "Interval 1 of 3" && Near(r.TargetW, 180f), $"20 s later: Interval 1 of 3 at 180 W (got {r.Step.Label}, {r.TargetW} W, t={r.Elapsed})");
            Check(r.NextStep != null && r.NextStep.Label == "Recovery 1 of 3", "next step is Recovery 1 of 3");
            Check(Near(r.StepRemainingS, 10f) && Near(r.TotalRemainingS, 100f) && WorkoutRunner.Fmt(r.TotalRemainingS) == "1:40", "time left in step and workout");
        }

        Console.WriteLine("Runner: text events fire once, from the first pedal stroke; ones jumped over by skip are dropped");
        {
            var w = WorkoutParser.Parse("<workout_file><workout>"
                + "<SteadyState Duration='10' Power='0.5'><textevent timeoffset='0' message='a'/><textevent timeoffset='5' message='b'/></SteadyState>"
                + "<SteadyState Duration='10' Power='0.5'><textevent timeoffset='0' message='c'/><textevent timeoffset='3' message='d'/></SteadyState>"
                + "</workout></workout_file>", "t.zwo", Ftp);
            var r = new WorkoutRunner(w, 5f, 3f);
            var got = new List<string>();
            r.TakeDueTexts(got);
            Check(got.Count == 0, "nothing while armed");
            r.Tick(0.25f, true); r.TakeDueTexts(got);
            Check(got.Count == 1 && got[0] == "a", "offset 0 shows on the first stroke");
            for (int i = 0; i < 8; i++) { r.Tick(0.25f, true); r.TakeDueTexts(got); }
            Check(got.Count == 1, "each one only once");
            r.Skip(); r.TakeDueTexts(got);
            Check(got.Count == 2 && got[1] == "c", $"skip drops 'b' (5 s) and shows 'c' at the new step's start (got {string.Join(",", got)})");
            for (int i = 0; i < 16; i++) { r.Tick(0.25f, true); r.TakeDueTexts(got); }
            Check(got.Count == 3 && got[2] == "d", "'d' at 13 s");
        }

        Console.WriteLine("Runner: pauses when pedalling stops, resumes when it restarts");
        {
            var r = new WorkoutRunner(t2, 5f, 3f);
            for (int i = 0; i < 40; i++) r.Tick(0.25f, true);   // 10 s
            float t0 = r.Elapsed;
            for (int i = 0; i < 8; i++) r.Tick(0.25f, false);   // 2 s idle: inside the 3 s grace
            Check(!r.Paused && Near(r.Elapsed, t0 + 2f), "2 s of nothing is not yet a pause, time keeps counting");
            for (int i = 0; i < 20; i++) r.Tick(0.25f, false);  // 5 more s
            float tp = r.Elapsed;
            Check(r.Paused && Near(tp, t0 + 3.25f, 0.3f), $"paused after 3 s idle and time stops (elapsed +{tp - t0:0.00} s)");
            float held = r.TargetW;
            for (int i = 0; i < 40; i++) r.Tick(0.25f, false);
            Check(r.Elapsed == tp && r.TargetW == held, "while paused, elapsed and the target hold");
            r.Tick(0.25f, true);
            Check(!r.Paused && Near(r.Elapsed, tp + 0.25f), "pedalling again resumes at once");
        }

        Console.WriteLine("Runner: bias, skip, free ride, messages, end");
        {
            var r = new WorkoutRunner(t2, 5f, 3f);
            for (int i = 0; i < 88; i++) r.Tick(0.25f, true);   // 22 s: inside interval 1 (180 W)
            r.AdjustBias(2);
            Check(Near(r.TargetW, 198f) && Near(r.BaseTargetW, 180f), "bias +2 steps = +10 %: 198 W (base stays 180)");
            r.AdjustBias(-30);
            Check(Near(r.BiasFactor, 0.5f), "bias is limited to 50 %");
            r.AdjustBias(60);
            Check(Near(r.BiasFactor, 1.5f), "and to 150 %");
            r.BiasSteps = 0;
            r.Skip();
            Check(r.Step.Label == "Recovery 1 of 3" && Near(r.Elapsed, 30f) && Near(r.TargetW, 90f), "skip jumps to the next step's start (Recovery 1 of 3, 90 W)");


            for (int i = 0; i < 20; i++) r.Skip();
            Check(r.Finished && float.IsNaN(r.TargetW), "skipping past the last step finishes the workout, no target");

            var f = new WorkoutRunner(t2, 5f, 3f);
            for (int i = 0; i < 4 * 121 + 4; i++) f.Tick(0.25f, true);
            Check(f.Finished && Near(f.Elapsed, 120f), "pedalling through 120 s finishes the workout");

            var fr = WorkoutParser.Parse("<workout_file><workout><SteadyState Duration='10' Power='1'/><FreeRide Duration='10'/></workout></workout_file>", "f.zwo", Ftp);
            var fx = new WorkoutRunner(fr, 5f, 3f);
            for (int i = 0; i < 44; i++) fx.Tick(0.25f, true);   // 11 s
            Check(fx.InFreeRide && float.IsNaN(fx.TargetW) && !fx.Finished, "a free-ride step has no target (the mod switches to freeride mode)");
        }
    }

    /// <summary>The workout list model of the window's Workouts tab (V2.19; no longer an in-game list): folder scan, order, broken files.</summary>
    private static void PickerTests()
    {
        Console.WriteLine("Workout picker");
        string? src = FindWorkout("test2min.zwo");
        string? src30 = FindWorkout("workout30.zwo");
        if (src == null || src30 == null) { Check(false, "workouts folder found for the picker test"); return; }
        string dir = Path.Combine(Path.GetTempPath(), "descenders_picker_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(src, Path.Combine(dir, "test2min.zwo"));
            File.Copy(src30, Path.Combine(dir, "workout30.zwo"));
            File.WriteAllText(Path.Combine(dir, "Broken.zwo"), "<not xml");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignored");

            var p = WorkoutPicker.Workouts(dir, "", 180f, out string msg);
            Check(p.Count == 3 && msg == "", $"3 .zwo files, .txt ignored (got {p.Count})");
            Check(p[0].File == "Broken.zwo" && p[1].File == "test2min.zwo" && p[2].File == "workout30.zwo", "files sorted by name, ignoring case");
            Check(!p[0].Selectable && p[0].Error != null, "the broken file is listed but cannot be started");
            Check(p[1].Title == "Test 2 min" && Near(p[1].TotalS, 120f) && p[1].Steps == 8 && p[1].Selectable, "title, duration and step count come from the file");
            Check(p[1].Path == Path.Combine(dir, "test2min.zwo"), "each entry carries its full path, to start it");
            Check(p[2].Title == "workout30" && Near(p[2].TotalS, 1950f), "an unnamed workout shows its file name without the extension");
            Check(WorkoutPicker.Folders(dir).SequenceEqual(new[] { "" }), "no sub-folders: the folder list is the Workouts folder itself");

            File.Copy(src, Path.Combine(dir, "another.zwo"));
            p = WorkoutPicker.Workouts(dir, "", 180f, out _);
            Check(p.Count == 4 && p[0].File == "another.zwo", "a file added while the bridge runs shows up on the next listing");

            // sub-folders (the library's Workouts folder may be nested) and .xml workouts
            string sub = Path.Combine(dir, "Blocks"), deep = Path.Combine(sub, "Week 1");
            Directory.CreateDirectory(deep);
            Directory.CreateDirectory(Path.Combine(dir, "Empty"));
            File.Copy(src, Path.Combine(sub, "inner.xml"));
            File.Copy(src30, Path.Combine(deep, "deep.zwo"));
            var folders = WorkoutPicker.Folders(dir);
            Check(folders.SequenceEqual(new[] { "", "Blocks", Path.Combine("Blocks", "Week 1") }),
                $"the folder list: the Workouts folder, then every folder with workouts in it, nested ones too; an empty folder is left out (got {string.Join(" | ", folders)})");
            var inner = WorkoutPicker.Workouts(dir, "Blocks", 180f, out msg);
            Check(inner.Count == 1 && inner[0].File == Path.Combine("Blocks", "inner.xml") && inner[0].Title == "Test 2 min" && inner[0].Selectable && msg == "",
                ".xml workouts are listed, File is relative to the Workouts folder; a folder lists only its own files");
            var deepList = WorkoutPicker.Workouts(dir, Path.Combine("Blocks", "Week 1"), 180f, out _);
            Check(deepList.Count == 1 && deepList[0].File == Path.Combine("Blocks", "Week 1", "deep.zwo") && deepList[0].Path == Path.Combine(deep, "deep.zwo"), "nested folders list too");
            Check(WorkoutPicker.Workouts(dir, "Empty", 180f, out msg).Count == 0 && msg.Contains("No workouts"), "an empty folder: nothing listed, and the message says so");
            Check(WorkoutPicker.Workouts(dir, "Gone", 180f, out msg).Count == 0 && msg.Contains("No workouts"), "a folder that no longer exists: the same, no error");

            string many = Path.Combine(dir, "Many");
            Directory.CreateDirectory(many);
            for (int i = 0; i <= WorkoutPicker.FolderMax; i++) File.WriteAllText(Path.Combine(many, "w" + i + ".zwo"), "<x/>");
            Check(WorkoutPicker.Workouts(dir, "Many", 180f, out msg).Count == 0 && msg.Contains("too many"), $"more than {WorkoutPicker.FolderMax} files in one folder: refused with a message");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        string empty = Path.Combine(Path.GetTempPath(), "descenders_picker_missing_" + Guid.NewGuid().ToString("N"));
        Check(WorkoutPicker.Folders(empty).Count == 0 && !Directory.Exists(empty), "a missing Workouts folder: no folders listed, and nothing is created");
        Check(WorkoutPicker.Workouts(empty, "", 180f, out string none).Count == 0 && none.Contains("No workouts"), "and no workouts, with the message saying so");
    }

    private static string? FindFixture(string relPath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, relPath);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>TP1 and parser robustness (docs/PLANS.md "Plan format"), on the real src\Plan.cs.</summary>
    private static void PlanTests()
    {
        Console.WriteLine("TP1: parse a minimal hand-written plan fixture (tests/plans/fixture_min_plan.xml)");
        string? fixturePath = FindFixture(Path.Combine("tests", "plans", "fixture_min_plan.xml"));
        Check(fixturePath != null, "tests/plans/fixture_min_plan.xml found");
        if (fixturePath != null)
        {
            var p = PlanParser.Load(fixturePath);
            Check(p.Ok, "parses: " + p.Error);
            Check(p.Name == "fixture min plan", $"LOC_ name falls back to the prettified file stem (got '{p.Name}')");
            Check(p.SupportsCycling, "supports_sports=\"CYCLING\" read");
            Check(p.Blocks.Count == 2, $"2 blocks (got {p.Blocks.Count})");
            if (p.Blocks.Count == 2)
            {
                var w1 = p.Blocks[0].Activities;
                Check(w1.Count == 3
                    && w1[0].WorkoutName == "Steady B" && w1[0].Ref == "b0.a0"
                    && w1[1].WorkoutName == "Steady A" && w1[1].Ref == "b0.a1"
                    && w1[2].WorkoutName == "Optional Spin" && w1[2].Ref == "b0.a2",
                    "Week 1 activities reordered into priority order (file listed Steady A before Steady B)");
                Check(!w1[0].IsOptional && !w1[1].IsOptional && w1[2].IsOptional, "optional flag read (only Optional Spin is optional)");

                var w2 = p.Blocks[1].Activities;
                Check(w2.Count == 2 && w2[0].WorkoutName == "Build 1" && w2[0].Ref == "b1.a0" && w2[1].WorkoutName == "Build 2" && w2[1].Ref == "b1.a1",
                    "Week 2 activities also reordered into priority order");
            }
        }

        Console.WriteLine("Plan parser robustness");
        {
            Check(!PlanParser.Parse("<not xml", "bad.xml").Ok, "malformed XML does not throw, Ok = false");
            Check(!PlanParser.Parse("<workout_file/>", "wrong-root.xml").Ok, "wrong root element = not ok");
            Check(!PlanParser.Parse("<plan name='Empty'/>", "empty.xml").Ok, "a plan with no blocks = not ok");
            Check(!PlanParser.Load(Path.Combine(AppContext.BaseDirectory, "nope_plan.xml")).Ok, "missing file = not ok");

            var literal = PlanParser.Parse("<plan name='My Plan'><block name='W1'><activity workout_filename='a.xml'/></block></plan>", "x.xml");
            Check(literal.Ok && literal.Name == "My Plan", "a literal (non-LOC_) name is kept as-is");

            var empty = PlanParser.Parse("<plan name=''><block name='W1'><activity workout_filename='a.xml'/></block></plan>", "empty_name_plan.xml");
            Check(empty.Ok && empty.Name == "empty name plan", "an empty name also falls back to the prettified stem");

            var unknown = PlanParser.Parse("<plan name='U'><foo/><block name='W1'><activity workout_filename='a.xml' bogus='1'/><bar/></block></plan>", "u.xml");
            Check(unknown.Ok && unknown.Blocks.Count == 1 && unknown.Blocks[0].Activities.Count == 1, "unknown elements and attributes are skipped, not errors");

            var order = PlanParser.Parse("<plan name='O'><block name='W1'>"
                + "<activity workout_filename='first.xml' priority='5'/>"
                + "<activity workout_filename='second.xml'/>"
                + "<activity workout_filename='third.xml'/>"
                + "</block></plan>", "o.xml");
            Check(order.Ok && order.Blocks[0].Activities[0].WorkoutFilename == "second.xml" && order.Blocks[0].Activities[1].WorkoutFilename == "third.xml" && order.Blocks[0].Activities[2].WorkoutFilename == "first.xml",
                "missing priority falls back to document order, ranked below an explicit priority that is numerically higher only if it actually is (here 5 sorts last)");

            var sports = PlanParser.Parse("<plan name='S' supports_sports='RUNNING'><block name='W1'><activity workout_filename='a.xml'/></block></plan>", "s.xml");
            Check(!sports.SupportsCycling, "supports_sports without CYCLING is not a cycling plan");
            var sports2 = PlanParser.Parse("<plan name='S2' supports_sports='RUNNING,CYCLING'><block name='W1'><activity workout_filename='a.xml'/></block></plan>", "s2.xml");
            Check(sports2.SupportsCycling, "CYCLING anywhere in the comma list counts");
        }
    }

    /// <summary>TP4 (path resolution), TP5 (derived next-session), TP6 (progress round-trip/corruption), TP7 (plan changed detection) - docs/PLANS.md.</summary>
    private static void ProgressTests()
    {
        Console.WriteLine("TP4: path resolution");
        {
            string root = Path.Combine(Path.GetTempPath(), "descenders_libroot_data_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Workouts", "X"));
            File.WriteAllText(Path.Combine(root, "Workouts", "X", "y.xml"), "<workout_file/>");
            try
            {
                string? err;
                string? r1 = PlanPaths.Resolve(root, "data/Workouts/X/y.xml", out err);
                Check(r1 != null && string.Equals(r1, Path.Combine(root, "Workouts", "X", "y.xml"), StringComparison.OrdinalIgnoreCase),
                    $"resolves under a root that IS the data folder (err={err})");

                string root2 = Path.Combine(Path.GetTempPath(), "descenders_libroot_named_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root2, "Workouts", "X"));
                File.WriteAllText(Path.Combine(root2, "Workouts", "X", "y.xml"), "<workout_file/>");
                try
                {
                    string? r2 = PlanPaths.Resolve(root2, "data\\Workouts\\X\\y.xml", out err);
                    Check(r2 != null, "resolves identically under a differently-named root (the root's own name does not matter)");
                }
                finally { try { Directory.Delete(root2, true); } catch { } }

                Directory.CreateDirectory(Path.Combine(root, "Workouts", "Z"));
                File.WriteAllText(Path.Combine(root, "Workouts", "Z", "w.zwo"), "<workout_file/>");
                string? rz = PlanPaths.Resolve(root, "data/Workouts/Z/w.xml", out err);
                Check(rz != null && rz.EndsWith("w.zwo", StringComparison.OrdinalIgnoreCase), $".zwo fallback found when only .zwo exists (err={err})");

                string? evil = PlanPaths.Resolve(root, "..\\..\\evil.xml", out err);
                Check(evil == null && err != null, "..\\..\\evil.xml is rejected: " + err);

                string absPath = Path.Combine(Path.GetTempPath(), "outside_evil_" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(absPath, "x");
                try
                {
                    string? abs = PlanPaths.Resolve(root, absPath, out err);
                    Check(abs == null && err != null, "an absolute path is rejected: " + err);
                }
                finally { try { File.Delete(absPath); } catch { } }

                string? missing = PlanPaths.Resolve(root, "data/Workouts/nope/nope.xml", out err);
                Check(missing == null && err != null, "a workout_filename found nowhere is unresolved with a reason: " + err);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        Console.WriteLine("TP5: derived next-session");
        {
            var planA = PlanParser.Parse(
                "<plan name='A'><block name='W1'>"
                + "<activity workout_filename='a0.xml' priority='1'/><activity workout_filename='a1.xml' priority='2'/>"
                + "<activity workout_filename='a2.xml' priority='3'/><activity workout_filename='a3.xml' priority='4'/>"
                + "</block><block name='W2'><activity workout_filename='b0.xml' priority='1'/></block></plan>", "planA.xml");
            Check(planA.Ok && planA.Blocks.Count == 2, "PlanA parses");

            var prog = new PlanProgressEntry();
            prog.Completed.Add(new ProgressRecord { Ref = "b0.a0", File = "a0.xml" });
            prog.Completed.Add(new ProgressRecord { Ref = "b0.a1", File = "a1.xml" });
            var next = Progress.NextActivity(planA, prog);
            Check(next != null && next.Ref == "b0.a2", $"next after a0,a1 done is b0.a2 (got {next?.Ref})");

            prog.Completed.Add(new ProgressRecord { Ref = "b1.a0", File = "b0.xml" });   // out-of-order completion
            var next2 = Progress.NextActivity(planA, prog);
            Check(next2 != null && next2.Ref == "b0.a2", "out-of-order completion of b1.a0 does not move next past b0.a2");

            var planB = PlanParser.Parse(
                "<plan name='B'><block name='W1'>"
                + "<activity workout_filename='a0.xml' priority='1'/><activity workout_filename='a1.xml' priority='2'/>"
                + "<activity workout_filename='a2.xml' priority='3' is_optional='1'/><activity workout_filename='a3.xml' priority='4'/>"
                + "</block></plan>", "planB.xml");
            Check(planB.Ok, "PlanB parses");
            var a2 = planB.Blocks[0].Activities[2];
            Check(a2.Ref == "b0.a2" && a2.IsOptional, "a2 is optional");

            var progB = new PlanProgressEntry();
            progB.Completed.Add(new ProgressRecord { Ref = "b0.a0", File = "a0.xml" });
            progB.Completed.Add(new ProgressRecord { Ref = "b0.a1", File = "a1.xml" });

            var nextOptOff = Progress.NextActivity(planB, progB);
            Check(nextOptOff != null && nextOptOff.Ref == "b0.a3", "optional a2 unfinished, PlanIncludeOptional=false: next steps past it to b0.a3");
            Check(!Progress.IsOffered(a2, false), "a2 not offered when PlanIncludeOptional=false");

            var nextOptOn = Progress.NextActivity(planB, progB);
            Check(nextOptOn != null && nextOptOn.Ref == "b0.a3", "optional a2 unfinished, PlanIncludeOptional=true: next is still b0.a3 (optionals never block progression)");
            Check(Progress.IsOffered(a2, true), "a2 stays selectable when PlanIncludeOptional=true");
        }

        ProgressConfigTests.Run(Check, FindFixture);   // TP6, tests\unit\ProgressConfigTests.cs

        Console.WriteLine("TP7: plan changed detection");
        {
            var plan1 = PlanParser.Parse("<plan name='C'><block name='W1'><activity workout_filename='first.xml' priority='1'/><activity workout_filename='second.xml' priority='2'/></block></plan>", "planC.xml");
            var prog = new PlanProgressEntry();
            prog.Completed.Add(new ProgressRecord { Ref = "b0.a0", File = "first.xml" });
            Check(!Progress.Changed(plan1, prog), "unmodified plan: not changed");

            var plan2 = PlanParser.Parse("<plan name='C'><block name='W1'><activity workout_filename='second.xml' priority='1'/><activity workout_filename='first.xml' priority='2'/></block></plan>", "planC.xml");
            Check(Progress.Changed(plan2, prog), "reordering activities so b0.a0 now names a different file: changed is true");
            Check(prog.Completed.Count == 1 && prog.Completed[0].Ref == "b0.a0", "no records deleted by detecting the change");
            var next = Progress.NextActivity(plan2, prog);
            Check(next != null, "nextActivity is still a valid activity after the reorder");
        }
    }

    /// <summary>TP2, TP3 (real plan data), TP9 (in-game): skip cleanly when WorkoutLibrary is unset (docs/PLANS.md repo hygiene - no third-party data ships with the repo).</summary>
    private static void LibraryDataTests()
    {
        string? libRoot = Environment.GetEnvironmentVariable("WorkoutLibrary");
        bool present = !string.IsNullOrEmpty(libRoot) && Directory.Exists(libRoot);

        Console.WriteLine("TP2: parse a real single-block plan (user data)");
        if (!present) Console.WriteLine("  SKIP: set the WorkoutLibrary environment variable to a workout library folder (with TrainingPlans and Workouts) to run this");
        else
        {
            string tpDir = Path.Combine(libRoot!, "TrainingPlans");
            string[] files = Directory.Exists(tpDir) ? Directory.GetFiles(tpDir, "*.xml") : Array.Empty<string>();
            Plan? single = null;
            foreach (var f in files) { var pl = PlanParser.Load(f); if (pl.Ok && pl.Blocks.Count == 1) { single = pl; break; } }
            if (single == null) Console.WriteLine("  SKIP: no single-block plan found under " + tpDir);
            else
            {
                Check(single.Blocks[0].Name.Length > 0, "block has a name");
                var acts = single.Blocks[0].Activities;
                bool ordered = true;
                for (int i = 1; i < acts.Count; i++) if ((acts[i].Priority ?? acts[i].DocOrder) < (acts[i - 1].Priority ?? acts[i - 1].DocOrder)) ordered = false;
                Check(ordered, "activities in non-decreasing priority order");
                int optionalCount = acts.Count(a => a.IsOptional);
                Check(optionalCount >= 0, $"{optionalCount} optional activities (benchmark tests are typically flagged optional)");
                int unresolved = 0;
                foreach (var a in acts) { string? err; if (PlanPaths.Resolve(libRoot!, a.WorkoutFilename, out err) == null) unresolved++; }
                Check(unresolved == 0, $"every workout_filename resolves under {libRoot} (unresolved: {unresolved})");
            }
        }

        Console.WriteLine("TP3: parse every plan in a real TrainingPlans folder (user data)");
        if (!present) Console.WriteLine("  SKIP: set the WorkoutLibrary environment variable to a workout library folder (with TrainingPlans and Workouts) to run this");
        else
        {
            string tpDir = Path.Combine(libRoot!, "TrainingPlans");
            if (!Directory.Exists(tpDir)) Console.WriteLine("  SKIP: " + tpDir + " not found");
            else
            {
                string[] files = Directory.GetFiles(tpDir, "*.xml");
                int unresolvedTotal = 0, cyclingPlans = 0, exceptions = 0;
                foreach (var f in files)
                {
                    Plan pl;
                    try { pl = PlanParser.Load(f); } catch (Exception) { exceptions++; continue; }
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (string.Equals(name, "trainingplans", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "TrainingPlanSummaries", StringComparison.OrdinalIgnoreCase))
                    {
                        Check(!pl.Ok, "catalogue file " + name + " does not parse as a <plan>");
                        continue;
                    }
                    if (!pl.Ok || !pl.SupportsCycling) continue;
                    cyclingPlans++;
                    Check(pl.Blocks.Count >= 1, name + ": at least one block");
                    foreach (var b in pl.Blocks)
                        foreach (var a in b.Activities)
                        { string? err; if (PlanPaths.Resolve(libRoot!, a.WorkoutFilename, out err) == null) unresolvedTotal++; }
                }
                Check(exceptions == 0, "no exception while parsing any file");
                Check(cyclingPlans > 0, "at least one cycling plan found");
                Console.WriteLine("  unresolved workout_filenames: " + unresolvedTotal + " (expected non-zero on an incomplete library)");
            }
        }

        Console.WriteLine("TP9: in-game picker performance with a full library (user data) - SKIP: in-game only (Screen/pad timing), not runnable headless");
    }}
