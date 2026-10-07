using System.Text.Json;
using DescendersBridge.Ant;
using DescendersCyclingPOC;

namespace DescendersBridge;

/// <summary>
/// The bridge owns the session (mode, manual ERG target, running workout) and tells the mod with the `session` message; the mod
/// sends button presses as `intent` messages and acts on each event once (src\SessionView.cs). Also the `run --sim` fake trainer.
/// Run by `turbotrainer-bridge selftest`; simulated clock, no radio, no game.
/// </summary>
public static class SessionTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private const string Zwo = "<workout_file><name>Session test</name><workout>"
        + "<SteadyState Duration='10' Power='0.5'><textevent timeoffset='2' message='hello'/></SteadyState>"
        + "<SteadyState Duration='10' Power='1'/>"
        + "<FreeRide Duration='5'/>"
        + "<SteadyState Duration='10' Power='0.75'/>"
        + "</workout></workout_file>";

    private sealed class FakeTrainer : ITrainerControl
    {
        public readonly List<(string Kind, double Value)> Writes = new();
        public Task<bool> SetTrackResistanceAsync(double g, double crr, CancellationToken ct = default) { Writes.Add(("grade", g)); return Task.FromResult(true); }
        public Task<bool> SetTargetPowerAsync(double w, CancellationToken ct = default) { Writes.Add(("erg", w)); return Task.FromResult(true); }
    }

    /// <summary>A session on a simulated clock, with every message read back by the mod's own parser and event filter.</summary>
    private sealed class Rig
    {
        public readonly List<string> Log = new();
        public readonly Session S;
        public DateTime Now = T0;
        public readonly SessionEventFilter Filter = new();
        public readonly List<SessionEvent> Got = new();
        public SessionView? Last;
        public Rig(double ftp = 200) { S = new Session(new SessionConfig(), () => ftp, Log.Add); }

        public void Do(string name, double? value = null) => S.Apply(new Intent(name, value), Now);

        /// <summary>Ticks of 250 ms; <paramref name="deliverEvery"/> = 3 drops two messages in three on the way to the mod.</summary>
        public void Ticks(int n, bool pedalling = true, bool riding = true, double watts = 150, int deliverEvery = 1)
        {
            for (int i = 0; i < n; i++)
            {
                Now = Now.AddMilliseconds(250);
                S.Tick(0.25, pedalling, riding, watts, Now);
                if (i % deliverEvery == 0) Deliver();
            }
        }

        public SessionView Deliver()
        {
            var v = SessionView.Parse((Dictionary<string, object>)JsonReader.Parse(S.Message(Now)));
            Filter.Take(v, Got);
            Last = v;
            return v;
        }
    }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Session: toggle, adjust, limits");
        {
            var r = new Rig();
            check(!r.S.Erg && r.S.ErgTargetW is null && r.Deliver().Mode == "freeride", "starts in freeride, no target");
            r.Do("adjust", 1);
            check(r.S.ManualW == 120 && r.Log.Exists(l => l.Contains("adjust ignored")), "adjust in freeride is ignored (and logged)");
            r.Do("erg_toggle");
            check(r.S.Erg && r.S.ErgTargetW == 120, "toggle: ERG at the default 120 W");
            var v = r.Deliver();
            check(v.Mode == "erg" && v.TargetW == 120f && v.Note == "ERG 120 W" && v.ModeLabel == "ERG 120 W", "session message: mode erg, targetW 120, note");
            r.Do("adjust", 1);
            check(r.S.ErgTargetW == 130, "adjust +1 = +10 W");
            r.Do("adjust", 100);
            check(r.S.ErgTargetW == 400 && r.S.Note(r.Now) == "ERG 400 W (max)", "clamped at 400 W, note says (max)");
            r.Do("adjust", -100);
            check(r.S.ErgTargetW == 30 && r.S.Note(r.Now).EndsWith("(min)"), "clamped at 30 W, note says (min)");
            check(r.S.Note(r.Now.AddSeconds(3)) == "", "the note clears after 2 s");
            r.Do("erg_toggle");
            check(!r.S.Erg && r.S.ErgTargetW is null && float.IsNaN(r.Deliver().TargetW), "toggle again: freeride, targetW null");
            r.Do("nonsense");
            check(r.Log.Exists(l => l.Contains("unknown intent")), "an unknown intent is logged and ignored");
            check(r.Log.Exists(l => l.StartsWith("[intent] erg_toggle")) && r.Log.Exists(l => l.StartsWith("[intent] adjust 1")), "every intent is logged");
        }

        string dir = Path.Combine(Path.GetTempPath(), "descenders_session_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Sub"));
        File.WriteAllText(Path.Combine(dir, "Sub", "t.zwo"), Zwo);
        try { WorkoutRuns(check, dir); ClockWaitsForGame(check, dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine("Session events: the mod acts on each seq once");
        {
            var f = new SessionEventFilter();
            var got = new List<SessionEvent>();
            SessionView V(long seq, params long[] ev) { var v = new SessionView { EventSeq = seq }; foreach (long s in ev) v.Events.Add(new SessionEvent { Seq = s, Kind = "step" }); return v; }
            f.Take(V(5, 4, 5), got);
            check(got.Count == 0, "the first message only primes: events from before the mod connected are not replayed");
            f.Take(V(7, 5, 7, 6), got);
            check(got.Count == 2 && got[0].Seq == 6 && got[1].Seq == 7, "new events once each, in seq order");
            f.Take(V(7, 5, 6, 7), got);
            check(got.Count == 2, "a repeated message adds nothing");
            f.Take(V(2, 1, 2), got);
            check(got.Count == 2, "seq going backwards (bridge restarted) re-primes without playing");
            f.Take(V(3, 2, 3), got);
            check(got.Count == 3 && got[2].Seq == 3, "and the next new event plays");
            f.Reset();
            f.Take(V(9, 8, 9), got);
            check(got.Count == 3, "after Reset (session went stale) the next message primes again");
        }

        Console.WriteLine("RideLoop holds ERG from the session and releases to flat on stale telemetry");
        {
            var cfg = new ResistanceConfig();
            var loop = new RideLoop(cfg, training: new TrainingConfig { TrainerDifficulty = 1 });   // the 5 % road gradient reaches the trainer as is
            var tr = new FakeTrainer();
            var s = new Session(new SessionConfig(), () => 200, _ => { });
            var fresh = new ModState(true, 0.1, true, true, 0.05);
            DateTime now = T0;
            void Step(ModState m) { now = now.AddMilliseconds(250); loop.Step(tr, m, s.ErgTargetW, true, now, CancellationToken.None); }
            Step(fresh);
            check(tr.Writes.Count == 1 && tr.Writes[0].Kind == "grade" && Math.Abs(tr.Writes[0].Value - 5.0) < 1e-9, "freeride session: the terrain gradient (5 %)");
            s.Apply(new Intent("erg_toggle"), now);
            Step(fresh);
            check(tr.Writes[^1] == ("erg", 120.0) && loop.ErgApplied == 120, "ERG toggled in the session: 120 W on the next tick");
            Step(fresh with { AgeSeconds = 2.0 });
            check(tr.Writes[^1] == ("grade", 0.0) && loop.ErgApplied is null, "stale telemetry: released to flat at once");
            check(!RideLoop.Riding(fresh with { AgeSeconds = 2.0 }, cfg) && !RideLoop.Riding(fresh with { Riding = false }, cfg) && RideLoop.Riding(fresh, cfg),
                "riding = fresh telemetry, player found and the game not paused");
            Step(fresh);
            check(tr.Writes[^1] == ("erg", 120.0), "fresh again: ERG straight back");
        }

        Console.WriteLine("Intent message parsing (mod -> bridge)");
        {
            Intent? P(string json) { using var d = JsonDocument.Parse(json); return ModLink.ParseIntent(d.RootElement); }
            check(P("{\"type\":\"intent\",\"name\":\"adjust\",\"value\":-1}") == new Intent("adjust", -1), "adjust with value -1");
            check(P("{\"type\":\"intent\",\"name\":\"menu_confirm\",\"file\":\"x.xml\"}") == new Intent("menu_confirm"), "menu_confirm (no file / root / tag any more, extra fields ignored)");
            check(P("{\"type\":\"intent\"}") is null && P("{\"type\":\"intent\",\"name\":\"\"}") is null, "no name = not an intent");
        }

        Console.WriteLine("Pedalling rule (as the mod had it): power > 5 W or cadence > 10 rpm");
        check(Session.IsPedalling(6, null) && Session.IsPedalling(0, 11) && !Session.IsPedalling(5, 10) && !Session.IsPedalling(null, null), "6 W or 11 rpm pedal; 5 W / 10 rpm / nothing do not");

        Console.WriteLine("run --sim options and the fake trainer");
        {
            var o = SimOptions.Parse(new[] { "run", "--sim", "--watts-profile", "30:250,0:150", "--idle-between", "40,50", "--stop-sending-after", "90", "--cadence", "70" }, out string err);
            check(o != null && err == "", "options parse: " + err);
            if (o != null)
            {
                check(o.WattsAt(0) == 150 && o.WattsAt(29.9) == 150 && o.WattsAt(30) == 250 && o.WattsAt(45) == 0 && o.WattsAt(50) == 250, "profile (sorted by time) and the idle window");
                double t = 10;
                var sim = new SimTrainer(o, () => t);
                check(sim.PowerW == 150 && sim.CadenceRpm == 70 && sim.TrainerPaired && !sim.Muted, "150 W at 70 rpm, paired, sending");
                sim.SetTargetPowerAsync(200).GetAwaiter().GetResult();
                check(sim.PowerW == 200, "ERG target held while the fake rider pedals");
                t = 45;
                check(sim.PowerW == 0 && sim.CadenceRpm == 0, "idle window: 0 W, 0 rpm even in ERG");
                t = 60; sim.SetTrackResistanceAsync(3, double.NaN).GetAwaiter().GetResult();
                check(sim.PowerW == 250, "resistance write leaves ERG: the rider's own 250 W");
                t = 95;
                check(sim.PowerW is null && !sim.TrainerPaired && sim.Muted, "after --stop-sending-after: no data, trainer messages stop");
            }
            check(SimOptions.Parse(new[] { "run", "--sim" }, out _) is { Watts: 200 }, "no options: 200 W");
            check(SimOptions.Parse(new[] { "run", "--sim", "--watts", "x" }, out string e1) is null && e1.Contains("--watts"), "a bad number is refused with the option named");
            check(SimOptions.Parse(new[] { "run", "--sim", "--idle-between", "50,40" }, out _) is null, "idle window must not run backwards");
        }
    }

    /// <summary>A Start press on the Workouts tab arms the workout and opens the game. The rider pedals while the game loads
    /// (to wake the cadence sensor and their legs); that must not start the workout.</summary>
    private static void ClockWaitsForGame(Action<bool, string> check, string dir)
    {
        Console.WriteLine("Session: a workout armed with no game connected waits for the game, however long the rider pedals");
        var r = new Rig(ftp: 200);
        r.Deliver();   // the mod primes on its first message
        r.S.StartWorkout(Path.Combine(dir, "Sub", "t.zwo"), r.Now);
        r.Ticks(4 * 180, pedalling: true, riding: false, watts: 180);   // 3 minutes of pedalling while the game loads
        check(r.S.Run is { Started: false, Elapsed: 0, StepIndex: 0 },$"3 min of pedalling, no game riding: the clock is still at zero (elapsed {r.S.Run?.Elapsed:0.00})");
        var v = r.Deliver();
        check(v.Workout is { Started: false, Step: 1 } && r.S.Summary().Contains(" armed") && r.S.ErgTargetW == 100, "still armed on step 1, its first target ready");
        check(!r.Log.Exists(l => l.Contains("Workout: started")) && !r.Got.Exists(e => e.Kind is "step" or "countdown" or "text" or "complete"), "nothing started, no workout event was sent");
        r.Ticks(8, pedalling: false, riding: true);
        check(r.S.Run is { Started: false, Elapsed: 0 }, "the game reports riding but the rider is not pedalling yet: still armed");
        r.Ticks(4, pedalling: true, riding: true);
        check(r.S.Run is { Started: true } && Math.Abs(r.S.Run.Elapsed - 1f) < 0.01f && r.Log.Exists(l => l.Contains("Workout: started")),
            $"the game reports riding and the rider pedals: the clock starts, 1 s counted (elapsed {r.S.Run?.Elapsed:0.00})");
    }

    private static void WorkoutRuns(Action<bool, string> check, string dir)
    {
        Console.WriteLine("Session: a workout from the menu (FTP from the bridge), armed until the first stroke");
        var r = new Rig(ftp: 200);
        r.Deliver();   // the mod primes on its first message
        r.S.StartWorkout(Path.Combine(dir, "Sub", "t.zwo"), r.Now, "plan|b0.a1");
        check(r.S.Run != null && r.S.Erg && r.S.Tag == "plan|b0.a1", "loaded, ERG on, tag kept");
        check(r.S.ErgTargetW == 100, $"first step 0.5 x FTP 200 = 100 W (got {r.S.ErgTargetW})");
        r.Ticks(8, pedalling: false);
        check(!r.S.Run!.Started && r.S.Run.Elapsed == 0, "armed: the clock waits for the first pedal stroke");
        var v = r.Deliver();
        check(v.Workout != null && !v.Workout.Started && v.Workout.Name == "Session test" && v.Workout.Steps == 4 && v.Workout.Step == 1 && v.Workout.Tag == "plan|b0.a1",
            "session message: workout armed, step 1 of 4, tag");
        check(v.Workout != null && v.Workout.Upcoming.Count == 3 && v.Workout.Upcoming[1].FreeRide && v.Workout.Upcoming[0].StartW == 200f && v.Workout.Current.StartW == 100f && v.Workout.MoreAfterUpcoming == 0,
            "current row and every upcoming row (free ride flagged, planned watts)");

        Console.WriteLine("Session: the workout clock holds while not riding, stale, or not pedalling");
        r.Ticks(4);   // 1 s
        check(r.S.Run.Started && Math.Abs(r.S.Run.Elapsed - 1f) < 0.01f, "pedalling and riding: 1 s counted");
        r.Ticks(20, riding: false);
        check(Math.Abs(r.S.Run.Elapsed - 1f) < 0.01f, "riding = false (game paused / menu / stale telemetry): the clock holds");
        r.Ticks(40, pedalling: false);
        check(r.S.Run.Paused && r.S.Run.Elapsed < 4.5f, $"not pedalling: paused after 3 s (elapsed {r.S.Run.Elapsed:0.00})");
        float held = r.S.Run.Elapsed;
        r.Ticks(1);
        check(!r.S.Run.Paused && r.S.Run.Elapsed > held, "pedalling again resumes");
        check(r.Got.Exists(e => e.Kind == "text" && e.Text == "hello"), "the text event at 2 s reached the mod");

        Console.WriteLine("Session: step, countdown and free-ride step");
        r.Ticks(4 * 12, deliverEvery: 3);   // to ~17 s, two messages in three dropped
        check(r.S.Run.StepIndex == 1 && r.S.ErgTargetW == 200, $"step 2 at 1.0 x FTP = 200 W (got {r.S.ErgTargetW})");
        check(r.Got.Count(e => e.Kind == "step") == 1, "one step event, though most messages were dropped");
        var cd = r.Got.Where(e => e.Kind == "countdown").Select(e => e.Left).ToList();
        check(cd.Count(x => x == 1) == 1 && cd.Count(x => x == 2) == 1 && cd.Contains(5), $"countdown events at 5..1 s left before the step change, each once (got {string.Join(",", cd)})");
        r.Ticks(4 * 5);   // into the free ride (20..25 s)
        check(r.S.Run.InFreeRide && r.S.ErgTargetW is null && !r.S.ErgNow && r.S.Erg, "free-ride step: no ERG target, ERG still requested");
        v = r.Deliver();
        check(v.Mode == "freeride" && float.IsNaN(v.TargetW) && v.ModeLabel == "FREERIDE (workout)", "session message: freeride, label FREERIDE (workout)");

        Console.WriteLine("Session: bias, then skip to the end -> complete with skipped");
        r.Do("adjust", 2);
        check(Math.Abs(r.Deliver().Workout!.BiasPct - 10f) < 0.01f, "adjust in a workout = bias +10 %");
        r.Do("skip");
        check(r.S.ErgTargetW is { } t165 && Math.Abs(t165 - 165) < 0.01, $"skip: last step 0.75 x 200 x 1.1 = 165 W (got {r.S.ErgTargetW})");
        r.Do("skip");
        check(r.S.Run == null && !r.S.Erg && r.S.ErgTargetW is null, "skipping past the last step ends the workout: freeride");
        r.Deliver();
        var done = r.Got.Where(e => e.Kind == "complete").ToList();
        check(done.Count == 1 && done[0].Skipped && done[0].Name == "Session test" && done[0].Tag == "plan|b0.a1" && done[0].TotalS == 35f && done[0].FtpW == 200f && Math.Abs(done[0].BiasPct - 10f) < 0.01f,
            "one complete event: name, tag, total, FTP, bias, skipped");
        check(done.Count == 1 && Math.Abs(done[0].AvgW - 150f) < 0.5f && done[0].ElapsedS == 35f, $"avg watts over the pedalled time (got {(done.Count > 0 ? done[0].AvgW : 0)})");
        var seqs = r.Got.Select(e => e.Seq).ToList();
        check(seqs.Count > 0 && seqs.Count == r.S.EventSeq && seqs.Zip(seqs.Skip(1), (a, b) => b > a).All(x => x) && seqs[^1] == r.S.EventSeq,
            $"every event reached the mod exactly once, seq strictly rising ({seqs.Count} events, last seq {r.S.EventSeq})");
        r.Deliver(); r.Deliver();
        check(r.Got.Count == seqs.Count, "repeats of the last message add nothing");

        Console.WriteLine("Session: finish, cancel, manual ERG, bad file");
        var f = new Rig(ftp: 200);
        f.Deliver();
        string t = Path.Combine(dir, "Sub", "t.zwo");
        f.S.StartWorkout(t, f.Now);
        f.Ticks(4 * 36);
        check(f.S.Run == null && f.Got.Exists(e => e.Kind == "complete" && !e.Skipped), "pedalling to the end finishes: complete, not skipped");
        f.S.StartWorkout(t, f.Now);
        f.Ticks(8);
        int before = f.Got.Count(e => e.Kind == "complete");
        ModeButton(check, f, before);
        f.S.StartWorkout(t, f.Now);
        f.S.ManualErg(f.Now);
        check(f.S.Run == null && f.S.Erg && f.S.ErgTargetW == 120, "manual ERG from the menu replaces the workout");
        check(!f.S.StartWorkout(Path.Combine(dir, "missing.zwo"), f.Now), "a file that does not load: StartWorkout says so");
        check(f.S.Run == null && f.S.Erg && f.S.Note(f.Now) == "WORKOUT NOT LOADED", "and leaves manual ERG on, note says so");
        f.Do("start_workout");
        check(f.Log.Exists(l => l.Contains("unknown intent 'start_workout'")), "the old start_workout intent is gone (the menu starts workouts)");
    }

    /// <summary>
    /// V2.22 (Garage test: "RB and D-pad Up cycled ERG and freeride but not the training plan"): with a workout loaded the mode button
    /// goes workout -> freeride -> manual ERG -> workout. The workout is never cancelled by it: it waits on hold, clock stopped, and resumes
    /// where it was. <paramref name="f"/> is 2 s into the test workout (step 1, 100 W).
    /// </summary>
    private static void ModeButton(Action<bool, string> check, Rig f, int completeBefore)
    {
        Console.WriteLine("Session (V2.22): the mode button cycles freeride -> ERG -> the loaded workout, which waits on hold");
        float at = f.S.Run!.Elapsed;
        f.Do("erg_toggle");
        var v = f.Deliver();
        check(f.S.Run != null && f.S.Held && !f.S.Erg && f.S.ErgTargetW is null && f.Got.Count(e => e.Kind == "complete") == completeBefore,
            "in the workout, the button goes to freeride: the workout is kept on hold, not cancelled, no complete event");
        check(v.Mode == "freeride" && v.ModeLabel == "FREERIDE" && v.Workout is { Held: true, Name: "Session test" } && v.Note == "FREERIDE (workout on hold)",
            $"session message: freeride, the workout still listed with held = true (note '{v.Note}')");
        f.Ticks(8);
        check(f.S.Run!.Elapsed == at, "on hold: its clock does not run while the rider pedals in freeride");
        f.Do("skip"); f.Do("adjust", 1);
        check(f.S.Run!.StepIndex == 0 && f.S.Run!.BiasFactor == 1f && f.Log.Exists(l => l.Contains("skip ignored (the workout is on hold)")), "on hold: skip and the bias buttons leave it alone");

        f.Do("erg_toggle");
        v = f.Deliver();
        check(f.S.Erg && f.S.Held && f.S.ErgTargetW == 120 && v.Mode == "erg" && v.TargetW == 120f && v.Workout is { Held: true },
            "again: manual ERG at 120 W (not the workout's 100 W), the workout still on hold");
        f.Do("adjust", 1);
        check(f.S.ManualW == 130 && f.S.Run!.BiasFactor == 1f, "RB + Right there moves the manual target, not the workout's bias");
        f.Do("adjust", -1);
        f.Ticks(8);
        check(f.S.Run!.Elapsed == at, "its clock holds in manual ERG too");

        f.Do("erg_toggle");
        v = f.Deliver();
        check(f.S.Run != null && !f.S.Held && f.S.Erg && f.S.ErgTargetW == 100 && v.Mode == "erg" && v.TargetW == 100f && v.Workout is { Held: false },
            $"a third press is back in the workout: its own 100 W target (got {f.S.ErgTargetW})");
        f.Ticks(4);
        check(Math.Abs(f.S.Run!.Elapsed - (at + 1f)) < 0.01f, $"and its clock carries on from where it stopped ({f.S.Run!.Elapsed:0.00} s after {at:0.00} s + 1 s)");
        check(f.Log.Exists(l => l.StartsWith("Workout: on hold at")) && f.Log.Exists(l => l.StartsWith("Workout: back in 'Session test'")), "hold and return are logged");

        f.Do("erg_toggle");
        f.Ticks(4 * 40);
        check(f.S.Run != null && f.S.Held && f.Got.Count(e => e.Kind == "complete") == completeBefore, "left on hold it never finishes by itself");
        f.Do("erg_toggle"); f.Do("erg_toggle");
        f.Ticks(4 * 36);
        check(f.S.Run == null && !f.S.Held && !f.S.Erg && f.Got.Count(e => e.Kind == "complete" && !e.Skipped) == completeBefore + 1,
            "resumed and ridden to its end: complete as usual, back to freeride with nothing on hold");
        f.Do("erg_toggle"); f.Do("erg_toggle");
        check(!f.S.Erg && f.S.Run == null, "with no workout loaded the button is freeride / ERG, as it always was");
    }
}
