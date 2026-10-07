using System;
using System.Reflection;
using UnityEngine;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// Fitness mode: real pedalling power (from the bridge's `trainer` message) drives the bike's speed, like an indoor-cycling app.
    /// The mod integrates its own speed from power, total mass, gradient, rolling resistance and air drag (real metres, real
    /// g = 9.81) and, every physics step in the VehicleController.FixedUpdate postfix, eases the rigidbody's speed to it
    /// (direction untouched, so the game keeps steering, grip, bumps, crashes). See docs/FORCE_MODEL.md. The integrator itself is the shared,
    /// Unity-free <see cref="SpeedModel"/>; this class is the Descenders glue around it.
    /// It yields (and re-syncs its model to the real speed, so nothing jumps) when the rider brakes or the game does,
    /// in the air, while the pad trigger is squeezed, or while the T2 pedal test runs. Trainer data lost for TrainerTimeoutSeconds:
    /// watts fade to zero (the bike still obeys physics, never brakes); after FitnessOffSeconds the mode switches off.
    /// </summary>
    internal sealed class FitnessMode
    {
        // ---- status (telemetry / HUD)
        public bool Active;                 // trainer data seen within FitnessOffSeconds and the rider profile known
        public bool WaitingForRider;        // trainer data arriving but no `rider` message from the bridge yet
        public bool FailSafe;               // data late (or trainer reports not connected): watts fading/zero
        public bool PadOverride;            // pad trigger above the dead zone: the pad has the pedal
        public string State = "off";        // off | governing | blending | pad | brake | air | test
        public float RawW, SmoothW, Avg3W, Cadence, HeartRate, AgeSec = -1f;
        public bool HasCadence;             // the latest trainer message carried a cadence
        public float VModel;                // the model's speed (m/s); tracks the real speed whenever not governing
        public float GradeSin;              // slope used by the model (sin of the angle, + = uphill along travel)
        public float PadRaw = float.NaN;    // pedal value the game wrote (the pad) before we touched it
        public float Weight;                // 0..1 how much of the model correction is applied (pad blend)
        public int GovernedSteps, YieldedSteps;
        public int RollbackSteps;           // physics steps spent arresting a backward roll on a climb

        // ---- per-frame inputs from OnUpdate (the postfix runs on the physics step, these are the latest frame values)
        public bool Grounded = true;
        public float Brake;
        public float GradePct = float.NaN, PathGradPct = float.NaN;
        public PedalTest PedalTest;
        public float SpeedCapMs = float.PositiveInfinity;   // set by the U-turn controller: the model may not exceed this (auto-braking through the model)
        public float TurnAssistMs;                          // > 0 while turning: keep at least this much forward speed even at zero watts
        public bool Governing { get { return Active && (State == "governing" || State == "blending"); } }

        // The game pulls the bike down a slope with its own gravity (17.5, applied manually, docs/GAME_ANALYSIS.md), which the mod's speed writes
        // must out-run: MaxAccelMs2 is the acceleration ON TOP of that pull. Without it the bike could not pull away on 21-24 % pitches
        // (17.5 x sin = 3.7-4.1 m/s^2 against the 4 m/s^2 limit) and, if it slipped back, the mod accelerated it backwards down the hill.
        private const float GameGravityMs2 = 17.5f;

        private float _padWeight = 1f;
        private bool _connected = true;
        private MethodInfo _getPedal, _setPedal;
        private bool _resolved;
        private bool _loggedFirst;

        public void Reset()
        {
            VModel = 0f; SmoothW = 0f; Avg3W = 0f; _padWeight = 1f; PadOverride = false; State = Active ? "governing" : "off";
            GovernedSteps = YieldedSteps = RollbackSteps = 0; _loggedFirst = false;
        }

        /// <summary>From OnUpdate: refresh the latest trainer state and the mode/failsafe flags.</summary>
        public void UpdateTrainer(BridgeLink bridge)
        {
            TrainerSample s;
            if (bridge != null && bridge.TryGetTrainer(out s))
            {
                AgeSec = (float)((DateTime.UtcNow.Ticks - s.RecvTicks) / (double)TimeSpan.TicksPerSecond);
                RawW = (float)Math.Max(0.0, s.PowerW);
                Cadence = (float)s.CadenceRpm; HasCadence = s.HasCadence;
                HeartRate = (float)s.HeartRateBpm;
                _connected = s.Connected;
            }
            else AgeSec = -1f;
            bool was = Active;
            bool fresh = AgeSec >= 0f && AgeSec <= Config.FitnessOffSeconds;
            // no rider profile from the bridge yet: no mass to model, so wait rather than guess one
            WaitingForRider = RiderState.Waiting(Config.FitnessEnabled, fresh, Config.Rider.Known);
            if (WaitingForRider && Config.Rider.TakeWaitLog()) Log.Info("Fitness: trainer data arriving, waiting for the rider profile from the bridge before starting");
            Active = RiderState.FitnessReady(Config.FitnessEnabled, fresh, Config.Rider.Known);
            FailSafe = Active && (!_connected || AgeSec > Config.TrainerTimeoutSeconds);
            if (Active != was) Log.Info("Fitness: mode " + (Active ? "ON (trainer data arriving)" : "OFF (no trainer data for " + Config.FitnessOffSeconds.ToString("F0") + " s), pad and game physics only"));
            if (!Active) { State = "off"; SmoothW = 0f; Avg3W = 0f; }
        }

        /// <summary>Harmony postfix, once per physics step for the local player (after the game wrote its inputs).</summary>
        public void Step(Vehicle v, Rigidbody rb, Transform t, float dt)
        {
            if (!Active || dt <= 0f) return;
            if (!_resolved) Resolve();
            PadRaw = ReadPedal(v);
            Vector3 vel = rb.velocity;
            float s = vel.magnitude;

            // watts: fade to zero when the data is late; smoothed so a power spike does not jerk the bike
            float fade = !_connected ? 0f : (AgeSec <= Config.TrainerTimeoutSeconds ? 1f : Mathf.Clamp01(1f - (AgeSec - Config.TrainerTimeoutSeconds) / Mathf.Max(0.1f, Config.TrainerFadeSeconds)));
            float target = RawW * fade;
            SmoothW += (1f - Mathf.Exp(-dt / Mathf.Max(0.05f, Config.WattSmoothSeconds))) * (target - SmoothW);
            Avg3W += (1f - Mathf.Exp(-dt / 3f)) * (target - Avg3W);

            // pad trigger: past the dead zone the pad has the pedal (and the game's own physics); released, blend back over PadBlendSeconds
            bool padDown = !float.IsNaN(PadRaw) && PadRaw > Config.PadDeadZone;
            PadOverride = padDown;
            if (padDown) _padWeight = 0f;
            else _padWeight = Mathf.MoveTowards(_padWeight, 1f, dt / Mathf.Max(0.05f, Config.PadBlendSeconds));

            string yield = null;
            if (PedalTest != null && PedalTest.Active) yield = "test";
            else if (!Grounded) yield = "air";
            else if (Brake > 0.05f) yield = "brake";       // the rider's brake, or the game's finish takeover: never fight it
            else if (_padWeight <= 0f) yield = "pad";
            if (yield != null)
            {
                State = yield; Weight = 0f; VModel = s; YieldedSteps++;
                return;
            }

            // ---- rolling backwards on a climb (facing uphill, moving downhill): an indoor-cycling app never rolls you backwards. Bring it to a stop at the usual
            // deceleration limit plus the slope's own pull, then the normal path pulls away. Not during the U-turns (their pivots slide sideways).
            Vector3 fwd = t.forward;
            if (TurnAssistMs <= 0f && fwd.y > 0.03f && s > 0.05f && Vector3.Dot(vel, fwd) < -0.15f)
            {
                float back = Mathf.MoveTowards(s, 0f, (Config.MaxDecelMs2 + GameGravityMs2 * fwd.y) * dt);
                rb.velocity = vel / s * (s + (back - s) * _padWeight);
                VModel = 0f; Weight = _padWeight; State = _padWeight >= 0.999f ? "governing" : "blending";
                GovernedSteps++; RollbackSteps++;
                if (RollbackSteps == 1) Log.Info("Fitness: rolling backwards on a climb (" + (s * 3.6f).ToString("F1") + " km/h, slope sin " + fwd.y.ToString("F2") + "): arresting");
                if (_padWeight >= 0.999f) WritePedal(v, 0f);
                return;
            }

            // ---- physics (SI units, real metres): the shared integrator (src\SpeedModel.cs), bike CdA / Crr from the bridge's `rider` message
            float mass = Config.Rider.MassKg;
            float sin = SlopeSin(vel, s);
            GradeSin = sin;
            float pw = SmoothW * (1f + (Config.DescentPowerFactor - 1f) * Mathf.Clamp01(-sin / 0.03f));   // descents: watts count DescentPowerFactor times over
            // capped at the real speed + MaxLeadMs: do not run away from what the game lets the bike do
            VModel = SpeedModel.Step(VModel, pw, sin, mass, Config.Rider.CdA, Config.Rider.Crr, dt, s + Config.MaxLeadMs);
            if (TurnAssistMs > 0f && VModel < TurnAssistMs) VModel = Mathf.Min(TurnAssistMs, s + Config.MaxLeadMs);
            if (VModel > SpeedCapMs) VModel = SpeedCapMs;

            // ---- apply: ease the real speed to the model's, keep the game's direction
            Vector3 dir = s > 0.15f ? vel / s : t.forward.normalized;
            float pull = GameGravityMs2 * Mathf.Max(0f, dir.y);                     // the game's own slope pull against the motion (0 on the flat and downhill)
            float lim = (VModel > s ? Config.MaxAccelMs2 + pull : Config.MaxDecelMs2) * dt;
            float sNew = Mathf.MoveTowards(s, VModel, lim);
            float sWrite = s + (sNew - s) * _padWeight;
            rb.velocity = dir * sWrite;
            Weight = _padWeight;
            State = _padWeight >= 0.999f ? "governing" : "blending";
            GovernedSteps++;
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Log.Info("Fitness: governing started: " + SmoothW.ToString("F0") + " W smoothed, " + (s * 3.6f).ToString("F1") + " km/h, slope sin " + sin.ToString("F3") + ", mass " + mass.ToString("F0") + " kg, " + Config.Rider.BikeType + " bike (CdA " + Config.Rider.CdA.ToString("0.###") + ", Crr " + Config.Rider.Crr.ToString("0.####") + ")");
            }

            // keep the legs moving without asking the game for real push (our speed write overrides its push anyway)
            if (_padWeight >= 0.999f) WritePedal(v, (SmoothW > 10f && sWrite > 0.5f) ? Config.PedalAnim : 0f);
        }

        /// <summary>The raw road gradient under the bike, as a fraction (0.13 = 13 %), NaN if unknown: telemetry `fitness.roadGradient`, which the bridge
        /// scales by its trainerDifficulty for the trainer's resistance. The physics scales it separately (ClimbPhysicsScale, see SlopeSin).</summary>
        public float RoadGradient
        {
            get { float pct = !float.IsNaN(GradePct) ? GradePct : PathGradPct; return float.IsNaN(pct) ? float.NaN : pct / 100f; }
        }

        private float SlopeSin(Vector3 vel, float speed)
        {
            float pct = !float.IsNaN(GradePct) ? GradePct : PathGradPct;
            if (!float.IsNaN(pct)) { float gr = pct / 100f * Config.ScaleFor(pct); return Mathf.Clamp(gr / Mathf.Sqrt(1f + gr * gr), -0.7f, 0.7f); }
            return speed > 1f ? Mathf.Clamp(vel.y / speed * Config.ScaleFor(vel.y), -0.7f, 0.7f) : 0f;
        }

        private void Resolve()
        {
            _resolved = true;
            PropertyInfo p = typeof(Vehicle).GetProperty("j[fCiJt", BindingFlags.Public | BindingFlags.Instance);
            if ((object)p != null) { _getPedal = p.GetGetMethod(true); _setPedal = p.GetSetMethod(true); }
            Log.Info("Fitness: pedal property " + ((object)p == null ? "NOT FOUND (no pad override, no leg animation)" : "found, getter " + ((object)_getPedal != null) + ", setter " + ((object)_setPedal != null)));
        }

        private float ReadPedal(Vehicle v)
        {
            if ((object)_getPedal == null) return float.NaN;
            try { return (float)_getPedal.Invoke(v, null); }
            catch (Exception) { return float.NaN; }
        }

        private void WritePedal(Vehicle v, float value)
        {
            if ((object)_setPedal == null) return;
            try { _setPedal.Invoke(v, new object[] { value }); }
            catch (Exception ex) { Log.ErrorLimited("fitness-pedal", "FitnessMode.WritePedal", ex); }
        }
    }
}
