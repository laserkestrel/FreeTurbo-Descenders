using System.Drawing;
using DescendersBridge.Ant;
using DescendersBridge.Gui;

namespace DescendersBridge;

/// <summary>
/// The window's accent (the bar under the selected tab, the filled buttons, focus, the graphs) is teal #00A0B1 instead of blue
/// #3987E5, and `window.accentColour` of TrainerBridgeConfig.json changes it (edited by hand, no field in the window). The pure rules, the
/// theme and the settings section. Uses a temp folder, never the real %APPDATA% file.
/// </summary>
public static class AccentColourTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Accent colour: teal #00A0B1, window.accentColour changes it");
        check(AccentRules.DefaultHex == "#00A0B1" && AccentRules.Default == Color.FromArgb(255, 0, 160, 177), "the standard accent is teal, RGB 0, 160, 177");
        check(AccentRules.OldBlueHex == "#3987E5" && AccentRules.OldBlue == Theme.Hex(0x3987E5), "the old blue is kept by name, to change back");

        check(AccentRules.TryParse("#00A0B1", out var c) && c == AccentRules.Default && AccentRules.TryParse(" 3987e5 ", out c) && c == AccentRules.OldBlue,
            "a colour is six hex digits, with or without #, either case");
        check(new[] { null, "", "teal", "#00A0B", "#00A0B1FF", "#00A0BG", "0x00A0B1" }.All(s => !AccentRules.TryParse(s, out _)), "anything else is not a colour");
        check(AccentRules.ToHex(Color.FromArgb(255, 0, 160, 177)) == "#00A0B1", "a colour is written back as #RRGGBB");

        // what is worked out from the one colour
        var teal = AccentRules.For(AccentRules.Default, Theme.Page, Theme.Surface);
        check(teal.Accent == AccentRules.Default, "teal is used as written (it stands out on a card)");
        check(Buttons(teal).All(b => Theme.Contrast(Color.White, b) >= 4.4) && Theme.Contrast(Color.White, teal.Accent) < 4.4,
            $"teal buttons: white text reads 4.4:1 or better in every state, on deeper steps of the teal ({AccentRules.ToHex(teal.Button)})");
        check(Buttons(teal).Distinct().Count() == 3 && Theme.Contrast(Color.White, teal.ButtonHover) < Theme.Contrast(Color.White, teal.Button)
            && Theme.Contrast(Color.White, teal.Button) < Theme.Contrast(Color.White, teal.ButtonPressed), "hover is the lightest button step, pressed the darkest");
        check(Theme.Contrast(Theme.Text, teal.Wash) >= 7 && Theme.Contrast(Theme.Text, teal.InfoWash) >= 7 && teal.Wash != Theme.Field && teal.Wash.B > teal.Wash.R,
            "a selected row and the information bar: dark, tinted with the accent, text 7:1 or better");

        var blue = AccentRules.For(AccentRules.OldBlue, Theme.Page, Theme.Surface);
        check(blue.Accent == Theme.Hex(0x3987E5) && blue.Wash == Theme.Hex(0x1C2C45) && blue.InfoWash == Theme.Hex(0x15253C) && blue.Button == Theme.Hex(0x256ABF)
            && blue.ButtonHover == Theme.Hex(0x2A78D6) && blue.ButtonPressed == Theme.Hex(0x1C5CAB), "the old blue gives exactly the colours from before the issue");

        foreach (int rgb in new[] { 0x000000, 0x101020, 0xFFFFFF, 0xFFE000, 0xFF0000, 0x00FF00, 0x0000FF, 0x7F7F7F })
        {
            var p = AccentRules.For(Theme.Hex(rgb), Theme.Page, Theme.Surface);
            check(Theme.Contrast(p.Accent, Theme.Surface) >= 3 && Theme.Contrast(p.Accent, Theme.Page) >= 3 && Buttons(p).All(b => Theme.Contrast(Color.White, b) >= 4.4)
                && Theme.Contrast(Theme.Text, p.Wash) >= 7 && Theme.Contrast(Theme.Text, p.InfoWash) >= 7,
                $"any colour gives a usable window: #{rgb:X6} -> accent {AccentRules.ToHex(p.Accent)}, button {AccentRules.ToHex(p.Button)}");
        }

        // the theme
        try
        {
            Theme.SetAccent(AccentRules.OldBlueHex);
            check(Theme.Accent == AccentRules.OldBlue && Theme.AccentButton == Theme.Hex(0x256ABF) && Theme.InfoWash == Theme.Hex(0x15253C)
                && Theme.ZoneColor(EffortZone.Endurance) == AccentRules.OldBlue && Theme.HealthColor(Health.Searching) == AccentRules.OldBlue,
                "the theme takes a colour: buttons, the information bar, the endurance zone and the searching light follow");
            Theme.SetAccent("not a colour");
            check(Theme.Accent == AccentRules.Default, "not a colour: teal");
        }
        finally { Theme.SetAccent(AccentRules.DefaultHex); }
        check(Theme.Accent == AccentRules.Default && Theme.AccentButton == teal.Button && Theme.AccentWash == teal.Wash, "the theme's standard accent is teal");
        check(Enum.GetValues<Health>().Select(Theme.HealthColor).Distinct().Count() == 6, "with teal each health state still has its own light colour");

        // the `window` section of the settings file
        string dir = Path.Combine(Path.GetTempPath(), "descenders_accent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string p = Path.Combine(dir, TrainerBridgeConfig.FileName);
            var log = new List<string>();
            check(TrainerBridgeConfig.LoadAccentColour(p) == "#00A0B1" && TrainerBridgeConfig.EnsureAccentColour(p, log.Add) == "#00A0B1" && !File.Exists(p) && log.Count == 0,
                "no settings file: teal, and none is made for it");

            TrainerBridgeConfig.SaveRider(p, new RiderConfig { FtpW = 250 });
            TrainerBridgeConfig.EnsureGraphHistorySeconds(p, _ => { });
            check(TrainerBridgeConfig.LoadAccentColour(p) == "#00A0B1" && !File.ReadAllText(p).Contains("accentColour"), "a file without the setting: teal; reading does not change the file");
            check(TrainerBridgeConfig.EnsureAccentColour(p, log.Add) == "#00A0B1" && File.ReadAllText(p).Contains("\"accentColour\": \"#00A0B1\"") && log.Count == 1
                && log[0].Contains("window.accentColour added") && log[0].Contains("#3987E5"),
                "the window writes the setting into the file with teal, once, and the log line names the old blue");
            check(TrainerBridgeConfig.EnsureAccentColour(p, log.Add) == "#00A0B1" && log.Count == 1, "the next start leaves the file alone");
            check(TrainerBridgeConfig.Load(p, "", "", () => null, _ => { }).Rider.FtpW == 250 && TrainerBridgeConfig.LoadGraphHistorySeconds(p) == 300
                && File.ReadAllText(p).Contains("historySeconds"), "the rider and graphs sections are kept");

            File.WriteAllText(p, File.ReadAllText(p).Replace("\"#00A0B1\"", "\"3987e5\""));
            check(TrainerBridgeConfig.LoadAccentColour(p) == "#3987E5" && TrainerBridgeConfig.EnsureAccentColour(p, log.Add) == "#3987E5" && log.Count == 1, "a hand-edited colour is used");
            TrainerBridgeConfig.SaveRider(p, new RiderConfig { FtpW = 260 });
            TrainerBridgeConfig.SaveTraining(p, new TrainingConfig());
            TrainerBridgeConfig.SaveLogsDir(p, "");
            check(TrainerBridgeConfig.LoadAccentColour(p) == "#3987E5", "rider, training and logs saves keep it");

            File.WriteAllText(p, "{ \"WINDOW\": { \"AccentColour\": \"#ff8800\" } }");
            check(TrainerBridgeConfig.LoadAccentColour(p) == "#FF8800", "names are case-insensitive");
            foreach (string bad in new[] { "\"teal\"", "12", "\"\"" })
            {
                string text = "{ \"window\": { \"accentColour\": " + bad + " } }";
                File.WriteAllText(p, text);
                int before = log.Count;
                check(TrainerBridgeConfig.LoadAccentColour(p) == "#00A0B1" && TrainerBridgeConfig.EnsureAccentColour(p, log.Add) == "#00A0B1" && File.ReadAllText(p) == text
                    && log.Count == before + 1 && log[^1].Contains("is not a colour"), $"not a colour ({bad}): teal, a log line, and the file is left as written");
            }
            File.WriteAllText(p, "{ broken");
            int count = log.Count;
            check(TrainerBridgeConfig.LoadAccentColour(p) == "#00A0B1" && TrainerBridgeConfig.EnsureAccentColour(p, log.Add) == "#00A0B1" && File.ReadAllText(p) == "{ broken" && log.Count == count,
                "an unreadable settings file: teal, and it is left alone (never throws)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static Color[] Buttons(AccentPalette p) => new[] { p.Button, p.ButtonHover, p.ButtonPressed };
}
