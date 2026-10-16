using DescendersBridge.Ant;

namespace DescendersBridge;

/// <summary>
/// The ride: every 250 ms apply the mod's button presses to the <see cref="Session"/>, tick its workout clock, send the chosen devices'
/// readings to the mod as the `trainer` message and the session as the `session` message, and control the trainer: the session's ERG
/// target (page 0x31), else about once a second the mod's raw road gradient x the rider's trainer difficulty as FE-C track resistance
/// (the difficulty is the bridge's, the game's speed never depends on it). The `training` section is re-read when the settings file
/// changes, so the window's Resistance tab applies mid-ride. Resistance rules carry over from kcd2-turbo (docs/failsafe.md there):
///   - never below 0 %: the Tacx Neo is motorised, a negative grade DRIVES the flywheel;
///   - clamp to MaxGradePercent; ignore moves smaller than DeadbandPercent;
///   - mod silent, not riding, or gradient unknown: flat (0 %), never left loaded on stale data; the same releases an ERG target;
///   - Ctrl+C / exit: retry a flat write until acknowledged.
/// The trainer write never holds the game-facing side up (a write the trainer does not acknowledge takes about 2 s, and a tick that
/// waited for it made the game's HUD flash red and its menu flicker): <see cref="Step"/> starts the write and returns, its result is taken on a
/// later tick. At most one write is in flight; a tick that finds one pending skips, and the next write is decided from the state of that tick.
/// </summary>
public sealed class RideLoop
{
    /// <summary>The loop's tick. A late tick still counts its whole time on the session's clocks, up to <see cref="MaxLateSeconds"/>.</summary>
    public const int TickMs = 250;
    /// <summary>A gap longer than this is not a late tick (the computer slept): the workout clock does not jump over it.</summary>
    public const double MaxLateSeconds = 10.0;

    private readonly ITrainerSource _src;
    private readonly ModLink _mod;
    private readonly ResistanceConfig _cfg;
    private double? _lastSent;
    private DateTime _lastWrite = DateTime.MinValue;
    private DateTime _lastStatus = DateTime.MinValue;
    private DateTime? _lastTick;                  // null until the first tick

    /// <summary>A trainer write that has been started: what it will have set once acknowledged. <see cref="Void"/> = the link dropped meanwhile,
    /// its result no longer says what the trainer holds.</summary>
    private sealed class Write
    {
        public readonly Task<bool> Task; public readonly bool Erg; public readonly double Value;
        public bool Void;
        public Write(Task<bool> task, bool erg, double value) { Task = task; Erg = erg; Value = value; }
    }
    private Write? _write;                        // the write in flight, null = none

    private readonly double? _fixedErgW;          // `run --erg W`: hold this with no mod involved
    private double? _ergApplied;                  // the target last acknowledged by the trainer (ramped), null = not in ERG
    private DateTime _lastErgTick = DateTime.UtcNow;

    /// <summary>Mode, manual target, running workout and plan progress (the bridge owns them, the game only sends intents).</summary>
    public Session Session { get; }
    private readonly TrainingWatch? _watch;       // null in tests

    /// <summary>Writes a .fit activity file after each workout and each free ride; null = none (tests set their own, on a temp folder).</summary>
    internal ActivityRecorder? Activities { get; set; }

    // ---- the bridge window's Workouts tab: its presses run here, on the loop's thread, like the game's intents ----
    private readonly System.Collections.Concurrent.ConcurrentQueue<(Func<Session, DateTime, MenuResult> Action, TaskCompletionSource<MenuResult> Done)> _posted = new();
    private static volatile RideLoop? _current;
    private volatile string _sessionLine = Session.NoWorkoutText;

    /// <summary>The loop of the ride in progress (null between rides): where the window posts to and reads <see cref="SessionLine"/> from.</summary>
    internal static RideLoop? Current => _current;

    /// <summary><see cref="Session.Describe"/> as of the last tick; safe to read from the window's thread.</summary>
    internal string SessionLine => _sessionLine;

    /// <summary>Run <paramref name="action"/> on the session at the next tick (at most <see cref="TickMs"/> later); the task ends with what it
    /// did. Safe from any thread. A loop that stops first never runs it: the caller waits with a time limit.</summary>
    internal Task<MenuResult> Post(Func<Session, DateTime, MenuResult> action)
    {
        var done = new TaskCompletionSource<MenuResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _posted.Enqueue((action, done));
        return done.Task;
    }
    /// <summary>The training settings in use (trainer difficulty, ERG limits, ...), see <see cref="ApplyTraining"/>.</summary>
    public TrainingConfig TrainingSettings { get; private set; }

    /// <summary>The real ride: plan progress from TrainerBridgeConfig.json (loaded once), the training section read again whenever the file changes.</summary>
    public RideLoop(ITrainerSource src, ModLink mod, ResistanceConfig cfg, RiderConfig rider, double? fixedErgW = null)
        : this(src, mod, cfg, fixedErgW, new TrainingWatch(TrainerBridgeConfig.Path, Console.WriteLine), rider) { }

    private RideLoop(ITrainerSource src, ModLink mod, ResistanceConfig cfg, double? fixedErgW, TrainingWatch watch, RiderConfig rider)
        : this(src, mod, cfg, fixedErgW, watch.Current, new Session(SessionConfig.From(watch.Current, cfg), () => rider.FtpW, Console.WriteLine,
            PlanBook.Load(TrainerBridgeConfig.Path, Console.WriteLine)))
    {
        _watch = watch;
        Activities = new ActivityRecorder(() => ActivityFiles.UsableDir(Console.WriteLine), Console.WriteLine);
        Console.WriteLine("Training: " + Training.Describe(watch.Current));
        Console.WriteLine("Activity files (.fit, one after each workout or free ride): " + ActivityFiles.Dir);
    }

    /// <summary>Also the test constructor for <see cref="Tick"/> (tests\unit\RideLoopTests.cs): a fake source, a link on test ports, no settings file.</summary>
    internal RideLoop(ITrainerSource src, ModLink mod, ResistanceConfig cfg, double? fixedErgW, TrainingConfig training, Session session)
    {
        _src = src; _mod = mod; _cfg = cfg; _fixedErgW = fixedErgW;
        TrainingSettings = training;
        Session = session;
    }

    /// <summary>Test constructor: no source, no mod, no settings file; `selftest` calls <see cref="Step"/> directly. <paramref name="training"/> null = the defaults.</summary>
    internal RideLoop(ResistanceConfig cfg, double? fixedErgW = null, TrainingConfig? training = null)
        : this(null!, null!, cfg, fixedErgW, training ?? new TrainingConfig(), new Session(SessionConfig.From(training ?? new TrainingConfig(), cfg), () => 180, _ => { })) { }

    /// <summary>New training settings mid-ride: the next resistance write uses the new difficulty, the session the new ERG limits.</summary>
    internal void ApplyTraining(TrainingConfig t, DateTime now)
    {
        TrainingSettings = t;
        Session.ApplyConfig(SessionConfig.From(t, _cfg), now);
    }

    /// <summary>The game says the rider is on the course and the game is not paused, and its telemetry is fresh: the workout clock may run.</summary>
    public static bool Riding(ModState m, ResistanceConfig cfg) => m.Seen && m.AgeSeconds <= cfg.StaleSeconds && m.PlayerFound && m.Riding;

    /// <summary>
    /// The ERG target to hold, or null for freeride / failsafe. ERG needs a session target (<see cref="DescendersBridge.Session.ErgTargetW"/>),
    /// fresh telemetry and a player, else the trainer is released to flat (an ERG target is never held on stale data). Pure, like <see cref="TargetGrade"/>.
    /// </summary>
    public static double? ErgTarget(ModState m, ResistanceConfig cfg, double? sessionTargetW)
    {
        if (!cfg.Enabled || !m.Seen || m.AgeSeconds > cfg.StaleSeconds || !m.PlayerFound) return null;
        if (sessionTargetW is not { } w || double.IsNaN(w) || w <= 0) return null;
        return Math.Min(w, cfg.ErgMaxW);
    }

    /// <summary>Target resistance, percent: the mod's raw road gradient x <paramref name="trainerDifficulty"/>, never below 0, at most MaxGradePercent.
    /// Pure so it can be tested without a radio.</summary>
    public static double TargetGrade(ModState m, ResistanceConfig cfg, double trainerDifficulty)
    {
        if (!cfg.Enabled || !m.Seen || m.AgeSeconds > cfg.StaleSeconds || !m.PlayerFound || m.RoadGradient is not { } g || double.IsNaN(g) || double.IsNaN(trainerDifficulty))
            return 0.0;
        return Math.Clamp(g * trainerDifficulty * 100.0, 0.0, cfg.MaxGradePercent);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var last = DateTime.UtcNow;
        _current = this;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wait for the next 250 ms tick; a button press is handled at once, so the game's HUD answers without waiting for the tick.
                try
                {
                    while (last.AddMilliseconds(TickMs) - DateTime.UtcNow is { Ticks: > 0 } left)
                        if (await _mod.WaitIntentAsync(left, ct)) TakeIntents(DateTime.UtcNow, sendNow: true);
                }
                catch (OperationCanceledException) { break; }
                // A lost ANT+ stick does not end the loop: the source reads as "no trainer, no readings" until Ride's RecoveringSource has
                // the stick back, and the session (mode, workout, plan) carries on meanwhile.

                var now = last = DateTime.UtcNow;
                Tick(now, ct);
                if ((now - _lastStatus).TotalSeconds >= 1) { _lastStatus = now; Status(); }
            }
        }
        finally
        {
            _current = null;
            StopActivity(DateTime.UtcNow);
            while (_posted.TryDequeue(out var p)) p.Done.TrySetResult(new MenuResult(false, "The connection to your devices was restarted first: try again in a moment."));
        }

        // A write still in flight holds the trainer's send lock for at most its acknowledge wait: let it end, so the caller's release
        // (retried until acknowledged) follows it and nothing is left unobserved.
        if (_write is { } w)
            try { await w.Task.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* cancelled, failed or too slow: not acknowledged */ }
    }

    /// <summary>
    /// One tick, nothing in it waits: the mod's button presses and the window's (<see cref="Post"/>), the session's clocks, the `trainer` / `session` messages, then
    /// <see cref="Step"/> for the trainer. `now` is injected for tests; the time since the last tick is counted whole (a late tick loses none).
    /// </summary>
    internal void Tick(DateTime now, CancellationToken ct = default)
    {
        double dt = _lastTick is { } last ? Math.Clamp((now - last).TotalSeconds, 0.0, MaxLateSeconds) : 0.0;
        _lastTick = now;
        bool trainerUp = _src.TrainerPaired;
        int? power = _src.PowerW;
        double? cadence = _src.CadenceRpm;
        var m = _mod.State;

        if (_watch is { } tw && tw.Poll(now)) ApplyTraining(tw.Current, now);
        RollRideLog();
        TakeIntents(now, sendNow: false);
        TakePosted(now);
        bool riding = Riding(m, _cfg);
        Session.Tick(dt, Session.IsPedalling(power, cadence), riding, power ?? 0, now);
        Activities?.Tick(Session, riding, power, cadence, _src.HeartRateBpm, m.GameSpeedMs, m.SpeedMs, dt, now);
        _sessionLine = Session.Describe();

        if (!_src.Muted) _mod.SendTrainer(power ?? 0, cadence, _src.HeartRateBpm, connected: trainerUp && power.HasValue);
        _mod.SendSession(Session.Message(now));

        Step(_src.Control, m, Session.ErgTargetW, trainerUp, now, ct);
    }

    /// <summary>The ride is over (the window closed, the bridge stopped): a workout still running, or the free ride, gets its activity file
    /// when it was ridden at all. Called when <see cref="RunAsync"/> ends; tests call it themselves.</summary>
    internal void StopActivity(DateTime now) => Activities?.Stop(now);

    /// <summary>The `status` message for the mod's HUD and the console line, once a second.</summary>
    private void Status()
    {
        bool trainerUp = _src.TrainerPaired;
        int? power = _src.PowerW;
        double? cadence = _src.CadenceRpm;
        var m = _mod.State;
        bool noStick = _src.LinkLost;   // whichever transport: the source says what is lost
        if (noStick) ModStatus.Send("nostick", _src.LinkLostText);
        else if (_src.Control is null) ModStatus.Send("notrainer", "No trainer chosen");
        else if (trainerUp && power.HasValue) ModStatus.Send("connected", "Trainer connected");
        else ModStatus.Send("searching", "Searching for trainer");
        Console.WriteLine($"P {Fmt(power, "W")}  cad {Fmt(cadence, "rpm")}  HR {Fmt(_src.HeartRateBpm, "bpm")}{(_src.HeartRateBpm is null && _src.HeartRateDiagnosis.Length > 0 ? " (" + _src.HeartRateDiagnosis + ")" : "")}  | trainer {(noStick ? "LINK LOST" : _src.Control is null ? "none" : trainerUp ? "paired" : "SEARCHING")}"
            + $"  | mod {(m.Seen ? (m.AgeSeconds < 2 ? (m.Riding ? "ok" : "ok, not riding") : $"silent {m.AgeSeconds:0}s") : "not heard")}"
            + $"  road {(m.RoadGradient.HasValue && m.AgeSeconds < 2 ? (m.RoadGradient.Value * 100).ToString("0.0") + "%" : "-")} x difficulty {TrainingSettings.TrainerDifficulty:0.##}  {(_ergApplied is { } ea ? $"  ERG {ea:0} W" : $"  resistance {(_lastSent.HasValue ? _lastSent.Value.ToString("0.0") + "%" : "-")}")}"
            + (WritePending ? "  (write pending)" : "")
            + $"  | {Session.Summary()}");
    }

    /// <summary>The bridge is the hub: the game quitting ends the ride log only after it has been gone a while, and a game coming back
    /// starts a new one; the ride, the session and the bridge keep running (the trainer is flat meanwhile: stale telemetry).</summary>
    private void RollRideLog()
    {
        double since = _mod.SecondsSinceHeard;
        switch (GamePresence.LogStep(_mod.LogOpen, _mod.CanReopenLog, since))
        {
            case RideLogStep.End: _mod.EndLog($"no game heard for {since:0} s"); break;
            case RideLogStep.Reopen: _mod.ReopenLog(); break;
        }
    }

    /// <summary>Apply the mod's button presses in order; <paramref name="sendNow"/> (between ticks) sends the session straight back.</summary>
    internal void TakeIntents(DateTime now, bool sendNow)
    {
        bool any = false;
        while (_mod.TryTakeIntent(out var it)) { Session.Apply(it, now); any = true; }
        if (!any || !sendNow) return;
        _mod.SendSession(Session.Message(now));
    }

    /// <summary>Run what the window posted (<see cref="Post"/>), in order. One that throws ends with its error as the result, never the ride.</summary>
    internal void TakePosted(DateTime now)
    {
        while (_posted.TryDequeue(out var p))
        {
            MenuResult r;
            try { r = p.Action(Session, now); }
            catch (Exception ex) { Console.WriteLine("ERROR in a Workouts tab action: " + ex); r = new MenuResult(false, "That did not work (see the Logs tab)."); }
            p.Done.TrySetResult(r);
        }
    }

    internal double? ErgApplied => _ergApplied;
    internal double? LastSentGrade => _lastSent;

    /// <summary>A trainer write has been started and has not ended yet: no other is started meanwhile.</summary>
    internal bool WritePending => _write is { Task.IsCompleted: false };

    /// <summary>One control tick: decide between ERG (page 0x31, ramped and refreshed) and resistance (page 0x33), releasing ERG at once when it ends.
    /// Never waits: the write is started here and its result taken when it has ended (at once for a trainer that answers at once, else on a later
    /// tick); while one is pending nothing is decided, so nothing queues and the next write is for the target of that later tick.
    /// <paramref name="sessionTargetW"/> is <see cref="DescendersBridge.Session.ErgTargetW"/>; `now` is injected for tests.</summary>
    internal void Step(ITrainerControl? trainer, ModState m, double? sessionTargetW, bool trainerUp, DateTime now, CancellationToken ct)
    {
        bool up = trainer is not null && trainerUp;
        if (_write is { } w)
        {
            if (!up) w.Void = true;   // the link dropped under the write
            if (!w.Task.IsCompleted) { if (!up) { _lastSent = null; _ergApplied = null; } return; }
            Landed(w);
        }
        if (!up) { _lastSent = null; _ergApplied = null; return; }   // re-pair: forget, so the first write goes out

        double? erg = _fixedErgW is { } f ? Math.Min(f, _cfg.ErgMaxW) : ErgTarget(m, _cfg, sessionTargetW);
        if (erg is { } want) { WriteErg(trainer!, want, now, ct); return; }

        if (_ergApplied.HasValue)   // ERG -> freeride or failsafe: release at once, ignoring the write interval and the deadband
        {
            _ergApplied = null; _lastSent = null; _lastWrite = DateTime.MinValue;
        }
        if ((now - _lastWrite).TotalMilliseconds < _cfg.WriteIntervalMs) return;

        // A session in ERG whose target cannot be held (stale telemetry, no player) goes flat: the terrain never loads the trainer in ERG (V2.4 / V2.5).
        double target = sessionTargetW.HasValue ? 0.0 : TargetGrade(m, _cfg, TrainingSettings.TrainerDifficulty);
        bool needFlat = target == 0.0 && _lastSent != 0.0;                       // always release, whatever the deadband
        if (!needFlat && _lastSent is { } last && Math.Abs(target - last) < _cfg.DeadbandPercent) return;

        Start(trainer!.SetTrackResistanceAsync(target, double.NaN, ct), erg: false, target, now);
    }

    /// <summary>A write has been started: take its result now if the trainer has already answered, else on the tick that finds it ended.</summary>
    private void Start(Task<bool> task, bool erg, double value, DateTime now)
    {
        _lastWrite = now;
        var w = new Write(task, erg, value);
        if (task.IsCompleted) Landed(w); else _write = w;
    }

    /// <summary>The write has ended. Only an acknowledged one moves what the trainer is known to hold (<see cref="_lastSent"/> / <see cref="_ergApplied"/>);
    /// a failed, cancelled or thrown one, or one the link dropped under, changes nothing and the next tick writes again.</summary>
    private void Landed(Write w)
    {
        _write = null;
        if (w.Task.IsFaulted) _ = w.Task.Exception;   // observed: the device threw, same as not acknowledged
        if (w.Void || w.Task.Status != TaskStatus.RanToCompletion || !w.Task.Result) return;
        if (w.Erg) { _ergApplied = w.Value; _lastSent = null; }   // _lastSent null: leaving ERG must write flat
        else _lastSent = w.Value;
    }

    /// <summary>Approach <paramref name="want"/> at ErgRampWPerSec (the first write goes straight to it), and resend the current value every ErgRefreshMs.</summary>
    private void WriteErg(ITrainerControl trainer, double want, DateTime now, CancellationToken ct)
    {
        double dt = Math.Clamp((now - _lastErgTick).TotalSeconds, 0.0, 1.0);
        _lastErgTick = now;

        double cur = _ergApplied ?? want;
        double step = _cfg.ErgRampWPerSec * dt;
        double next = Math.Abs(want - cur) <= step ? want : cur + Math.Sign(want - cur) * step;

        bool moving = _ergApplied is null || Math.Abs(next - cur) > 0.01 || Math.Abs(want - cur) > 0.01;
        bool refresh = (now - _lastWrite).TotalMilliseconds >= _cfg.ErgRefreshMs;
        if (!moving && !refresh) return;

        Start(trainer.SetTargetPowerAsync(next, ct), erg: true, next, now);
    }

    private static string Fmt(double? v, string unit) => v.HasValue ? $"{v.Value:0} {unit}" : "--";
}
