#nullable disable
using System;
using System.Collections.Generic;

// Pure logic (no Unity, no logging): also compiled into the bridge (bridge/DescendersBridge.csproj links this file), whose Session writes
// the `session` message these classes read (docs/IPC.md), so `selftest` checks the two ends against each other.
namespace DescendersCyclingPOC
{
    /// <summary>One event of the bridge's `session` message: step | countdown | text | complete. <see cref="Seq"/> is monotonic per bridge run.</summary>
    internal sealed class SessionEvent
    {
        public long Seq;
        public string Kind = "";
        public string Text = "";   // text: the .zwo message
        public int Left;           // countdown: whole seconds left in the step
        // complete: the finished workout (the bridge records plan progress itself; the mod plays the sound / confetti)
        public string Name = "", Tag = "";
        public float TotalS, ElapsedS, AvgW, BiasPct, FtpW;
        public bool Skipped;
    }

    /// <summary>The running workout as the bridge reports it: enough for the workout panel (current step + a capped list of upcoming ones).</summary>
    internal sealed class SessionWorkout
    {
        public string Name = "", Tag = "", Label = "";
        public string PlanTitle = "", PlanSessionLabel = "";   // a plan session: "FTP Builder", "W2 D3 (9/22)"; "" otherwise
        public bool Started, Paused, FreeRide;
        /// <summary>On hold (V2.22): the rider stepped out with the mode button, the mode is plain freeride / manual ERG until it comes back round.</summary>
        public bool Held;
        public float ElapsedS, TotalS, StepRemainingS, BiasPct;
        public int Step, Steps;          // Step is 1-based
        public WorkoutStep Current;      // Kind / DurationS / StartW / EndW (planned watts, before bias); StartS is 0
        public readonly List<WorkoutStep> Upcoming = new List<WorkoutStep>();

        public float BiasFactor { get { return 1f + BiasPct / 100f; } }
        public float TotalRemainingS { get { return Math.Max(0f, TotalS - ElapsedS); } }
        /// <summary>Steps after the ones listed in <see cref="Upcoming"/>.</summary>
        public int MoreAfterUpcoming { get { return Math.Max(0, Steps - Step - Upcoming.Count); } }
    }

    /// <summary>The latest {"type":"session"} message (docs/IPC.md): mode, the target the bridge holds, the running workout, recent events.</summary>
    internal sealed class SessionView
    {
        public string Mode = "freeride";
        public float TargetW = float.NaN;   // NaN unless Mode is "erg"
        public string Note = "";
        public SessionWorkout Workout;      // null = no workout
        public long EventSeq;
        public readonly List<SessionEvent> Events = new List<SessionEvent>();

        public bool Erg { get { return Mode == "erg"; } }

        /// <summary>Text for the HUD's mode row.</summary>
        public string ModeLabel
        {
            get
            {
                if (Erg) return "ERG " + (float.IsNaN(TargetW) ? "?" : TargetW.ToString("F0")) + " W";
                return Workout != null && !Workout.Held && Workout.FreeRide ? "FREERIDE (workout)" : "FREERIDE";
            }
        }

        /// <summary>Reads a parsed message (JsonReader tree). Missing fields take their defaults; never throws on a well-formed object.</summary>
        public static SessionView Parse(Dictionary<string, object> root)
        {
            var v = new SessionView();
            v.Mode = Str(root, "mode", "freeride");
            v.TargetW = Num(root, "targetW", float.NaN);
            v.Note = Str(root, "note", "");
            v.EventSeq = Long(root, "eventSeq");
            object o;
            var wo = root.TryGetValue("workout", out o) ? o as Dictionary<string, object> : null;
            if (wo != null)
            {
                var w = new SessionWorkout
                {
                    Name = Str(wo, "name", ""), Tag = Str(wo, "tag", ""), Label = Str(wo, "label", ""),
                    Started = Bool(wo, "started"), Paused = Bool(wo, "paused"), FreeRide = Bool(wo, "freeRide"), Held = Bool(wo, "held"),
                    ElapsedS = Num(wo, "elapsedS", 0f), TotalS = Num(wo, "totalS", 0f), StepRemainingS = Num(wo, "stepRemainingS", 0f), BiasPct = Num(wo, "biasPct", 0f),
                    Step = (int)Num(wo, "step", 1f), Steps = (int)Num(wo, "steps", 1f),
                };
                var plan = wo.TryGetValue("plan", out o) ? o as Dictionary<string, object> : null;
                if (plan != null) { w.PlanTitle = Str(plan, "title", ""); w.PlanSessionLabel = Str(plan, "sessionLabel", ""); }
                w.Current = Row(wo.TryGetValue("current", out o) ? o as Dictionary<string, object> : null) ?? new WorkoutStep { Kind = w.FreeRide ? "freeride" : "steady" };
                var up = wo.TryGetValue("upcoming", out o) ? o as List<object> : null;
                if (up != null)
                    foreach (object r in up) { WorkoutStep s = Row(r as Dictionary<string, object>); if (s != null) w.Upcoming.Add(s); }
                v.Workout = w;
            }
            var ev = root.TryGetValue("events", out o) ? o as List<object> : null;
            if (ev != null)
                foreach (object x in ev)
                {
                    var e = x as Dictionary<string, object>;
                    if (e == null) continue;
                    v.Events.Add(new SessionEvent
                    {
                        Seq = Long(e, "seq"), Kind = Str(e, "kind", ""), Text = Str(e, "text", ""), Left = (int)Num(e, "left", 0f),
                        Name = Str(e, "name", ""), Tag = Str(e, "tag", ""), TotalS = Num(e, "totalS", 0f), ElapsedS = Num(e, "elapsedS", 0f),
                        AvgW = Num(e, "avgW", 0f), BiasPct = Num(e, "biasPct", 0f), FtpW = Num(e, "ftpW", 0f), Skipped = Bool(e, "skipped"),
                    });
                }
            return v;
        }

        private static WorkoutStep Row(Dictionary<string, object> r)
        {
            if (r == null) return null;
            bool free = Bool(r, "freeRide");
            return new WorkoutStep { Kind = free ? "freeride" : Str(r, "kind", "steady"), DurationS = Num(r, "durationS", 0f), StartW = Num(r, "startW", 0f), EndW = Num(r, "endW", 0f) };
        }

        private static string Str(Dictionary<string, object> d, string k, string dflt) { object o; return d.TryGetValue(k, out o) && o is string ? (string)o : dflt; }
        private static float Num(Dictionary<string, object> d, string k, float dflt) { object o; return d.TryGetValue(k, out o) && o is double ? (float)(double)o : dflt; }
        private static long Long(Dictionary<string, object> d, string k) { object o; return d.TryGetValue(k, out o) && o is double ? (long)(double)o : 0L; }
        private static bool Bool(Dictionary<string, object> d, string k) { object o; return d.TryGetValue(k, out o) && o is bool && (bool)o; }
    }

    /// <summary>
    /// Hands each session event to the mod once (sound, text, confetti), however many `session` messages repeat it and
    /// even when some are dropped: the bridge resends its last few events with every message and the mod acts on each seq once.
    /// The first message after a (re)connect only primes it, so an old event is never replayed; a seq going backwards (bridge restarted) re-primes.
    /// </summary>
    internal sealed class SessionEventFilter
    {
        private long _last;
        private bool _primed;

        public void Reset() { _primed = false; }

        public void Take(SessionView v, List<SessionEvent> into)
        {
            if (!_primed || v.EventSeq < _last) { _primed = true; _last = v.EventSeq; return; }
            // events arrive oldest first; sort anyway so a reordered list still plays in order
            var fresh = new List<SessionEvent>();
            foreach (SessionEvent e in v.Events) if (e.Seq > _last && e.Seq <= v.EventSeq) fresh.Add(e);
            fresh.Sort(delegate(SessionEvent a, SessionEvent b) { return a.Seq.CompareTo(b.Seq); });
            into.AddRange(fresh);
            _last = v.EventSeq;
        }
    }
}
