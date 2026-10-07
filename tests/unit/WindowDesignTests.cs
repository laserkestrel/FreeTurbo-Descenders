using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using DescendersBridge.Ant;
using DescendersBridge.Gui;

namespace DescendersBridge;

/// <summary>
/// The window's redesign. The pure rules (palette contrast, the device-choice lights, the tab widths), then the real window in
/// demo mode: it is built, shown off-screen (nobody sees it), laid out at the minimum and the normal size and painted tab by tab into
/// bitmaps, so a control that lost its Name, falls off the edge, overlaps its neighbour or throws while painting fails `selftest`.
/// `selftest --snapshots &lt;folder&gt;` keeps the bitmaps as PNG files (for a pull request, or to look at a change without a desktop).
/// The "My devices" tab at the size the window opens at needs no scroll bar.
/// The Training tab has no "Workouts" card (the workout step sits under "Trainer feel", the pause time is in the settings file
/// only and a Save keeps it) and needs no scroll bar at the size the window opens at, nor on a 1366 x 768 screen.
/// The tabs are called Resistance and Logs (Names unchanged); the settings file's path is on the Logs tab only, above the log;
/// no "(saved)" after a device's name; no "Not riding" line on the Workouts tab; the Logs tab leaves the settings printout out.
/// No Start / Stop button; the window connects by itself when it opens (the demo ride here), a device search pauses the
/// connection and brings it back, a Save just says "Saved.".
/// The Ride tab is the four reading tiles and one slim status strip (Trainer, Game); no "Riding / Not riding" line, no top
/// bar, no Games card, no buttons; every status light is one size.
/// Temp settings file only, never the real %APPDATA% one.
/// </summary>
public static class WindowDesignTests
{
    /// <summary>Every control Name (= UI Automation id) tests\bridge-gui\GuiSmoke.ps1 or a screen reader may look for. Never rename one.</summary>
    private static readonly string[] Names =
    {
        "MessagePanel", "MessageText", "MessageAction", "MessageClose", "Tabs", "RideTab", "DevicesTab", "RiderTab", "TrainingTab", "WorkoutsTab", "DetailsTab",
        "StatusStrip", "TrainerState", "GameState",   // RideState, RideBar and GamesBox are gone (one status strip)
        "PowerTile", "PowerTitle", "PowerValue", "PowerUnit", "PowerGraph", "PowerHealth", "CadenceTile", "CadenceValue", "CadenceGraph", "CadenceHealth",
        "HeartRateTile", "HeartRateValue", "HeartRateGraph", "HeartRateHealth", "SpeedTile", "SpeedValue", "SpeedGraph", "SpeedHealth",
        "SearchButton", "SearchProgress", "SearchStatus", "FoundList", "TrainerChoice", "HeartRateChoice", "CadenceChoice", "PowerChoice", "SaveDevicesButton", "DevicesStatus",
        "RiderWeight", "BikeWeight", "Ftp", "WheelDiameter", "GearRatio", "BikeType", "SaveRiderButton", "UndoRiderButton", "RiderStatus", "ConfigPath",
        "TrainerDifficulty", "ErgStartW", "ErgMinW", "ErgMaxW", "ErgStepW", "WorkoutBiasStepPct",
        "SaveTrainingButton", "UndoTrainingButton", "StandardTrainingButton", "TrainingStatus",
        "LibraryDir", "BrowseLibraryButton", "SaveLibraryButton", "LibraryStatus", "LogsDir", "BrowseLogsButton", "SaveLogsButton", "LogsStatus", "ActivePlan", "NextSession", "LogText", "OpenLogsButton",
        // workouts and training plans are chosen on the Workouts tab
        // (StartSessionButton and StartWorkoutButton are gone: Enter or a click on the chosen entry / its graph starts)
        "PlanChoice", "ResetPlanButton", "PlanProgress", "SessionChoice", "WorkoutFolderChoice", "WorkoutChoice", "WorkoutDetail", "NowRiding",
        // the four choices above are left / right pickers now (same Names); each card has an effort graph
        "SessionGraph", "WorkoutGraph",
        "PlanHint", "WorkoutHint",   // "Enter or click to start" under the card that has the focus
        "SearchNote",                // the quiet line under the search button ("No ANT+ stick found: searching Bluetooth only")
    };

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>A left click at <paramref name="at"/> of <paramref name="c"/>, as the mouse would send it (button down, button up).</summary>
    private static void ClickAt(Control c, Point at)
    {
        const int LeftDown = 0x0201, LeftUp = 0x0202, LeftHeld = 0x0001;
        var where = (IntPtr)((at.Y << 16) | (at.X & 0xFFFF));
        SendMessage(c.Handle, LeftDown, (IntPtr)LeftHeld, where);
        SendMessage(c.Handle, LeftUp, IntPtr.Zero, where);
    }

    /// <summary>`selftest --snapshots &lt;folder&gt;`: keep the painted pictures there as PNG files.</summary>
    public static string? SnapshotDir { get; set; }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Window design: palette, device-choice lights, tab widths");
        Rules(check);

        Console.WriteLine("Window design: the real window, off-screen, in demo mode");
        var results = new List<(bool Ok, string What)>();
        var ui = new Thread(() =>
        {
            try { Window(results); }
            catch (Exception ex) { results.Add((false, "the window builds, lays out and paints without an error (" + ex.GetType().Name + ": " + ex.Message + ")\n" + ex)); }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        foreach (var (ok, what) in results) check(ok, what);
    }

    private static void Rules(Action<bool, string> check)
    {
        // text must be readable on every surface it sits on; a status light must stand out from the card
        Color[] surfaces = { Theme.Page, Theme.Surface, Theme.Field };
        check(surfaces.All(s => Theme.Contrast(Theme.Text, s) >= 7 && Theme.Contrast(Theme.TextSecondary, s) >= 7), "main and secondary text: contrast 7:1 or better on the page, a card and a field");
        check(surfaces.All(s => Theme.Contrast(Theme.Muted, s) >= 4.5), "muted text (help lines): 4.5:1 or better on every surface");
        check(new[] { Theme.AccentButton, Theme.AccentButtonHover, Theme.DangerButton, Theme.DangerButtonHover, Theme.DangerButtonPressed, Theme.AccentButtonPressed }
            .All(c => Theme.Contrast(Color.White, c) >= 4.4), "white text on the filled buttons (Start, Save, Stop): 4.4:1 or better in every state");
        check(new[] { Theme.Good, Theme.Warning, Theme.Serious, Theme.Critical, Theme.Accent }.All(c => Theme.Contrast(c, Theme.Surface) >= 3 && Theme.Contrast(c, Theme.Page) >= 3),
            "status lights and the accent: 3:1 or better against a card and the page");
        check(Math.Abs(Theme.Contrast(Color.Black, Color.White) - 21) < 0.01 && Math.Abs(Theme.Contrast(Theme.Page, Theme.Page) - 1) < 1e-9, "contrast: black on white is 21, a colour on itself is 1");
        check(Enum.GetValues<Health>().Select(Theme.HealthColor).Distinct().Count() == 6, "each health state has its own light colour");

        var a = new DeviceKey(17, 12345, 5);
        var b = new DeviceKey(17, 222, 5);
        check(DeviceChoiceRules.Classify(null, null, false, false) == ChoiceState.NotSet && DeviceChoiceRules.Classify(null, null, false, true) == ChoiceState.NotSet, "device choice: nothing chosen, nothing saved = not set");
        check(DeviceChoiceRules.Classify(a, a, false, false) == ChoiceState.Saved, "device choice: the saved device, before any search = saved");
        check(DeviceChoiceRules.Classify(a, a, true, true) == ChoiceState.SavedFound, "device choice: the saved device answered in the search = saved and found");
        check(DeviceChoiceRules.Classify(a, a, false, true) == ChoiceState.SavedNotFound, "device choice: the saved device did not answer in the search = saved but not found");
        check(DeviceChoiceRules.Classify(b, a, true, true) == ChoiceState.Unsaved && DeviceChoiceRules.Classify(a, null, true, true) == ChoiceState.Unsaved
            && DeviceChoiceRules.Classify(null, a, false, false) == ChoiceState.Unsaved, "device choice: anything different from the settings file (another device, a new one, cleared) = not saved yet");
        check(Enum.GetValues<ChoiceState>().Select(s => DeviceChoiceRules.Text(s)).Distinct().Count() == 5 && DeviceChoiceRules.Text(ChoiceState.Unsaved) == "Not saved"
            && DeviceChoiceRules.Advice(ChoiceState.Unsaved).Contains("press Save") && DeviceChoiceRules.Text(ChoiceState.NotSet, trainerFallback: true).Contains("trainer"),
            "device choice: every state has its own words; unsaved says so and its tooltip says press Save; power / cadence fall back to the trainer");
        // the lights under the drop-downs use the Ride tab's words and colours (the state only, the rest in the tooltip)
        check(DeviceChoiceRules.Text(ChoiceState.NotSet) == HealthRules.Text(Health.NotChosen) && DeviceChoiceRules.Text(ChoiceState.SavedNotFound) == HealthRules.Text(Health.NotFound)
            && DeviceChoiceRules.Light(ChoiceState.NotSet) == Health.NotChosen && DeviceChoiceRules.Light(ChoiceState.SavedNotFound) == Health.NotFound
            && DeviceChoiceRules.Light(ChoiceState.SavedFound) == Health.Good && DeviceChoiceRules.Text(ChoiceState.SavedFound) == "Found",
            "device choice: \"Not set up\" and \"Not found\" are the Ride tab's own words and colours; found is the good light");
        check(Enum.GetValues<ChoiceState>().All(s => new[] { false, true }.All(f => DeviceChoiceRules.Text(s, f).Length <= 16 && !DeviceChoiceRules.Text(s, f).Contains("search")
                && DeviceChoiceRules.Advice(s, f).Length > DeviceChoiceRules.Text(s, f).Length))
            && Enum.GetValues<ChoiceState>().Select(DeviceChoiceRules.Light).Distinct().Count() == 5,
            "device choice: a light says the state in a few words, its tooltip the whole sentence; each state has its own colour");

        // the saved device's name carries no "(saved)" in the drop-down; the Logs tab leaves the settings printout out
        check(DeviceChoiceRules.Suffix(ChoiceOrigin.Saved) == "" && DeviceChoiceRules.Suffix(ChoiceOrigin.Heard) == "",
            "device drop-down: nothing after the name of the saved device or of a device the search heard (the light underneath says saved / found / not found)");
        check(DeviceChoiceRules.Suffix(ChoiceOrigin.UsedBefore) == " (used before)", "device drop-down: a device used before still says so");
        var sink = new LogSink();
        sink.WriteLine(TrainerBridgeConfig.Dump("C:\\x\\TrainerBridgeConfig.json", new UserConfig(new RiderConfig(), new DevicePreferences())));
        sink.WriteLine("Bridge config: no rider section in C:\\x\\TrainerBridgeConfig.json, using the defaults");
        sink.WriteLine("Devices saved:");
        string shown = sink.Drain();
        check(!shown.Contains("riderWeightKg") && !shown.Contains("{") && shown.Contains("Bridge config: no rider section") && shown.Contains("Devices saved:"),
            "Logs tab: the printout of the settings values is left out, every other line (settings problems too) is kept");

        check(ModernTabs.TabWidth(744, 6, 84, 150) == 122 && ModernTabs.TabWidth(744, 6, 84, 150) * 6 <= 744, "tabs: six share the minimum window's width (122 px each)");
        check(ModernTabs.TabWidth(2000, 6, 84, 150) == 150 && ModernTabs.TabWidth(300, 6, 84, 150) == 84 && ModernTabs.TabWidth(800, 0, 84, 150) == 150, "tabs: never wider than 150 px, never narrower than 84 px");

        // the size the window opens at follows the screen's scaling, within the screen
        check(BridgeWindow.StartSize(Size.Empty, 1) == new Size(960, 760) && BridgeWindow.StartSize(new Size(1920, 1040), 1) == new Size(960, 760), "start size: 960 x 760 at 100 % scaling");
        check(BridgeWindow.StartSize(new Size(1920, 1040), 1.25) == new Size(1200, 950), "start size: grows with the screen's scaling (1200 x 950 at 125 %)");
        check(BridgeWindow.StartSize(new Size(1366, 728), 1.25) == new Size(1200, 728) && BridgeWindow.StartSize(new Size(640, 480), 1) == BridgeWindow.SmallestSize,
            "start size: never bigger than the screen's free area, never under the minimum size");
    }

    private static void Window(List<(bool Ok, string What)> results)
    {
        void Check(bool ok, string what) => results.Add((ok, what));
        string dir = Path.Combine(Path.GetTempPath(), "descenders_window_" + Guid.NewGuid().ToString("N"));
        TrainerBridgeConfig.PathOverride = Path.Combine(dir, TrainerBridgeConfig.FileName);
        var realOut = Console.Out;
        Console.SetOut(TextWriter.Null);   // the window logs what it does (settings dumps, "Devices saved"): not part of the test report
        try
        {
            // an error while the window lays out or paints must come back here as a failed check, never as a dialog nobody is there to close
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
            using var w = new BridgeWindow(demo: true, new LogSink()) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false };
            var missing = Names.Where(n => w.Controls.Find(n, true).Length != 1).ToList();
            Check(missing.Count == 0, "every control keeps its Name / AutomationId" + (missing.Count > 0 ? " (missing or doubled: " + string.Join(", ", missing) + ")" : ""));
            using (var own = AppIcon.Load())
            {
                // the sizes come from the file's own list (6-byte header, then 16 bytes per picture, width first, 0 = 256): System.Drawing hands back 128 px when asked for 256
                var file = new MemoryStream();
                using (var s = typeof(AppIcon).Assembly.GetManifestResourceStream(AppIcon.ResourceName)) s?.CopyTo(file);
                byte[] ico = file.ToArray();
                var inFile = Enumerable.Range(0, ico.Length >= 6 ? BitConverter.ToUInt16(ico, 4) : 0).Select(n => ico[6 + 16 * n] == 0 ? 256 : ico[6 + 16 * n]).ToList();
                var sizes = new[] { 16, 24, 32, 48, 256 }.Where(s => !inFile.Contains(s)).ToList();
                static bool SamePicture(Icon a, Icon b)
                {
                    using Bitmap x = a.ToBitmap(), y = b.ToBitmap();
                    if (x.Size != y.Size) return false;
                    for (int row = 0; row < x.Height; row++)
                        for (int col = 0; col < x.Width; col++)
                            if (x.GetPixel(col, row) != y.GetPixel(col, row)) return false;
                    return true;
                }
                Check(own != null && sizes.Count == 0 && w.Icon != null && SamePicture(w.Icon, own),
                    "the window carries the bridge's own icon, drawn at 16, 24, 32, 48 and 256 px" + (sizes.Count > 0 ? " (missing: " + string.Join(", ", sizes) + ")" : ""));
            }
            w.Size = w.MinimumSize;
            w.Shown += async (_, _) =>
            {
                try { await Drive(w, Check); }
                catch (Exception ex) { Check(false, "the window can be driven through its tabs without an error (" + ex.GetType().Name + ": " + ex.Message + ")\n" + ex); }
                finally { w.Close(); }
            };
            Application.Run(w);   // a real message loop: the window's timer and its async handlers behave as they do for a rider
        }
        finally
        {
            Console.SetOut(realOut);
            TrainerBridgeConfig.PathOverride = null;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>What a tester would do, without a tester: every tab at two sizes, the demo ride (connected by itself), a demo search and Save.</summary>
    private static async Task Drive(Form w, Action<bool, string> Check)
    {
        string? shots = SnapshotDir;
        static Task Pump(int ms) => Task.Delay(ms);   // the message loop keeps running meanwhile: timer ticks, painting, async continuations
        {
            var tabs = (TabControl)w.Controls.Find("Tabs", true)[0];
            Check(tabs.TabPages.Cast<TabPage>().Select(p => p.Text).SequenceEqual(new[] { "Ride", "Workouts", "My devices", "Rider and bike", "Resistance", "Logs" }),
                "the six tabs keep their names, in a real tab control, Workouts second (Training is called Resistance, Details is called Logs; the order)");
            await Pump(400);
            Check(Text(w, "MessageText").StartsWith("Welcome", StringComparison.Ordinal) && w.Controls.Find("MessagePanel", true)[0].Visible, "a new settings file shows the Welcome message bar");

            foreach (bool small in new[] { true, false })
            {
                if (!small) { w.Size = new Size(1100, 820); await Pump(150); }
                string size = small ? "minimum size" : "1100 x 820";
                for (int i = 0; i < tabs.TabCount; i++)
                {
                    tabs.SelectedIndex = i;
                    await Pump(150);
                    string tab = tabs.TabPages[i].Text;
                    var bad = Clipped(w, tabs.TabPages[i], vertical: i == 0);
                    Check(bad.Count == 0, $"{tab}, {size}: nothing is cut off" + (bad.Count > 0 ? " (" + string.Join(", ", bad) + ")" : ""));
                    Check(Paint(w, shots, $"{(small ? "min" : "big")}-{i + 1}-{tab.Replace(' ', '-').ToLowerInvariant()}"), $"{tab}, {size}: paints (cards, text and background are all there)");
                }
            }

            // the settings file's path is on the Logs tab only, in its own area above the log, which scrolls by itself
            Control cfgPath = w.Controls.Find("ConfigPath", true)[0], logText = w.Controls.Find("LogText", true)[0];
            static Rectangle OnScreen(Control c) => c.RectangleToScreen(c.ClientRectangle);
            Check(tabs.TabPages[5].Contains(cfgPath) && cfgPath.Text.EndsWith(TrainerBridgeConfig.Path, StringComparison.Ordinal) && tabs.TabPages[5].Contains(w.Controls.Find("OpenLogsButton", true)[0]),
                $"Logs: the settings file's path and the logs folder button are on this tab (got '{cfgPath.Text}')");
            Check(CardOf(cfgPath) is { } about && CardOf(logText) is { } logCard && !ReferenceEquals(about, logCard) && about.Controls.Find("BuildText", true).Length == 1
                && OnScreen(about).Bottom <= OnScreen(logCard).Top && logText is TextBox { Multiline: true, ScrollBars: ScrollBars.Both },
                "Logs: the build and the settings file sit in their own area above the log, and the log has its own scroll bars");
            // the workout library folder (moved from the Workouts tab) and the logs folder sit together on the Logs tab,
            // between the build / settings file area and the log
            Control libDir = w.Controls.Find("LibraryDir", true)[0], logsDir = w.Controls.Find("LogsDir", true)[0];
            Check(CardOf(libDir) is { } folders && ReferenceEquals(folders, CardOf(logsDir)) && tabs.TabPages[5].Contains(folders)
                && new[] { "BrowseLibraryButton", "SaveLibraryButton", "LibraryStatus", "BrowseLogsButton", "SaveLogsButton", "LogsStatus" }.All(n => folders.Controls.Find(n, true).Length == 1)
                && !All(tabs.TabPages[1]).Any(c => c.Name.Contains("Library", StringComparison.Ordinal))
                && OnScreen(CardOf(cfgPath)!).Bottom <= OnScreen(folders).Top && OnScreen(folders).Bottom <= OnScreen(CardOf(logText)!).Top,
                "Logs: the workout library folder and the logs folder are in one area on this tab, each with Browse... and Save; the Workouts tab has no library folder");
            Check(libDir is TextBox && logsDir is TextBox && libDir.Parent!.Left == logsDir.Parent!.Left && libDir.Parent.Width == logsDir.Parent.Width && logsDir.Parent.Width >= Theme.S(250),
                $"Logs: the two folder boxes line up under each other and have room for a path (library {libDir.Parent!.Bounds}, logs {logsDir.Parent!.Bounds})");
            await LogsFolderBox(w, Check);

            var savedIn = new[] { 3, 4 }.SelectMany(i => All(tabs.TabPages[i])).Where(c => c is Label && c.Text.Contains(TrainerBridgeConfig.Path, StringComparison.Ordinal)).ToList();
            Check(savedIn.Count == 0, "Rider and bike, Resistance: no line saying where the settings file is");

            // the Ride tab, riding (demo readings), at the minimum size: the tightest layout. Nothing was pressed, the window
            // connected by itself when it opened, and there is no Start / Stop button
            w.Size = w.MinimumSize;
            tabs.SelectedIndex = 0;
            await Pump(150);
            Check(w.Controls.Find("StartStopButton", true).Length == 0 && !All(w).Any(c => c is Button && c.Text.Contains("riding", StringComparison.OrdinalIgnoreCase)),
                "Ride: there is no Start / Stop riding button");
            Check(Text(w, "TrainerState") == "Trainer: Connected", $"the window connected by itself when it opened: the status strip says the trainer is connected (got '{Text(w, "TrainerState")}')");
            // no "Riding / Not riding" line, no top bar, no Games card: the four tiles, then one slim strip (Trainer, Game), no buttons
            Control strip = w.Controls.Find("StatusStrip", true)[0], readings = w.Controls.Find("Readings", true)[0];
            var stripLights = All(strip).OfType<StatusIndicator>().ToList();
            Check(new[] { "RideState", "RideBar", "GamesBox" }.All(n => w.Controls.Find(n, true).Length == 0) && !All(tabs.TabPages[0]).Any(c => c is Button)
                && !All(tabs.TabPages[0]).Any(c => c is Label && (c.Text.StartsWith("Riding", StringComparison.Ordinal) || c.Text == "Not riding")),
                "Ride: no Riding / Not riding line, no top bar, no Games card and no buttons");
            Check(stripLights.Select(c => c.Name).SequenceEqual(new[] { "TrainerState", "GameState" }) && ReferenceEquals(stripLights[0].Font, stripLights[1].Font)
                && stripLights.All(c => c.Light is not null) && readings.Bottom <= strip.Top && strip.Height <= Theme.S(40)
                && Text(w, "GameState") == "Game: Descenders connected",
                $"Ride: one slim status strip under the readings, Trainer and Game, each a light and a few words in the same font (strip {strip.Bounds}, readings {readings.Bounds}, '{Text(w, "GameState")}')");
            Check(tabs.TabPages[0].Controls.Count == 1 && tabs.TabPages[0].Controls[0].Controls.Cast<Control>().Select(c => c.Name).SequenceEqual(new[] { "Readings", "StatusStrip" })
                && readings.Controls.Count == 4, "Ride: the tab holds the four reading tiles and the status strip, nothing else");
            // one size of light everywhere: lines in different fonts (a tile's small one, the strip's) keep the same room for it
            var tileLight = (StatusIndicator)w.Controls.Find("SpeedHealth", true)[0];
            Check(!ReferenceEquals(tileLight.Font, stripLights[0].Font) && tileLight.Measure(0).Width - TextRenderer.MeasureText(tileLight.Text, tileLight.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width
                    == stripLights[0].Measure(0).Width - TextRenderer.MeasureText(stripLights[0].Text, stripLights[0].Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width,
                "status lights: a reading tile's light and the strip's take the same room, though their fonts differ");
            Check(!Text(w, "GameState").Contains("Start riding", StringComparison.OrdinalIgnoreCase), "Ride: the Games line does not name the button that is gone");
            Check(w.Controls.Find("LaunchDescendersButton", true).Length == 0 && w.Controls.Find("RideHint", true).Length == 0
                && !All(w).Any(c => c is Button && c.Text.Contains("Launch", StringComparison.OrdinalIgnoreCase)) && Text(w, "GameState").Length > 0,
                "Ride: no Launch Descenders button and no hint text, the game status line stays (starting a workout opens the game)");
            Check(int.TryParse(Text(w, "PowerValue"), out int watts) && watts is >= 150 and <= 250 && Text(w, "SpeedHealth").Contains("Connected"), $"riding: the power tile shows watts and the trainer's light says Connected (got '{Text(w, "PowerValue")}', '{Text(w, "SpeedHealth")}')");
            foreach (string key in new[] { "Power", "Cadence", "HeartRate", "Speed" })
            {
                Control value = w.Controls.Find(key + "Value", true)[0], health = w.Controls.Find(key + "Health", true)[0], graph = w.Controls.Find(key + "Graph", true)[0],
                    tile = w.Controls.Find(key + "Tile", true)[0], unit = w.Controls.Find(key + "Unit", true)[0];
                bool apart = !value.Bounds.IntersectsWith(health.Bounds) && !value.Bounds.IntersectsWith(graph.Bounds) && !graph.Bounds.IntersectsWith(health.Bounds)
                    && !unit.Bounds.IntersectsWith(value.Bounds) && !unit.Bounds.IntersectsWith(graph.Bounds);
                bool inside = new[] { value, health, graph, unit }.All(c => tile.ClientRectangle.Contains(c.Bounds));
                Check(apart && inside && graph.Width >= 40 && graph.Height >= 20,
                    $"{key} tile at the minimum size: number, unit, graph and light each have their own room (tile {tile.Size}, value {value.Bounds}, unit {unit.Bounds}, graph {graph.Bounds}, light {health.Bounds})");
            }
            // the demo's saved power meter is a Bluetooth one, named on its tile by the name it advertises
            Check(Text(w, "PowerHealth") == "Connected - " + DemoLiveSource.BluetoothPower.Name, $"riding: a Bluetooth device's light names it by its advertised name (got '{Text(w, "PowerHealth")}')");
            Check(Paint(w, shots, "min-ride-riding"), "Ride while riding, minimum size: paints");
            w.Size = new Size(1100, 820);
            await Pump(300);
            Check(Paint(w, shots, "big-ride-riding"), "Ride while riding, 1100 x 820: paints");

            // My devices after a demo search: the list rows and the lights under the drop-downs
            tabs.SelectedIndex = 2;
            await Pump(150);
            Check(Text(w, "SearchStatus") == BridgeWindow.SearchHint && Text(w, "SearchButton") == "Search for devices",
                "My devices before a search: the search button heads the card, with what to do next to it");
            // a saved Bluetooth device shows its kind in its drop-down, and its light, before any search
            var powerBox = (ComboBox)w.Controls.Find("PowerChoice", true)[0];
            Check(powerBox.SelectedItem?.ToString() == DemoLiveSource.BluetoothPower.Name + " (Bluetooth)" && Text(w, "PowerChoiceState") == "Saved" && Text(w, "SearchNote").Length == 0,
                $"My devices before a search: the demo's saved Bluetooth power meter is shown by name with \"(Bluetooth)\", its light says Saved (got '{powerBox.SelectedItem}', '{Text(w, "PowerChoiceState")}')");
            ((Button)w.Controls.Find("SearchButton", true)[0]).PerformClick();
            await Pump(1500);
            Check(Paint(w, shots, "big-devices-searching"), "My devices while searching: paints");
            Check(Text(w, "TrainerState") == "Trainer: paused during the device search" && Text(w, "PowerValue") == "--" && Text(w, "SearchStatus").StartsWith("Searching", StringComparison.Ordinal),
                $"a search while connected is not refused: it pauses the connection by itself, the readings show -- (got '{Text(w, "TrainerState")}', '{Text(w, "PowerValue")}')");
            await Pump(2300);
            Check(Text(w, "TrainerState") == "Trainer: Connected", $"after the search the window connects again by itself (got '{Text(w, "TrainerState")}')");
            var found = (ListView)w.Controls.Find("FoundList", true)[0];
            Check(found.Items.Count == 5 && Text(w, "SearchStatus").StartsWith("Found 5", StringComparison.Ordinal), $"the demo search lists 5 devices (got {found.Items.Count}, '{Text(w, "SearchStatus")}')");
            // one search finds both kinds; each row says how it was found, and the trainer that does both is there twice
            var rows = found.Items.Cast<ListViewItem>().Select(i => i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).ToArray()).ToList();
            var howFound = rows.Select(r => r[0] + " / " + r[1]).ToList();
            Check(found.Columns.Cast<ColumnHeader>().Select(c => c.Text).SequenceEqual(new[] { "Device", "Found by", "Make", "Number", "Signal", "Reading now" })
                && howFound.Count(h => h == "Trainer / ANT+") == 1 && howFound.Count(h => h == "Trainer / Bluetooth") == 1 && howFound.Contains("Power meter / Bluetooth")
                && rows.All(r => r[1] is "ANT+" or "Bluetooth"),
                $"the found list has a \"Found by\" column, ANT+ or Bluetooth; the trainer is listed once for each (got {string.Join(", ", howFound)})");
            Check(rows.Where(r => r[1] == "Bluetooth").All(r => r[2].Length > 0 && r[3].Length == 0 && r[4] is "Strong" or "OK" or "Weak")
                && rows.Any(r => r[1] == "Bluetooth" && r[2] == DemoLiveSource.BluetoothTrainer.Name) && rows.Any(r => r[1] == "ANT+" && r[2] == "Tacx" && r[3] == "12345"),
                "a Bluetooth row: the name the device advertises, no number, a signal in the same words; an ANT+ row as before (make, number)");
            Check(Text(w, "SearchNote").Length == 0, "the demo has a stick and Bluetooth: no quiet line under the search button");
            Check(Text(w, "TrainerChoiceState") == "Not saved" && Text(w, "CadenceChoiceState").Contains("trainer") && Text(w, "PowerChoiceState") == "Found",
                $"after the search: the suggested trainer says it is not saved yet, cadence says it comes from the trainer, the saved Bluetooth power meter was found (got '{Text(w, "TrainerChoiceState")}', '{Text(w, "CadenceChoiceState")}', '{Text(w, "PowerChoiceState")}')");
            // a drop-down line is the brand (when known) and the number, as on the Ride tab: no kind of device, no reading.
            // And how it is reached, "(ANT+)" or "(Bluetooth)" (a Bluetooth device by its advertised name)
            var lines = new[] { "TrainerChoice", "HeartRateChoice", "CadenceChoice", "PowerChoice" }
                .SelectMany(n => ((ComboBox)w.Controls.Find(n, true)[0]).Items.Cast<object>().Select(i => i.ToString() ?? "")).ToList();
            var devices = lines.Where(l => l != "None" && l != "Use the trainer's own").ToList();
            Check(lines.Any(l => l.StartsWith("Tacx ", StringComparison.Ordinal)) && lines.Any(l => l.StartsWith("device ", StringComparison.Ordinal))
                && devices.All(l => !new[] { " number ", " - ", "rainer", "rate", "sensor" }.Any(l.Contains))
                && devices.All(l => l.EndsWith(" (ANT+)", StringComparison.Ordinal) || l.EndsWith(" (Bluetooth)", StringComparison.Ordinal)),
                $"device drop-downs: each line is \"Tacx 44226 (ANT+)\" / \"device 44226 (ANT+)\" / \"<name> (Bluetooth)\", nothing more (got '{string.Join("', '", lines)}')");
            var trainerLines = ((ComboBox)w.Controls.Find("TrainerChoice", true)[0]).Items.Cast<object>().Select(i => i.ToString() ?? "").ToList();
            Check(trainerLines.SequenceEqual(new[] { "None", "Tacx 12345 (ANT+)", DemoLiveSource.BluetoothTrainer.Name + " (Bluetooth)" })
                && ((ComboBox)w.Controls.Find("TrainerChoice", true)[0]).SelectedIndex == 1
                && !((ComboBox)w.Controls.Find("PowerChoice", true)[0]).Items.Cast<object>().Any(i => i.ToString() == DemoLiveSource.BluetoothTrainer.Name + " (Bluetooth)"),
                $"the trainer found both ways is offered twice for the Trainer job (the ANT+ one suggested), and not as a Bluetooth power meter (got '{string.Join("', '", trainerLines)}')");
            ((Button)w.Controls.Find("SaveDevicesButton", true)[0]).PerformClick();
            await Pump(150);
            Check(Text(w, "TrainerChoiceState") == "Found" && Text(w, "DevicesStatus") == "Saved.",
                $"after Save: the trainer's light says found, the status just says Saved. (got '{Text(w, "TrainerChoiceState")}', '{Text(w, "DevicesStatus")}')");
            Check(Text(w, "TrainerState") == "Trainer: Connected", "after Save the window is connected again by itself");
            Check(Paint(w, shots, "big-devices-found"), "My devices after the search: paints");

            // with devices saved, a search says under each reading why it shows nothing, and the readings come back by themselves
            ((Button)w.Controls.Find("SearchButton", true)[0]).PerformClick();
            await Pump(1000);
            Check(Text(w, "SpeedHealth").Contains("Paused during the device search", StringComparison.Ordinal) && Text(w, "SpeedValue") == "--",
                $"a search with devices saved: the readings say they are paused for it (got '{Text(w, "SpeedHealth")}', '{Text(w, "SpeedValue")}')");
            await Pump(2800);
            Check(Text(w, "TrainerState") == "Trainer: Connected" && Text(w, "SpeedHealth").Contains("Connected", StringComparison.Ordinal)
                && Text(w, "TrainerChoiceState") == "Found",
                $"the search is over: connected again, the saved choices kept (got '{Text(w, "TrainerState")}', '{Text(w, "SpeedHealth")}', '{Text(w, "TrainerChoiceState")}')");
            tabs.SelectedIndex = 3;   // a button is pressed on the tab that shows it
            await Pump(150);
            ((Button)w.Controls.Find("SaveRiderButton", true)[0]).PerformClick();
            await Pump(150);
            Check(Text(w, "RiderStatus") == "Saved." && Text(w, "TrainerState") == "Trainer: Connected",
                $"Rider and bike: Save just says Saved. and the window stays connected (got '{Text(w, "RiderStatus")}')");
            tabs.SelectedIndex = 2;
            await Pump(150);

            // at the size the window opens at, the My devices page and every device found show without a scroll bar
            ((Button)w.Controls.Find("MessageClose", true)[0]).PerformClick();
            var startSize = BridgeWindow.StartSize(Size.Empty, Theme.Scale);
            w.Size = startSize;
            await Pump(200);
            StackPanel? body = null;
            for (Control? p = found.Parent; p is not null && body is null; p = p.Parent) body = p is StackPanel { Scrolls: true } s ? s : null;
            Check(w.Size == startSize && body is not null && !body.VerticalScroll.Visible && body.AutoScrollMinSize.Height <= body.ClientSize.Height,
                $"My devices at the size the window opens at: the page needs no scroll bar (window {w.Size}, page needs {body?.AutoScrollMinSize.Height}, has {body?.ClientSize.Height})");
            int lastRow = found.Items[found.Items.Count - 1].Bounds.Bottom;
            Check(lastRow <= found.ClientSize.Height, $"My devices at the size the window opens at: the list shows every device found without scrolling (last row ends at {lastRow}, list is {found.ClientSize.Height} high)");
            // a scroll bar takes its room out of the list's inside, so inside = outside means there is none
            Check(ColumnsWidth(found) <= found.ClientSize.Width && found.ClientSize == found.Size,
                $"My devices, window made smaller: the list has no scroll bar of its own (columns {ColumnsWidth(found)}, list {found.Size}, inside {found.ClientSize})");
            var cut = Clipped(w, tabs.TabPages[2], vertical: true);
            Check(cut.Count == 0, "My devices at the size the window opens at: nothing is cut off, Save included" + (cut.Count > 0 ? " (" + string.Join(", ", cut) + ")" : ""));
            Check(Paint(w, shots, "start-devices-found"), "My devices after the search, at the size the window opens at: paints");

            w.Size = w.MinimumSize;
            await Pump(200);
            Check(ColumnsWidth(found) <= found.ClientSize.Width && found.ClientSize.Height == found.Height,
                $"My devices at the minimum size: the list's columns fit it, no sideways scroll bar (columns {ColumnsWidth(found)}, list {found.Size}, inside {found.ClientSize})");
            Check(Paint(w, shots, "min-devices-found"), "My devices after the search, minimum size: paints");

            // the Training tab without its "Workouts" card: no scroll bar at the size the window opens at, the pause time survives a Save
            tabs.SelectedIndex = 4;
            await Pump(200);
            Control step = w.Controls.Find("WorkoutBiasStepPct", true)[0], difficulty = w.Controls.Find("TrainerDifficulty", true)[0];
            Check(w.Controls.Find("WorkoutPauseSeconds", true).Length == 0, "Training: the workout pause time has no field (settings file only)");
            Check(CardOf(step) is { } feel && ReferenceEquals(feel, CardOf(difficulty)) && Cards(tabs.TabPages[4]) == 2,
                "Training: the workout step sits in the \"Trainer feel\" card, next to \"ERG mode\"; there is no \"Workouts\" card");
            StackPanel? trBody = null;
            for (Control? p = step.Parent; p is not null && trBody is null; p = p.Parent) trBody = p is StackPanel { Scrolls: true } s ? s : null;
            // the size the window opens at, and on a 1366 x 768 laptop (free area 728 high: the window opens 32 px shorter)
            foreach (var trSize in new[] { startSize, new Size(startSize.Width, startSize.Height - Theme.S(32)) })
            {
                w.Size = trSize;
                await Pump(200);
                Check(w.Size == trSize && trBody is not null && !trBody.VerticalScroll.Visible && trBody.AutoScrollMinSize.Height <= trBody.ClientSize.Height,
                    $"Training at {trSize.Width} x {trSize.Height}: the page needs no scroll bar (window {w.Size}, page needs {trBody?.AutoScrollMinSize.Height}, has {trBody?.ClientSize.Height})");
                var trCut = Clipped(w, tabs.TabPages[4], vertical: true);
                Check(trCut.Count == 0, $"Training at {trSize.Width} x {trSize.Height}: nothing is cut off, the buttons included" + (trCut.Count > 0 ? " (" + string.Join(", ", trCut) + ")" : ""));
            }
            Check(Paint(w, shots, "small-training"), "Training on a 1366 x 768 screen: paints");

            TrainerBridgeConfig.SaveTraining(TrainerBridgeConfig.Load().Training with { WorkoutPauseSeconds = 7 });   // as a hand edit of the file
            ((NumericUpDown)step).Value = 8;
            ((Button)w.Controls.Find("StandardTrainingButton", true)[0]).PerformClick();
            ((NumericUpDown)difficulty).Value = 0.5m;
            ((Button)w.Controls.Find("SaveTrainingButton", true)[0]).PerformClick();
            await Pump(150);
            var kept = TrainerBridgeConfig.Load().Training;
            Check(Text(w, "TrainingStatus") == "Saved." && kept.TrainerDifficulty == 0.5 && kept.WorkoutBiasStepPct == 5 && kept.WorkoutPauseSeconds == 7,
                $"Training: Back to standard + Save stores the fields shown and keeps the file's pause time (got '{Text(w, "TrainingStatus")}', {Training.Describe(kept)})");

            await Workouts(w, tabs, startSize, Check, shots);
            await Pad(w, tabs, startSize, Check, shots);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// The Xbox pad drives the window. Made-up pad readings go in where the pad's timer puts the real ones
    /// (BridgeWindow.PadStep; the timer itself is stopped, there is no pad here): LB / RB walk the tabs and wrap, the D-pad moves the focus
    /// and steps the Workouts pickers (held: it repeats), A starts the session / workout and presses "Reset plan", the hint says
    /// "A to start"; nothing moves while the pad is not listened to, or for a button held when listening starts; a question box is
    /// answered with D-pad left / right, A and B. Called after <see cref="Workouts"/>: its library and its followed plan are still there.
    /// </summary>
    private static async Task Pad(Form w, TabControl tabs, Size startSize, Action<bool, string> Check, string? shots)
    {
        static Task Pump(int ms) => Task.Delay(ms);
        const int Settle = PickerRow.SettleMs + 400;
        const PadButtons None = PadButtons.None, A = PadButtons.A, B = PadButtons.B, LB = PadButtons.LB, RB = PadButtons.RB;
        var window = (BridgeWindow)w;
        window.StopPadTimer();
        double t = 1000;
        void Feed(PadButtons held, bool listen = true) => window.PadStep(held, listen, t += 0.05);
        void Press(PadButtons button) { Feed(None); Feed(button); Feed(None); }
        PickerRow plans = Picker(w, "PlanChoice"), sessions = Picker(w, "SessionChoice"), folders = Picker(w, "WorkoutFolderChoice"), workouts = Picker(w, "WorkoutChoice");
        var reset = (Button)w.Controls.Find("ResetPlanButton", true)[0];

        w.Size = startSize;
        tabs.SelectedIndex = 1;
        await Pump(200);
        var seen = new List<int>();
        foreach (var button in new[] { RB, LB, LB, LB, RB, RB })
        {
            Press(button);
            seen.Add(tabs.SelectedIndex);
        }
        Check(seen.SequenceEqual(new[] { 2, 1, 0, 5, 0, 1 }) && ReferenceEquals(w.ActiveControl, tabs),
            $"pad: RB / LB go to the next / previous tab and wrap at both ends; the focus is on the tab strip (tabs {string.Join(", ", seen)})");

        // not listened to (a game is connected, or another window is in front): nothing moves; nor for a button held when listening starts
        Feed(None, listen: false);
        foreach (var button in new[] { RB, PadButtons.Down, A, LB }) Feed(button, listen: false);
        Check(tabs.SelectedIndex == 1 && ReferenceEquals(w.ActiveControl, tabs), "pad, not listened to (a game is connected or the window is not in front): no press moves anything");
        Feed(RB);
        Feed(RB);
        Check(tabs.SelectedIndex == 1, "pad: RB held when listening starts does not change the tab");
        Press(RB);
        Check(tabs.SelectedIndex == 2, "pad: let go and pressed again, RB counts");
        Press(LB);

        // the D-pad is the arrow keys: down into the page, left / right step the picker (held: it repeats), A starts
        Press(PadButtons.Down);
        Check(!reset.Enabled && ReferenceEquals(w.ActiveControl, plans), "pad: D-pad down from the tab strip puts the focus on the plan picker (nothing to reset yet, so Reset plan is passed over)");
        Press(PadButtons.Down);
        int at = sessions.Index;
        Check(ReferenceEquals(w.ActiveControl, sessions) && Text(w, "PlanHint") == "A to start" && Text(w, "WorkoutHint").Length == 0,
            $"pad: D-pad down again reaches the session picker, and the hint under its card names the pad's button (got '{Text(w, "PlanHint")}')");
        Press(PadButtons.Right);
        Feed(None);
        Feed(PadButtons.Right);   // held for half a second: once at the press, once more after 400 ms
        t += 0.2;
        Feed(PadButtons.Right);
        t += 0.2;
        Feed(PadButtons.Right);
        Press(PadButtons.Left);
        Check(sessions.Index == at + 2, $"pad: D-pad right steps the session picker, held it repeats, left steps back (from {at} to {sessions.Index})");
        string[] words = sessions.Text.Split(' ');
        string started = "Started Week " + words[0][1..] + " Day " + words[1][1..];
        Press(A);
        await Pump(Settle);
        Check(Text(w, "NowRiding").StartsWith(started, StringComparison.Ordinal), $"pad: A on the session picker starts the chosen session (wanted '{started}', got '{Text(w, "NowRiding")}')");
        Press(PadButtons.Down);
        Check(ReferenceEquals(w.ActiveControl, folders), "pad: D-pad down walks on to the folder picker");
        Press(PadButtons.Down);
        at = workouts.Index;
        Press(PadButtons.Right);
        string workout = workouts.Text;
        Check(ReferenceEquals(w.ActiveControl, workouts) && workouts.Index == at + 1 && Text(w, "WorkoutHint") == "A to start" && Text(w, "PlanHint").Length == 0,
            $"pad: the workout picker is reached and stepped the same way, the hint moves under its card (got '{Text(w, "WorkoutHint")}')");
        Check(Paint(w, shots, "workouts-pad"), "Workouts, driven by the pad (\"A to start\" under the card): paints");
        Press(A);
        await Pump(Settle);
        Check(Text(w, "NowRiding").StartsWith("Started \"" + workout + "\"", StringComparison.Ordinal), $"pad: A on the workout picker starts the chosen workout (got '{Text(w, "NowRiding")}')");

        // a key (or the mouse) takes the hint back, the pad takes it again
        PostMessage(workouts.Handle, 0x0100, (IntPtr)0x10, IntPtr.Zero);   // Shift down, as the keyboard sends it
        PostMessage(workouts.Handle, 0x0101, (IntPtr)0x10, IntPtr.Zero);
        await Pump(150);
        Check(Text(w, "WorkoutHint") == "Enter or click to start", $"a key pressed after the pad: the hint names Enter and the click again (got '{Text(w, "WorkoutHint")}')");
        Press(PadButtons.Up);
        Check(ReferenceEquals(w.ActiveControl, folders) && Text(w, "WorkoutHint") == "A to start", "pad: used again, the hint names A again");

        // Reset plan: reachable with the D-pad once the plan has a finished session, pressed with A
        var store = DescendersCyclingPOC.ProgressFile.Load(TrainerBridgeConfig.Path).Store;
        store.RecordCompletion("builder", "b0.a0", "Intervals/w1d1.zwo", "2026-10-04T10:00:00Z", 1080, 150, 180, 100, false);
        DescendersCyclingPOC.ProgressFile.Save(TrainerBridgeConfig.Path, store);
        Press(LB);
        Press(RB);   // away and back: the tab reads the settings file again
        await Pump(200);
        Check(reset.Enabled && plans.Model.Current?.Detail == "1 of 24 done", $"a finished session: the plan can be reset (plan picker says '{plans.Model.Current?.Detail}')");
        Press(PadButtons.Down);
        bool first = ReferenceEquals(w.ActiveControl, reset);
        Press(PadButtons.Down);
        bool second = ReferenceEquals(w.ActiveControl, plans);
        Press(PadButtons.Up);
        Check(first && second && ReferenceEquals(w.ActiveControl, reset), "pad: with something to reset, Reset plan is the first stop under the tab strip, the plan picker the next");
        Press(A);   // the demo skips the "are you sure" question (asked below)
        await Pump(Settle);
        Check(!reset.Enabled && DescendersCyclingPOC.ProgressFile.Load(TrainerBridgeConfig.Path).Store.Plans["builder"].Completed.Count == 0
            && Text(w, "NowRiding").Contains("starts again", StringComparison.Ordinal), $"pad: A on Reset plan resets the plan (got '{Text(w, "NowRiding")}')");

        // the question box ("are you sure?", and closing with a game connected): No is focused; D-pad left / right, A, B
        async Task<bool?> Ask(params PadButtons[] presses)
        {
            bool? answer = null;
            w.BeginInvoke(new Action(() => answer = window.Ask("Forget every finished session of \"Eight week builder\" and start the plan again from its first session?")));
            await Pump(300);
            try
            {
                bool opened = window.OpenQuestion is { Visible: true, OnYes: false };
                int tab = tabs.SelectedIndex;
                Press(RB);   // the window behind a question does not move
                foreach (var button in presses) Press(button);
                await Pump(300);
                return opened && window.OpenQuestion is null && tabs.SelectedIndex == tab ? answer : null;
            }
            finally
            {
                window.OpenQuestion?.Close();   // never leave a box open that nobody is there to answer
                await Pump(100);
            }
        }
        Check(await Ask(A) == false, "pad, question box: it opens with No focused, so A alone answers No (and RB does not change the tab behind it)");
        Check(await Ask(PadButtons.Left, A) == true, "pad, question box: D-pad left moves to Yes, A presses it");
        Check(await Ask(PadButtons.Left, B) == false, "pad, question box: B is No, wherever the focus is");
        Check(await Ask(PadButtons.Left, PadButtons.Right, A) == false, "pad, question box: D-pad right moves back to No");
    }

    /// <summary>
    /// The Workouts tab is where a training plan is chosen and followed and a workout is started (the game has no menu any more).
    /// A library in the temp folder; the demo ride stands in for a real one. No scroll bar at the size the window opens at, on a
    /// 1366 x 768 screen, or at the minimum size.
    /// The four choices are left / right pickers (no drop-down anywhere on the tab), each card shows the effort graph of the
    /// session / workout in the middle, and the arrows and Enter drive the whole tab (through BridgeWindow.WorkoutsCommand, what the keys
    /// call). The library is a large one: a 24-session plan (plus one whose file is missing) and a folder of 55 workouts.
    /// </summary>
    private static async Task Workouts(Form w, TabControl tabs, Size startSize, Action<bool, string> Check, string? shots)
    {
        static Task Pump(int ms) => Task.Delay(ms);
        const int Settle = PickerRow.SettleMs + 400;   // a picker has settled, and what follows from it is done
        string lib = Path.Combine(Path.GetDirectoryName(TrainerBridgeConfig.Path)!, "Library");
        Directory.CreateDirectory(Path.Combine(lib, "Workouts", "Intervals"));
        Directory.CreateDirectory(Path.Combine(lib, "TrainingPlans"));
        // 18:00: a warm-up, a tempo block, two intervals, a minute of free ride, a cool-down
        static string Zwo(string name) => "<workout_file><name>" + name + "</name><workout><Warmup Duration='300' PowerLow='0.4' PowerHigh='0.75'/><SteadyState Duration='300' Power='0.9'/>"
            + "<IntervalsT Repeat='2' OnDuration='60' OffDuration='60' OnPower='1.15' OffPower='0.5'/><FreeRide Duration='60'/><Cooldown Duration='180' PowerLow='0.7' PowerHigh='0.35'/></workout></workout_file>";
        File.WriteAllText(Path.Combine(lib, "Workouts", "easy.zwo"), Zwo("Easy spin"));
        File.WriteAllText(Path.Combine(lib, "Workouts", "Intervals", "hard.zwo"), Zwo("Hard intervals"));
        for (int i = 0; i < 30; i++) File.WriteAllText(Path.Combine(lib, "Workouts", "Intervals", "x" + i.ToString("00") + ".zwo"), Zwo("Extra workout with a long name number " + i));
        var plan = new System.Text.StringBuilder("<plan name='Eight week builder' supports_sports='CYCLING'>");
        for (int week = 1; week <= 8; week++)
        {
            plan.Append("<block name='Week " + week + "'>");
            for (int day = 1; day <= 3; day++)
            {
                string file = "w" + week + "d" + day + ".zwo";
                File.WriteAllText(Path.Combine(lib, "Workouts", "Intervals", file), Zwo("Week " + week + " day " + day));
                plan.Append("<activity workout_filename='data/Workouts/Intervals/" + file + "' workout_name='Week " + week + " day " + day + "' priority='" + day + "'/>");
            }
            plan.Append("</block>");
        }
        plan.Append("<block name='Extras'><activity workout_filename='data/Workouts/Intervals/gone.zwo' workout_name='Bonus' priority='1' is_optional='1'/></block>");
        File.WriteAllText(Path.Combine(lib, "TrainingPlans", "builder.xml"), plan.Append("</plan>").ToString());

        var window = (BridgeWindow)w;
        w.Size = startSize;
        tabs.SelectedIndex = 1;
        await Pump(200);
        PickerRow plans = Picker(w, "PlanChoice"), sessions = Picker(w, "SessionChoice"), folders = Picker(w, "WorkoutFolderChoice"), workouts = Picker(w, "WorkoutChoice");
        Control sessionGraph = w.Controls.Find("SessionGraph", true)[0], workoutGraph = w.Controls.Find("WorkoutGraph", true)[0];
        Check(!All(tabs.TabPages[1]).Any(c => c is ComboBox or ListBox or ListView), "Workouts: no drop-down and no list anywhere on the tab");
        // the only button left on the tab is "Reset plan"; starting is Enter or a click, and a hint line says so
        var buttons = All(tabs.TabPages[1]).OfType<Button>().ToList();
        Check(w.Controls.Find("StartSessionButton", true).Length == 0 && w.Controls.Find("StartWorkoutButton", true).Length == 0 && buttons.Count == 1
            && buttons[0].Name == "ResetPlanButton" && buttons[0].Text == "Reset plan",
            $"Workouts: no Start session / Start workout button; the one button is \"Reset plan\" (got {string.Join(", ", buttons.Select(b => b.Name + " '" + b.Text + "'"))})");
        Check(plans.Count == 0 && sessions.Count == 0 && folders.Count == 0 && workouts.Count == 0 && Text(w, "PlanHint").Length == 0 && Text(w, "WorkoutHint").Length == 0
            && Text(w, "ActivePlan") == PlanSummary.NoLibrary && workoutGraph.Text.StartsWith("Choose your workout library folder", StringComparison.Ordinal) && sessionGraph.Text.Length == 0,
            $"Workouts with no library: nothing to choose, nothing to start, and it says to choose the folder first (got '{Text(w, "ActivePlan")}', '{workoutGraph.Text}')");
        tabs.Select();
        Check(window.WorkoutsCommand(NavCommand.Down) && ReferenceEquals(w.ActiveControl, tabs), "with nothing to choose, Down from the tab strip leaves the focus where it is");
        Check(Text(w, "NowRiding") == Session.NoWorkoutText,
            $"Workouts, connected (the window did that by itself), nothing started yet: the line under the cards says free ride (got '{Text(w, "NowRiding")}')");

        // the library folder is set on the Logs tab; the Workouts tab shows what is in it when it is opened again
        tabs.SelectedIndex = 5;
        await Pump(150);
        w.Controls.Find("LibraryDir", true)[0].Text = lib;
        ((Button)w.Controls.Find("SaveLibraryButton", true)[0]).PerformClick();
        await Pump(200);
        Check(Text(w, "LibraryStatus").StartsWith("Saved. The workouts and training plans in this folder are listed on the Workouts tab", StringComparison.Ordinal),
            $"Logs: saving the workout library says where its workouts are listed (got '{Text(w, "LibraryStatus")}')");
        w.Controls.Find("LibraryDir", true)[0].Select();
        Check(!window.WorkoutsCommand(NavCommand.Down) && !window.WorkoutsCommand(NavCommand.Previous) && !window.WorkoutsCommand(NavCommand.Activate),
            "in the library folder's text box the arrows and Enter are the text box's own");
        tabs.SelectedIndex = 1;
        await Pump(200);
        Check(plans.Count == 2 && plans.Index == 0 && plans.Text == "Choose a training plan" && plans.Model.Position == "Training plan · 1 of 2" && plans.Model.Next?.Title == "Eight week builder"
            && sessions.Count == 0 && Text(w, "ActivePlan") == PlanSummary.NoPlan,
            $"library saved: the plan picker offers the plan to its right, none followed yet (got {plans.Count} entries, '{plans.Text}', '{Text(w, "ActivePlan")}')");
        Check(folders.Count == 2 && folders.Text == "Workouts" && folders.Model.Current?.Detail == "1 workout" && folders.Model.Next?.Title == "Intervals" && workouts.Count == 1
            && workouts.Model.Current == new PickerEntry("Easy spin", "18:00") && workouts.Model.Position == "Workout · 1 of 1",
            $"the workout pickers: the Workouts folder with its sub-folder to the right, the workout with its length under it (got '{folders.Text}', '{workouts.Text}', '{workouts.Model.Current?.Detail}')");
        Check(workoutGraph.Text == "18:00 · hardest target 207 W (115 % of FTP)" && Text(w, "WorkoutDetail").StartsWith("18:00", StringComparison.Ordinal), $"the effort graph of the workout: total time and hardest target, from the Rider tab's FTP (got '{workoutGraph.Text}')");

        // the keyboard alone: Down from the tab strip, Right follows the plan, Down to the sessions, Right / Left step them, Enter starts
        tabs.Select();
        Check(Text(w, "PlanHint").Length == 0 && Text(w, "WorkoutHint").Length == 0, "the focus on the tab strip: no hint line under either card");
        Check(window.WorkoutsCommand(NavCommand.Down) && ReferenceEquals(w.ActiveControl, plans), "Down from the tab strip puts the focus on the plan picker");
        Check(Text(w, "PlanHint").Length == 0, "on the plan picker with no plan followed there is nothing to start: no hint");
        Check(window.WorkoutsCommand(NavCommand.Next) && plans.Index == 1 && plans.Pending && sessions.Count == 0, "Right steps to the plan; nothing is saved until the picker settles");
        await Pump(Settle);
        Check(DescendersCyclingPOC.ProgressFile.Load(TrainerBridgeConfig.Path).Store.ActivePlan == "builder" && plans.Count == 1 && plans.Model.Current == new PickerEntry("Eight week builder", "0 of 24 done")
            && !plans.Model.CanPrevious && !plans.Model.CanNext,
            $"choosing a plan follows it: saved as the active plan, shown with its progress under it (got '{plans.Text}', '{plans.Model.Current?.Detail}')");
        Check(Text(w, "ActivePlan").Length == 0 && Text(w, "NextSession") == "Next session: Week 1 Day 1 Session Week 1 day 1" && sessions.Count == 25
            && sessions.Model.Current == new PickerEntry("W1 D1 Week 1 day 1", "18:00 · next") && sessions.Model.Position == "Session · 1 of 25" && sessions.Model.Previous is null
            && sessions.Model.Next?.Title == "W1 D2 Week 1 day 2",
            $"its sessions show, the next one in the middle (got '{Text(w, "ActivePlan")}', '{Text(w, "NextSession")}', '{sessions.Text}', '{sessions.Model.Current?.Detail}', '{sessions.Model.Position}')");
        Check(sessionGraph.Text == "18:00 · hardest target 207 W (115 % of FTP)", $"the effort graph of the session in the middle (got '{sessionGraph.Text}')");
        Check(Text(w, "PlanHint") == NavRules.StartHint && Text(w, "WorkoutHint").Length == 0,
            $"with sessions to start, the hint is under the card that has the focus, and only there (got '{Text(w, "PlanHint")}', '{Text(w, "WorkoutHint")}')");
        Check(window.WorkoutsCommand(NavCommand.Down) && ReferenceEquals(w.ActiveControl, sessions) && window.WorkoutsCommand(NavCommand.Next) && window.WorkoutsCommand(NavCommand.Next)
            && window.WorkoutsCommand(NavCommand.Previous) && sessions.Text == "W1 D2 Week 1 day 2" && sessions.Model.Position == "Session · 2 of 25" && sessions.Model.Previous?.Title == "W1 D1 Week 1 day 1",
            $"Down moves to the session picker, Right / Left step it (got '{sessions.Text}', '{sessions.Model.Position}')");
        for (int i = 0; i < 40; i++) window.WorkoutsCommand(NavCommand.Next);   // a held key: it stops at the last entry
        Check(sessions.Index == 24 && !sessions.Model.CanNext && sessions.Model.Current == new PickerEntry("W9 D1 Bonus", "file missing")
            && sessionGraph.Text.StartsWith("This session's workout file is missing", StringComparison.Ordinal) && Text(w, "PlanHint").Length == 0,
            $"held Right stops at the last session; one whose workout file is missing says so where its graph would be, and has no start hint (got '{sessions.Text}', '{sessionGraph.Text}')");
        Check(Paint(w, shots, "workouts-session-missing-file"), "Workouts, a session whose file is missing: paints");
        for (int i = 0; i < 40; i++) window.WorkoutsCommand(NavCommand.Previous);
        Check(sessions.Index == 0 && !sessions.Model.CanPrevious, "held Left stops at the first session");
        Check(!Text(w, "NowRiding").StartsWith("Started", StringComparison.Ordinal), $"stepping left and right, however far, started nothing (got '{Text(w, "NowRiding")}')");

        window.WorkoutsCommand(NavCommand.Activate);   // Enter on the session picker
        await Pump(150);
        Check(Text(w, "NowRiding").StartsWith("Started Week 1 Day 1", StringComparison.Ordinal), $"Enter on the session picker: the session starts, nothing had to be pressed on the Ride tab first (got '{Text(w, "NowRiding")}')");
        Check(window.WorkoutsCommand(NavCommand.Down) && ReferenceEquals(w.ActiveControl, folders) && Text(w, "PlanHint").Length == 0 && Text(w, "WorkoutHint") == NavRules.StartHint
            && window.WorkoutsCommand(NavCommand.Down) && ReferenceEquals(w.ActiveControl, workouts) && window.WorkoutsCommand(NavCommand.Down) && ReferenceEquals(w.ActiveControl, workouts),
            $"Down walks on: the folder picker (the hint moves under that card), the workout picker, where it stops; no button on the way (got '{Text(w, "PlanHint")}', '{Text(w, "WorkoutHint")}')");
        plans.Select();
        Check(window.WorkoutsCommand(NavCommand.Up) && ReferenceEquals(w.ActiveControl, tabs), "Up from the first picker goes back to the tab strip");

        folders.Select();
        Check(window.WorkoutsCommand(NavCommand.Next) && folders.Text == "Intervals" && folders.Model.Current?.Detail == "55 workouts" && folders.Pending && workouts.Count == 0 && Text(w, "WorkoutHint").Length == 0,
            $"Right on the folder picker steps to the next folder; its workouts are listed once the picker settles (got '{folders.Text}', '{folders.Model.Current?.Detail}')");
        await Pump(Settle);
        Check(workouts.Count == 55 && workouts.Text == "Hard intervals" && workouts.Model.Position == "Workout · 1 of 55" && workoutGraph.Text.StartsWith("18:00", StringComparison.Ordinal),
            $"another folder lists its own workouts (got '{folders.Text}', {workouts.Count}, '{workouts.Text}')");
        window.WorkoutsCommand(NavCommand.Down);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 60; i++) { window.WorkoutsCommand(NavCommand.Next); workoutGraph.Update(); workouts.Update(); }   // every step read, worked out and painted
        clock.Stop();
        Check(ReferenceEquals(w.ActiveControl, workouts) && workouts.Index == 54 && !workouts.Model.CanNext && workouts.Text == "Extra workout with a long name number 29" && clock.ElapsedMilliseconds < 3000,
            $"held Right crosses the 55 workouts smoothly and stops at the last ({clock.ElapsedMilliseconds} ms for 54 steps, each one painted; got '{workouts.Text}')");
        Check(Paint(w, shots, "workouts-last-workout"), "Workouts, the last workout of a long folder (a long name, cut): paints");
        for (int i = 0; i < 60; i++) window.WorkoutsCommand(NavCommand.Previous);
        window.WorkoutsCommand(NavCommand.Activate);   // Enter on the workout picker
        await Pump(Settle);
        Check(Text(w, "NowRiding").StartsWith("Started \"Hard intervals\"", StringComparison.Ordinal), $"Enter on the workout picker while riding: started, and the line under the cards says so (got '{Text(w, "NowRiding")}')");
        Check(folders.Text == "Intervals" && workouts.Text == "Hard intervals", "the folder and workout stay chosen when the tab refills");

        // the mouse. A click on an arrow or a neighbour only steps; a click on the chosen entry, or on its graph, starts it
        sessions.Select();
        var rightArrow = new Point(sessions.Width - Theme.S(8), sessions.Height - Theme.S(12));
        for (int i = 0; i < 4; i++) ClickAt(sessions, rightArrow);
        ClickAt(sessions, new Point(Theme.S(40), sessions.Height - Theme.S(12)));   // the neighbour on the left: one step back
        await Pump(150);
        Check(sessions.Text == "W2 D1 Week 2 day 1" && Text(w, "NowRiding").StartsWith("Started \"Hard intervals\"", StringComparison.Ordinal),
            $"clicks on the right arrow and on the left neighbour step the session picker and start nothing (got '{sessions.Text}', '{Text(w, "NowRiding")}')");
        ClickAt(sessions, new Point(sessions.Width / 2, Theme.S(4)));   // the small line above the row: not the chosen entry
        await Pump(150);
        Check(Text(w, "NowRiding").StartsWith("Started \"Hard intervals\"", StringComparison.Ordinal), "a click on the line above the row starts nothing");
        ClickAt(sessions, new Point(sessions.Width / 2, sessions.Height - Theme.S(12)));
        await Pump(Settle);
        Check(Text(w, "NowRiding").StartsWith("Started Week 2 Day 1", StringComparison.Ordinal), $"a click on the chosen session starts it (got '{Text(w, "NowRiding")}')");
        Check(sessions.Index == 0, "after a start the session picker is back on the plan's next session");
        ClickAt(workoutGraph, new Point(workoutGraph.Width / 2, workoutGraph.Height / 2));
        await Pump(Settle);
        Check(Text(w, "NowRiding").StartsWith("Started \"Hard intervals\"", StringComparison.Ordinal) && ReferenceEquals(w.ActiveControl, workouts) && Text(w, "WorkoutHint") == NavRules.StartHint,
            $"a click on the workout's graph starts the workout, and the focus goes to its row (got '{Text(w, "NowRiding")}')");
        sessions.StepBy(1);
        ClickAt(sessionGraph, new Point(sessionGraph.Width / 2, sessionGraph.Height / 2));
        await Pump(Settle);
        Check(Text(w, "NowRiding").StartsWith("Started Week 1 Day 2", StringComparison.Ordinal) && ReferenceEquals(w.ActiveControl, sessions),
            $"a click on the session's graph starts the chosen session (got '{Text(w, "NowRiding")}')");
        ClickAt(folders, new Point(folders.Width / 2, folders.Height - Theme.S(12)));
        ClickAt(plans, new Point(plans.Width / 2, plans.Height - Theme.S(12)));
        await Pump(Settle);
        Check(Text(w, "NowRiding").StartsWith("Started Week 1 Day 2", StringComparison.Ordinal), $"a click on the chosen folder or the chosen plan starts nothing (got '{Text(w, "NowRiding")}')");

        // the settings file changes under the tab (a ride recorded a finished session): it refills, the workout pickers stay where the rider left them
        workouts.StepBy(1);
        workouts.StepBy(1);
        sessions.StepBy(1);
        File.SetLastWriteTimeUtc(TrainerBridgeConfig.Path, DateTime.UtcNow.AddSeconds(5));
        await Pump(700);
        Check(sessions.Index == 0 && folders.Text == "Intervals" && workouts.Index == 2 && workouts.Text == "Week 1 day 2",
            $"the settings file changed: the tab refills (the session picker on the next session again), the folder and workout stay chosen (got session {sessions.Index}, '{folders.Text}', '{workouts.Text}')");

        // no scroll bar: at the size the window opens at, on a 1366 x 768 laptop (free area 728 high), and at the minimum size
        sessions.StepBy(1);   // a session with both neighbours, for the pictures
        StackPanel? body = null;
        for (Control? p = plans.Parent; p is not null && body is null; p = p.Parent) body = p is StackPanel { Scrolls: true } s ? s : null;
        foreach (var size in new[] { startSize, new Size(startSize.Width, startSize.Height - Theme.S(32)), w.MinimumSize })
        {
            w.Size = size;
            await Pump(200);
            Check(w.Size == size && body is not null && !body.VerticalScroll.Visible && !body.HorizontalScroll.Visible && body.AutoScrollMinSize.Height <= body.ClientSize.Height,
                $"Workouts at {size.Width} x {size.Height}: the page needs no scroll bar (page needs {body?.AutoScrollMinSize.Height}, has {body?.ClientSize.Height})");
            var cut = Clipped(w, tabs.TabPages[1], vertical: true);
            Check(cut.Count == 0, $"Workouts at {size.Width} x {size.Height}: nothing is cut off, the buttons and the line under the cards included" + (cut.Count > 0 ? " (" + string.Join(", ", cut) + ")" : ""));
            Check(Paint(w, shots, $"workouts-{size.Width}x{size.Height}"), $"Workouts with a plan, at {size.Width} x {size.Height}: paints");
            Check(sessionGraph.Height >= EffortGraphView.MinHeight && workoutGraph.Height >= EffortGraphView.MinHeight && sessionGraph.Width >= Theme.S(200),
                $"Workouts at {size.Width} x {size.Height}: both effort graphs have room (session {sessionGraph.Size}, workout {workoutGraph.Size})");
            // a graph used to take all the spare height of its card (about 60 % of the card at the opening size); now well under half
            Card planCard = CardOf(sessionGraph)!, workoutCard = CardOf(workoutGraph)!;
            Check(sessionGraph.Height * 5 <= planCard.Height * 2 && workoutGraph.Height * 5 <= workoutCard.Height * 2,
                $"Workouts at {size.Width} x {size.Height}: a graph is no more than 40 % of its card (session {sessionGraph.Height} of {planCard.Height}, workout {workoutGraph.Height} of {workoutCard.Height})");
            Check(new[] { plans, sessions, folders, workouts }.All(p => p.Height >= p.Measure(0).Height && p.Height <= p.Measure(0).Height + PickerRow.MaxGrow)
                && sessions.Measure(0).Height >= Theme.Small.Height * 2 + Theme.Picker.Height + Theme.S(18) && Theme.Picker.Height > Theme.BodyBold.Height,
                $"Workouts at {size.Width} x {size.Height}: the picker rows are tall, the chosen entry in a larger font (rows {plans.Height}, {sessions.Height}, {folders.Height}, {workouts.Height})");
            Control planHint = w.Controls.Find("PlanHint", true)[0], sessionLine = w.Controls.Find("NextSession", true)[0];
            static Rectangle OnScreen(Control c) => c.RectangleToScreen(c.ClientRectangle);
            Check(OnScreen(planHint).Top >= OnScreen(planCard).Bottom && OnScreen(sessionLine).Top >= OnScreen(sessionGraph).Bottom && OnScreen(sessionLine).Bottom <= OnScreen(planCard).Bottom
                && OnScreen(w.Controls.Find("WorkoutDetail", true)[0]).Top >= OnScreen(workoutGraph).Bottom,
                $"Workouts at {size.Width} x {size.Height}: the session / workout line is under its graph, the hint line under the card");
        }
    }

    /// <summary>
    /// The logs folder box on the Logs tab (the caller has that tab open). It starts on the standard folder; a relative path is
    /// refused; another folder is made, stored in the settings file's `logs` section and used; an empty box goes back to the standard one.
    /// </summary>
    private static async Task LogsFolderBox(Form w, Action<bool, string> Check)
    {
        var tabs = (TabControl)w.Controls.Find("Tabs", true)[0];
        tabs.SelectedIndex = 5;
        await Task.Delay(150);
        Control box = w.Controls.Find("LogsDir", true)[0];
        var save = (Button)w.Controls.Find("SaveLogsButton", true)[0];
        Check(box.Text == Ride.DefaultLogsDir && Text(w, "LogsStatus").StartsWith("The standard folder", StringComparison.Ordinal) && TrainerBridgeConfig.LoadLogsDir() == "",
            $"Logs: the logs folder box starts on the standard folder (got '{box.Text}', '{Text(w, "LogsStatus")}')");
        box.Text = "relative\\logs";
        save.PerformClick();
        Check(Text(w, "LogsStatus").StartsWith("Not saved", StringComparison.Ordinal) && TrainerBridgeConfig.LoadLogsDir() == "", $"Logs: a relative logs folder is refused (got '{Text(w, "LogsStatus")}')");
        string own = Path.Combine(Path.GetDirectoryName(TrainerBridgeConfig.Path)!, "My logs");
        box.Text = "  " + own + " ";
        save.PerformClick();
        await Task.Delay(150);
        Check(Text(w, "LogsStatus").StartsWith("Saved", StringComparison.Ordinal) && TrainerBridgeConfig.LoadLogsDir() == own && Directory.Exists(own) && Ride.LogsDir == own && box.Text == own,
            $"Logs: another logs folder is made, saved and used from then on (got '{Text(w, "LogsStatus")}', settings '{TrainerBridgeConfig.LoadLogsDir()}')");
        box.Text = "";
        save.PerformClick();
        await Task.Delay(150);
        Check(Text(w, "LogsStatus").StartsWith("Saved", StringComparison.Ordinal) && TrainerBridgeConfig.LoadLogsDir() == "" && Ride.LogsDir == Ride.DefaultLogsDir && box.Text == Ride.DefaultLogsDir,
            $"Logs: an empty box goes back to the standard folder (got '{Text(w, "LogsStatus")}', box '{box.Text}')");
    }

    private static PickerRow Picker(Form w, string name) => (PickerRow)w.Controls.Find(name, true)[0];

    private static string Text(Form w, string name) => w.Controls.Find(name, true)[0].Text;

    private static Card? CardOf(Control c)
    {
        for (Control? p = c.Parent; p is not null; p = p.Parent) if (p is Card card) return card;
        return null;
    }

    private static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));

    private static int Cards(Control parent) => parent.Controls.Cast<Control>().Sum(c => (c is Card ? 1 : 0) + Cards(c));

    private static int ColumnsWidth(ListView list) => list.Columns.Cast<ColumnHeader>().Sum(c => c.Width);

    /// <summary>The named controls on a tab that stick out of the window (to the side always; below too on a tab that does not scroll).</summary>
    private static List<string> Clipped(Form w, Control page, bool vertical)
    {
        var bad = new List<string>();
        var limit = w.ClientRectangle;
        Walk(page);
        return bad;

        void Walk(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (!c.Visible) continue;
                bool inner = c is UpDownBase || c.Parent is UpDownBase || c.Parent?.Parent is UpDownBase;   // a number box is clipped by its frame on purpose
                if (!inner && c.Name.Length > 0 && (c is not Label || c.Text.Length > 0))
                {
                    var r = w.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
                    if (r.Width <= 0 || r.Height <= 0 || r.Left < 0 || r.Right > limit.Right || (vertical && (r.Top < 0 || r.Bottom > limit.Bottom))) bad.Add($"{c.Name} {r}");
                }
                if (!inner) Walk(c);
            }
        }
    }

    /// <summary>Paint the whole window into a bitmap; true when it holds the page, card and text colours (not a blank or half-painted window).</summary>
    private static bool Paint(Form w, string? folder, string name)
    {
        using var bmp = new Bitmap(w.Width, w.Height);
        w.DrawToBitmap(bmp, new Rectangle(0, 0, w.Width, w.Height));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
            bmp.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
        }
        int page = 0, card = 0, bright = 0;
        for (int y = 0; y < bmp.Height; y += 3)
            for (int x = 0; x < bmp.Width; x += 3)
            {
                var c = bmp.GetPixel(x, y);
                if (c.ToArgb() == Theme.Page.ToArgb()) page++;
                else if (c.ToArgb() == Theme.Surface.ToArgb()) card++;
                else if (c.GetBrightness() > 0.6f) bright++;
            }
        return page > 200 && card > 200 && bright > 20;
    }
}
