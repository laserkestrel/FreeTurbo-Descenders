using System;
using MelonLoader;
using UnityEngine;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// Settings. Rider-facing ones are MelonPreferences entries (UserData/MelonPreferences.cfg, category "DescendersCyclingPOC");
    /// developer tools and tuning constants are fixed below and changed in code (a saved cfg value would otherwise mask a new code default).
    /// </summary>
    internal static class Config
    {
        private static MelonPreferences_Category _cat;

        // ---- rider settings (cfg) ----
        public static KeyCode HudKey, AutoSteerKey, SpectatorKey;
        public static bool HudVisibleAtStart, AutoStart, IpcEnabled;
        public static string HudSide, AutoStartSeed, SfxMode;
        public static bool FitnessEnabled, CadenceLegs, UTurnEnabled, AsAutoEngage;
        public static int UTurnSide;
        /// <summary>Rider profile: only the bridge's `rider` message (<see cref="ApplyBridgeRider"/>); fitness mode waits until it is Known.</summary>
        public static readonly RiderState Rider = new RiderState();
        /// <summary>Speed physics only: the trainer's resistance uses the bridge's own trainerDifficulty on the raw road gradient.</summary>
        public static float ClimbPhysicsScale, DescentGradientScale, DescentPowerFactor;
        /// <summary>Physics gradient scale for a slope (+ = uphill along travel): climbs use ClimbPhysicsScale, descents DescentGradientScale.</summary>
        public static float ScaleFor(float slope) { return slope >= 0f ? ClimbPhysicsScale : DescentGradientScale; }
        public static KeyCode ErgToggleKey, ErgUpKey, ErgDownKey, ErgSkipKey;
        // The workout library folder is the bridge's (`library` section of TrainerBridgeConfig.json, set in the bridge window);
        // workouts and training plans are chosen there too (no in-game picker, so no picker keys or size here).
        // ERG limits / step / start target, workout bias step, pause time and trainer difficulty are the bridge's (`training` section of TrainerBridgeConfig.json).
        public static int PadIndex;
        public static int WorkoutHudRows;
        public static float WorkoutHudScale, WorkoutHudOpacity;
        public static string WorkoutDoneSound, WorkoutStepSound, WorkoutCountdownSound;
        public static int WorkoutCountdownS;
        public static float WorkoutConfettiS;
        public static int SpectatorCount;
        public static bool SpectatorHats, SpectatorClothes;
        public static float SpectatorNoiseShare, SpectatorNoiseVolume;
        public static int TelemetryPort, CommandPort;
        public static bool BridgeAutoStart;
        public static string BridgePath;
        public const string BridgeProcessName = "turbotrainer-bridge";   // the bridge exe's name, without .exe
        private const string DefaultBridgePath = "DescendersBridge\\" + BridgeProcessName + ".exe";

        // ---- developer tools (fixed) ----
        public static readonly KeyCode WriteTestKey = KeyCode.F7, DumpKey = KeyCode.F8, RayProbeKey = KeyCode.Insert, PedalTestKey = KeyCode.F6, ZoneMapKey = KeyCode.End, SpectatorLabelKey = KeyCode.F11;
        public static bool HudDiagnostics = false;       // old POC diagnostic panel below the rider stats
        public static readonly bool AutoCensusOnLoad = false;   // true: terrain census + course/track dumps 4 s after every level load (as DumpKey does); off so UserData stays clean
        public static bool SpectatorLabels = false;      // debug label over each spectator; toggled at run time with SpectatorLabelKey
        public static readonly float TestCapKmh = 10f, TestCapSeconds = 5f;   // F7 write test: speed cap and duration
        public static readonly string PedalTestLevels = "0.25,0.5,0.75,1.0";  // F6 T2 test: pedal levels forced in order
        public static readonly float PedalTestSeconds = 10f, PedalTestLeadInSeconds = 3f, PedalTestMaxKmh = 95f;   // game zeroes the pedal above ~99 km/h
        public static readonly float AutoStartDelaySec = 1.5f;   // lets the main menu finish initialising
        public static readonly int TelemetryHz = 20;
        public static readonly bool BridgeShowWindow = false;   // true: the auto-started bridge gets a minimised console window instead of none (its log file is written either way)

        // ---- tuning constants (fixed; see docs/FORCE_MODEL.md, docs/SPECTATORS.md) ----
        // auto-steer
        public static readonly float AsLookaheadBase = 3f, AsLookaheadPerMs = 0.45f, AsLookaheadMin = 4f, AsLookaheadMax = 12f;
        public static readonly float AsFeedback = 1.5f;    // wheel-angle feedback gain (0 = pure feed-forward)
        public static readonly bool AsInvert = false;
        public static readonly float AsSlideGain = 0f;     // R-stick slide added in proportion to the steer command
        public static readonly float AsLineOffset = 2.5f;  // racing line: max metres off the course centre
        public static readonly float AsEndWarnM = 35f, AsEndWarnSeconds = 3.5f, AsEndOvershootM = 4f;
        public static readonly string[] SfxOverride = { "", "", "", "" };   // explicit FMOD events for engage / disengage / end warning / hand-over ("" = auto)
        // U-turns
        public static readonly float UTurnStartBeforeFinishM = 24f, UTurnHardStopBeforeFinishM = 4f;   // the Ascender flag fires ~27 m before the finish trigger
        public static readonly float UTurnTopStartM = 11.5f, UTurnTopHardStopM = 7f;                    // start cabin and roll-in ramp are at path 5 m and below
        public static readonly float UTurnSpeedMs = 4f, UTurnBrakeDecelMs2 = 3.5f, UTurnHardDecelMs2 = 5f, UTurnAssistMs = 2.5f, UTurnMaxSeconds = 8f;
        // fitness physics: CdA / Crr come from the bridge's bike preset (`rider` message), air density / drivetrain / drive force are src\SpeedModel.cs
        public static readonly float WattSmoothSeconds = 1f, TrainerTimeoutSeconds = 1.5f, TrainerFadeSeconds = 1f, FitnessOffSeconds = 10f;
        public static readonly float MaxAccelMs2 = 4f, MaxDecelMs2 = 5f, MaxLeadMs = 1.5f;   // an instant speed change throws the rider (F7 lesson)
        public static readonly float PadDeadZone = 0.10f, PadBlendSeconds = 1f, PedalAnim = 0.35f;
        public static readonly float CadenceLegsScale = 1f;   // crank animation cycles per pedal revolution
        // spectators
        public static readonly float SpectatorHatDropM = 0.05f, SpectatorFemaleShare = 0.4f, SpectatorCullM = 120f, SpectatorExciteM = 25f, SpectatorOffsetM = 5f;
        public static readonly bool SpectatorFemaleHair = true;
        public static readonly bool SpectatorHelmets = false;   // bare heads: hats and ears sit on the head (docs/SPECTATORS.md "Look")
        public static readonly bool SpectatorHair = false;      // experiment: the game's hair meshes hang out from under a helmet (a slab on a bare head) and applying them briefly changes the real rider's hair
        public static readonly float SpectatorNoiseGapS = 2.5f;
        public static readonly string[] SpectatorNoiseEvents = { "event:/sfx/noisemakers/bell/cow_high", "event:/sfx/noisemakers/bell/cow_low", "event:/sfx/noisemakers/horn/classic_1", "event:/sfx/noisemakers/horn/classic_2", "event:/sfx/noisemakers/horn/vuvu_good" };
        public static readonly int SpectatorBellRings = 3;
        // workouts
        public static readonly bool ErgPadEnabled = true;         // read the pad through XInput for the RB + D-pad chord

        public static void Load()
        {
            _cat = MelonPreferences.CreateCategory("DescendersCyclingPOC", "Descenders Cycling POC");

            // general
            var hudKey = _cat.CreateEntry("HudToggleKey", "F9", "HUD toggle key (Unity KeyCode name)");
            var hudDefault = _cat.CreateEntry("HudVisibleAtStart", true, "Show HUD at startup");
            var hudSide = _cat.CreateEntry("HudSide", "right", "HUD side of the screen: right or left");
            var autoStart = _cat.CreateEntry("AutoStart", true, "Skip the menus: when the main menu appears, load AutoStartSeed straight into the course (once per game launch)");
            var autoSeed = _cat.CreateEntry("AutoStartSeed", "20012021493", "World seed to auto-load: '<seed>' or '<seed>-<modifiers>' (default = the forest test course, docs/testcourse_worldseed.md)");
            // fitness
            var fitOn = _cat.CreateEntry("FitnessEnabled", true, "Fitness mode master switch: power from the bridge (trainer messages) drives the bike speed (docs/FORCE_MODEL.md)");
            var gscale = _cat.CreateEntry("ClimbPhysicsScale", 0.35f, "Fitness: multiplies the road gradient of CLIMBS in the power/speed physics only, i.e. how fast you climb on screen (1 = real gradients; 0.35 = a 13 % climb rides like 4.5 %). What your legs feel is the bridge's trainer difficulty, set in the bridge window");
            var gscaleDown = _cat.CreateEntry("DescentGradientScale", 0.7f, "Fitness: like ClimbPhysicsScale but for DESCENTS (physics only; the trainer never goes below 0 %). 1 = real gravity; lower = slower descents");
            var descPow = _cat.CreateEntry("DescentPowerFactor", 2.5f, "Fitness: on descents your watts count this many times over, so pedalling still adds speed (physics only; HUD and trainer use your real watts)");
            var cadLegs = _cat.CreateEntry("CadenceLegs", true, "Fitness: the rider's legs, cranks and chain sound turn at the trainer's cadence");
            // auto-steer
            var asKey = _cat.CreateEntry("AutoSteerKey", "Home", "Toggle path-following auto-steer (needs a generated course)");
            var asAuto = _cat.CreateEntry("AutoSteerAutoEngage", true, "Engage auto-steer by itself when heart-rate or cadence data arrives from the trainer link");
            var sfxMode = _cat.CreateEntry("AutoSteerSound", "game", "Auto-steer sound cues: game (FMOD UI events), beep (synthesised tones), off");
            var utOn = _cat.CreateEntry("UTurnEnabled", true, "U-turns: in fitness mode slow the bike near each end of the course and turn it round automatically (auto-steer on)");
            var utSide = _cat.CreateEntry("UTurnSide", 1, "U-turns: 1 = turn right, -1 = turn left");
            // workouts / ERG
            var ergKey = _cat.CreateEntry("ErgToggleKey", "F4", "ERG: key toggling freeride / ERG (pad: RB + D-pad Up)");
            var ergUp = _cat.CreateEntry("ErgUpKey", "PageUp", "ERG: key raising the target one step (10 W; in a workout +5 %) (pad: RB + D-pad Right)");
            var ergDn = _cat.CreateEntry("ErgDownKey", "PageDown", "ERG: key lowering the target one step (10 W; in a workout -5 %) (pad: RB + D-pad Left)");
            var ergSkip = _cat.CreateEntry("ErgSkipKey", "Delete", "Workout: key skipping to the next step (pad: RB + D-pad Down)");
            var padIdx = _cat.CreateEntry("PadIndex", 0, "XInput pad index (0-3) for the RB + D-pad chords");
            var wRows = _cat.CreateEntry("WorkoutHudRows", 8, "Workout panel: how many upcoming sections to list under the current one");
            var wScale = _cat.CreateEntry("WorkoutHudScale", 1f, "Workout panel: size multiplier (on top of the screen-height scaling)");
            var wOpac = _cat.CreateEntry("WorkoutHudOpacity", 0.5f, "Workout panel: backdrop opacity, 0.15 (see-through) to 1 (solid)");
            var woDone = _cat.CreateEntry("WorkoutDoneSound", "event:/sfx/general/finish_line", "FMOD event played when a workout is completed (blank = none)");
            var woStep = _cat.CreateEntry("WorkoutStepSound", "event:/ui/gameplay/stunt_cleanlanding", "FMOD event played when a workout moves on to its next step (blank = none)");
            var woCd = _cat.CreateEntry("WorkoutCountdownSound", "event:/ui/postrun/health_plus_one", "FMOD event ticked once a second over the last WorkoutCountdownS seconds of each step (blank = none)");
            var woCdS = _cat.CreateEntry("WorkoutCountdownS", 5, "Seconds of countdown ticks before each workout step ends (0 = none)");
            var woConf = _cat.CreateEntry("WorkoutConfettiSeconds", 6f, "Seconds of confetti raining down the screen when a workout is completed (0 = none)");
            // spectators
            var specKey = _cat.CreateEntry("SpectatorKey", "F10", "Spectators: key that places / removes the crowd beside the course");
            var specN = _cat.CreateEntry("SpectatorCount", 12, "Spectators: how many to place along the path, in small groups (1-512; only those within 120 m are animated, about 40 us per frame each)");
            var specHats = _cat.CreateEntry("SpectatorHats", true, "Spectators: random hat / crown / mohawk / animal head");
            var specClothes = _cat.CreateEntry("SpectatorClothes", true, "Spectators: random shirt and trousers");
            var specNoise = _cat.CreateEntry("SpectatorNoiseShare", 0.35f, "Spectators: share (0-1) carrying a noisemaker (cow bell, air horn, vuvuzela). 0 = silent crowd");
            var specNoiseVol = _cat.CreateEntry("SpectatorNoiseVolume", 2f, "Spectators: noisemaker volume (0-4; above 1 amplifies)");
            // bridge
            var ipcEnabled = _cat.CreateEntry("IpcEnabled", true, "Enable localhost UDP bridge");
            var telemetryPort = _cat.CreateEntry("TelemetryPort", 9871, "UDP port telemetry is sent TO (127.0.0.1)");
            var commandPort = _cat.CreateEntry("CommandPort", 9872, "UDP port commands are received ON (127.0.0.1)");
            var brAuto = _cat.CreateEntry("BridgeAutoStart", true, "Start the trainer bridge with the game when none is running (no window). It keeps running when the game quits, and stops by itself once no game has been connected for 10 minutes. Skipped when a bridge (or its window) is already running");
            var brPath = _cat.CreateEntry("BridgePath", DefaultBridgePath, "Where the trainer bridge is: relative to the game folder, or absolute");

            HudKey = ParseKey(hudKey.Value, KeyCode.F9); HudVisibleAtStart = hudDefault.Value;
            HudSide = string.Equals(hudSide.Value, "left", StringComparison.OrdinalIgnoreCase) ? "left" : "right";
            AutoStart = autoStart.Value; AutoStartSeed = autoSeed.Value;

            FitnessEnabled = fitOn.Value;
            ClimbPhysicsScale = Mathf.Clamp(gscale.Value, 0.05f, 2f); DescentGradientScale = Mathf.Clamp(gscaleDown.Value, 0.05f, 2f); DescentPowerFactor = Mathf.Clamp(descPow.Value, 1f, 6f);
            CadenceLegs = cadLegs.Value;

            AutoSteerKey = ParseKey(asKey.Value, KeyCode.Home); AsAutoEngage = asAuto.Value; SfxMode = SfxNorm(sfxMode.Value);
            UTurnEnabled = utOn.Value; UTurnSide = utSide.Value >= 0 ? 1 : -1;

            ErgToggleKey = ParseKey(ergKey.Value, KeyCode.F4); ErgUpKey = ParseKey(ergUp.Value, KeyCode.PageUp); ErgDownKey = ParseKey(ergDn.Value, KeyCode.PageDown);
            ErgSkipKey = ParseKey(ergSkip.Value, KeyCode.Delete);
            PadIndex = Clamp(padIdx.Value, 0, 3);
            WorkoutHudRows = Clamp(wRows.Value, 1, 30); WorkoutHudScale = Mathf.Clamp(wScale.Value, 0.5f, 3f); WorkoutHudOpacity = Mathf.Clamp(wOpac.Value, 0.15f, 1f);
            WorkoutDoneSound = (woDone.Value ?? "").Trim(); WorkoutStepSound = (woStep.Value ?? "").Trim(); WorkoutCountdownSound = (woCd.Value ?? "").Trim();
            WorkoutCountdownS = Clamp(woCdS.Value, 0, 30); WorkoutConfettiS = Mathf.Clamp(woConf.Value, 0f, 30f);

            SpectatorKey = ParseKey(specKey.Value, KeyCode.F10); SpectatorCount = Clamp(specN.Value, 1, 512);
            SpectatorHats = specHats.Value; SpectatorClothes = specClothes.Value;
            SpectatorNoiseShare = Mathf.Clamp01(specNoise.Value); SpectatorNoiseVolume = Mathf.Clamp(specNoiseVol.Value, 0f, 4f);

            IpcEnabled = ipcEnabled.Value;
            TelemetryPort = Clamp(telemetryPort.Value, 1024, 65535);
            CommandPort = Clamp(commandPort.Value, 1024, 65535);
            BridgeAutoStart = brAuto.Value;
            // The exe was renamed descenders-bridge.exe -> turbotrainer-bridge.exe: move a cfg still holding the old default to the new one.
            if (string.Equals((brPath.Value ?? "").Trim(), "DescendersBridge\\descenders-bridge.exe", StringComparison.OrdinalIgnoreCase)) brPath.Value = DefaultBridgePath;
            BridgePath = string.IsNullOrEmpty(brPath.Value) ? DefaultBridgePath : brPath.Value.Trim();

            _cat.SaveToFile(false);
        }

        /// <summary>
        /// The bridge's rider profile (docs/IPC.md `rider`), clamped (<see cref="RiderState.Apply"/>). Game thread. Logs once per change; a repeat of
        /// the same values does nothing. The FTP only colours the workout panel: the bridge turns workout powers into watts.
        /// </summary>
        public static void ApplyBridgeRider(float riderKg, float bikeKg, float ftpW, string bikeType, float cdA, float crr)
        {
            bool first = !Rider.Known;
            if (!Rider.Apply(riderKg, bikeKg, ftpW, bikeType, cdA, crr)) return;
            Log.Info("Rider profile from bridge: " + Rider.RiderKg.ToString("0.#") + " kg + " + Rider.BikeKg.ToString("0.#") + " kg, FTP " + Rider.FtpW.ToString("0") + " W, "
                + Rider.BikeType + " bike (CdA " + Rider.CdA.ToString("0.###") + ", Crr " + Rider.Crr.ToString("0.####") + (bikeType == null ? ", the bridge sent no bike type" : "") + ")"
                + (first ? " (fitness mode can start)" : ""));
        }

        private static string SfxNorm(string s) { s = (s ?? "").Trim().ToLowerInvariant(); return s == "beep" || s == "off" ? s : "game"; }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private static KeyCode ParseKey(string s, KeyCode fallback)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), s, true); }
            catch (Exception)
            {
                Log.Warn("Config: '" + s + "' is not a valid KeyCode, using " + fallback);
                return fallback;
            }
        }
    }
}
