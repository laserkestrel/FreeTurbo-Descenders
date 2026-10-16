#nullable disable

using System;
using System.Text;

// Pure logic (no Unity, no logging): also compiled into the bridge (bridge/DescendersBridge.csproj links this file) so `selftest` checks the
// words of the game's HUD (tests\unit\HudRulesTests.cs).
namespace FreeTurboDescenders
{
    /// <summary>
    /// The rider block at the top of the game's HUD: TRAINER, MODE, AUTOSTEER and PARTY MODE, each toggle with its key beside it,
    /// in the bridge window's teal. No climbed metres and no build line: both are in the bridge window.
    /// </summary>
    internal static class HudRules
    {
        /// <summary>The bridge window's accent (its `AccentRules.DefaultHex`): the HUD's normal text. Warnings keep their own colours.</summary>
        public const string AccentHex = "#00A0B1";
        public const byte AccentR = 0, AccentG = 160, AccentB = 177;
        public const string WarnHex = "#ffcc00";   // something is on that changes the ride, or the trainer is not there yet

        /// <summary>Pixels (at 1080 lines) the HUD starts below its old place: the game's own status box is in that corner.</summary>
        public const float TopOffsetPx = 50f;
        public const float EdgePx = 12f;
        /// <summary>Top of the HUD. The game's HUD scales with the screen height only (docs/HUD_PLACEMENT.md), so the offset does too.</summary>
        public static float TopY(int screenHeight) { return EdgePx + TopOffsetPx * Math.Max(1, screenHeight) / 1080f; }

        public const string OldAutoSteerKey = "Home", AutoSteerKey = "F8";
        /// <summary>The auto-steer key moved from Home to F8, beside the mod's other keys. A cfg still holding the old standard key moves with it;
        /// any other key is the rider's own choice and stays.</summary>
        public static string MigrateAutoSteerKey(string saved)
        {
            return string.Equals((saved ?? "").Trim(), OldAutoSteerKey, StringComparison.OrdinalIgnoreCase) ? AutoSteerKey : saved;
        }

        public const string OldModeKey = "F4", ModeKey = "F7";
        /// <summary>The mode key moved from F4 to F7: the mod's keys are F7 to F10 in a row. As with the auto-steer key, a cfg still
        /// holding the old standard key moves with it; any other key is the rider's own choice and stays.</summary>
        public static string MigrateModeKey(string saved)
        {
            return string.Equals((saved ?? "").Trim(), OldModeKey, StringComparison.OrdinalIgnoreCase) ? ModeKey : saved;
        }

        /// <summary>"AUTOSTEER (F8)": the name of a line with the key that changes it. No key ("", "None") = the name alone.</summary>
        public static string Label(string name, string key)
        {
            key = (key ?? "").Trim();
            return key.Length == 0 || string.Equals(key, "None", StringComparison.OrdinalIgnoreCase) ? name : name + " (" + key + ")";
        }

        /// <summary>The TRAINER line's words. "No ANT+ USB stick found" is wrong for a rider on Bluetooth, so the HUD says "Not connected".</summary>
        public static string TrainerText(string state, string text)
        {
            text = (text ?? "").Trim();
            if (state == "nostick" && (text.Length == 0 || text.StartsWith("No ANT+", StringComparison.OrdinalIgnoreCase))) return "Not connected";
            return text;
        }

        /// <summary>The whole rider block as IMGUI rich text. <paramref name="autoSteer"/> / <paramref name="party"/>: null = not known yet ("--").</summary>
        public static string RiderBlock(string trainer, bool trainerOk, string modeLabel, bool modeHot, string modeKey,
            bool? autoSteer, string autoSteerKey, bool? party, string partyKey, string note)
        {
            var b = new StringBuilder(256);
            if (!string.IsNullOrEmpty(trainer)) b.Append(Line("TRAINER", 16, trainerOk ? trainer : Warn(trainer)));
            // No "no trainer bridge" here: the TRAINER line already says when nothing is connected.
            b.Append(Line(Label("MODE", modeKey), 18, modeHot ? Warn(modeLabel) : modeLabel));
            b.Append(Line(Label("AUTOSTEER", autoSteerKey), 18, OnOff(autoSteer)));
            b.Append(Line(Label("PARTY MODE", partyKey), 18, OnOff(party)));
            if (!string.IsNullOrEmpty(note)) b.Append("<size=16>" + Warn(note) + "</size>\n");
            // the workout itself is the WorkoutPanel; its .zwo text events are shown mid-screen by GameText
            return b.ToString().TrimEnd('\n');
        }

        private static string Line(string label, int size, string value) { return "<size=13>" + label + "</size>  <size=" + size + "><b>" + value + "</b></size>\n"; }
        private static string OnOff(bool? on) { return !on.HasValue ? "--" : on.Value ? Warn("ON") : "OFF"; }
        private static string Warn(string s) { return "<color=" + WarnHex + ">" + s + "</color>"; }
    }
}
