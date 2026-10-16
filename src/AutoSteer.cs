using System;
using System.Reflection;
using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// Path following: reads where the rider is relative to the game's generated course centreline, always (for
    /// telemetry: cross-track error, progress, path gradient), and when engaged overwrites the bike's steering input
    /// with a pure-pursuit command every physics step. Only steering is written; pedal and brake stay with the game.
    /// Steering is injected the way the game's own replay system and Sandbox's ReverseSteering do it: set Vehicle.swebLyg
    /// (steer -1..1) after VehicleController.FixedUpdate has written it (Harmony postfix, see AutoSteerPatch).
    /// </summary>
    internal sealed class AutoSteer
    {
        public readonly PathTrack Track = new PathTrack();
        public EndTurn Turn;                // descent U-turn controller (fitness mode); null = plain auto-steer
        public bool SuppressEndWarn;        // set by the U-turn controller: it replaces the end-of-path warning
        public bool Engaged;
        public bool EndReached;
        public int Dir = 1;                 // +1 = travelling toward increasing path index
        public int Idx = -1;
        public bool OnPath;                 // within MaxOffPath of the centreline
        public float DistToPath = float.NaN;
        public float XTrack = float.NaN;    // signed lateral offset, + = rider is to the RIGHT of the path (in travel direction)
        public float AlphaDeg, Cmd, Lookahead, DeltaDeg, WheelAngleDeg, Progress = float.NaN;
        public float PathGradPct = float.NaN;
        public float EndDist = float.NaN;   // metres to the end of the path in the direction of travel
        public float PastEnd = float.NaN;   // metres beyond the end (descent: beyond the game's finish trigger; climb: beyond the start arch)
        public float WarnAt = 35f;          // current warning distance (grows with speed)
        private bool _endWarned;
        public float LineOff = float.NaN;   // signed offset of the racing line from the centre at the rider (+ = right of travel)
        public readonly float[] PathAheadPct = { float.NaN, float.NaN, float.NaN, float.NaN };

        private const float MaxOffPath = 25f;        // engage refused / auto-disengage beyond this
        private const float SlewPerSec = 8f;         // command slew limit (full scale per 0.125 s)
        private PropertyInfo _steer, _lean;
        private bool _propsChecked, _propsOk;
        private float _wheelbase = 1.1f;
        private float _lastCmd;

        public void Reset() { if (Turn != null) Turn.Reset(); Track.Reset(); Disengage("reset"); Idx = -1; OnPath = false; XTrack = float.NaN; Progress = float.NaN; }

        /// <summary>Overrides this step's steering command (used by the U-turn controller) and keeps the slew limiter in step.</summary>
        public void ForceCmd(float c) { Cmd = c; _lastCmd = c; }

        /// <summary>Reverse the direction of travel after a U-turn (pure pursuit then follows the path the other way).</summary>
        public void FlipDirection() { Dir = -Dir; EndReached = false; _endWarned = false; _lastCmd = Cmd; }

        public bool CanSteer()
        {
            if (_propsChecked) return _propsOk;
            _propsChecked = true;
            _steer = typeof(Vehicle).GetProperty("swebLyg", BindingFlags.Public | BindingFlags.Instance);
            _lean = typeof(Vehicle).GetProperty("c{v}lhG", BindingFlags.Public | BindingFlags.Instance);
            _propsOk = (object)_steer != null && _steer.CanWrite;
            Log.Info("AutoSteer: steer property 'swebLyg' " + ((object)_steer != null ? "found" : "NOT FOUND")
                     + ", lean/axis property 'c{v}lhG' " + ((object)_lean != null ? "found" : "not found (steer only)"));
            return _propsOk;
        }

        /// <summary>True when <see cref="Toggle"/> would engage right now (used by the auto-engage, which must not spam warnings while it waits).</summary>
        public bool CanEngageNow(PlayerProbe probe)
        {
            return !Engaged && CanSteer() && Track.Loaded && probe.Player != null && probe.Rb != null && OnPath;
        }

        public void Toggle(PlayerProbe probe)
        {
            if (Engaged) { Disengage("key", Cue.Disengage); return; }
            if (!CanSteer()) { Log.Warn("AutoSteer: cannot engage, steering property missing"); return; }
            if (!Track.Loaded) { Log.Warn("AutoSteer: cannot engage, no generated path found in this scene"); return; }
            if (probe.Player == null || probe.Rb == null) { Log.Warn("AutoSteer: cannot engage, no player"); return; }

            var t = probe.Player.transform;
            Evaluate(t.position, probe.Rb.velocity, t.forward, 0f);
            if (!OnPath) { Log.Warn("AutoSteer: cannot engage, rider is " + DistToPath.ToString("F0") + " m from the path (limit " + MaxOffPath + " m)"); return; }

            var wf = t.Find("wheel_front"); var wb = t.Find("wheel_back");
            if (wf != null && wb != null) _wheelbase = Mathf.Clamp(Vector3.Distance(wf.position, wb.position), 0.6f, 2.5f);
            EndReached = false; _endWarned = false; _lastCmd = 0f; Engaged = true;
            Sfx.Play(Cue.Engage);
            Log.Info("AutoSteer: ENGAGED. path index " + Idx + "/" + Track.Points.Length + " (" + Progress.ToString("F0") + " of " + Track.Length.ToString("F0")
                     + " m), direction " + (Dir > 0 ? "toward end" : "toward start") + ", cross-track " + XTrack.ToString("F1") + " m, wheelbase " + _wheelbase.ToString("F2") + " m");
        }

        public void Disengage(string why, Cue? cue = null)
        {
            if (!Engaged) return;
            Engaged = false; Cmd = 0f;
            Log.Info("AutoSteer: disengaged (" + why + ")");
            if (cue.HasValue) Sfx.Play(cue.Value);
        }

        /// <summary>Called every physics step by the Harmony postfix while engaged.</summary>
        public void Step(Vehicle v, Rigidbody rb, Transform player, float dt)
        {
            if (!Track.Loaded) return;
            WheelAngleDeg = v.steeringAmount;        // measured front-wheel angle (degrees; real, unobfuscated name)
            Evaluate(player.position, rb.velocity, player.forward, dt);
            if (!Engaged) return;                                    // Evaluate may have handed control back (end of path)
            if (Turn != null) { Turn.Update(this, rb, player, dt); if (!Engaged) return; }   // may override Cmd (U-turn) or hand control back
            if (!OnPath) { Disengage("left the path (" + DistToPath.ToString("F0") + " m away)", Cue.HandOver); return; }
            try
            {
                _steer.SetValue(v, Cmd, null);
                if ((object)_lean != null)
                {
                    // The property is the R-stick slide axis. Add a slide proportional to steering (gain 0 disables), but never
                    // override the human: if their stick is pushed harder than ours, leave their value.
                    float ours = Cmd * Config.AsSlideGain, human = (float)_lean.GetValue(v, null);
                    _lean.SetValue(v, Mathf.Abs(human) > Mathf.Abs(ours) ? human : ours, null);
                }
            }
            catch (Exception ex) { Log.ErrorLimited("autosteer-write", "AutoSteer write", ex); Disengage("write failed", Cue.HandOver); }
        }

        /// <summary>Updates path-relative state; when engaged also computes the steering command.</summary>
        public void Evaluate(Vector3 pos, Vector3 vel, Vector3 forward, float dt)
        {
            if (!Track.Loaded) { OnPath = false; return; }
            float dist;
            Idx = Track.Nearest(pos, Idx, out dist);
            DistToPath = dist;
            OnPath = dist <= MaxOffPath;
            var pts = Track.Points;
            var p = pts[Idx];
            Vector3 tan = Track.Tangent(Idx);                       // toward increasing index
            Vector3 hv = new Vector3(vel.x, 0f, vel.z);
            Vector3 fwd = new Vector3(forward.x, 0f, forward.z); if (fwd.sqrMagnitude < 1e-4f) fwd = tan; fwd.Normalize();
            float speed = hv.magnitude;

            if (!Engaged)
            {
                Vector3 h = speed > 1.5f ? hv : fwd;
                Dir = Vector3.Dot(h, tan) >= 0f ? 1 : -1;            // direction estimate for telemetry; locked while engaged
            }

            // Signed lateral offset from the centreline, + = right of the direction of travel.
            Vector3 tdir = tan * Dir;
            float dx = pos.x - p.x, dz = pos.z - p.z;
            XTrack = dx * tdir.z - dz * tdir.x;
            Progress = Track.Cum[Idx];
            float endS = Track.FinishS >= 0f ? Track.FinishS : Track.Length;   // descent: the game takes over at its finish trigger
            EndDist = Dir > 0 ? endS - Progress : Progress;
            { Vector3 endPt = Dir > 0 ? pts[pts.Length - 1] : pts[0]; Vector3 outDir = Dir > 0 ? Track.EndTangent : -Track.StartTangent; PastEnd = Dir > 0 ? Progress - endS : (pos.x - endPt.x) * outDir.x + (pos.z - endPt.z) * outDir.z; }
            if (Track.Line != null) { float lx = Track.Line[Idx].x - p.x, lz = Track.Line[Idx].z - p.z; LineOff = lx * tdir.z - lz * tdir.x; }

            // Path gradient along the direction of travel (exact, from the course points; % , + = uphill).
            float s0 = Progress, y0 = p.y;
            PathGradPct = Grade(s0 - Dir * 3f, s0 + Dir * 3f);
            for (int i = 0; i < PathAheadPct.Length; i++)
            {
                float d = i == 0 ? 5f : i == 1 ? 10f : i == 2 ? 20f : 30f;
                float sd = Mathf.Clamp(s0 + Dir * d, 0f, Track.Length);
                float run = Mathf.Abs(sd - s0);
                PathAheadPct[i] = run > 0.5f ? (Track.At(sd).y - y0) / run * 100f : float.NaN;
            }

            if (!Engaged) return;

            // End of the path: the start/finish arch sits exactly on the end point, facing along the path. Warn ahead of it,
            // keep steering through it (the racing line is pinned to the centre there and extends straight beyond the end).
            WarnAt = Mathf.Max(Config.AsEndWarnM, Config.AsEndWarnSeconds * speed);
            // No automatic disengage at the finish/start line: the auto-steer stays in whatever state the rider left it in.
            // (It still hands back if the rider ends up more than 25 m from the path.) Re-arm the warning for the next pass.
            if (_endWarned && EndDist > WarnAt + 20f) _endWarned = false;
            if (!_endWarned && EndDist <= WarnAt && !SuppressEndWarn)
            {
                _endWarned = true;
                Log.Info("AutoSteer: " + EndDist.ToString("F0") + " m to the " + (Dir > 0 ? "finish trigger, where the game takes over" : "start arch") + "; auto-steer stays as you left it");
                Sfx.Play(Cue.Warn);
            }
            EndReached = EndDist < 0.5f;

            // Pure pursuit toward a point 'Lookahead' metres further along the path.
            Lookahead = Mathf.Clamp(Config.AsLookaheadBase + Config.AsLookaheadPerMs * speed, Config.AsLookaheadMin, Config.AsLookaheadMax);
            Vector3 target = Track.LineAt(Progress + Dir * Lookahead);   // racing line (not the raw centre); extends straight past the ends
            Vector3 to = new Vector3(target.x - pos.x, 0f, target.z - pos.z);
            float ld = to.magnitude;
            if (ld < 0.5f) { Cmd = Slew(0f, dt); return; }
            float alpha = Vector3.Angle(fwd, to) * Mathf.Deg2Rad;
            if (Vector3.Cross(fwd, to).y < 0f) alpha = -alpha;      // + = target is to the RIGHT of the bike's heading
            AlphaDeg = alpha * Mathf.Rad2Deg;

            float kappa = 2f * Mathf.Sin(alpha) / ld;                // pure-pursuit curvature
            float fwdSpeed = Mathf.Abs(Vector3.Dot(hv, fwd));
            // Measured (steer2 ride): yaw rate = ratio * v * tan(wheel) / wheelbase, ratio ~1.0 up to ~54 km/h, ~1.2-1.7 above 65 km/h.
            float ratio = Mathf.Lerp(1f, 1.25f, Mathf.InverseLerp(15f, 22f, fwdSpeed));
            float delta = Mathf.Atan(_wheelbase * kappa / ratio) * Mathf.Rad2Deg;   // front-wheel angle needed (deg)
            DeltaDeg = delta;
            // Feed-forward: command that would give 'delta' if the table below were exact. Feedback: push harder (or ease off)
            // by the error against the MEASURED wheel angle, so a table error can never cause bang-bang steering.
            float boosted = delta + Config.AsFeedback * (delta - WheelAngleDeg);
            float cmd = Mathf.Clamp(boosted / MaxAngleDeg(fwdSpeed), -1f, 1f);
            if (Config.AsInvert) cmd = -cmd;
            Cmd = Slew(cmd, dt);
        }

        // Wheel angle reached at full command (deg) vs speed (km/h), from the p95 of measured |steeringAmount| in a 100 s
        // ride (a lower bound where the command rarely reached 1, hence the feedback term above). The decompiled defaults
        // (5 deg, collapsing with speed) were wrong: prefabs override them.
        private static readonly float[] SpeedKmh = { 0f, 10f, 20f, 30f, 40f, 50f, 70f, 90f, 110f };
        private static readonly float[] MaxAngle = { 30f, 27f, 21f, 9.5f, 6.0f, 4.6f, 3.7f, 3.4f, 3.3f };

        private static float MaxAngleDeg(float speedMs)
        {
            float kmh = speedMs * 3.6f;
            if (kmh <= SpeedKmh[0]) return MaxAngle[0];
            for (int i = 1; i < SpeedKmh.Length; i++)
                if (kmh <= SpeedKmh[i]) return Mathf.Lerp(MaxAngle[i - 1], MaxAngle[i], (kmh - SpeedKmh[i - 1]) / (SpeedKmh[i] - SpeedKmh[i - 1]));
            return MaxAngle[MaxAngle.Length - 1];
        }
        private float Grade(float sA, float sB)
        {
            sA = Mathf.Clamp(sA, 0f, Track.Length); sB = Mathf.Clamp(sB, 0f, Track.Length);
            float run = Mathf.Abs(sB - sA);
            if (run < 0.5f) return float.NaN;
            // grade in the direction of travel: from the 'behind' point to the 'ahead' point
            float lo = Dir > 0 ? Mathf.Min(sA, sB) : Mathf.Max(sA, sB);
            float hi = Dir > 0 ? Mathf.Max(sA, sB) : Mathf.Min(sA, sB);
            return (Track.At(hi).y - Track.At(lo).y) / run * 100f;
        }

        private float Slew(float target, float dt)
        {
            float c = dt > 0f ? Mathf.MoveTowards(_lastCmd, target, SlewPerSec * dt) : target;
            _lastCmd = c;
            return c;
        }
    }

    /// <summary>Harmony postfix on VehicleController.FixedUpdate (private): runs right after the game wrote the player's inputs.</summary>
    public static class AutoSteerPatch
    {
        internal static AutoSteer Instance;
        internal static PlayerProbe Probe;

        internal static PedalTest PedalTest;
        internal static FitnessMode Fitness;

        public static void Postfix(VehicleController __instance)
        {
            var a = Instance;
            var pt = PedalTest;
            bool steer = a != null && a.Engaged;
            bool pedal = pt != null && pt.Active;
            var fm = Fitness;
            bool fit = fm != null && fm.Active;
            if (!steer && !pedal && !fit) return;
            var p = Probe;
            if (p == null || p.Vehicle == null || p.Rb == null || p.Player == null) return;
            if (!object.ReferenceEquals(__instance.gameObject, p.Player)) return;   // only the local player's controller
            if (steer)
            {
                try { a.Step(p.Vehicle, p.Rb, p.Player.transform, Time.fixedDeltaTime); }
                catch (Exception ex) { Log.ErrorLimited("autosteer-step", "AutoSteer.Step", ex); a.Disengage("error", Cue.HandOver); }
            }
            if (pedal)
            {
                try { pt.Apply(p.Vehicle, p.Rb.velocity.magnitude * 3.6f, Time.fixedDeltaTime); }
                catch (Exception ex) { Log.ErrorLimited("pedaltest-step", "PedalTest.Apply", ex); pt.Stop("error"); }
            }
            if (fit)
            {
                try { fm.Step(p.Vehicle, p.Rb, p.Player.transform, Time.fixedDeltaTime); }
                catch (Exception ex) { Log.ErrorLimited("fitness-step", "FitnessMode.Step", ex); }
            }
        }

        internal static void Install(AutoSteer instance, PlayerProbe probe)
        {
            Instance = instance; Probe = probe;
            var harmony = new HarmonyLib.Harmony("freeturbo.descenders.autosteer");
            MethodInfo target = typeof(VehicleController).GetMethod("FixedUpdate", BindingFlags.NonPublic | BindingFlags.Instance);
            if ((object)target == null) { Log.Error("AutoSteer: VehicleController.FixedUpdate not found; autosteer unavailable"); return; }
            MethodInfo post = typeof(AutoSteerPatch).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static);
            harmony.Patch(target, postfix: new HarmonyLib.HarmonyMethod(post));
            Log.Info("AutoSteer: Harmony postfix installed on VehicleController.FixedUpdate");
        }
    }
}
