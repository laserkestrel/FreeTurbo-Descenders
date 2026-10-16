using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// U-turns at both ends of the course (fitness mode only; auto-braking IS wanted here). Bottom (travelling toward the finish): the game
    /// ends the level when the bike reaches the finish trigger, so a speed limit along the path, applied through FitnessMode's model (never
    /// the game's brake), slows the bike to UTurnSpeedMs by the turn start point, gently (UTurnBrakeDecelMs2, below the 5 m/s^2 that throws
    /// the rider), with a harder absolute limit that stops it before the last metres. Top (climbing): the same limit toward the start
    /// cabin. At the turn start point, after the Ascender trigger for that end has fired, auto-steer goes to full lock until the bike
    /// points back along the path, then the auto-steer direction is flipped and pure pursuit carries on the other way, so the ride loops
    /// by itself. Space measured with the zone map: bottom flat and free +/-10 m from ~769 m to the finish trigger (~790 m); top open
    /// from ~6 m (cabin and roll-in ramp at path 5 m and below). If the rider takes over (brake, pad, air) or a turn fails, auto-steer
    /// hands control back.
    /// </summary>
    internal sealed class EndTurn
    {
        public string Phase = "idle";                 // idle | approach | turning
        public string End = "";                       // bottom | top (the end being handled)
        public float CapMs = float.PositiveInfinity;
        public int Turns;
        public float TurnSeconds;

        private readonly FitnessMode _fm;
        private int _finishHitsAtApproach, _startHitsAtApproach;
        private float _turnT;
        private float _turnHardS;                     // hard-stop line while turning: at least TurnRoomM behind where the turn started
        private const float TurnRoomM = 4f;           // the start trigger can fire right at the configured line (highlands: start trigger at path 0 m, fired at 7.8 m)

        public EndTurn(FitnessMode fm) { _fm = fm; }

        public void Reset() { Phase = "idle"; End = ""; _turnT = 0f; Release(null); }

        private void Release(AutoSteer a)
        {
            CapMs = float.PositiveInfinity;
            _fm.SpeedCapMs = float.PositiveInfinity;
            _fm.TurnAssistMs = 0f;
            if (a != null) a.SuppressEndWarn = false;
        }

        /// <summary>Once per physics step from AutoSteer.Step, after Evaluate and before the steering value is written.</summary>
        public void Update(AutoSteer a, Rigidbody rb, Transform player, float dt)
        {
            bool climbing = a.Dir < 0;
            bool can = Config.UTurnEnabled && _fm.Governing && a.OnPath && (climbing || a.Track.FinishS > 0f);
            if (!can)
            {
                if (Phase != "idle") Log.Warn("EndTurn: abandoned in phase " + Phase + " (fitness mode not governing, or off the path); control stays with auto-steer / the rider");
                Phase = "idle"; Release(a);
                return;
            }

            float s = a.Progress;
            // distances (m) still to go, measured in the direction of travel: to the turn start point and to the hard-stop line
            float sTurn, sHard, dTurn, dHard;
            if (climbing) { sTurn = Config.UTurnTopStartM; sHard = Config.UTurnTopHardStopM; dTurn = s - sTurn; dHard = s - sHard; }
            else { float fin = a.Track.FinishS; sTurn = fin - Config.UTurnStartBeforeFinishM; sHard = fin - Config.UTurnHardStopBeforeFinishM; dTurn = sTurn - s; dHard = sHard - s; }
            if (Phase == "turning") { sHard = _turnHardS; dHard = climbing ? s - sHard : sHard - s; }
            string end = climbing ? "top" : "bottom";
            float speed = rb.velocity.magnitude;
            bool eventFired = climbing ? AscenderEvents.StartHits > _startHitsAtApproach : AscenderEvents.FinishHits > _finishHitsAtApproach;

            if (dTurn > 250f)                                             // far from this end: nothing to do; remember the trigger counts
            {
                _finishHitsAtApproach = AscenderEvents.FinishHits; _startHitsAtApproach = AscenderEvents.StartHits;
                Phase = "idle"; Release(a);
                return;
            }
            a.SuppressEndWarn = true;                                     // the turn replaces the end-of-path warning

            float vT = Config.UTurnSpeedMs;
            float capHard = Mathf.Sqrt(2f * Config.UTurnHardDecelMs2 * Mathf.Max(0f, dHard));
            float capBrake = Mathf.Sqrt(vT * vT + 2f * Config.UTurnBrakeDecelMs2 * Mathf.Max(0f, dTurn));
            if (Phase == "idle")
            {
                Phase = "approach"; End = end;
                Log.Info("EndTurn: approach to the " + end + ", " + dTurn.ToString("F0") + " m from the turn point at path " + sTurn.ToString("F0") + " m, " + speed.ToString("F1") + " m/s");
            }

            if (Phase == "approach")
            {
                CapMs = Mathf.Min(capBrake, capHard);
                bool atPoint = climbing ? true : dTurn <= 0f;             // top: the Ascender start trigger itself marks the spot
                if (atPoint && eventFired && speed <= vT + 1f)
                {
                    Phase = "turning"; _turnT = 0f;
                    _turnHardS = climbing ? Mathf.Min(sHard, s - TurnRoomM) : Mathf.Max(sHard, s + TurnRoomM);
                    Log.Info("EndTurn: TURNING at the " + end + ", path " + s.ToString("F1") + " m, " + (speed * 3.6f).ToString("F1") + " km/h, full lock " + (Config.UTurnSide > 0 ? "right" : "left"));
                }
                else if (dHard <= 0f)
                {
                    Log.Warn("EndTurn: reached the hard-stop line at the " + end + " (path " + sHard.ToString("F0") + " m) without starting the turn (Ascender trigger " + eventFired + "); handing control back");
                    Fail(a);
                    return;
                }
            }

            if (Phase == "turning")
            {
                CapMs = Mathf.Min(vT, capHard);
                _turnT += dt; TurnSeconds = _turnT;
                _fm.TurnAssistMs = Config.UTurnAssistMs;                   // keep rolling so the bike can rotate even at zero watts
                a.ForceCmd(Config.UTurnSide > 0 ? 1f : -1f);
                Vector3 fwd = new Vector3(player.forward.x, 0f, player.forward.z);
                Vector3 tan = a.Track.Tangent(a.Idx) * a.Dir;                 // original direction of travel
                if (fwd.sqrMagnitude > 1e-4f && Vector3.Dot(fwd.normalized, tan) < -0.85f)
                {
                    Turns++;
                    Log.Info("EndTurn: " + end + " turn complete after " + _turnT.ToString("F1") + " s at path " + s.ToString("F1") + " m, " + (speed * 3.6f).ToString("F1") + " km/h; auto-steer now follows the path " + (climbing ? "down" : "back up"));
                    a.FlipDirection();
                    Phase = "idle"; Release(a);
                    return;
                }
                if (_turnT > Config.UTurnMaxSeconds || dHard < -1f)
                {
                    Log.Warn("EndTurn: " + end + " turn failed (" + _turnT.ToString("F1") + " s, path " + s.ToString("F1") + " m); handing control back");
                    Fail(a);
                    return;
                }
            }
            _fm.SpeedCapMs = CapMs;
        }

        private void Fail(AutoSteer a)
        {
            Phase = "idle"; Release(a);
            a.Disengage("u-turn failed", Cue.HandOver);
        }
    }
}
