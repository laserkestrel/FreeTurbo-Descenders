using System;
using System.Reflection;
using UnityEngine;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// Makes the rider's legs, the bike's cranks and the chain sound turn at the trainer's cadence.
    /// The game places the legs with Animator.Play("Cycling", layer 1, normalizedTime = CyclistModel.pedalPosition) every frame; BikeAnimation
    /// and VehicleSounds read the same phase (docs/GAME_ANALYSIS.md). CyclistModel.Update advances pedalPosition by a rate that is a smoothed
    /// function of the pedal input and speed, unrelated to cadence. A Harmony prefix on CyclistModel.Update overwrites pedalPosition with our own
    /// phase (advanced by cadence / 60 * dt) just before the game runs, so the game's small per-frame increment only adds a constant offset
    /// and the visible rate is the cadence. Only the local player's model, and only while fitness mode is live and the message carries a cadence.
    /// </summary>
    internal static class CadenceAnim
    {
        private static FitnessMode _fitness;
        private static PlayerProbe _probe;
        private static MethodInfo _getPos, _setPos;
        private static float _phase;
        private static bool _wasDriving, _loggedFirst;
        public static bool Driving;                 // for the HUD / telemetry
        public static float PhaseRevPerSec;         // the rate being applied (cycles / s)

        public static void Install(FitnessMode fitness, PlayerProbe probe)
        {
            _fitness = fitness; _probe = probe;
            BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type t = typeof(CyclistModel);
            MethodInfo update = t.GetMethod("Update", bf, null, Type.EmptyTypes, null);
            _getPos = t.GetMethod("get_pedalPosition", bf);
            _setPos = t.GetMethod("set_pedalPosition", bf);
            if ((object)update == null || (object)_getPos == null || (object)_setPos == null)
            {
                Log.Error("CadenceAnim: CyclistModel.Update / pedalPosition accessors not found (update " + ((object)update != null) + ", get " + ((object)_getPos != null) + ", set " + ((object)_setPos != null) + "); legs keep the game's own rate");
                return;
            }
            var harmony = new HarmonyLib.Harmony("descenders.cycling.poc.cadenceanim");
            harmony.Patch(update, prefix: new HarmonyLib.HarmonyMethod(typeof(CadenceAnim).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)));
            Log.Info("CadenceAnim: prefix installed on CyclistModel.Update (drives pedalPosition from the trainer cadence; CadenceLegs=" + Config.CadenceLegs + ")");
        }

        public static void Prefix(MonoBehaviour __instance)
        {
            try
            {
                Driving = false;
                if (!Config.CadenceLegs || _fitness == null || !_fitness.Active || _fitness.FailSafe || !_fitness.HasCadence) { _wasDriving = false; return; }
                GameObject player = _probe != null ? _probe.Player : null;
                if (player == null || !ReferenceEquals(__instance.transform.root, player.transform.root)) return;   // only the local rider

                if (!_wasDriving) { _phase = Mathf.Repeat((float)_getPos.Invoke(__instance, null), 1f); _wasDriving = true; }   // start from where the legs are: no jump
                float rate = Mathf.Max(0f, _fitness.Cadence) / 60f * Config.CadenceLegsScale;
                _phase = Mathf.Repeat(_phase + rate * Time.deltaTime, 1f);
                _setPos.Invoke(__instance, new object[] { _phase });
                Driving = true; PhaseRevPerSec = rate;
                if (!_loggedFirst) { _loggedFirst = true; Log.Info("CadenceAnim: driving pedalPosition at " + _fitness.Cadence.ToString("F0") + " rpm (scale " + Config.CadenceLegsScale.ToString("F2") + ")"); }
            }
            catch (Exception ex) { Log.ErrorLimited("cadenceanim", "CadenceAnim prefix", ex); }
        }
    }
}
