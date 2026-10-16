using System;
using System.Reflection;
using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// Skips the menus. Once the game's StateMachine first reaches MainMenu, starts the configured world seed through the
    /// game's own dev-console entry point (DevCommandsGameplay.LoadLevelFromSeed: StartNewSession(seed, 2) then push the
    /// Generating state). Everything is resolved by reflection and logged; the state enum type name is obfuscated
    /// but its member names (MainMenu = 2, Generating = 4, ...) are not. One-shot per game launch.
    /// </summary>
    internal sealed class AutoStart
    {
        private const int StateMainMenu = 2, StateOverworld = 9;
        private const float PollSec = 0.25f;
        private const float GiveUpSec = 180f;
        private const float WatchAfterFireSec = 90f;
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        private bool _resolved, _fired, _gaveUp;
        private float _nextPoll, _menuSince = -1f, _watchUntil;
        private string _lastState;
        private MethodInfo _smSp, _smState, _load, _sessionStarted, _sessSp;

        public void Tick()
        {
            if (!Config.AutoStart || _gaveUp) return;
            float now = Time.unscaledTime;
            if (_fired && now > _watchUntil) return;
            if (now < _nextPoll) return;
            _nextPoll = now + PollSec;

            if (!_resolved) Resolve();
            if (_gaveUp) return;

            object sm = Singleton(typeof(StateMachine), ref _smSp);
            if (object.ReferenceEquals(sm, null)) return;   // not created yet during boot
            object stateObj = _smState.Invoke(sm, null);
            string name = object.ReferenceEquals(stateObj, null) ? "null" : stateObj.ToString();
            int id = object.ReferenceEquals(stateObj, null) ? -1 : Convert.ToInt32(stateObj);
            if (name != _lastState)
            {
                Log.Info("AutoStart: game state -> " + name + " (" + id + ")" + (_fired ? " (after autostart)" : ""));
                _lastState = name;
                if (IsMenuState(id)) _menuSince = now;
            }
            if (_fired) return;

            if (IsMenuState(id))
            {
                if (_menuSince >= 0f && now - _menuSince >= Config.AutoStartDelaySec) Fire(now);
            }
            else if (now > GiveUpSec)
            {
                Log.Warn("AutoStart: main menu not reached within " + GiveUpSec + " s (state '" + name + "'); giving up");
                _gaveUp = true;
            }
        }

        private void Resolve()
        {
            _resolved = true;
            Type sm = typeof(StateMachine);
            // The obfuscator strips property metadata but leaves the get_ methods, so look up getters.
            _smState = sm.GetMethod("get_currentState", BindingFlags.Public | BindingFlags.Instance);
            Type dev = sm.Assembly.GetType("DevCommandsGameplay");
            if (!object.ReferenceEquals(dev, null))
                _load = dev.GetMethod("LoadLevelFromSeed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new Type[] { typeof(string) }, null);
            Log.Info("AutoStart: StateMachine.currentState " + (object.ReferenceEquals(_smState, null) ? "MISSING" : "ok")
                + ", DevCommandsGameplay.LoadLevelFromSeed " + (object.ReferenceEquals(_load, null) ? "MISSING" : "ok"));
            if (object.ReferenceEquals(_smState, null) || object.ReferenceEquals(_load, null))
            {
                Log.Warn("AutoStart: game members not found (game update?); autostart disabled");
                _gaveUp = true;
            }
        }

        private void Fire(float now)
        {
            _fired = true;
            _watchUntil = now + WatchAfterFireSec;
            string seed = (Config.AutoStartSeed ?? "").Trim();
            if (!ValidSeed(seed))
            {
                Log.Warn("AutoStart: seed '" + seed + "' is not '<digits>' or '<digits>-<digits>'; not loading");
                return;
            }
            Log.Info("AutoStart: main menu reached, loading seed " + seed + " via DevCommandsGameplay.LoadLevelFromSeed");
            try { _load.Invoke(null, new object[] { seed }); }
            catch (TargetInvocationException tex) { Log.Error("AutoStart: LoadLevelFromSeed threw", tex.InnerException ?? tex); return; }
            catch (Exception ex) { Log.Error("AutoStart: LoadLevelFromSeed call failed", ex); return; }
            Log.Info("AutoStart: call returned, session started = " + SessionStarted());
        }

        private string SessionStarted()
        {
            try
            {
                Type t = typeof(SessionManager);
                object sess = Singleton(t, ref _sessSp);
                if (object.ReferenceEquals(sess, null)) return "no SessionManager";
                if (object.ReferenceEquals(_sessionStarted, null)) _sessionStarted = t.GetMethod("SessionStarted", BindingFlags.Public | BindingFlags.Instance);
                return object.ReferenceEquals(_sessionStarted, null) ? "?" : _sessionStarted.Invoke(sess, null).ToString();
            }
            catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
        }

        /// <summary>Reads the static Singleton&lt;T&gt;.SP of a game manager, or null while it doesn't exist yet.</summary>
        private static object Singleton(Type t, ref MethodInfo cache)
        {
            if (object.ReferenceEquals(cache, null)) cache = t.GetMethod("get_SP", Any);
            if (object.ReferenceEquals(cache, null)) return null;
            return cache.Invoke(null, null);
        }

        /// <summary>MainMenu, or Overworld: the game may treat the mt_palumbo hub as its menu (log shows which one it settles on).</summary>
        private static bool IsMenuState(int id) { return id == StateMainMenu || id == StateOverworld; }

        private static bool ValidSeed(string s)
        {
            if (s.Length == 0) return false;
            int dashes = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '-') { if (++dashes > 1 || i == 0 || i == s.Length - 1) return false; }
                else if (c < '0' || c > '9') return false;
            }
            return true;
        }
    }
}
