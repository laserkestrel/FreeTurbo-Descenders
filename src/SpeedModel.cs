#nullable disable
using System;

// Pure logic (no Unity, no logging): also compiled into the bridge (bridge/DescendersBridge.csproj links this file).
namespace FreeTurboDescenders
{
    /// <summary>
    /// The bike-type presets: a fixed CdA + Crr pair per bike type, chosen in the bridge window (`rider.bikeType` in
    /// TrainerBridgeConfig.json) and sent to the game in the `rider` message as `bikeType` plus the resolved `cdA` / `crr`, so every game uses the
    /// same bike physics. Shared by the mod and the bridge (no Unity code). "road" is the physics the mod always had (CdA 0.32, Crr 0.004).
    /// </summary>
    internal static class BikePresets
    {
        public const string Road = "road", Gravel = "gravel", Mtb = "mtb";
        public const string Default = Road;
        /// <summary>Every preset name, in the order the window lists them.</summary>
        public static readonly string[] Names = { Road, Gravel, Mtb };

        /// <summary>A known preset name (case-insensitive, trimmed) in its canonical spelling, or null.</summary>
        public static string Normalize(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            for (int i = 0; i < Names.Length; i++) if (n == Names[i]) return Names[i];
            return null;
        }

        /// <summary>CdA (m^2) and Crr of a preset; an unknown name gives the default (road).</summary>
        public static void Resolve(string name, out float cdA, out float crr)
        {
            switch (Normalize(name))
            {
                case Gravel: cdA = 0.36f; crr = 0.006f; break;   // wider tyres, a bit more upright
                case Mtb: cdA = 0.45f; crr = 0.010f; break;      // knobbly tyres, upright on flat bars
                default: cdA = 0.32f; crr = 0.004f; break;       // road: the mod's constants earlier
            }
        }

        /// <summary>Words for the window's list.</summary>
        public static string Label(string name)
        {
            switch (Normalize(name))
            {
                case Gravel: return "Gravel bike";
                case Mtb: return "Mountain bike";
                default: return "Road bike";
            }
        }
    }

    /// <summary>
    /// The pure watts -> speed integrator, shared by the mod (<see cref="FitnessMode"/> keeps the Descenders glue: yield rules, pedal
    /// property, easing onto the rigidbody, descent factors, the U-turn cap) and the bridge (unit tests, later other games). No Unity code and
    /// old-Mono safe. SI units, real g. See docs/FORCE_MODEL.md.
    /// </summary>
    internal static class SpeedModel
    {
        public const float G = 9.81f, AirDensity = 1.226f, DrivetrainEff = 0.98f, MaxDriveForceN = 700f;

        // Speed floor in power / speed. Must be small: below it the force stops growing as the bike slows, and on a grade whose equilibrium
        // speed is under the floor the model stalls and can never restart (found in the first F1 run with a 1.5 m/s floor at 16.5 %).
        public const float MinDriveSpeedMs = 0.4f;

        /// <summary>
        /// One step: the model speed after <paramref name="dt"/> s at <paramref name="watts"/> on a slope of sine <paramref name="sin"/>
        /// (+ = uphill along travel). At rest it holds (never rolls backwards) unless the drive beats gravity plus rolling resistance; never below 0,
        /// never above <paramref name="maxMs"/> (the caller's cap, e.g. real speed + MaxLeadMs).
        /// </summary>
        public static float Step(float vModel, float watts, float sin, float massKg, float cdA, float crr, float dt, float maxMs)
        {
            float cos = (float)Math.Sqrt(Math.Max(0f, 1f - sin * sin));
            float drive = Math.Min(DrivetrainEff * watts / Math.Max(vModel, MinDriveSpeedMs), MaxDriveForceN);
            float fGrav = massKg * G * sin;                                  // + uphill (opposes motion)
            float fRoll = crr * massKg * G * cos;
            float fDrag = 0.5f * AirDensity * cdA * vModel * vModel;
            float net = drive - fGrav - fDrag;
            if (vModel <= 0.02f)
            {
                // at rest: hold (never roll the rider backwards) unless the drive beats gravity plus rolling resistance
                if (net > fRoll) vModel += (net - fRoll) / massKg * dt; else vModel = 0f;
            }
            else vModel += (net - fRoll) / massKg * dt;
            if (vModel < 0f) vModel = 0f;
            if (vModel > maxMs) vModel = maxMs;
            return vModel;
        }
    }
}
