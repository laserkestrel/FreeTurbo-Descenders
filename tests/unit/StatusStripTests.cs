using DescendersBridge.Gui;

namespace DescendersBridge;

/// <summary>
/// The Ride tab has no "Riding / Not riding" line; one slim status strip under the readings says Trainer and Game, each a light
/// plus a few words. What the strip says for each state (<see cref="StripRules.Trainer"/>, <see cref="GamePresence.Short"/>), the log line
/// for each change of the ride state, once (<see cref="StripRules.LogLine"/>), and the one size of status light.
/// Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class StatusStripTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Status strip: what Trainer and Game say, the ride state in the log, one size of light");
        const RideController.State Stopped = RideController.State.Stopped, Running = RideController.State.Running,
            Stopping = RideController.State.Stopping, Failed = RideController.State.Failed;

        // ---- Trainer ----
        check(StripRules.Trainer(Running, false, false, Health.Good, 12345) == new StripLine("Trainer: Connected", Health.Good, false),
            "trainer, connected and heard: Connected, a green light");
        check(StripRules.Trainer(Running, false, false, Health.Searching, 12345) == new StripLine("Trainer: Searching...", Health.Searching, false)
            && StripRules.Trainer(Running, false, false, Health.Weak, 12345).Text == "Trainer: Weak signal"
            && StripRules.Trainer(Running, false, false, Health.Lost, 12345) == new StripLine("Trainer: Signal lost", Health.Lost, false),
            "trainer, connected: searching, weak signal and signal lost each say so, with the health's own light");
        check(StripRules.Trainer(Running, false, false, Health.NotChosen, null) == new StripLine("Trainer: Not set up", Health.NotChosen, true),
            "connected with sensors only (no trainer chosen): not set up, a hollow light");
        var lost = StripRules.Trainer(Running, stickGone: true, false, Health.NotChosen, 12345);
        check(lost.Text.Contains("ANT+ stick lost, reconnecting") && lost.Text.Contains("USB") && lost.Light == Health.Weak && !lost.Hollow,
            $"the ANT+ stick lost under a running ride: the strip says so and what to check, a warning light ({lost.Text})");
        var stopping = StripRules.Trainer(Stopping, false, false, Health.NotChosen, 12345);
        check(stopping.Text.Contains("stopping (releasing the trainer)") && stopping.Light == Health.Weak && !stopping.Hollow
            && StripRules.Trainer(Stopping, false, searching: true, Health.NotChosen, 12345) == stopping, $"stopping: releasing the trainer, a warning light, during a search too ({stopping.Text})");
        var failed = StripRules.Trainer(Failed, false, false, Health.NotChosen, 12345);
        check(failed.Text.Contains("message above") && failed.Light == Health.Lost && !failed.Hollow, $"failed: points at the message bar, a red light ({failed.Text})");
        check(StripRules.Trainer(Stopped, false, searching: true, Health.NotChosen, 12345) == new StripLine("Trainer: paused during the device search", Health.Searching, true)
            && StripRules.Trainer(Stopped, false, searching: true, Health.NotChosen, null).Text.Contains("search"), "not connected because a device search has the stick: paused during the search");
        check(StripRules.Trainer(Stopped, false, false, Health.NotChosen, 12345) == new StripLine("Trainer: device 12345, not connected", Health.Searching, true),
            "not connected, a trainer saved: its number, not connected, a hollow light");
        check(StripRules.Trainer(Stopped, false, false, Health.NotChosen, null) == new StripLine("Trainer: not chosen yet (see My devices)", Health.NotChosen, true),
            "no trainer saved: not chosen yet, and where to choose one");

        // ---- Game ----
        const double never = double.PositiveInfinity;
        var h = new GameHello("descenders", "1.0.60", new[] { "gradient", "speed" });
        check(GamePresence.Short(false, null, never) == "Game: not connected", "game, the window not connected to the devices: not connected");
        check(GamePresence.Short(true, null, never) == "Game: waiting for a game", "game, connected to the devices, none heard yet: waiting");
        check(GamePresence.Short(true, h, 1) == "Game: Descenders connected" && GamePresence.Short(true, null, 1) == "Game: connected", "game heard in the last 5 s: connected, by name once it said hello");
        check(GamePresence.Short(true, h, 200) == "Game: Descenders not connected (last heard 3 min ago)" && GamePresence.Short(true, null, 30) == "Game: not connected (last heard 30 s ago)",
            $"game gone: not connected, and when it was last heard ({GamePresence.Short(true, h, 200)})");

        // every text of the strip: its subject first, a few words, no button that is gone, nothing about "riding"
        var texts = new List<string> { GamePresence.Short(false, null, never), GamePresence.Short(true, null, never), GamePresence.Short(true, h, 1), GamePresence.Short(true, h, 5000) };
        foreach (var state in Enum.GetValues<RideController.State>())
            foreach (bool stick in new[] { false, true })
                foreach (bool searching in new[] { false, true })
                    foreach (var health in Enum.GetValues<Health>())
                        foreach (int? number in new int?[] { null, 65535 })
                            texts.Add(StripRules.Trainer(state, stick, searching, health, number).Text);
        var bad = texts.Distinct().Where(t => !(t.StartsWith("Trainer: ", StringComparison.Ordinal) || t.StartsWith("Game: ", StringComparison.Ordinal)) || t.Length > 60
            || t.Contains("Not riding", StringComparison.OrdinalIgnoreCase) || t.Contains(": Riding", StringComparison.OrdinalIgnoreCase) || t.Contains("Launch", StringComparison.OrdinalIgnoreCase)).ToList();
        check(bad.Count == 0, "every strip text starts with Trainer: or Game:, is at most 60 characters, and has no \"Riding / Not riding\" in it" + (bad.Count > 0 ? " (" + string.Join(" | ", bad) + ")" : ""));

        // ---- the ride state in the log: each change once ----
        check(StripRules.Phase(Running, false) == RidePhase.Connected && StripRules.Phase(Running, true) == RidePhase.StickLost && StripRules.Phase(Stopping, true) == RidePhase.Stopping
            && StripRules.Phase(Failed, false) == RidePhase.Failed && StripRules.Phase(Stopped, true) == RidePhase.Stopped, "ride state: the stick only counts as lost under a running ride");
        check(Enum.GetValues<RidePhase>().All(p => StripRules.LogLine(p, p) is null), "ride state log: no change, no line (each change is logged once)");
        check(StripRules.LogLine(RidePhase.Stopped, RidePhase.Connected) == "Ride state: connected" && StripRules.LogLine(RidePhase.Failed, RidePhase.Connected) == "Ride state: connected",
            "ride state log: connected");
        check(StripRules.LogLine(RidePhase.Connected, RidePhase.StickLost) == "Ride state: ANT+ stick lost, reconnecting"
            && StripRules.LogLine(RidePhase.StickLost, RidePhase.Connected) == "Ride state: reconnected (the ANT+ stick is back)", "ride state log: stick lost, then reconnected");
        check(StripRules.LogLine(RidePhase.Connected, RidePhase.Stopping) == "Ride state: stopping (releasing the trainer)" && StripRules.LogLine(RidePhase.Stopping, RidePhase.Stopped) == "Ride state: stopped"
            && StripRules.LogLine(RidePhase.StickLost, RidePhase.Stopping) == "Ride state: stopping (releasing the trainer)", "ride state log: stopping, then stopped");
        check(StripRules.LogLine(RidePhase.Connected, RidePhase.Failed) == "Ride state: failed", "ride state log: failed");

        // the connection itself writes those lines: the demo one here (no stick; a temp settings file, never the real one), each change once
        var realOut = Console.Out;
        var log = new StringWriter();
        string dir = Path.Combine(Path.GetTempPath(), "descenders_strip_" + Guid.NewGuid().ToString("N"));
        TrainerBridgeConfig.PathOverride = Path.Combine(dir, TrainerBridgeConfig.FileName);
        Console.SetOut(log);
        try
        {
            var ride = new RideController(demo: true);
            ride.Start();
            ride.Start();   // already connected: no second line
            ride.StopAsync().GetAwaiter().GetResult();
            ride.StopAsync().GetAwaiter().GetResult();
        }
        finally
        {
            Console.SetOut(realOut);
            TrainerBridgeConfig.PathOverride = null;
            try { Directory.Delete(dir, true); } catch { }
        }
        var lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith("Ride state:", StringComparison.Ordinal)).ToList();
        check(lines.SequenceEqual(new[] { "Ride state: connected", "Ride state: stopped" }), $"a connection logs each change of its state once (got: {string.Join(" | ", lines)})");

        // ---- one size of light ----
        check(StatusIndicator.Dot == Theme.LightSize && Theme.LightSize == Theme.S(9), "status lights: one size from the theme, not from the font of the words next to them");
    }
}
