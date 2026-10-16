#nullable disable
using System;

namespace FreeTurboDescenders
{
    /// <summary>
    /// The rider profile the mod uses: only the bridge's `rider` message (docs/IPC.md; removed the cfg fallbacks). Until the first one
    /// arrives the mod knows no mass, so fitness mode waits rather than guessing one. It also carries the bike type and its CdA / Crr
    /// (<see cref="BikePresets"/>); a bridge that sends none gives the road preset, the physics the mod always had.
    /// Pure (no Unity), so `selftest` tests it (tests\unit\TrainingTests.cs, tests\unit\SpeedModelTests.cs).
    /// </summary>
    internal sealed class RiderState
    {
        public const float MinRiderKg = 30f, MaxRiderKg = 200f, MinBikeKg = 3f, MaxBikeKg = 40f, MinFtpW = 50f, MaxFtpW = 600f;   // same limits as the bridge's RiderProfile
        public const float MinCdA = 0.1f, MaxCdA = 1.0f, MinCrr = 0.001f, MaxCrr = 0.03f;   // sanity limits on what a bridge sends

        /// <summary>A `rider` message has arrived (the values below are 0 until then).</summary>
        public bool Known { get; private set; }
        public float RiderKg { get; private set; }
        public float BikeKg { get; private set; }
        public float FtpW { get; private set; }
        public float MassKg { get { return RiderKg + BikeKg; } }
        /// <summary>The bike preset's name ("road" until a message says otherwise) and its air drag area (m^2) and rolling resistance.</summary>
        public string BikeType { get; private set; }
        public float CdA { get; private set; }
        public float Crr { get; private set; }

        private bool _waitLogged;

        public RiderState()
        {
            float cdA, crr;
            BikePresets.Resolve(BikePresets.Default, out cdA, out crr);
            BikeType = BikePresets.Default; CdA = cdA; Crr = crr;
        }

        /// <summary>A `rider` message without the bike fields (a bridge older than): the road preset.</summary>
        public bool Apply(float riderKg, float bikeKg, float ftpW) { return Apply(riderKg, bikeKg, ftpW, null, float.NaN, float.NaN); }

        /// <summary>Take a `rider` message, clamped to the limits. True when the values changed (the caller logs); a repeat of the same values,
        /// or a message with a missing mass / FTP, changes nothing. A missing or unknown <paramref name="bikeType"/> is "road"; a missing CdA / Crr
        /// takes the preset's value.</summary>
        public bool Apply(float riderKg, float bikeKg, float ftpW, string bikeType, float cdA, float crr)
        {
            if (float.IsNaN(riderKg) || float.IsNaN(bikeKg) || float.IsNaN(ftpW)) return false;
            riderKg = Clamp(riderKg, MinRiderKg, MaxRiderKg); bikeKg = Clamp(bikeKg, MinBikeKg, MaxBikeKg); ftpW = Clamp(ftpW, MinFtpW, MaxFtpW);
            string type = BikePresets.Normalize(bikeType) ?? BikePresets.Default;
            float pCdA, pCrr;
            BikePresets.Resolve(type, out pCdA, out pCrr);
            cdA = float.IsNaN(cdA) ? pCdA : Clamp(cdA, MinCdA, MaxCdA);
            crr = float.IsNaN(crr) ? pCrr : Clamp(crr, MinCrr, MaxCrr);
            if (Known && riderKg == RiderKg && bikeKg == BikeKg && ftpW == FtpW && type == BikeType && cdA == CdA && crr == Crr) return false;
            RiderKg = riderKg; BikeKg = bikeKg; FtpW = ftpW; BikeType = type; CdA = cdA; Crr = crr; Known = true;
            return true;
        }

        /// <summary>Fitness mode may drive the bike: switched on, trainer data fresh, and the rider profile known.</summary>
        public static bool FitnessReady(bool enabled, bool trainerFresh, bool riderKnown) { return enabled && trainerFresh && riderKnown; }

        /// <summary>Fitness mode would start but no rider profile has arrived yet (the HUD / telemetry say "waiting").</summary>
        public static bool Waiting(bool enabled, bool trainerFresh, bool riderKnown) { return enabled && trainerFresh && !riderKnown; }

        /// <summary>True the first time only (the caller logs the wait once per game run).</summary>
        public bool TakeWaitLog()
        {
            if (_waitLogged) return false;
            _waitLogged = true;
            return true;
        }

        private static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
