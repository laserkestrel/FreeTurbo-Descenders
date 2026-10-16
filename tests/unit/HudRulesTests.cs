using DescendersBridge.Gui;
using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// The game's HUD (src\HudRules.cs, drawn by src\Hud.cs): no CLIMBED and no build line, the key beside MODE / AUTOSTEER /
/// PARTY MODE, "Not connected" instead of the ANT+ stick words, no "no trainer bridge" on the MODE line, the bridge window's teal, 50 px
/// lower, and the auto-steer key on F8. The mode key on F7.
/// </summary>
public static class HudRulesTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Game HUD: keys beside the toggles, PARTY MODE, Not connected, teal");
        string hud = HudRules.RiderBlock("Trainer connected", true, "FREERIDE", false, "F7", false, "F8", false, "F10", "");
        string[] lines = hud.Split('\n');
        check(lines.Length == 4 && lines[0].Contains("TRAINER") && lines[0].Contains("Trainer connected"), "four lines, the first is TRAINER");
        check(lines[1].Contains("MODE (F7)") && lines[1].Contains("FREERIDE"), "MODE has its key beside it");
        check(lines[2].Contains("AUTOSTEER (F8)") && lines[2].Contains("<b>OFF</b>"), "AUTOSTEER has its key beside it");
        check(lines[3].Contains("PARTY MODE (F10)") && lines[3].Contains("<b>OFF</b>"), "PARTY MODE (F10) and whether the crowd is out");
        check(!hud.Contains("CLIMBED") && !hud.ToLowerInvariant().Contains("build") && !hud.EndsWith("\n"), "no climbed metres, no build line, no empty last line");
        check(!hud.Contains("<color"), "nothing coloured when all is well and off: the text is the HUD's teal");

        string on = HudRules.RiderBlock("Trainer connected", true, "ERG 150 W", true, "F7", true, "F8", true, "F10", "TARGET 150 W");
        string[] onLines = on.Split('\n');
        check(onLines.Length == 5 && onLines[2].Contains(">ON<") && onLines[3].Contains(">ON<") && onLines[4].Contains("TARGET 150 W"), "auto-steer on, crowd on, and the note as the last line");
        check(onLines[1].Contains(HudRules.WarnHex) && onLines[2].Contains(HudRules.WarnHex), "ERG and ON keep their warning colour");

        string none = HudRules.RiderBlock("Not connected", false, "FREERIDE", false, "F7", null, "F8", null, "F10", null);
        check(!none.Contains("no trainer bridge") && none.Split('\n')[1].IndexOf("color", StringComparison.Ordinal) < 0, "no bridge session: the MODE line is FREERIDE alone, no \"no trainer bridge\"");
        check(none.Split('\n')[0].Contains(HudRules.WarnHex + ">Not connected"), "a trainer that is not connected stays in the warning colour");
        check(none.Contains("AUTOSTEER (F8)</size>  <size=18><b>--</b>"), "not known yet is --");
        check(HudRules.RiderBlock("", false, "FREERIDE", false, "F7", false, "F8", false, "F10", "").Split('\n').Length == 3, "no TRAINER words = no TRAINER line");

        check(HudRules.TrainerText("nostick", "No ANT+ USB stick found") == "Not connected", "\"No ANT+ USB stick found\" is shown as \"Not connected\" (wrong on Bluetooth)");
        check(HudRules.TrainerText("nostick", "") == "Not connected" && HudRules.TrainerText("nostick", null) == "Not connected", "no stick and no words: Not connected");
        check(HudRules.TrainerText("nostick", "ANT+ stick lost: reconnecting") == "ANT+ stick lost: reconnecting", "a stick lost mid-ride keeps its own words");
        check(HudRules.TrainerText("searching", "Searching for trainer") == "Searching for trainer" && HudRules.TrainerText("connected", "Trainer connected") == "Trainer connected"
            && HudRules.TrainerText("error", "UDP port 9871 is in use by another program") == "UDP port 9871 is in use by another program", "every other status is shown as the bridge sent it");

        check(HudRules.Label("MODE", "F7") == "MODE (F7)" && HudRules.Label("MODE", "None") == "MODE" && HudRules.Label("MODE", "") == "MODE" && HudRules.Label("MODE", null) == "MODE",
            "a line with no key shows its name alone");

        check(HudRules.AutoSteerKey == "F8" && HudRules.MigrateAutoSteerKey("Home") == "F8" && HudRules.MigrateAutoSteerKey(" home ") == "F8", "the auto-steer key is F8; a saved Home (the old standard key) moves to it");
        check(HudRules.MigrateAutoSteerKey("F5") == "F5" && HudRules.MigrateAutoSteerKey("End") == "End" && HudRules.MigrateAutoSteerKey("F8") == "F8", "a key the rider chose stays");

        // the mode key is F7, so the mod's keys are F7 / F8 / F9 / F10 in a row and F6 (another mod's key) is left alone
        check(HudRules.ModeKey == "F7" && HudRules.MigrateModeKey("F4") == "F7" && HudRules.MigrateModeKey(" f4 ") == "F7", "the mode key is F7; a saved F4 (the old standard key) moves to it");
        check(HudRules.MigrateModeKey("F5") == "F5" && HudRules.MigrateModeKey("M") == "M" && HudRules.MigrateModeKey("F7") == "F7" && HudRules.MigrateModeKey(null) == null, "a mode key the rider chose stays");

        check(HudRules.AccentHex ==AccentRules.DefaultHex && AccentRules.Default.R == HudRules.AccentR && AccentRules.Default.G == HudRules.AccentG && AccentRules.Default.B == HudRules.AccentB,
            "the HUD's text is the bridge window's teal #00A0B1");
        check(HudRules.TopY(1080) == 62f && HudRules.TopY(2160) == 112f && HudRules.TopY(720) > 45f && HudRules.TopY(720) < 46f, "the HUD starts 50 px lower at 1080 lines, in step with the screen height");
    }
}
