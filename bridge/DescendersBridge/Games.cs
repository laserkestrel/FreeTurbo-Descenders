using System.Globalization;
using System.Text.Json;

namespace DescendersBridge;

/// <summary>
/// The `hello` message a game sends (docs/IPC.md): which game is connected, its build, and what it can show. The bridge logs it and
/// the Ride tab's status strip shows it.
/// </summary>
public sealed record GameHello(string Game, string Version, IReadOnlyList<string> Capabilities)
{
    /// <summary>Reads one `hello`; null when it has no game name. Pure, for `selftest`.</summary>
    public static GameHello? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("game", out var g) || g.ValueKind != JsonValueKind.String || g.GetString() is not { Length: > 0 } game) return null;
        string version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var caps = new List<string>();
        if (root.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array)
            foreach (var e in c.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) caps.Add(s);
        return new GameHello(game, version, caps);
    }

    /// <summary>"Descenders" for "descenders": the game's id with a capital first letter.</summary>
    public string DisplayName => Game.Length == 0 ? "A game" : char.ToUpperInvariant(Game[0]) + Game[1..];

    public string CapabilityText => Capabilities.Count == 0 ? "none" : string.Join(", ", Capabilities);

    /// <summary>For the log: "descenders, version 1.0.60 ..., can show gradient, speed, hud, menu".</summary>
    public string Describe() => $"{Game}, version {(Version.Length > 0 ? Version : "unknown")}, can show {CapabilityText}";

    /// <summary>Same game, build and capabilities (records compare lists by reference).</summary>
    public bool SameAs(GameHello? o) => o is not null && o.Game == Game && o.Version == Version && o.Capabilities.SequenceEqual(Capabilities);
}

/// <summary>What <see cref="GamePresence.LogStep"/> asks the ride loop to do with the ride log.</summary>
public enum RideLogStep { None, End, Reopen }

/// <summary>
/// The bridge-as-hub rules, pure so `selftest` covers them: a game counts as connected while any of its packets (telemetry, `hello`,
/// `intent`) arrived in the last <see cref="ConnectedSeconds"/>; the ride log is ended when the game has been gone for <see cref="GoneSeconds"/>
/// (not the moment it quits: a game restart or a crash-and-relaunch keeps one log), and a new one starts when a game is heard again.
/// The headless `run --auto` bridge (started by the game, no window) stops once no game has been heard for <see cref="AutoIdleExitMinutes"/>.
/// </summary>
public static class GamePresence
{
    public const double ConnectedSeconds = 5, GoneSeconds = 120, AutoIdleExitMinutes = 10;

    /// <param name="secondsSinceHeard">since the game's last packet; +infinity = never heard.</param>
    public static bool Connected(double secondsSinceHeard) => secondsSinceHeard <= ConnectedSeconds;

    /// <summary>End an open log once the game has been gone <see cref="GoneSeconds"/> (a log with no game heard yet stays open, the game may still be
    /// starting); reopen one (<paramref name="canReopen"/>: the ride keeps logging) as soon as a game is heard again.</summary>
    public static RideLogStep LogStep(bool logOpen, bool canReopen, double secondsSinceHeard)
    {
        if (logOpen) return !double.IsInfinity(secondsSinceHeard) && secondsSinceHeard > GoneSeconds ? RideLogStep.End : RideLogStep.None;
        return canReopen && Connected(secondsSinceHeard) ? RideLogStep.Reopen : RideLogStep.None;
    }

    /// <summary>The headless bridge should stop: no game heard for <paramref name="limitSeconds"/>, counted from the last packet, or from the start
    /// when none ever came (the game crashed while loading, say).</summary>
    public static bool IdleExit(double secondsSinceHeard, double uptimeSeconds, double limitSeconds) =>
        (double.IsInfinity(secondsSinceHeard) ? uptimeSeconds : secondsSinceHeard) > limitSeconds;

    /// <summary>The Game entry of the Ride tab's status strip: a few words; <see cref="Describe"/> is its tooltip.</summary>
    public static string Short(bool listening, GameHello? hello, double secondsSinceHeard)
    {
        if (!listening) return "Game: not connected";
        if (Connected(secondsSinceHeard)) return "Game: " + (hello is null ? "connected" : hello.DisplayName + " connected");
        if (double.IsInfinity(secondsSinceHeard)) return "Game: waiting for a game";
        return $"Game: {(hello is null ? "" : hello.DisplayName + " ")}not connected (last heard {Ago(secondsSinceHeard)} ago)";
    }

    /// <summary>The game in a sentence, with what to do (the tooltip of the status strip's Game entry). <paramref name="listening"/>: a ride
    /// is running, so a game can connect at all (the window starts it by itself once a device is saved; there is no button for it).</summary>
    public static string Describe(bool listening, GameHello? hello, double secondsSinceHeard)
    {
        if (!listening) return "No game connected. A game can connect once this window is connected to your devices.";
        string name = hello?.DisplayName ?? "A game";
        if (Connected(secondsSinceHeard))
            return hello is null ? "A game is connected." : $"{name} is connected. It can show: {hello.CapabilityText}.";
        if (double.IsInfinity(secondsSinceHeard)) return "Waiting for a game. Start a workout on the Workouts tab to open Descenders, or start the game from Steam.";
        return $"{name} is not connected (last heard {Ago(secondsSinceHeard)} ago). The trainer is kept flat until it comes back.";
    }

    /// <summary>Closing the window with a game connected asks first.</summary>
    public const string CloseQuestion = "A game is still connected. Close anyway?\n\nThe trainer is released (made easy to pedal) before the bridge closes.";

    private static string Ago(double s) => s < 90 ? ((int)s).ToString(CultureInfo.InvariantCulture) + " s" : ((int)(s / 60)).ToString(CultureInfo.InvariantCulture) + " min";
}

/// <summary>
/// Launching a game from the window: through Steam, as tools\pr-validate.ps1 does. Later other games.
/// There is no button for it; starting a workout or a plan session on the Workouts tab opens the game when none is connected
/// (<see cref="LaunchOnStart"/>, pure, tests\unit\HubTests.cs). A free ride without a workout: the rider starts the game from Steam.
/// </summary>
public static class GameLauncher
{
    public const int DescendersSteamAppId = 681280;

    /// <summary>The game takes about a minute from Steam to its first packet: a second Start press in that time must not ask Steam again.</summary>
    public const double RelaunchGuardSeconds = 120;

    /// <summary>Added to the Workouts tab's line when the Start press also opened the game.</summary>
    public const string OpeningText = "Opening Descenders through Steam: the workout starts when you pedal in the game.";

    public static string SteamUri(int appId) => "steam://rungameid/" + appId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Should this Start press on the Workouts tab open the game?</summary>
    /// <param name="armed">the press armed its workout (a file that did not load, or no connection to the devices, opens nothing).</param>
    /// <param name="secondsSinceHeard">since the game's last packet; +infinity = never heard (<see cref="GamePresence.Connected"/>).</param>
    /// <param name="secondsSinceLaunch">since this window last asked Steam for the game; +infinity = never.</param>
    public static bool LaunchOnStart(bool armed, double secondsSinceHeard, double secondsSinceLaunch) =>
        armed && !GamePresence.Connected(secondsSinceHeard) && secondsSinceLaunch > RelaunchGuardSeconds;
}
