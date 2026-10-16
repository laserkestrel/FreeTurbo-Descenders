using DescendersBridge.Ant;
using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// The shared watts -> speed integrator (src\SpeedModel.cs) and the bike-type presets that travel in the `rider` message.
/// Steady-state speeds are checked against an independent power balance (bisection), so a change to the integrator that alters the feel shows up here.
/// Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class SpeedModelTests
{
    private const float Mass = 84f, Dt = 0.02f;

    /// <summary>Integrate from rest until settled (no cap).</summary>
    private static float Settle(float watts, float gradePct, float cdA, float crr, float start = 0f, float seconds = 300f)
    {
        float sin = Sin(gradePct), v = start;
        for (float t = 0; t < seconds; t += Dt) v = SpeedModel.Step(v, watts, sin, Mass, cdA, crr, Dt, float.PositiveInfinity);
        return v;
    }

    private static float Sin(float gradePct) { float g = gradePct / 100f; return g / MathF.Sqrt(1f + g * g); }

    /// <summary>The speed where drivetrain power equals the resistive power (gravity + rolling + air), by bisection: the textbook answer.</summary>
    private static double Balance(double watts, double gradePct, double cdA, double crr)
    {
        double sin = Sin((float)gradePct), cos = Math.Sqrt(1 - sin * sin);
        double Need(double v) => v * (Mass * SpeedModel.G * sin + crr * Mass * SpeedModel.G * cos + 0.5 * SpeedModel.AirDensity * cdA * v * v);
        double lo = 0, hi = 40;
        for (int i = 0; i < 80; i++) { double mid = (lo + hi) / 2; if (Need(mid) < SpeedModel.DrivetrainEff * watts) lo = mid; else hi = mid; }
        return lo;
    }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Speed model: steady-state speeds with the road preset match the power balance");
        {
            BikePresets.Resolve(BikePresets.Road, out float cdA, out float crr);
            var speeds = new Dictionary<(int W, int G), float>();
            foreach (int w in new[] { 150, 250, 350 })
                foreach (int g in new[] { 0, 5, 10 })
                {
                    float v = Settle(w, g, cdA, crr);
                    double want = Balance(w, g, cdA, crr);
                    speeds[(w, g)] = v;
                    check(Math.Abs(v - want) < 0.05, $"{w} W on {g} %: {v * 3.6:0.0} km/h (power balance {want * 3.6:0.0} km/h)");
                }
            check(speeds[(150, 0)] < speeds[(250, 0)] && speeds[(250, 0)] < speeds[(350, 0)] && speeds[(250, 0)] > speeds[(250, 5)] && speeds[(250, 5)] > speeds[(250, 10)],
                "more watts = faster, steeper = slower");
            check(speeds[(250, 0)] * 3.6 > 33 && speeds[(250, 0)] * 3.6 < 40, $"250 W on the flat is a believable road-bike speed ({speeds[(250, 0)] * 3.6:0.0} km/h)");
        }

        Console.WriteLine("Speed model: never rolls backwards, never above the cap");
        {
            BikePresets.Resolve(BikePresets.Road, out float cdA, out float crr);
            float v = 5f, min = float.MaxValue;
            for (int i = 0; i < 2000; i++) { v = SpeedModel.Step(v, 0f, Sin(10), Mass, cdA, crr, Dt, float.PositiveInfinity); min = Math.Min(min, v); }
            check(min >= 0f && v == 0f, "0 W on 10 % from 18 km/h: slows to a stop and stays there, never negative");
            float h = 0f; bool moved = false;
            for (int i = 0; i < 500; i++) { h = SpeedModel.Step(h, 50f, Sin(20), Mass, cdA, crr, Dt, float.PositiveInfinity); moved |= h != 0f; }
            check(!moved, "50 W on 20 % from rest: too weak to pull away, holds at 0 (no backward roll)");
            check(Settle(150, 20, cdA, crr) > 0.5f, "150 W on 20 % from rest pulls away (the drive-speed floor is small enough)");
            float c = 0f, max = 0f;
            for (int i = 0; i < 3000; i++) { c = SpeedModel.Step(c, 400f, Sin(-10), Mass, cdA, crr, Dt, 8f); max = Math.Max(max, c); }
            check(max <= 8f && c == 8f, "400 W down a 10 % slope with an 8 m/s cap: reaches the cap, never above it");
            check(SpeedModel.Step(12f, 0f, 0f, Mass, cdA, crr, Dt, 10f) == 10f, "already above the cap: pulled down to it at once (the U-turn cap)");
        }

        Console.WriteLine("Bike presets: road is the old constants, the others are slower");
        {
            BikePresets.Resolve("road", out float rc, out float rr);
            check(rc == 0.32f && rr == 0.004f, "road = CdA 0.32, Crr 0.004 (the mod's constants before #47, so the feel does not change)");
            BikePresets.Resolve("tandem", out float uc, out float ur);
            check(uc == rc && ur == rr && BikePresets.Normalize("tandem") is null && BikePresets.Normalize(null!) is null, "an unknown or missing name = road");
            check(BikePresets.Normalize(" MTB ") == "mtb" && BikePresets.Default == "road", "names are case-insensitive, default road");
            BikePresets.Resolve("gravel", out float gc, out float gr);
            BikePresets.Resolve("mtb", out float mc, out float mr);
            float road = Settle(250, 0, rc, rr), gravel = Settle(250, 0, gc, gr), mtb = Settle(250, 0, mc, mr);
            check(road > gravel && gravel > mtb, $"250 W on the flat: road {road * 3.6:0.0} > gravel {gravel * 3.6:0.0} > mtb {mtb * 3.6:0.0} km/h");
        }

        Console.WriteLine("Bike type in the rider profile and the `rider` message");
        {
            var c = RiderProfile.Clamp(new RiderConfig { BikeType = "Gravel" }, out string none);
            check(c.BikeType == "gravel" && none == "", "a known name in other case is kept (canonical spelling), not reported as clamped");
            var u = RiderProfile.Clamp(new RiderConfig { BikeType = "tandem" }, out string named);
            check(u.BikeType == "road" && named.Contains("bikeType"), "an unknown bike type becomes road and is reported");
            check(RiderProfile.Clamp(new RiderConfig { BikeType = null! }, out _).BikeType == "road", "a missing bike type (older file) is road");
            check(new RiderConfig().BikeType == "road", "the default rider is on a road bike");
            string msg = RiderProfile.Message(new RiderConfig { RiderWeightKg = 70, BikeWeightKg = 12, FtpW = 200, BikeType = "mtb" });
            check(msg.EndsWith(",\"bikeType\":\"mtb\",\"cdA\":0.45,\"crr\":0.01}"), $"the message carries the type and its resolved CdA / Crr ({msg})");
        }

        Console.WriteLine("Mod: the rider profile takes the bike from the `rider` message (src\\RiderState.cs)");
        {
            var rs = new RiderState();
            check(rs.BikeType == "road" && rs.CdA == 0.32f && rs.Crr == 0.004f, "before any message: road preset values (fitness waits anyway)");
            check(rs.Apply(75, 9, 180) && rs.BikeType == "road" && rs.CdA == 0.32f, "a message from an older bridge (no bike fields): road");
            check(rs.Apply(75, 9, 180, "mtb", 0.45f, 0.01f) && rs.BikeType == "mtb" && rs.CdA == 0.45f && rs.Crr == 0.01f, "bike changed in the bridge: applied, reported as a change");
            check(!rs.Apply(75, 9, 180, "mtb", 0.45f, 0.01f), "the same bike again changes nothing");
            check(rs.Apply(75, 9, 180, "gravel", float.NaN, float.NaN) && rs.CdA == 0.36f && rs.Crr == 0.006f, "type without values: the preset's values");
            check(rs.Apply(75, 9, 180, "road", 5f, 0f) && rs.CdA == RiderState.MaxCdA && rs.Crr == RiderState.MinCrr, "silly values from a bridge are clamped");
            check(rs.Apply(75, 9, 180, "penny-farthing", float.NaN, float.NaN) && rs.BikeType == "road" && rs.CdA == 0.32f, "an unknown type: road");
        }
    }
}
