using System;
using System.Reflection;
using UnityEngine;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// T2 harness: forces Vehicle.j[fCiJt (pedal, 0..1) to a fixed level, or steps through a list of levels, every physics
    /// step. Runs in the VehicleController.FixedUpdate postfix (after the game has written the pad's value), so the pad
    /// cannot overrule it. Counts, per step, what the pad wanted vs what we forced, and (from OnUpdate) whether the value
    /// still reads back as forced after the game's own physics ran. Nothing to restore on stop: the game rewrites the
    /// pedal from the controller on the next step.
    /// </summary>
    internal sealed class PedalTest
    {
        public bool Active { get; private set; }
        public float Target = float.NaN;          // level currently being forced (NaN during the lead-in)
        public int LevelIndex, LevelCount;
        public int ForcedSteps, SkippedSteps, PadOverrideSteps, WriteMismatch, ReadbackChecks, ReadbackMismatch;
        public float PadRaw = float.NaN;          // what the game wrote (the pad) before we overwrote it, latest step
        public float PadRawMax;
        public float StepRemainingSec { get { return Active ? Mathf.Max(0f, _levelLeft) : 0f; } }
        public float LeadInRemainingSec { get { return Active ? Mathf.Max(0f, _leadIn) : 0f; } }

        private float[] _levels = new float[0];
        private float _hold, _levelLeft, _leadIn, _maxKmh, _lastWritten = float.NaN;
        private bool _lastForced;
        private PropertyInfo _pedal;
        private MethodInfo _getter, _setter;
        private bool _resolved;

        public void Start(float[] levels, float holdSec, float leadInSec, float maxKmh, string source)
        {
            if (!Resolve()) { Log.Warn("PedalTest: pedal property missing or read-only, cannot start"); return; }
            _levels = levels; _hold = Mathf.Clamp(holdSec, 0.5f, 120f); _leadIn = Mathf.Max(0f, leadInSec); _maxKmh = maxKmh;
            LevelCount = levels.Length; LevelIndex = -1; _levelLeft = 0f; Target = float.NaN;
            ForcedSteps = SkippedSteps = PadOverrideSteps = WriteMismatch = ReadbackChecks = ReadbackMismatch = 0;
            PadRawMax = 0f; PadRaw = float.NaN; _lastForced = false;
            Active = true;
            Log.Info("PedalTest: start (" + source + "), levels " + string.Join(", ", Array.ConvertAll(levels, delegate (float f) { return f.ToString("F2"); }))
                + ", " + _hold.ToString("F1") + " s each, lead-in " + _leadIn.ToString("F1") + " s, forcing skipped above " + _maxKmh.ToString("F0") + " km/h");
            Sfx.Play(Cue.Engage);
        }

        public void Stop(string why)
        {
            if (!Active) return;
            Active = false; _lastForced = false;
            Log.Info("PedalTest: ended (" + why + "). forced " + ForcedSteps + " steps, skipped " + SkippedSteps + " (over " + _maxKmh.ToString("F0") + " km/h); "
                + "pad disagreed with the forced value on " + PadOverrideSteps + " steps (pad max " + PadRawMax.ToString("F2") + "); "
                + "write round-trip mismatches " + WriteMismatch + "; read-back after game physics: " + ReadbackMismatch + " mismatches in " + ReadbackChecks + " checks");
            Sfx.Play(Cue.Disengage);
        }

        /// <summary>Harmony postfix, once per physics step for the local player.</summary>
        public void Apply(Vehicle v, float speedKmh, float dt)
        {
            if (!Active) return;
            PadRaw = ReadPedal(v);
            if (!float.IsNaN(PadRaw) && PadRaw > PadRawMax) PadRawMax = PadRaw;

            if (_leadIn > 0f) { _leadIn -= dt; _lastForced = false; return; }
            _levelLeft -= dt;
            if (_levelLeft <= 0f)
            {
                LevelIndex++;
                if (LevelIndex >= _levels.Length) { Stop("complete"); return; }
                Target = _levels[LevelIndex]; _levelLeft = _hold;
                Log.Info("PedalTest: level " + (LevelIndex + 1) + "/" + _levels.Length + " = " + Target.ToString("F2") + " at " + speedKmh.ToString("F1") + " km/h");
                if (LevelIndex > 0) Sfx.Play(Cue.Warn);
            }

            if (speedKmh > _maxKmh) { SkippedSteps++; _lastForced = false; return; }   // leave the game's own over-speed rule alone

            if (!float.IsNaN(PadRaw) && Mathf.Abs(PadRaw - Target) > 0.05f) PadOverrideSteps++;
            _setter.Invoke(v, new object[] { Target });
            float rb = ReadPedal(v);
            if (float.IsNaN(rb) || Mathf.Abs(rb - Target) > 0.001f) WriteMismatch++;
            ForcedSteps++; _lastWritten = Target; _lastForced = true;
        }

        /// <summary>From OnUpdate (after the physics steps of the frame): did the forced value survive the game's own code?</summary>
        public void CheckReadback(float pedalNow)
        {
            if (!Active || !_lastForced || float.IsNaN(pedalNow)) return;
            ReadbackChecks++;
            if (Mathf.Abs(pedalNow - _lastWritten) > 0.001f) ReadbackMismatch++;
        }

        private bool Resolve()
        {
            if (_resolved) return (object)_setter != null && (object)_getter != null;
            _resolved = true;
            _pedal = typeof(Vehicle).GetProperty("j[fCiJt", BindingFlags.Public | BindingFlags.Instance);
            if ((object)_pedal != null) { _getter = _pedal.GetGetMethod(true); _setter = _pedal.GetSetMethod(true); }
            Log.Info("PedalTest: pedal property 'j[fCiJt' " + ((object)_pedal == null ? "NOT FOUND" : "found, getter " + ((object)_getter != null) + ", setter " + ((object)_setter != null)));
            return (object)_setter != null && (object)_getter != null;
        }

        private float ReadPedal(Vehicle v)
        {
            try { return (float)_getter.Invoke(v, null); }
            catch (Exception) { return float.NaN; }
        }

        /// <summary>Parse "0.25,0.5,0.75,1" (invariant culture); falls back to the T2 defaults.</summary>
        public static float[] ParseLevels(string s)
        {
            float[] def = { 0.25f, 0.5f, 0.75f, 1f };
            if (string.IsNullOrEmpty(s)) return def;
            string[] parts = s.Split(',');
            var list = new System.Collections.Generic.List<float>();
            foreach (string p in parts)
            {
                float f;
                if (float.TryParse(p.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f))
                    list.Add(Mathf.Clamp01(f));
            }
            return list.Count > 0 ? list.ToArray() : def;
        }
    }
}
