#nullable disable

using System;
using System.IO;

// Pure logic (no Unity, no logging): also compiled into the bridge (bridge/DescendersBridge.csproj links this file) so `selftest` checks it
// (tests\unit\WorkoutShotTests.cs).
namespace FreeTurboDescenders
{
    /// <summary>
    /// The picture of a completed workout (taken by WorkoutShot.cs): where it is saved and how long after the `complete` event.
    /// </summary>
    internal static class WorkoutShotRules
    {
        /// <summary>Always this one name, overwritten after each workout: never dated or numbered, the rider's PC must not fill up with pictures.</summary>
        public const string FileName = "last-workout.png";
        /// <summary>The folder of the user's settings file (the bridge's TrainerBridgeConfig.Dir), under %APPDATA%.</summary>
        public const string FolderName = "TrainerBridge";
        /// <summary>The confetti fades out over its last second (Confetti.OnGUI).</summary>
        public const float ConfettiFadeS = 1f;

        /// <summary>The picture's full path for the given %APPDATA% folder; "" when that folder is not known.</summary>
        public static string FilePath(string appData)
        {
            appData = (appData ?? "").Trim();
            return appData.Length == 0 ? "" : Path.Combine(Path.Combine(appData, FolderName), FileName);
        }

        /// <summary>
        /// Seconds after the `complete` event at which the picture is taken. <paramref name="wantedS"/> (Config.WorkoutShotDelayS) when the
        /// confetti is still fully showing then, and when there is no confetti at all. A shorter shower: just before it starts to fade, and
        /// never later than half way through it, so the confetti is in the picture.
        /// </summary>
        public static float DelayS(float confettiS, float wantedS)
        {
            if (float.IsNaN(wantedS) || wantedS < 0f) wantedS = 0f;
            if (float.IsNaN(confettiS) || confettiS <= 0f) return wantedS;
            return Math.Min(wantedS, Math.Max(confettiS - ConfettiFadeS, confettiS * 0.5f));
        }
    }
}
