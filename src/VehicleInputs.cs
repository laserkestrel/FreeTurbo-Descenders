using System;
using System.Reflection;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// The player's live control inputs as the game holds them on Vehicle (written every physics step by VehicleController
    /// from the human's controller, or by our auto-steer). Names are obfuscated, so they are looked up by reflection and
    /// logged; a missing one reads as NaN. Meaning, from VehicleController / Vehicle code:
    ///   steer = swebLyg (left stick X, -1..1), slide = c{v}lhG (right stick X: also lowers lateral grip, i.e. the R-stick slide),
    ///   brake = NYsPlot, pedal = j[fCiJt (0..1), tilt = zsEdyM} (left stick Y).
    /// </summary>
    internal sealed class VehicleInputs
    {
        public float Steer = float.NaN, Slide = float.NaN, Brake = float.NaN, Pedal = float.NaN, Tilt = float.NaN;

        private PropertyInfo _steer, _slide, _brake, _pedal, _tilt;
        private bool _resolved;

        public void Read(Vehicle v)
        {
            if (v == null) return;
            if (!_resolved) Resolve();
            Steer = Get(_steer, v); Slide = Get(_slide, v); Brake = Get(_brake, v); Pedal = Get(_pedal, v); Tilt = Get(_tilt, v);
        }

        private void Resolve()
        {
            _resolved = true;
            _steer = Find("swebLyg");
            _slide = Find("c{v}lhG");
            _brake = Find("NYsPlot");
            _pedal = Find("j[fCiJt");
            _tilt = Find("zsEdyM}");
            Log.Info("Inputs: steer=" + Has(_steer) + " slide=" + Has(_slide) + " brake=" + Has(_brake) + " pedal=" + Has(_pedal) + " tilt=" + Has(_tilt));
        }

        private static PropertyInfo Find(string name)
        {
            return typeof(Vehicle).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        }

        private static string Has(PropertyInfo p) { return (object)p != null ? "ok" : "MISSING"; }

        private static float Get(PropertyInfo p, Vehicle v)
        {
            if ((object)p == null) return float.NaN;
            try { return (float)p.GetValue(v, null); }
            catch (Exception) { return float.NaN; }
        }
    }
}
