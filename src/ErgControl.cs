using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// The game end of the training session. The bridge owns the ride mode (freeride | ERG), the manual ERG target, the
    /// running workout (bridge Session.cs, docs/REQUIREMENTS.md V2.1-V2.20) and the plan progress; workouts and training plans are chosen in the
    /// bridge window (its Workouts tab, bridge Menu.cs, docs/PLANS.md), not in the game (the in-game menu is gone). This class only
    /// reads the pad / keys and sends them as `intent` messages, keeps the latest `session` message for the HUD and the workout panel, and
    /// plays the sound / text / confetti of each new session event once.
    /// With no `session` for a few seconds the HUD shows FREERIDE (and "NO TRAINER BRIDGE" for a moment when a button is pressed): ERG and workouts need the bridge.
    /// Controls: hold RB and tap D-pad Up (the mode button: freeride -> ERG -> the loaded workout, V2.22) / Left / Right (target -/+, or workout bias -/+) / Down (skip a workout step), read straight
    /// from XInput (read-only), plus keyboard keys. Only while a ride is active (player found, game window focused).
    /// </summary>
    internal sealed class ErgControl
    {
        private const double SessionStaleS = 3.0;

        private BridgeLink _bridge;

        /// <summary>The latest fresh session from the bridge; null = none for SessionStaleS (no bridge: FREERIDE, no ERG, no workout).</summary>
        public SessionView Session;
        private readonly SessionEventFilter _filter = new SessionEventFilter();
        private readonly List<SessionEvent> _newEvents = new List<SessionEvent>();

        private string _note = "";
        private float _noteUntil;

        private ushort _prevButtons;
        private bool _padBroken;
        private const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008, RightShoulder = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }
        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint userIndex, out XInputState state);

        // "Workout complete" panel state (V2.18 item 11): shown for 5 s after the workout ends while the mode is already back to freeride.
        public string CompleteName = "";
        public float CompleteTotalS;
        private float _completeUntil;
        public bool ShowComplete { get { return Workout == null && Time.unscaledTime < _completeUntil; } }

        public bool HaveSession { get { return Session != null; } }
        /// <summary>The running workout as the bridge reports it (null = none).</summary>
        public SessionWorkout Workout { get { return Session != null ? Session.Workout : null; } }
        /// <summary>The workout the rider is in: null when none is loaded or it is on hold (V2.22: the panel still shows a held one, <see cref="Workout"/>).</summary>
        public SessionWorkout ActiveWorkout { get { SessionWorkout w = Workout; return w != null && !w.Held ? w : null; } }
        /// <summary>ERG requested (a workout counts, even in its free-ride steps): the HUD mode row is highlighted and RB + Left / Right / Down act.</summary>
        public bool ErgRequested { get { return Session != null && (Session.Erg || ActiveWorkout != null); } }
        /// <summary>"Plan - W1 D2 (3/12)" while the running workout is a plan session (from the bridge's session message), else "".</summary>
        public string PlanLine { get { return Workout != null && Workout.PlanTitle.Length > 0 ? Workout.PlanTitle + " - " + Workout.PlanSessionLabel : ""; } }

        /// <summary>A local note (no bridge) for 2 s, else the bridge's own note ("ERG 180 W", "PLAN RESET").</summary>
        public string Note { get { return Time.unscaledTime < _noteUntil ? _note : Session != null ? Session.Note : ""; } }

        /// <summary>Text for the HUD's mode row.</summary>
        public string ModeLabel { get { return Session != null ? Session.ModeLabel : "FREERIDE"; } }

        public void Tick(bool riding, BridgeLink bridge)
        {
            _bridge = bridge;
            UpdateSession();
            if (!riding || !Application.isFocused) { _prevButtons = 0; return; }

            if (Input.GetKeyDown(Config.ErgToggleKey)) Toggle("key");
            if (ErgRequested && Input.GetKeyDown(Config.ErgUpKey)) Adjust(1);
            if (ErgRequested && Input.GetKeyDown(Config.ErgDownKey)) Adjust(-1);
            if (ActiveWorkout != null && Input.GetKeyDown(Config.ErgSkipKey)) Skip();
            PollPad();
        }

        /// <summary>Take the latest session; play each new event once (SessionEventFilter). Stale: forget it, so the HUD says FREERIDE.</summary>
        private void UpdateSession()
        {
            SessionView v;
            if (_bridge == null || !_bridge.TryGetSession(SessionStaleS, out v))
            {
                if (Session != null) Log.Warn("Session: no session message from the bridge for " + SessionStaleS.ToString("F0") + " s: FREERIDE, ERG unavailable");
                Session = null;
                _filter.Reset();
                return;
            }
            if (object.ReferenceEquals(v, Session)) return;
            SessionView old = Session;
            Session = v;
            if (old == null) Log.Info("Session: bridge session received, " + v.ModeLabel + (v.Workout != null ? ", workout '" + v.Workout.Name + "'" : ""));
            else if (old.Mode != v.Mode || (old.Workout == null) != (v.Workout == null) || (v.Workout != null && old.Workout.Held != v.Workout.Held)) Log.Info("Session: " + v.ModeLabel + (v.Workout != null ? ", workout '" + v.Workout.Name + "' step " + v.Workout.Step + "/" + v.Workout.Steps + (v.Workout.Held ? " (on hold)" : "") : ""));
            _newEvents.Clear();
            _filter.Take(v, _newEvents);
            for (int i = 0; i < _newEvents.Count; i++) OnEvent(_newEvents[i]);
        }

        private void OnEvent(SessionEvent e)
        {
            switch (e.Kind)
            {
                case "step":
                    Sfx.PlayHere(Config.WorkoutStepSound);
                    if (Workout != null) Log.Info("Workout: step " + Workout.Step + "/" + Workout.Steps + " " + Workout.Label);
                    break;
                case "countdown":
                    if (e.Left <= Config.WorkoutCountdownS) Sfx.PlayHere(Config.WorkoutCountdownSound);
                    break;
                case "text":
                    GameText.Show(e.Text);
                    break;
                case "complete":
                    Complete(e);
                    break;
                default:
                    Log.WarnLimited("sessevent", "Session: unknown event '" + e.Kind + "' ignored", 30f);
                    break;
            }
        }

        /// <summary>The workout finished (or was skipped to the end): panel, sound, confetti, picture. A plan session is recorded by the bridge.</summary>
        private void Complete(SessionEvent e)
        {
            CompleteName = e.Name; CompleteTotalS = e.TotalS; _completeUntil = Time.unscaledTime + 5f;
            Sfx.PlayHere(Config.WorkoutDoneSound);
            Confetti.Burst(Config.WorkoutConfettiS);
            WorkoutShot.Schedule(Config.WorkoutConfettiS);   // last-workout.png, also with no confetti
            Log.Info("Workout: '" + e.Name + "' " + (e.Skipped ? "skipped to the end" : "finished") + " at " + WorkoutRunner.Fmt(e.ElapsedS) + " of " + WorkoutRunner.Fmt(e.TotalS) + ", avg " + e.AvgW.ToString("F0") + " W"
                + (e.Tag.Length > 0 ? " (plan session " + e.Tag + ", recorded by the bridge)" : ""));
        }

        private void PollPad()
        {
            if (!Config.ErgPadEnabled || _padBroken) return;
            XInputState st;
            try
            {
                if (XInputGetState((uint)Config.PadIndex, out st) != 0) { _prevButtons = 0; return; }   // 0 = ERROR_SUCCESS; else pad not connected
            }
            catch (Exception ex)
            {
                _padBroken = true;
                Log.Warn("ERG: XInput unavailable (" + ex.GetType().Name + ": " + ex.Message + "); pad chord disabled, keyboard keys still work");
                return;
            }
            ushort now = st.Gamepad.Buttons, prev = _prevButtons;
            _prevButtons = now;
            ushort pressed = (ushort)(now & ~prev);
            // RB must already have been held when the D-pad edge arrives (prev), so RB pressed together with a D-pad tap is not a chord.
            if ((prev & RightShoulder) == 0 || (now & RightShoulder) == 0) return;
            if ((pressed & DpadUp) != 0) Toggle("pad");
            else if (ErgRequested && (pressed & DpadRight) != 0) Adjust(1);
            else if (ErgRequested && (pressed & DpadLeft) != 0) Adjust(-1);
            else if (ActiveWorkout != null && (pressed & DpadDown) != 0) Skip();
        }

        /// <summary>False (with a note) when there is no bridge session to send it to: ERG and workouts are unavailable without the bridge.</summary>
        private bool Send(string name, double? value)
        {
            if (!HaveSession || _bridge == null)
            {
                Say("NO TRAINER BRIDGE");
                Log.Info("ERG: '" + name + "' not sent, no trainer bridge session");
                return false;
            }
            _bridge.SendIntent(name, value);
            Log.Info("ERG: intent " + name + (value.HasValue ? " " + value.Value.ToString("F0") : ""));
            return true;
        }

        public void Toggle(string src) { Send("erg_toggle", null); Log.Info("ERG: toggle (" + src + ")"); }

        public void Adjust(int steps) { Send("adjust", steps); }

        public void Skip() { Send("skip", null); }

        private void Say(string s) { _note = s; _noteUntil = Time.unscaledTime + 2f; }
    }
}
