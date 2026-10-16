using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// The bridge as the hub: the game's `hello` message (src\Hello.cs, read by <see cref="GameHello"/>), the presence rules
/// (<see cref="GamePresence"/>: connected, ride log ended when the game has been gone a while, headless idle exit, the window's words) and
/// <see cref="ModLink"/> ending / reopening the ride log. Run by `FreeTurbo-Bridge selftest`; temp files and an ephemeral UDP port only.
/// </summary>
public static class HubTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Hello: the mod's message, read by the bridge");
        {
            string json = Hello.Message("1.0.60 abc123 on main");
            using var doc = JsonDocument.Parse(json);
            var h = GameHello.Parse(doc.RootElement);
            check(doc.RootElement.GetProperty("type").GetString() == "hello", "type hello");
            check(h is { Game: "descenders", Version: "1.0.60 abc123 on main" } && h.Capabilities.SequenceEqual(new[] { "gradient", "speed", "hud" }),
                $"game, version and capabilities come through, no in-game menu any more ({json})");
            check(h!.DisplayName == "Descenders" && h.Describe().Contains("can show gradient, speed, hud") && !h.Describe().Contains("menu"), "shown as Descenders, capabilities listed");
            using var noGame = JsonDocument.Parse("{\"type\":\"hello\",\"version\":\"1\"}");
            check(GameHello.Parse(noGame.RootElement) is null, "a hello without a game name is ignored");
            using var bare = JsonDocument.Parse("{\"type\":\"hello\",\"game\":\"other\",\"capabilities\":[\"gradient\",3,\"\"]}");
            var b = GameHello.Parse(bare.RootElement);
            check(b is { Version: "" } && b.Capabilities.SequenceEqual(new[] { "gradient" }), "missing version is empty, non-text capabilities are skipped");
            check(h.SameAs(GameHello.Parse(doc.RootElement)) && !h.SameAs(b) && !h.SameAs(null), "same game and build compare equal (a repeat is not logged again)");
        }

        Console.WriteLine("Game presence: connected, ride log, idle exit");
        {
            double never = double.PositiveInfinity;
            check(GamePresence.Connected(0.2) && GamePresence.Connected(5) && !GamePresence.Connected(6) && !GamePresence.Connected(never), "connected = heard in the last 5 s");
            check(GamePresence.LogStep(true, true, 1) == RideLogStep.None, "game there: the log stays open");
            check(GamePresence.LogStep(true, true, 60) == RideLogStep.None, "game quit a minute ago: the log stays open (it may be restarting)");
            check(GamePresence.LogStep(true, true, 121) == RideLogStep.End, "gone for over 2 minutes: the ride log ends");
            check(GamePresence.LogStep(true, true, never) == RideLogStep.None, "no game heard yet: the log stays open (the game may still be loading)");
            check(GamePresence.LogStep(false, true, 0.5) == RideLogStep.Reopen, "a game back after the log ended: a new log");
            check(GamePresence.LogStep(false, true, 30) == RideLogStep.None && GamePresence.LogStep(false, false, 0.5) == RideLogStep.None,
                "still gone, or no log ever started: nothing to do");
            check(!GamePresence.IdleExit(30, 3600, 600) && GamePresence.IdleExit(601, 3600, 600), "headless bridge: stops 10 min after the game was last heard");
            check(!GamePresence.IdleExit(never, 300, 600) && GamePresence.IdleExit(never, 601, 600), "no game ever heard: 10 min after the bridge started");
        }

        Console.WriteLine("Game presence: the window's Games line and the close question");
        {
            var h = new GameHello("descenders", "1.0.60", new[] { "gradient", "speed" });
            string notListening = GamePresence.Describe(false, null, double.PositiveInfinity);
            check(notListening.StartsWith("No game connected") && !notListening.Contains("Start riding"), "not connected to the devices: no game can connect, and no button is named");
            string waiting = GamePresence.Describe(true, null, double.PositiveInfinity);
            check(waiting.StartsWith("Waiting for a game") && waiting.Contains("Workouts tab") && waiting.Contains("Steam") && !waiting.Contains("Launch Descenders"),
                $"riding, nothing heard: waiting, and how to open the game without the button that is gone ({waiting})");
            check(GamePresence.Describe(true, h, 1) == "Descenders is connected. It can show: gradient, speed.", "connected: the game and what it can show");
            string gone = GamePresence.Describe(true, h, 200);
            check(gone.StartsWith("Descenders is not connected") && gone.Contains("3 min") && gone.Contains("flat"), $"gone: when it was last heard, the trainer is flat ({gone})");
            check(GamePresence.Describe(true, h, 30).Contains("30 s"), "seconds under 90 s");
            check(GamePresence.CloseQuestion.StartsWith("A game is still connected. Close anyway?"), "the close question matches the issue's wording");
            check(GameLauncher.SteamUri(GameLauncher.DescendersSteamAppId) == "steam://rungameid/681280", "the game is opened through Steam, as tools\\pr-validate.ps1 does");
        }

        Console.WriteLine("Launch the game on a Start press? (no Launch Descenders button)");
        {
            double never = double.PositiveInfinity;
            check(GameLauncher.LaunchOnStart(armed: true, secondsSinceHeard: never, secondsSinceLaunch: never), "workout armed, no game ever heard: open Descenders");
            check(GameLauncher.LaunchOnStart(true, 6, never) && GameLauncher.LaunchOnStart(true, 900, never), "workout armed, the game quit (not heard for over 5 s): open it again");
            check(!GameLauncher.LaunchOnStart(true, 0.2, never) && !GameLauncher.LaunchOnStart(true, 5, never), "a game is connected: the press only arms the workout, as before");
            check(!GameLauncher.LaunchOnStart(false, never, never) && !GameLauncher.LaunchOnStart(false, 0.2, never),
                "nothing armed (not connected to the devices, or the file did not load): no game is opened");
            check(!GameLauncher.LaunchOnStart(true, never, 30) && !GameLauncher.LaunchOnStart(true, never, GameLauncher.RelaunchGuardSeconds),
                "a second Start press while the game is still loading does not ask Steam again");
            check(GameLauncher.LaunchOnStart(true, never, GameLauncher.RelaunchGuardSeconds + 1), "the game never came up after 2 minutes: the next Start press asks Steam again");
            check(GameLauncher.OpeningText.Contains("Steam") && GameLauncher.OpeningText.Contains("pedal in the game"), "the Workouts tab's line says the game is opening and when the workout starts");
            check(Gui.FriendlyText.SteamMissing.Text.Contains("from Steam") && !Gui.FriendlyText.SteamMissing.Text.Contains("Launch Descenders")
                && !Gui.FriendlyText.AlreadyRunning.Text.Contains("Launch Descenders"), "Steam missing / another bridge copy: the banners do not name the button that is gone");
        }

        Console.WriteLine("ModLink: hello received, ride log ended and reopened");
        string dir = Path.Combine(Path.GetTempPath(), "descenders_hub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var mod = new ModLink(telemetryPort: 0, commandPort: 9);   // an ephemeral port: never the real 9871
            check(double.IsPositiveInfinity(mod.SecondsSinceHeard) && mod.Hello is null && !mod.LogOpen && !mod.CanReopenLog, "fresh link: nothing heard, no log");
            string first = Path.Combine(dir, "ride_1.jsonl"), second = Path.Combine(dir, "ride_2.jsonl");
            mod.StartLog(first, 1, () => second);
            using (var udp = Loopback.Sender())
            {
                byte[] b = Encoding.UTF8.GetBytes(Hello.Message("test build"));
                udp.Send(b, b.Length, "127.0.0.1", mod.ListenPort);
            }
            for (int i = 0; i < 100 && mod.Hello is null; i++) Thread.Sleep(20);
            check(mod.Hello is { Game: "descenders", Version: "test build" }, "the hello arrives and is kept");
            check(GamePresence.Connected(mod.SecondsSinceHeard), "a hello counts as the game being there");
            check(mod.LogOpen && mod.CanReopenLog, "the log is open");
            mod.EndLog("test");
            check(!mod.LogOpen && File.ReadAllText(first).Contains("\"type\":\"hello\""), "EndLog closes it; the hello was logged");
            mod.EndLog("again");
            mod.ReopenLog();
            check(mod.LogOpen && File.Exists(second), "ReopenLog starts the next file");
            mod.DropIntents();
            check(!mod.TryTakeIntent(out _), "DropIntents leaves nothing queued");

            int stops = 0;
            mod.ShutdownRequested = () => Interlocked.Increment(ref stops);
            using (var udp = Loopback.Sender())
            {
                byte[] b = Encoding.UTF8.GetBytes("{\"type\":\"shutdown\"}");   // what `FreeTurbo-Bridge stop` sends
                udp.Send(b, b.Length, "127.0.0.1", mod.ListenPort);
            }
            for (int i = 0; i < 100 && Volatile.Read(ref stops) == 0; i++) Thread.Sleep(20);
            check(stops == 1, "`stop` reaches the headless bridge's handler (run --auto then releases the trainer and exits)");

            // the game says `bye` when it quits; the headless bridge stops on it, a bridge with no handler (the window, run --sim) carries on
            using (var doc = JsonDocument.Parse(Hello.Bye()))
                check(doc.RootElement.GetProperty("type").GetString() == "bye" && doc.RootElement.GetProperty("game").GetString() == "descenders", $"the mod's bye message ({Hello.Bye()})");
            int quits = 0;
            mod.GameQuit = () => Interlocked.Increment(ref quits);
            using (var udp = Loopback.Sender())
            {
                byte[] b = Encoding.UTF8.GetBytes(Hello.Bye());
                udp.Send(b, b.Length, "127.0.0.1", mod.ListenPort);
            }
            for (int i = 0; i < 100 && Volatile.Read(ref quits) == 0; i++) Thread.Sleep(20);
            check(quits == 1 && stops == 1, "the game's `bye` reaches the headless bridge's handler (run --auto then releases the trainer and exits), and is not a `stop`");
            mod.GameQuit = null;
            long rxBefore = Interlocked.Read(ref mod.RxCount);
            using (var udp = Loopback.Sender())
            {
                byte[] b = Encoding.UTF8.GetBytes(Hello.Bye());
                udp.Send(b, b.Length, "127.0.0.1", mod.ListenPort);
                for (int i = 0; i < 100 && Interlocked.Read(ref mod.RxCount) == rxBefore; i++) Thread.Sleep(20);
                b = Encoding.UTF8.GetBytes(Hello.Message("after bye"));
                udp.Send(b, b.Length, "127.0.0.1", mod.ListenPort);
            }
            for (int i = 0; i < 100 && mod.Hello is not { Version: "after bye" }; i++) Thread.Sleep(20);
            check(quits == 1 && mod.Hello is { Version: "after bye" }, "a bridge with no handler (the window, run --sim) ignores `bye` and keeps listening: the game may come back");
        }
        catch (SocketException ex) { check(false, "could not open a UDP port for the test: " + ex.Message); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
