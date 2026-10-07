using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// A virtual "press any button" for the start cabin, for tools\SmokeTest.ps1 (bridge command `press_start`).
    /// State_PreGame.OnUpdate asks InputManager.get_AnyKeyOrButtonWasPressed (any pad button || Input.anyKeyDown: why Alt-Tab
    /// starts a run) every frame and, if true, starts the run itself (respawn at the start line, leave PreGame). A Harmony
    /// postfix on that getter reports one press while armed, so the game runs its own start code in its own update. Only
    /// State_PreGame and a demo splash screen call the getter, so a virtual press cannot leak into other menus.
    /// Both names are unobfuscated. State_PreGame is padded with obfuscated decoys that copy OnUpdate's shape with other
    /// calls (a first version found one by IL shape and patched a method the game never calls), so anchor on real names only;
    /// the IL of OnUpdate is only read to confirm it calls the getter. Calling the start method directly from the mod broke
    /// the state machine's stack (ArgumentOutOfRangeException in the pop), which is why this goes through the input.
    /// </summary>
    internal static class StartGate
    {
        private const int StatePreGame = 5;
        private const float ArmSec = 2f;   // an unused press expires, so it cannot fire in some later cabin
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        private static MethodInfo _smSp, _smState, _inputCheck;
        private static bool _patched, _triedPatch;
        private static float _armedUntil = -1f;

        public static bool PressStart(out string detail)
        {
            try
            {
                int id; string name;
                if (!ReadState(out id, out name)) { detail = name; return false; }
                if (id != StatePreGame) { detail = "not in the start cabin (state " + name + " (" + id + "))"; return false; }
                if (!_triedPatch) { _triedPatch = true; Patch(); }
                if (!_patched) { detail = "input check not found or not patched (game update?)"; return false; }
                _armedUntil = Time.unscaledTime + ArmSec;
                detail = "armed (virtual button press for " + ArmSec.ToString("F0") + " s)";
                return true;
            }
            catch (Exception ex) { detail = ex.GetType().Name + ": " + ex.Message; Log.Error("StartGate", ex); return false; }
        }

        /// <summary>Harmony postfix on the "any button pressed" check: report one press while armed.</summary>
        public static void InputPostfix(ref bool __result)
        {
            if (_armedUntil < 0f) return;
            if (Time.unscaledTime > _armedUntil) { _armedUntil = -1f; Log.Info("StartGate: virtual press expired unused"); return; }
            _armedUntil = -1f;
            if (__result) return;   // a real press this frame anyway
            __result = true;
            Log.Info("StartGate: start pressed (virtual button via InputManager." + _inputCheck.Name + ")");
        }

        private static bool ReadState(out int id, out string name)
        {
            id = -1;
            Type smType = typeof(StateMachine);
            if (object.ReferenceEquals(_smSp, null)) _smSp = smType.GetMethod("get_SP", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (object.ReferenceEquals(_smState, null)) _smState = smType.GetMethod("get_currentState", BindingFlags.Public | BindingFlags.Instance);
            if (object.ReferenceEquals(_smSp, null) || object.ReferenceEquals(_smState, null)) { name = "StateMachine members not found (game update?)"; return false; }
            object sm = _smSp.Invoke(null, null);
            if (object.ReferenceEquals(sm, null)) { name = "no StateMachine yet"; return false; }
            object state = _smState.Invoke(sm, null);
            if (object.ReferenceEquals(state, null)) { name = "null"; return true; }
            id = Convert.ToInt32(state); name = state.ToString();
            return true;
        }

        private static void Patch()
        {
            _inputCheck = FindInputCheck();
            if (object.ReferenceEquals(_inputCheck, null)) return;
            try
            {
                var harmony = new HarmonyLib.Harmony("descenders.cycling.poc.startgate");
                harmony.Patch(_inputCheck, postfix: new HarmonyLib.HarmonyMethod(typeof(StartGate).GetMethod("InputPostfix", BindingFlags.Public | BindingFlags.Static)));
                _patched = true;
                Log.Info("StartGate: postfix installed on InputManager." + _inputCheck.Name);
            }
            catch (Exception ex) { Log.Error("StartGate: patching the input check failed", ex); }
        }

        private static MethodInfo FindInputCheck()
        {
            Type pre = typeof(StateMachine).Assembly.GetType("State_PreGame");
            Type input = typeof(StateMachine).Assembly.GetType("InputManager");
            if (object.ReferenceEquals(pre, null) || object.ReferenceEquals(input, null)) { Log.Warn("StartGate: State_PreGame / InputManager type not found"); return null; }
            MethodInfo check = input.GetMethod("get_AnyKeyOrButtonWasPressed", Inst);
            if (object.ReferenceEquals(check, null)) { Log.Warn("StartGate: InputManager.get_AnyKeyOrButtonWasPressed not found (game update?)"); return null; }
            // Confirm the cabin really asks it: State_PreGame.OnUpdate calls the getter (decoys copy the shape, OnUpdate is the real one).
            try
            {
                MethodInfo onUpdate = pre.GetMethod("OnUpdate", Inst);
                bool asks = !object.ReferenceEquals(onUpdate, null) && IlReader.Calls(onUpdate).Exists(c => object.ReferenceEquals(c, check));
                if (asks) Log.Info("StartGate: State_PreGame.OnUpdate asks InputManager.get_AnyKeyOrButtonWasPressed");
                else Log.Warn("StartGate: State_PreGame.OnUpdate does not call get_AnyKeyOrButtonWasPressed (game update?); patching it anyway");
            }
            catch (Exception ex) { Log.Warn("StartGate: IL check skipped (" + ex.GetType().Name + ": " + ex.Message + ")"); }
            return check;
        }
    }
}
