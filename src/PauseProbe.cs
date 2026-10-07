using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// Is the game paused (telemetry `riding` = player found and not paused, so the bridge holds the workout clock)?
    /// Before the bridge ran the workout, a pause froze the clock only because Unity's frame time is 0 while paused (Time.timeScale 0),
    /// so that stays the rule. On top, a StateMachine state whose enum name contains "Paus" (found by reflection, logged). Other game
    /// members with "paus" in their name are only logged, to look at: a flag of some unrelated class (music, replay) read as "paused"
    /// would hold the workout forever, so none is used blindly.
    /// </summary>
    internal sealed class PauseProbe
    {
        private const float PollSec = 0.25f;
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        private bool _resolved, _statePaused, _paused;
        private float _nextPoll;
        private MethodInfo _smSp, _smState;
        private readonly List<string> _pauseStates = new List<string>();

        public bool Paused { get { return _paused; } }

        public void Tick()
        {
            if (!_resolved) { _resolved = true; Resolve(); }
            float now = Time.unscaledTime;
            if (now >= _nextPoll)
            {
                _nextPoll = now + PollSec;
                _statePaused = ReadStatePaused();
            }
            bool p = Time.timeScale < 0.01f || _statePaused;
            if (p != _paused) Log.Info("Pause: game " + (p ? "paused" : "resumed") + " (timeScale " + Time.timeScale.ToString("F2") + (_statePaused ? ", pause state" : "") + ")");
            _paused = p;
        }

        private void Resolve()
        {
            try
            {
                Type sm = typeof(StateMachine);
                _smSp = sm.GetMethod("get_SP", Any);
                _smState = sm.GetMethod("get_currentState", BindingFlags.Public | BindingFlags.Instance);
                if (!object.ReferenceEquals(_smState, null) && _smState.ReturnType.IsEnum)
                    foreach (string n in Enum.GetNames(_smState.ReturnType))
                        if (n.IndexOf("paus", StringComparison.OrdinalIgnoreCase) >= 0) _pauseStates.Add(n);

                var others = new List<string>();
                Type[] types;
                try { types = sm.Assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                foreach (Type t in types)
                {
                    if (object.ReferenceEquals(t, null) || others.Count >= 16) continue;
                    MethodInfo[] ms;
                    try { ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                    catch (Exception) { continue; }
                    foreach (MethodInfo m in ms)
                        if (m.Name.IndexOf("paus", StringComparison.OrdinalIgnoreCase) >= 0 && others.Count < 16) others.Add(t.Name + "." + m.Name + (m.IsStatic ? " (static)" : ""));
                }
                Log.Info("Pause: Time.timeScale == 0"
                    + (_pauseStates.Count > 0 ? " or StateMachine state " + string.Join(" / ", _pauseStates.ToArray()) : "; no pause state in the StateMachine enum")
                    + (others.Count > 0 ? "; game members named 'paus' (logged only, not used): " + string.Join(", ", others.ToArray()) : "; no game member named 'paus'"));
            }
            catch (Exception ex) { Log.Warn("Pause: reflection lookup failed (" + ex.GetType().Name + ": " + ex.Message + "); using Time.timeScale == 0 only"); }
        }

        private bool ReadStatePaused()
        {
            if (_pauseStates.Count == 0 || object.ReferenceEquals(_smSp, null) || object.ReferenceEquals(_smState, null)) return false;
            try
            {
                object sm = _smSp.Invoke(null, null);
                if (object.ReferenceEquals(sm, null)) return false;
                object st = _smState.Invoke(sm, null);
                return !object.ReferenceEquals(st, null) && _pauseStates.Contains(st.ToString());
            }
            catch (Exception ex) { Log.ErrorLimited("pausestate", "Pause state read", ex); return false; }
        }
    }
}
