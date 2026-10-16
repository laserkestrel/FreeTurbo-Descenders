using DescendersBridge.Ant;
using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// The ERG / workout / trainer-difficulty settings moved from the mod's MelonPreferences.cfg to the `training` section of
/// TrainerBridgeConfig.json; resistance = raw road gradient x trainerDifficulty; the mod's rider profile comes only from the bridge (src\RiderState.cs).
/// Run by `FreeTurbo-Bridge selftest`; temp files only, never the real %APPDATA% file.
/// </summary>
public static class TrainingTests
{
    private static readonly DateTime T0 = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private const string Zwo = "<workout_file><name>Bias test</name><workout><SteadyState Duration='60' Power='1'/></workout></workout_file>";

    private sealed class FakeTrainer : ITrainerControl
    {
        public readonly List<(string Kind, double Value)> Writes = new();
        public Task<bool> SetTrackResistanceAsync(double g, double crr, CancellationToken ct = default) { Writes.Add(("grade", g)); return Task.FromResult(true); }
        public Task<bool> SetTargetPowerAsync(double w, CancellationToken ct = default) { Writes.Add(("erg", w)); return Task.FromResult(true); }
    }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Training section: defaults and clamping");
        {
            var d = new TrainingConfig();
            check(d.TrainerDifficulty == 0.35 && d.ErgMinW == 30 && d.ErgMaxW == 400 && d.ErgStepW == 10 && d.ErgDefaultW == 120 && d.WorkoutBiasStepPct == 5 && d.WorkoutPauseSeconds == 3,
                "defaults = the old code defaults: difficulty 0.35, ERG 30-400 W step 10 start 120, bias 5 %, pause 3 s");
            check(Training.Clamp(d, out string none) == d && none == "", "the defaults are in range");
            var c = Training.Clamp(new TrainingConfig { TrainerDifficulty = 5, ErgMinW = -10, ErgMaxW = 9000, ErgStepW = 0, WorkoutBiasStepPct = 99, WorkoutPauseSeconds = 0.1 }, out string names);
            check(c.TrainerDifficulty == 2 && c.ErgMinW == 0 && c.ErgMaxW == 2000 && c.ErgStepW == 1 && c.WorkoutBiasStepPct == 25 && c.WorkoutPauseSeconds == 1,
                "out-of-range values clamped (difficulty 2, ERG 0-2000 W, step 1 W, bias 25 %, pause 1 s)");
            check(names.Contains("trainerDifficulty") && names.Contains("ergMinW") && names.Contains("workoutPauseSeconds"), $"clamped fields are named ({names})");
            var m = Training.Clamp(new TrainingConfig { ErgMinW = 200, ErgMaxW = 100, ErgDefaultW = 50 }, out _);
            check(m.ErgMinW == 200 && m.ErgMaxW == 200 && m.ErgDefaultW == 200, "a max below the min is raised to it, the start target moved into the range");
            var n = Training.Clamp(new TrainingConfig { TrainerDifficulty = double.NaN, ErgDefaultW = double.NaN }, out _);
            check(n.TrainerDifficulty == 0.35 && n.ErgDefaultW == 120, "NaN takes the default");
            check(Training.Clamp(new TrainingConfig { TrainerDifficulty = 0 }, out _).TrainerDifficulty == 0, "difficulty 0 (always flat) is allowed");
            // the window has no field for the pause time, so its Save keeps the file's
            var shown = new TrainingConfig { TrainerDifficulty = 0.5, ErgMaxW = 300, WorkoutBiasStepPct = 8 };
            check(Training.KeepHidden(shown, new TrainingConfig { TrainerDifficulty = 1, WorkoutBiasStepPct = 20, WorkoutPauseSeconds = 9 }) == shown with { WorkoutPauseSeconds = 9 },
                "the window's save: every field it shows, the pause time as the file has it (9 s)");
            check(Training.KeepHidden(shown, new TrainingConfig()) == shown, "the window's save: a file without the section keeps the standard 3 s pause");
        }

        string dir = Path.Combine(Path.GetTempPath(), "descenders_training_" + Guid.NewGuid().ToString("N"));
        try { FileTests(check, dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine("Trainer difficulty: TargetGrade = raw road gradient x difficulty");
        {
            var cfg = new ResistanceConfig();
            var m = new ModState(true, 0.1, true, true, 0.13);
            check(Math.Abs(RideLoop.TargetGrade(m, cfg, 0.35) - 4.55) < 1e-9, "13 % road x 0.35 = 4.55 % (as the mod's old GradientScale 0.35 sent it)");
            check(Math.Abs(RideLoop.TargetGrade(m, cfg, 0.5) - 6.5) < 1e-9, "x 0.5 = 6.5 %");
            check(RideLoop.TargetGrade(m, cfg, 1) == cfg.MaxGradePercent, $"x 1 = 13 %, clamped to MaxGradePercent {cfg.MaxGradePercent}");
            check(RideLoop.TargetGrade(m, cfg, 0) == 0, "difficulty 0 = flat");
            check(RideLoop.TargetGrade(m with { RoadGradient = -0.2 }, cfg, 0.35) == 0, "descents: never below 0 %");
            check(RideLoop.TargetGrade(m with { RoadGradient = null }, cfg, 0.35) == 0 && RideLoop.TargetGrade(m with { AgeSeconds = 5 }, cfg, 0.35) == 0,
                "no gradient or stale telemetry: flat");
        }

        Console.WriteLine("Trainer difficulty changed mid-ride: the next resistance write uses it");
        {
            var loop = new RideLoop(new ResistanceConfig());   // the default training section
            var tr = new FakeTrainer();
            var m = new ModState(true, 0.1, true, true, 0.13);
            DateTime now = T0;
            void Step(int n) { for (int i = 0; i < n; i++) { now = now.AddMilliseconds(250); loop.Step(tr, m, loop.Session.ErgTargetW, true, now, CancellationToken.None); } }
            Step(1);
            check(loop.TrainingSettings.TrainerDifficulty == 0.35 && tr.Writes.Count == 1 && Math.Abs(tr.Writes[0].Value - 4.55) < 1e-9, "default difficulty 0.35: 13 % road -> 4.55 %");
            loop.ApplyTraining(new TrainingConfig { TrainerDifficulty = 0.7 }, now);
            Step(4);
            check(tr.Writes.Count == 2 && Math.Abs(tr.Writes[1].Value - 9.1) < 1e-9, "difficulty 0.7 saved mid-ride: 9.1 % within a second");
            loop.ApplyTraining(new TrainingConfig { TrainerDifficulty = 0.72 }, now);
            Step(8);
            check(tr.Writes.Count == 2, "a change under the 0.5 % deadband writes nothing");
        }

        Console.WriteLine("Session respects the training section's ERG limits");
        {
            var r = new ResistanceConfig();
            var s1 = SessionConfig.From(new TrainingConfig(), r);
            check(s1.ErgMinW == 30 && s1.ErgMaxW == 400 && s1.ErgStepW == 10 && s1.ErgDefaultW == 120 && s1.BiasStepPct == 5f && s1.PauseSeconds == 3f, "defaults carry over to the session");
            var s2 = SessionConfig.From(new TrainingConfig { ErgMaxW = 1500 }, r with { ErgMaxW = 500 });
            check(s2.ErgMaxW == 500, "config.json's resistance.ErgMaxW is lower: it wins");
            var s3 = SessionConfig.From(new TrainingConfig { ErgMinW = 600, ErgMaxW = 800, ErgDefaultW = 700 }, r with { ErgMaxW = 500 });
            check(s3.ErgMaxW == 500 && s3.ErgMinW == 500 && s3.ErgDefaultW == 500, "a min above the install's max is held under it too");

            var log = new List<string>();
            var t = new TrainingConfig { ErgMinW = 50, ErgMaxW = 250, ErgStepW = 25, ErgDefaultW = 100 };
            var s = new Session(SessionConfig.From(t, r), () => 200, log.Add);
            s.Apply(new Intent("erg_toggle"), T0);
            check(s.ErgTargetW == 100, "ERG starts at the section's start target (100 W)");
            s.Apply(new Intent("adjust", 1), T0);
            check(s.ErgTargetW == 125, "one step = the section's 25 W");
            s.Apply(new Intent("adjust", 100), T0);
            check(s.ErgTargetW == 250 && s.Note(T0).EndsWith("(max)"), "clamped at the section's max 250 W");
            s.Apply(new Intent("adjust", -100), T0);
            check(s.ErgTargetW == 50 && s.Note(T0).EndsWith("(min)"), "clamped at the section's min 50 W");
            s.Apply(new Intent("adjust", 100), T0);
            s.ApplyConfig(SessionConfig.From(t with { ErgMaxW = 200 }, r), T0);
            check(s.ErgTargetW == 200 && s.Note(T0) == "ERG 200 W" && log.Exists(l => l.Contains("manual target 250 -> 200 W")),
                "max lowered mid-ride: the manual target follows at once, noted and logged");

            string wd = Path.Combine(Path.GetTempPath(), "descenders_training_wo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(wd);
            try
            {
                string zwo = Path.Combine(wd, "b.zwo");
                File.WriteAllText(zwo, Zwo);
                var w = new Session(SessionConfig.From(new TrainingConfig { WorkoutBiasStepPct = 10, WorkoutPauseSeconds = 8 }, r), () => 200, _ => { });
                w.StartWorkout(zwo, T0);
                w.Apply(new Intent("adjust", 1), T0);
                check(w.ErgTargetW is { } bw && Math.Abs(bw - 220) < 0.01, $"workout bias step from the section: +1 = +10 % = 220 W (got {w.ErgTargetW})");
                var now = T0;
                for (int i = 0; i < 4; i++) { now = now.AddMilliseconds(250); w.Tick(0.25, true, true, 200, now); }
                for (int i = 0; i < 28; i++) { now = now.AddMilliseconds(250); w.Tick(0.25, false, true, 0, now); }   // 7 s idle
                check(w.Run is { Paused: false }, "7 s without pedalling: not yet paused (the section says 8 s)");
                for (int i = 0; i < 8; i++) { now = now.AddMilliseconds(250); w.Tick(0.25, false, true, 0, now); }
                check(w.Run is { Paused: true }, "and paused after 8 s");
            }
            finally { try { Directory.Delete(wd, true); } catch { } }
        }

        Console.WriteLine("Mod: fitness mode waits for the bridge's rider profile (src\\RiderState.cs)");
        {
            var rs = new RiderState();
            check(!rs.Known && rs.MassKg == 0 && rs.FtpW == 0, "no rider message yet: nothing known, no mass");
            check(!RiderState.FitnessReady(true, true, rs.Known) && RiderState.Waiting(true, true, rs.Known), "trainer data but no rider: fitness waits");
            check(!RiderState.Waiting(false, true, false) && !RiderState.Waiting(true, false, false), "not waiting when fitness is off or there is no trainer data");
            check(rs.TakeWaitLog() && !rs.TakeWaitLog(), "the wait is logged once");
            check(!rs.Apply(float.NaN, 9, 200) && !rs.Known, "a message with a missing number is ignored");
            check(rs.Apply(80, 9, 250) && rs.Known && rs.MassKg == 89 && rs.FtpW == 250, "the first rider message: known, mass 89 kg");
            check(RiderState.FitnessReady(true, true, rs.Known) && !RiderState.Waiting(true, true, rs.Known), "then fitness may start");
            check(!rs.Apply(80, 9, 250), "a repeat of the same values changes nothing (nothing logged)");
            check(rs.Apply(500, 1, 10) && rs.RiderKg == 200 && rs.BikeKg == 3 && rs.FtpW == 50, "values clamped to the bridge's limits (200 kg, 3 kg, 50 W)");
            check(RiderState.MinRiderKg == RiderProfile.MinRiderKg && RiderState.MaxRiderKg == RiderProfile.MaxRiderKg && RiderState.MaxFtpW == RiderProfile.MaxFtpW && RiderState.MinBikeKg == RiderProfile.MinBikeKg,
                "the mod's limits match the bridge's RiderProfile");
        }
    }

    private static void FileTests(Action<bool, string> check, string dir)
    {
        Console.WriteLine("Training section in TrainerBridgeConfig.json: load, save, live reload");
        var log = new List<string>();
        string p = Path.Combine(dir, TrainerBridgeConfig.FileName);
        UserConfig Load() => TrainerBridgeConfig.Load(p, Path.Combine(dir, "rider.json"), Path.Combine(dir, "devices.json"), () => null, log.Add);

        var fresh = Load();
        check(fresh.Training == new TrainingConfig() && File.ReadAllText(p).Contains("\"training\"") && File.ReadAllText(p).Contains("\"trainerDifficulty\": 0.35"),
            "a new file gets a training section with the defaults (no migration from the mod's cfg)");
        check(log.Exists(l => l.Contains("\"training\"")), "the startup dump prints the training section");

        File.WriteAllText(p, "{ \"rider\": { \"ftpW\": 250 }, \"devices\": {} }");
        log.Clear();
        check(Load().Training == new TrainingConfig() && log.Exists(l => l.Contains("no training section")), "an older file without the section: defaults, logged");
        check(TrainerBridgeConfig.LoadTraining(p, log.Add) == new TrainingConfig(), "LoadTraining: defaults when the section is missing");

        var saved = TrainerBridgeConfig.SaveTraining(p, new TrainingConfig { TrainerDifficulty = 0.5, ErgMaxW = 300, ErgStepW = 5 });
        var back = Load();
        check(back.Training == saved && saved.TrainerDifficulty == 0.5 && back.Rider.FtpW == 250, "saving the training section reads back and keeps the rider section");
        TrainerBridgeConfig.SaveRider(p, back.Rider with { FtpW = 260 });
        check(Load().Training == saved, "saving the rider keeps the training section");
        check(TrainerBridgeConfig.SaveTraining(p, new TrainingConfig { TrainerDifficulty = 9 }).TrainerDifficulty == 2 && Load().Training.TrainerDifficulty == 2, "the section is clamped before it is stored");

        File.WriteAllText(p, "{ \"TRAINING\": { \"TRAINERDIFFICULTY\": 0.6, \"ergMinW\": 5000 } }");
        log.Clear();
        var hand = TrainerBridgeConfig.LoadTraining(p, log.Add);
        check(hand.TrainerDifficulty == 0.6 && hand.ErgMinW == 2000 && hand.ErgMaxW == 2000 && hand.ErgStepW == 10 && log.Exists(l => l.Contains("clamped")),
            "hand-edited: names case-insensitive, out of range clamped and logged, missing fields take their defaults");

        File.WriteAllText(p, "{ broken");
        log.Clear();
        check(TrainerBridgeConfig.LoadTraining(p, log.Add) == new TrainingConfig() && log.Count == 1, "unreadable file: defaults and one log line");
        bool threw = false;
        try { TrainerBridgeConfig.SaveTraining(p, new TrainingConfig()); } catch (InvalidOperationException) { threw = true; }
        check(threw && File.ReadAllText(p) == "{ broken", "saving into an unreadable file refuses and leaves it alone");

        TrainerBridgeConfig.SaveTraining(Path.Combine(dir, "w", TrainerBridgeConfig.FileName), new TrainingConfig { TrainerDifficulty = 0.4 });
        string wp = Path.Combine(dir, "w", TrainerBridgeConfig.FileName);
        log.Clear();
        var watch = new TrainingWatch(wp, log.Add);
        check(watch.Current.TrainerDifficulty == 0.4 && !watch.Poll(T0), "the watch reads the section at start; nothing changed yet");
        TrainerBridgeConfig.SaveTraining(wp, new TrainingConfig { TrainerDifficulty = 0.8 });
        File.SetLastWriteTimeUtc(wp, DateTime.UtcNow.AddSeconds(5));   // a distinct write time, whatever the file system's resolution
        check(!watch.Poll(T0.AddMilliseconds(500)), "checked at most once a second");
        check(watch.Poll(T0.AddSeconds(2)) && watch.Current.TrainerDifficulty == 0.8 && log.Exists(l => l.StartsWith("Training settings changed")), "a save in the window is picked up mid-ride and logged");
        File.WriteAllText(wp, "{ broken");
        File.SetLastWriteTimeUtc(wp, DateTime.UtcNow.AddSeconds(10));
        check(!watch.Poll(T0.AddSeconds(4)) && watch.Current.TrainerDifficulty == 0.8, "a broken file mid-ride keeps the last good values");
    }
}
