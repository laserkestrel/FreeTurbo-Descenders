using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// Session limits. The rider-facing ones come from the `training` section of TrainerBridgeConfig.json (<see cref="From"/>); the rest are
/// fixed. The defaults here are the training section's defaults.
/// </summary>
public sealed record SessionConfig
{
    /// <summary>The session limits for a training section: the ERG maximum is also held under config.json's resistance.ErgMaxW (the lower wins).</summary>
    public static SessionConfig From(TrainingConfig t, ResistanceConfig r)
    {
        double max = Math.Min(t.ErgMaxW, r.ErgMaxW), min = Math.Min(t.ErgMinW, max);
        return new SessionConfig
        {
            ErgMinW = min, ErgMaxW = max, ErgStepW = t.ErgStepW, ErgDefaultW = Math.Clamp(t.ErgDefaultW, min, max),
            BiasStepPct = (float)t.WorkoutBiasStepPct, PauseSeconds = (float)t.WorkoutPauseSeconds,
        };
    }

    public double ErgMinW { get; init; } = 30;
    public double ErgMaxW { get; init; } = 400;
    public double ErgStepW { get; init; } = 10;
    public double ErgDefaultW { get; init; } = 120;
    /// <summary>RB + Left / Right in a workout moves every target by this many percent.</summary>
    public float BiasStepPct { get; init; } = 5f;
    /// <summary>No power and no cadence for this long pauses the workout clock.</summary>
    public float PauseSeconds { get; init; } = 3f;
    /// <summary>Upcoming steps listed in the session message (the mod's panel shows WorkoutHudRows of them, cfg max 30).</summary>
    public int UpcomingRows { get; init; } = 30;
    /// <summary>Recent events repeated in every session message, so a dropped datagram loses none.</summary>
    public int EventsKept { get; init; } = 8;
    /// <summary>Countdown events are sent at this many, .., 2, 1 s left in each step; the mod ticks for the last WorkoutCountdownS of them.</summary>
    public int CountdownS { get; init; } = 30;
    public double NoteSeconds { get; init; } = 2;
}

/// <summary>A button press in the game (docs/IPC.md `intent`): erg_toggle (the mode button, V2.22) | adjust (value) | skip. Workouts and plan sessions are started in
/// the bridge window (the in-game menu and its menu_* intents are gone).</summary>
public readonly record struct Intent(string Name, double? Value = null)
{
    public override string ToString() =>
        Name + (Value is { } v ? " " + v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "");
}

/// <summary>
/// The training session, owned by the bridge: freeride | ERG, the manual ERG target and the running workout
/// (docs/REQUIREMENTS.md V2.1-V2.18), plus plan progress (<see cref="PlanBook"/>: a finished plan session is recorded here, V3.6).
/// The game only sends button presses (<see cref="Intent"/>) and whether the rider is riding;
/// <see cref="RideLoop"/> takes the ERG target from <see cref="ErgTargetW"/> and sends <see cref="Message"/> to the mod at 4 Hz.
/// <see cref="StartWorkout"/> is what the bridge window's Workouts tab calls through <see cref="Menu"/> (workouts and plan sessions
/// are chosen there, not in the game), handed over with <see cref="RideLoop.Post"/>.
/// Single-threaded: only the ride loop calls it. Time comes in as arguments, so `selftest` drives it on a simulated clock.
/// The workout clock runs only while the rider pedals (own ANT readings) AND the game says it is riding with fresh telemetry,
/// so a paused game, a menu or a dead mod hold it (docs/REQUIREMENTS.md V2.20).
/// </summary>
public sealed class Session
{
    public const double PedallingW = 5, PedallingRpm = 10;

    /// <summary>The trainer is producing power or the cranks are turning (the workout clock runs only then). Same rule the mod used.</summary>
    public static bool IsPedalling(int? powerW, double? cadenceRpm) => (powerW ?? 0) > PedallingW || (cadenceRpm ?? 0) > PedallingRpm;

    private SessionConfig _cfg;
    private readonly Func<double> _ftpW;
    private readonly Action<string> _log;

    /// <summary>ERG requested by the rider. A workout's free-ride steps override it for their duration, see <see cref="ErgNow"/>.</summary>
    public bool Erg { get; private set; }
    public double ManualW { get; private set; }
    internal WorkoutRunner? Run { get; private set; }
    /// <summary>The rider stepped out of the loaded workout with the mode button (V2.22): it stays loaded with its clock stopped, the mode is
    /// plain freeride or manual ERG meanwhile, and the mode button comes back round to it. False when no workout is loaded.</summary>
    public bool Held { get; private set; }
    /// <summary>The workout the rider is in right now: null when none is loaded or it is on hold.</summary>
    private WorkoutRunner? Active => Held ? null : Run;
    public string Tag { get; private set; } = "";

    private float _workoutFtp;
    private double _wattsSumWs, _wattsElapsedS;
    private int _lastStep = -1;
    private readonly List<string> _texts = new();
    private string _note = "";
    private DateTime _noteUntil = DateTime.MinValue;
    private readonly List<SessionEvent> _events = new();
    private long _seq;
    private readonly PlanBook _plans;
    private PlanRun? _plan;   // the plan session the running workout belongs to (V3.6), null = none

    /// <summary>Plan progress in memory only (tests).</summary>
    public Session(SessionConfig cfg, Func<double> ftpW, Action<string>? log = null) : this(cfg, ftpW, log, null) { }

    /// <param name="plans">plan progress; null = in memory only</param>
    internal Session(SessionConfig cfg, Func<double> ftpW, Action<string>? log, PlanBook? plans)
    {
        _cfg = cfg; _ftpW = ftpW; _log = log ?? Console.WriteLine;
        ManualW = Math.Clamp(cfg.ErgDefaultW, cfg.ErgMinW, cfg.ErgMaxW);
        _plans = plans ?? PlanBook.InMemory(_log);
    }

    internal PlanBook Plans => _plans;
    /// <summary>The rider's FTP right now (a workout keeps the one it was loaded with).</summary>
    internal double FtpW => _ftpW();
    internal void Log(string line) => _log(line);
    /// <summary>The plan session of the running workout, if any.</summary>
    internal PlanRun? Plan => _plan;

    public SessionConfig Config => _cfg;

    /// <summary>
    /// New limits mid-ride (the rider saved the Resistance tab): the manual target is kept inside the new ERG range at once; a new start
    /// target, bias step or pause time applies from the next ride / workout (a running workout keeps the ones it was loaded with).
    /// </summary>
    public void ApplyConfig(SessionConfig cfg, DateTime now)
    {
        _cfg = cfg;
        double was = ManualW;
        ManualW = Math.Clamp(ManualW, cfg.ErgMinW, cfg.ErgMaxW);
        _log("Session: limits ERG " + cfg.ErgMinW.ToString("F0") + "-" + cfg.ErgMaxW.ToString("F0") + " W, step " + cfg.ErgStepW.ToString("F0") + " W"
            + (Math.Abs(was - ManualW) > 0.01 ? "; manual target " + was.ToString("F0") + " -> " + ManualW.ToString("F0") + " W" : ""));
        if (Math.Abs(was - ManualW) > 0.01 && ErgNow && Run is null) Say("ERG " + ManualW.ToString("F0") + " W", now);
    }

    public long EventSeq => _seq;
    internal IReadOnlyList<SessionEvent> Events => _events;

    /// <summary>The trainer should hold a power target right now (ERG requested and not in a workout free-ride step).</summary>
    public bool ErgNow => Erg && !(Active is { } r && r.InFreeRide);

    /// <summary>The target to hold (the workout's, biased and clamped, or the manual one); null = freeride.</summary>
    public double? ErgTargetW
    {
        get
        {
            if (!ErgNow) return null;
            if (Active is { } r && !float.IsNaN(r.TargetW)) return Math.Clamp(r.TargetW, _cfg.ErgMinW, _cfg.ErgMaxW);
            return ManualW;
        }
    }

    public string Note(DateTime now) => now < _noteUntil ? _note : "";

    // ---------------------------------------------------------------- intents

    public void Apply(Intent it, DateTime now)
    {
        _log("[intent] " + it);
        switch (it.Name)
        {
            case "erg_toggle": Toggle(now); break;
            case "adjust":
            {
                int steps = (int)Math.Round(it.Value ?? 0);
                if (steps == 0 || !Erg) { _log("Session: adjust ignored (" + (Erg ? "no steps" : "not in ERG") + ")"); break; }
                Adjust(steps, now);
                break;
            }
            case "skip": Skip(now); break;
            default: _log("Session: unknown intent '" + it.Name + "' ignored"); break;
        }
    }

    /// <summary>
    /// The mode button (RB + D-pad Up, V2.22) goes round the modes: FREERIDE -> ERG (manual target) -> the loaded workout -> FREERIDE. With no
    /// workout loaded it is freeride / ERG as before. Leaving the workout does not cancel it: it is put on hold (<see cref="Held"/>) and the
    /// button comes back to it where it stopped, so a training plan session survives a look at the other modes.
    /// </summary>
    private void Toggle(DateTime now)
    {
        if (Active is { } r)
        {
            Held = true; Erg = false;
            Say("FREERIDE (workout on hold)", now);
            _log("Workout: on hold at " + WorkoutRunner.Fmt(r.Elapsed) + " of " + WorkoutRunner.Fmt(r.W.TotalS) + " (the mode button comes back to it after ERG)");
        }
        else if (!Erg)
        {
            Erg = true;
            ManualW = Math.Clamp(ManualW, _cfg.ErgMinW, _cfg.ErgMaxW);
            Say("ERG " + ManualW.ToString("F0") + " W", now);
        }
        else if (Run is { } held)
        {
            Held = false;
            Say("WORKOUT: " + (held.Started ? "resumed" : "pedal to start"), now);
            _log("Workout: back in '" + held.W.Name + "' at " + WorkoutRunner.Fmt(held.Elapsed) + " of " + WorkoutRunner.Fmt(held.W.TotalS));
        }
        else
        {
            Erg = false;
            Say("FREERIDE", now);
        }
        _log("Session: mode -> " + (Active is not null ? "workout" + (ErgTargetW is { } t ? " " + t.ToString("F0") + " W" : "") : Erg ? "erg " + (ErgTargetW ?? 0).ToString("F0") + " W" : "freeride")
            + (Held ? " (workout on hold)" : ""));
    }

    private void Adjust(int steps, DateTime now)
    {
        if (Active is { } r)
        {
            r.AdjustBias(steps);
            int pct = (int)Math.Round((r.BiasFactor - 1f) * 100f);
            Say("WORKOUT " + (pct >= 0 ? "+" : "") + pct + " %", now);
            _log("Workout: bias " + (pct >= 0 ? "+" : "") + pct + " % (target " + (ErgTargetW ?? 0).ToString("F0") + " W)");
            return;
        }
        double want = ManualW + steps * _cfg.ErgStepW, t = Math.Clamp(want, _cfg.ErgMinW, _cfg.ErgMaxW);
        bool clamped = Math.Abs(want - t) > 0.01;
        ManualW = t;
        Say("ERG " + ManualW.ToString("F0") + " W" + (clamped ? (steps > 0 ? " (max)" : " (min)") : ""), now);
        _log("Session: ERG target -> " + ManualW.ToString("F0") + " W");
    }

    private void Skip(DateTime now)
    {
        if (Active is not { } r) { _log("Session: skip ignored (" + (Run is null ? "no workout" : "the workout is on hold") + ")"); return; }
        r.Skip();
        if (r.Finished) { End("skipped to the end", now); return; }
        Say("SKIP: " + r.Step.Label, now);
        _log("Workout: skipped to " + r.Step.Label);
        Add(new SessionEvent { Kind = "step" });
        _lastStep = r.StepIndex; LogStep();
    }

    /// <summary>ERG at the manual target with no workout; replaces a running workout.</summary>
    public void ManualErg(DateTime now)
    {
        if (Run is { } r) { _log("Workout: replaced at " + WorkoutRunner.Fmt(r.Elapsed)); Run = null; Held = false; Tag = ""; _plan = null; _lastStep = -1; }
        Erg = true;
        ManualW = Math.Clamp(ManualW, _cfg.ErgMinW, _cfg.ErgMaxW);
        Say("ERG " + ManualW.ToString("F0") + " W", now);
        _log("Session: manual ERG " + ManualW.ToString("F0") + " W");
    }

    /// <summary>ERG on with the workout at <paramref name="path"/> (absolute) armed: its clock starts at the first pedal stroke. <paramref name="tag"/>
    /// comes back in the session message and the `complete` event; <paramref name="plan"/> = a plan session, recorded when it reaches its end.
    /// A file that does not load leaves manual ERG on (false).</summary>
    internal bool StartWorkout(string path, DateTime now, string tag = "", PlanRun? plan = null)
    {
        if (Run is { } old) { _log("Workout: replaced at " + WorkoutRunner.Fmt(old.Elapsed)); Run = null; _lastStep = -1; }
        Erg = true; Held = false; Tag = ""; _plan = null;
        ManualW = Math.Clamp(ManualW, _cfg.ErgMinW, _cfg.ErgMaxW);
        if (path.Length == 0) { _log("Workout: no file (manual ERG)"); Say("WORKOUT NOT LOADED", now); return false; }
        _workoutFtp = (float)_ftpW();
        Workout w = WorkoutParser.Load(path, _workoutFtp);
        foreach (string warn in w.Warnings) _log("Workout: " + w.FileName + ": " + warn);
        if (!w.Ok)
        {
            _log("Workout: could not load " + path + ": " + (w.Error ?? "no steps") + " (manual ERG)");
            Say("WORKOUT NOT LOADED", now);
            return false;
        }
        Run = new WorkoutRunner(w, _cfg.BiasStepPct, _cfg.PauseSeconds);
        Tag = tag;
        _plan = plan;
        _wattsSumWs = 0; _wattsElapsedS = 0;
        _lastStep = Run.StepIndex;
        _log("Workout: loaded '" + w.Name + "' (" + w.Steps.Count + " steps, " + WorkoutRunner.Fmt(w.TotalS) + ", FTP " + _workoutFtp.ToString("F0") + " W) from " + path + ", armed: starts at the first pedal stroke");
        LogStep();
        Say("WORKOUT: pedal to start", now);
        return true;
    }

    // ---------------------------------------------------------------- clock

    /// <summary>
    /// One tick of the bridge's clock. <paramref name="riding"/> = fresh telemetry from the game and the game says the rider is riding
    /// (player found, not paused): otherwise the workout clock holds, whatever the trainer reports.
    /// </summary>
    public void Tick(double dt, bool pedalling, bool riding, double watts, DateTime now)
    {
        if (Run is null || Held || !Erg || !riding || dt <= 0) return;
        var r = Run;
        bool wasPaused = r.Paused, wasStarted = r.Started;
        int stepBefore = r.StepIndex;
        float leftBefore = r.StepRemainingS;
        r.Tick((float)dt, pedalling);
        if (r.Started && !r.Paused) { _wattsSumWs += Math.Max(0, watts) * dt; _wattsElapsedS += dt; }
        if (!wasStarted && r.Started) _log("Workout: started (first pedal stroke)");
        if (r.Paused != wasPaused) _log("Workout: " + (r.Paused ? "paused" : "resumed") + " at " + WorkoutRunner.Fmt(r.Elapsed));
        if (r.StepIndex == stepBefore && !r.Finished) Countdown(leftBefore, r.StepRemainingS);
        _texts.Clear(); r.TakeDueTexts(_texts);
        foreach (string t in _texts) Add(new SessionEvent { Kind = "text", Text = t.Length > 300 ? t.Substring(0, 300) : t });
        if (r.Finished) { End("finished", now); return; }
        if (r.StepIndex != _lastStep) { Add(new SessionEvent { Kind = "step" }); _lastStep = r.StepIndex; LogStep(); }
    }

    /// <summary>A countdown event at CountdownS, .., 2, 1 s left in the step (workout clock, so it waits while paused); the step event follows at 0.</summary>
    private void Countdown(float before, float now)
    {
        for (int k = _cfg.CountdownS; k >= 1; k--)
            if (before > k && now <= k) { Add(new SessionEvent { Kind = "countdown", Left = k }); return; }
    }

    /// <summary>The workout is over (or cancelled): back to freeride. "finished" and "skipped to the end" send a complete event (sound, confetti in
    /// the game) and, for a plan session, record it in the plan progress (V3.6, saved here: the bridge is the only writer); "cancelled" does neither.</summary>
    private void End(string why, DateTime now)
    {
        if (Run is not { } r) return;
        float avgW = _wattsElapsedS > 0 ? (float)(_wattsSumWs / _wattsElapsedS) : 0f;
        bool skipped = why == "skipped to the end";
        if (why != "cancelled")
        {
            Add(new SessionEvent
            {
                Kind = "complete", Name = r.W.Name, Tag = Tag, TotalS = r.W.TotalS, ElapsedS = (float)Math.Round(r.Elapsed), AvgW = avgW,
                BiasPct = (r.BiasFactor - 1f) * 100f, FtpW = _workoutFtp, Skipped = skipped,
            });
            if (_plan is { } p)
            {
                _plans.Store.RecordCompletion(p.Key, p.Ref, p.File, Iso.NowUtc(), (int)Math.Round(r.Elapsed), avgW, _workoutFtp, r.BiasFactor * 100f, skipped);
                _plans.Save();
                _log("Plan: recorded " + p.Key + " " + p.Ref + (skipped ? " (skipped)" : "") + ", avg " + avgW.ToString("F0") + " W");
            }
        }
        _log("Workout: " + why + " at " + WorkoutRunner.Fmt(r.Elapsed) + " of " + WorkoutRunner.Fmt(r.W.TotalS) + (why != "cancelled" ? ", avg " + avgW.ToString("F0") + " W" : "") + "; back to freeride");
        Run = null; Held = false; Tag = ""; _plan = null; _lastStep = -1;
        Erg = false;
        Say(why == "cancelled" ? "FREERIDE" : "WORKOUT " + (why == "finished" ? "DONE" : "ENDED"), now);
    }

    private void LogStep()
    {
        if (Run is not { } r) return;
        var s = r.Step;
        _log("Workout: step " + (r.StepIndex + 1) + "/" + r.W.Steps.Count + " " + s.Label + " " + (s.FreeRide ? "free ride" : s.StartW.ToString("F0") + (Math.Abs(s.EndW - s.StartW) > 0.5f ? " -> " + s.EndW.ToString("F0") : "") + " W") + ", " + s.DurationS.ToString("F0") + " s, at " + WorkoutRunner.Fmt(r.Elapsed));
    }

    private void Add(SessionEvent e)
    {
        e.Seq = ++_seq;
        _events.Add(e);
        while (_events.Count > Math.Max(1, _cfg.EventsKept)) _events.RemoveAt(0);
    }

    /// <summary>The session note for 2 s ("ERG 180 W", "PLAN RESET"), shown on the game's HUD.</summary>
    internal void Say(string s, DateTime now) { _note = s; _noteUntil = now.AddSeconds(_cfg.NoteSeconds); }

    // ---------------------------------------------------------------- message

    /// <summary>The `session` message (docs/IPC.md), read by the mod's SessionView.</summary>
    public string Message(DateTime now)
    {
        var w = new JsonWriter().Obj().Prop("type", "session").Prop("mode", ErgNow ? "erg" : "freeride");
        if (ErgTargetW is { } t) w.Prop("targetW", (float)Math.Round(t, 1)); else w.PropNull("targetW");
        w.Prop("note", Note(now));
        if (Run is not { } r) w.PropNull("workout");
        else
        {
            w.Obj("workout").Prop("name", r.W.Name).Prop("tag", Tag).Prop("started", r.Started).Prop("paused", r.Paused)
                .Prop("elapsedS", (float)Math.Round(r.Elapsed, 2)).Prop("totalS", r.W.TotalS).Prop("step", r.StepIndex + 1).Prop("steps", r.W.Steps.Count)
                .Prop("label", r.Step.Label).Prop("stepRemainingS", (float)Math.Round(r.StepRemainingS, 2)).Prop("freeRide", r.InFreeRide)
                .Prop("held", Held).Prop("biasPct", (float)Math.Round((r.BiasFactor - 1f) * 100f, 2));
            if (_plan is { } p) w.Obj("plan").Prop("title", p.Title).Prop("sessionLabel", p.SessionLabel).End(); else w.PropNull("plan");
            Row(w.Obj("current"), r.Step);
            w.Arr("upcoming");
            int n = Math.Min(_cfg.UpcomingRows, r.W.Steps.Count - r.StepIndex - 1);
            for (int i = 1; i <= n; i++) Row(w.Obj(), r.W.Steps[r.StepIndex + i]);
            w.EndArr().End();
        }
        w.Prop("eventSeq", _seq).Arr("events");
        foreach (var e in _events)
        {
            w.Obj().Prop("seq", e.Seq).Prop("kind", e.Kind);
            switch (e.Kind)
            {
                case "text": w.Prop("text", e.Text); break;
                case "countdown": w.Prop("left", e.Left); break;
                case "complete":
                    w.Prop("name", e.Name).Prop("tag", e.Tag).Prop("totalS", e.TotalS).Prop("elapsedS", e.ElapsedS).Prop("avgW", (float)Math.Round(e.AvgW, 1))
                        .Prop("biasPct", (float)Math.Round(e.BiasPct, 2)).Prop("ftpW", e.FtpW).Prop("skipped", e.Skipped);
                    break;
            }
            w.End();
        }
        return w.EndArr().End().ToString();
    }

    private static void Row(JsonWriter w, WorkoutStep s) =>
        w.Prop("kind", s.Kind).Prop("label", s.Label).Prop("durationS", s.DurationS).Prop("startW", s.StartW).Prop("endW", s.EndW).Prop("freeRide", s.FreeRide).End();

    /// <summary>What is running, in plain words, for the bridge window's Workouts tab.</summary>
    public string Describe()
    {
        if (Run is not { } r) return Erg ? "ERG mode at " + ManualW.ToString("F0") + " W, no workout running" : NoWorkoutText;
        return (_plan is { } p ? p.Title + " " + p.SessionLabel + ", " : "Workout ") + "\"" + r.W.Name + "\": " + WorkoutRunner.Fmt(r.Elapsed) + " of " + WorkoutRunner.Fmt(r.W.TotalS)
            + ", section " + (r.StepIndex + 1) + " of " + r.W.Steps.Count
            + (Held ? " (on hold, " + (Erg ? "ERG mode at " + ManualW.ToString("F0") + " W" : "free ride") + ": RB + D-pad Up in the game comes back to it)"
                : !r.Started ? " (starts when you pedal)" : r.Paused ? " (paused)" : "");
    }

    public const string NoWorkoutText = "Free ride, no workout running";

    /// <summary>One line for the console status (once a second).</summary>
    public string Summary() =>
        (ErgNow ? $"ERG {ErgTargetW:0} W" : Erg ? "FREERIDE (workout)" : "FREERIDE")
        + (Run is { } r ? $"  workout '{r.W.Name}' {WorkoutRunner.Fmt(r.Elapsed)}/{WorkoutRunner.Fmt(r.W.TotalS)} step {r.StepIndex + 1}/{r.W.Steps.Count}{(Held ? " ON HOLD" : r.Started ? r.Paused ? " PAUSED" : "" : " armed")}" : "");
}
