using System;
using System.Reflection;
using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// Is the game world up? True once Player_Human, its Vehicle and Rigidbody exist and the game's StateMachine
    /// is not in a boot / menu / loading state (Splash, MainMenu, Generating: docs/GAME_ANALYSIS.md). The rider HUD
    /// (power / heart rate / cadence around the speedometer, rider panel) is drawn only while this is true, so it stays off
    /// the splash, menus and loading screens. If the state getter is not found (game update) only the player check applies.
    /// </summary>
    internal sealed class WorldGate
    {
        private const int StateSplash = 1, StateMainMenu = 2, StateGenerating = 4;
        private const float PollSec = 0.25f;
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        private bool _resolved, _loading, _ready;
        private float _nextPoll;
        private MethodInfo _smSp, _smState;

        public bool Ready { get { return _ready; } }

        public void Tick(GameState s)
        {
            float now = Time.unscaledTime;
            if (now >= _nextPoll)
            {
                _nextPoll = now + PollSec;
                _loading = ReadLoading();
            }
            bool ready = s.RigidbodyFound && !_loading;
            if (ready != _ready) Log.Info("WorldGate: rider HUD " + (ready ? "shown (world ready)" : "hidden (menu / loading, player " + (s.RigidbodyFound ? "found" : "not found") + ")"));
            _ready = ready;
        }

        private bool ReadLoading()
        {
            try
            {
                if (!_resolved)
                {
                    _resolved = true;
                    Type t = typeof(StateMachine);
                    _smSp = t.GetMethod("get_SP", Any);
                    _smState = t.GetMethod("get_currentState", BindingFlags.Public | BindingFlags.Instance);
                    if (object.ReferenceEquals(_smSp, null) || object.ReferenceEquals(_smState, null))
                        Log.Warn("WorldGate: StateMachine.SP / currentState not found (game update?); rider HUD follows the player only");
                }
                if (object.ReferenceEquals(_smSp, null) || object.ReferenceEquals(_smState, null)) return false;
                object sm = _smSp.Invoke(null, null);
                if (object.ReferenceEquals(sm, null)) return true;   // not created yet: still booting
                object st = _smState.Invoke(sm, null);
                if (object.ReferenceEquals(st, null)) return true;
                int id = Convert.ToInt32(st);
                return id == StateSplash || id == StateMainMenu || id == StateGenerating;
            }
            catch (Exception ex) { Log.ErrorLimited("worldgate", "WorldGate state read", ex); return false; }
        }
    }
}
