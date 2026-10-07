using System;
using System.Reflection;
using UnityEngine;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// Observes the game's "Ascender" (a completed climb): AscendersTriggerFinishLine (bottom) -> State_InGame.EnteredFinishArea sets a
    /// flag; AscendersTriggerStartLine (top) -> State_InGame.EnteredStartArea sets MadeClimb only if that flag was set, otherwise it
    /// clears the flag. So an Ascender needs a pass through the finish trigger first, then the start trigger. Harmony postfixes on both
    /// (unobfuscated names) log each event with the rider's path progress, and read MadeClimb via its unobfuscated getter get_MadeClimb.
    /// </summary>
    internal static class AscenderEvents
    {
        public static int StartHits, FinishHits;
        public static bool MadeClimb;
        public static string LastEvent = "";
        public static float LastProgressM = float.NaN, LastSpeedKmh = float.NaN;

        private static AutoSteer _auto;
        private static PlayerProbe _probe;
        private static MethodInfo _getMadeClimb;
        private static float _lastStartAt = -99f, _lastFinishAt = -99f;   // the game calls each Entered*Area once per player collider (19x within ~0.1 s); count one event per burst

        public static void Reset() { _lastStartAt = _lastFinishAt = -99f; StartHits = FinishHits = 0; MadeClimb = false; LastEvent = ""; LastProgressM = float.NaN; LastSpeedKmh = float.NaN; }

        public static void Install(AutoSteer auto, PlayerProbe probe)
        {
            _auto = auto; _probe = probe;
            var harmony = new HarmonyLib.Harmony("descenders.cycling.poc.ascender");
            BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo start = typeof(State_InGame).GetMethod("EnteredStartArea", bf);
            MethodInfo finish = typeof(State_InGame).GetMethod("EnteredFinishArea", bf);
            _getMadeClimb = typeof(State_InGame).GetMethod("get_MadeClimb", bf);
            if ((object)start == null || (object)finish == null)
            {
                Log.Error("Ascender: State_InGame.EnteredStartArea/EnteredFinishArea not found (start " + ((object)start != null) + ", finish " + ((object)finish != null) + "); no ascender events");
                return;
            }
            harmony.Patch(start, postfix: new HarmonyLib.HarmonyMethod(typeof(AscenderEvents).GetMethod("StartPostfix", BindingFlags.Public | BindingFlags.Static)));
            harmony.Patch(finish, postfix: new HarmonyLib.HarmonyMethod(typeof(AscenderEvents).GetMethod("FinishPostfix", BindingFlags.Public | BindingFlags.Static)));
            Log.Info("Ascender: postfixes installed on State_InGame.EnteredStartArea / EnteredFinishArea; MadeClimb getter " + ((object)_getMadeClimb != null ? "found" : "MISSING"));
        }

        public static void StartPostfix(State_InGame __instance) { Record("start (top) trigger", __instance, true); }
        public static void FinishPostfix(State_InGame __instance) { Record("finish (bottom) trigger", __instance, false); }

        private static void Record(string what, State_InGame gs, bool isStart)
        {
            try
            {
                float now = Time.unscaledTime;
                if (now - (isStart ? _lastStartAt : _lastFinishAt) < 2f) { if (isStart) _lastStartAt = now; else _lastFinishAt = now; return; }
                if (isStart) { StartHits++; _lastStartAt = now; } else { FinishHits++; _lastFinishAt = now; }
                MadeClimb = ReadMadeClimb(gs);
                LastEvent = isStart ? "start" : "finish";
                LastProgressM = (_auto != null && _auto.OnPath) ? _auto.Progress : float.NaN;
                LastSpeedKmh = (_probe != null && _probe.Rb != null) ? _probe.Rb.velocity.magnitude * 3.6f : float.NaN;
                Log.Info("Ascender: entered " + what + " at path progress " + (float.IsNaN(LastProgressM) ? "n/a" : LastProgressM.ToString("F1") + " m")
                         + " of " + (_auto != null && _auto.Track.Loaded ? _auto.Track.Length.ToString("F0") : "?") + ", " + (float.IsNaN(LastSpeedKmh) ? "n/a" : LastSpeedKmh.ToString("F1")) + " km/h, MadeClimb=" + MadeClimb);
            }
            catch (Exception ex) { Log.ErrorLimited("ascender-evt", "AscenderEvents.Record", ex); }
        }

        private static bool ReadMadeClimb(State_InGame gs)
        {
            if ((object)_getMadeClimb == null || gs == null) return false;
            try { return (bool)_getMadeClimb.Invoke(gs, null); }
            catch (Exception) { return false; }
        }
    }
}
