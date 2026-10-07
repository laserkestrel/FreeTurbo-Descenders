using System.Globalization;
using DescendersBridge.Ant;

namespace DescendersBridge.Gui;

/// <summary>A device's connection health as the window shows it.</summary>
public enum Health { NotChosen, Searching, Good, Weak, Lost, NotFound }

/// <summary>
/// The rules behind the window's health lights. Pure, so `selftest` covers them. ANT+ sensors broadcast about 4 times a second:
/// a device heard less than <see cref="WeakRate"/> times a second, or not for over <see cref="WeakGapSeconds"/>, has a weak signal (too far
/// from the USB stick, or blocked); not heard for <see cref="AntSession.StaleSeconds"/> it is lost (its readings show as "--" by then too).
/// A sensor that was searched for and did not answer is "not found": the ride carries on without it and it is looked for again.
/// </summary>
public static class HealthRules
{
    public const double WeakRate = 2.5, WeakGapSeconds = 1.0, BluetoothWeakGapSeconds = 2.0;

    /// <param name="recentRate">messages per second over the last few seconds; null = not measured yet (the first seconds).</param>
    public static Health Classify(LinkInfo? link, double? recentRate)
    {
        if (link is not { } l) return Health.NotChosen;
        if (l.NotFound) return Health.NotFound;
        if (l.Messages == 0) return Health.Searching;
        if (l.SecondsSinceLast > AntSession.StaleSeconds) return Health.Lost;
        // a Bluetooth trainer sends its data about once a second, which is not a weak signal; only a gap counts
        if (l.Key.IsBluetooth) return l.SecondsSinceLast > BluetoothWeakGapSeconds ? Health.Weak : Health.Good;
        if (l.SecondsSinceLast > WeakGapSeconds || recentRate < WeakRate) return Health.Weak;
        return Health.Good;
    }

    /// <summary>Short words for the light: the state only (the tooltip, <see cref="Advice"/>, says what it means for the ride).</summary>
    public static string Text(Health h) => h switch
    {
        Health.NotChosen => "Not set up",
        Health.Searching => "Searching...",
        Health.Good => "Connected",
        Health.Weak => "Weak signal",
        Health.NotFound => "Not found",
        _ => "Signal lost",
    };

    /// <summary>A sentence for the tooltip: what it means and what to do.</summary>
    /// <param name="bluetooth">the device is a Bluetooth one: nothing about the USB stick then.</param>
    public static string Advice(Health h, bool bluetooth = false) => h switch
    {
        Health.NotChosen => "No device is chosen for this. Go to the \"My devices\" tab and press \"Search for devices\".",
        Health.Searching => "Looking for this device. Make sure it is switched on; pedal or move to wake it up.",
        Health.Good => "Working well.",
        Health.Weak => bluetooth ? "The signal is weak. Move the device and this PC closer together."
            : "The signal is weak. Move the USB stick closer to the device (a USB extension cable helps).",
        Health.NotFound => "This device did not answer, so the ride carries on without it. It is looked for again all through the ride: switch it on (or put the strap on) and it will appear here.",
        _ => bluetooth ? "Nothing heard from this device for a few seconds. Check it is switched on, close to this PC, and not connected to another app."
            : "Nothing heard from this device for a few seconds. Check it is switched on and close to the USB stick.",
    };
}

/// <summary>
/// One press of "Search for devices" searches ANT+ and Bluetooth together, on whatever this PC has. No stick is no error by
/// itself. Pure, so `selftest` covers the four cases (tests\unit\DeviceSearchTests.cs).
/// </summary>
public static class SearchRules
{
    public const string BluetoothOnly = "No ANT+ stick found: searching Bluetooth only";
    public const string NoKeyBluetoothOnly = "This copy of the bridge cannot search ANT+: searching Bluetooth only";
    public const string AntOnly = ": searching ANT+ only";
    /// <summary>A Bluetooth scan that Windows stopped for a reason of its own (the log has it).</summary>
    public const string BluetoothFailed = "Bluetooth could not be searched on this PC";

    /// <summary>The quiet line under the search button; null = nothing to say (both are searched, or neither: <see cref="Failure"/>).</summary>
    /// <param name="ant">the ANT+ stick was opened.</param>
    /// <param name="bluetoothProblem">why Bluetooth is not searched (<see cref="Bluetooth.BleRules.Problem"/>: "This PC has no Bluetooth"); null = it is.</param>
    /// <param name="keyMissing">ANT+ is out because this build has no ANT+ key, not because the stick is missing.</param>
    public static string? Note(bool ant, string? bluetoothProblem, bool keyMissing = false)
    {
        if (ant) return bluetoothProblem is null ? null : bluetoothProblem.TrimEnd('.') + AntOnly;
        if (bluetoothProblem is not null) return null;
        return keyMissing ? NoKeyBluetoothOnly : BluetoothOnly;
    }

    /// <summary>The one message of a search that has nothing to search with; null = it can search.</summary>
    public static FriendlyMessage? Failure(bool ant, string? bluetoothProblem, bool keyMissing = false) =>
        ant || bluetoothProblem is null ? null : keyMissing ? FriendlyText.NoKey : FriendlyText.NoRadio;

    /// <summary>The found list's Signal cell for a Bluetooth device, in the words of the ANT+ rows, from its signal strength.</summary>
    public static string BluetoothSignal(int rssiDbm) => rssiDbm >= -70 ? "Strong" : rssiDbm >= -85 ? "OK" : "Weak";
}

/// <summary>What the window does about its connection right now (<see cref="ConnectRules.Decide"/>).</summary>
public enum ConnectStep { Connect, Wait, NoDevice, NoKey }

/// <summary>
/// The window connects by itself (the ANT+ stick, the saved devices, UDP 9871, the ride loop); there is no Start / Stop button.
/// The rule is asked when the window opens, after a device search, after a Save on My devices / Rider and bike, and on "Try again".
/// Pure, so `selftest` covers it (tests\unit\ConnectRulesTests.cs).
/// </summary>
public static class ConnectRules
{
    /// <param name="state">the connection as it stands.</param>
    /// <param name="deviceSaved">at least one device is saved in the settings file.</param>
    /// <param name="keyPresent">this build can talk to ANT+ devices.</param>
    /// <param name="searching">a device search has the USB stick: the connection waits until it is done.</param>
    /// <param name="asked">the rider did something that asks for a new try (Try again, Save, a search). A connection that
    /// failed is not tried again by itself: its banner offers "Try again".</param>
    /// <param name="demo">`gui --demo`: made-up readings, no stick and no saved device needed.</param>
    public static ConnectStep Decide(RideController.State state, bool deviceSaved, bool keyPresent, bool searching, bool asked, bool demo = false)
    {
        if (searching || state is RideController.State.Running or RideController.State.Stopping) return ConnectStep.Wait;
        if (state == RideController.State.Failed && !asked) return ConnectStep.Wait;
        if (demo) return ConnectStep.Connect;
        if (!deviceSaved) return ConnectStep.NoDevice;
        return keyPresent ? ConnectStep.Connect : ConnectStep.NoKey;
    }

    public static bool AnyDevice(DevicePreferences prefs) => Enum.GetValues<Role>().Any(r => prefs.Get(r) is not null);

    /// <summary>The ANT+ key is only needed when a saved device is ANT+; Bluetooth devices connect without it.</summary>
    public static bool KeyOk(bool needsAnt, bool keyPresent) => keyPresent || !needsAnt;

    /// <summary>Under a reading whose device is saved, while the window is not connected: why not.</summary>
    public static string IdleText(RideController.State state, bool searching) =>
        searching ? "Paused during the device search"
        : state == RideController.State.Stopping ? "Reconnecting..."
        : "Not connected";

    /// <summary>The Workouts tab's answer to a Start press while the window is not connected: what to do about it.</summary>
    public static string CannotStartText(RideController.State state, bool deviceSaved, bool searching) =>
        searching ? "Not connected during the device search: start it here when the search is done."
        : state == RideController.State.Stopping ? "Reconnecting to your devices: try again in a moment."
        : state == RideController.State.Failed ? "Not connected: see the message at the top of the window, then start it here."
        : !deviceSaved ? "Not connected yet: choose your trainer on the \"My devices\" tab first, then start it here."
        : "Not connected yet: try again in a moment.";
}

/// <summary>The ride as the log names it: the connection's state, plus the ANT+ stick lost under a running ride.</summary>
public enum RidePhase { Stopped, Connected, StickLost, Stopping, Failed }

/// <summary>One entry of the Ride tab's status strip: a few words and their light (the health colours; hollow = nothing there).</summary>
public sealed record StripLine(string Text, Health Light, bool Hollow);

/// <summary>
/// The Ride tab has no "Riding / Not riding" line. Under the four readings one slim strip says Trainer and Game, each a light plus
/// a few words; the trainer's entry also carries what the rider must act on (ANT+ stick lost, stopping, failed). The game's words are
/// <see cref="GamePresence.Short"/>. Each change of the ride state goes to the log once (<see cref="LogLine"/>).
/// Pure, so `selftest` covers it (tests\unit\StatusStripTests.cs).
/// </summary>
public static class StripRules
{
    public static RidePhase Phase(RideController.State state, bool stickGone) => state switch
    {
        RideController.State.Running => stickGone ? RidePhase.StickLost : RidePhase.Connected,
        RideController.State.Stopping => RidePhase.Stopping,
        RideController.State.Failed => RidePhase.Failed,
        _ => RidePhase.Stopped,
    };

    /// <summary>The log line for a change of the ride state; null = no change, nothing to log.</summary>
    public static string? LogLine(RidePhase from, RidePhase to) => from == to ? null : "Ride state: " + to switch
    {
        RidePhase.Connected => from == RidePhase.StickLost ? "reconnected (the ANT+ stick is back)" : "connected",
        RidePhase.StickLost => "ANT+ stick lost, reconnecting",
        RidePhase.Stopping => "stopping (releasing the trainer)",
        RidePhase.Failed => "failed",
        _ => "stopped",
    };

    /// <summary>The strip's Trainer entry.</summary>
    /// <param name="trainer">the trainer's health while connected (the Speed tile's light adds the device number).</param>
    /// <param name="searching">a device search has the USB stick.</param>
    /// <param name="savedNumber">the saved trainer's device number; null = none chosen.</param>
    /// <param name="savedMake">the saved trainer's manufacturer ID, when known ("Tacx 44226" instead of "device 44226").</param>
    /// <param name="savedName">a saved Bluetooth trainer's name (it has no device number); null = an ANT+ trainer.</param>
    /// <param name="wasConnected">a Bluetooth trainer answered earlier in this connection: not answering now is "lost", not "connecting".</param>
    /// <param name="noStickOnly">the connection failed only because no ANT+ stick is plugged in, on a PC that has Bluetooth (<see cref="FriendlyText.NoStickBluetoothHere"/>).</param>
    public static StripLine Trainer(RideController.State state, bool stickGone, bool searching, Health trainer, int? savedNumber, int? savedMake = null, string? savedName = null,
        bool wasConnected = false, bool noStickOnly = false)
    {
        var phase = Phase(state, stickGone);
        // a Bluetooth trainer has its own words, and none about the stick (a stick lost under it only takes an ANT+ sensor
        // away, which that sensor's own light says)
        if (savedName is not null && phase is RidePhase.Connected or RidePhase.StickLost)
            return trainer switch
            {
                Health.Good => new("Trainer: Connected (Bluetooth)", Health.Good, false),
                Health.Weak => new("Trainer: Weak signal (Bluetooth)", Health.Weak, false),
                _ when wasConnected => new("Trainer: Bluetooth connection lost, reconnecting", Health.Weak, false),
                _ => new("Trainer: connecting over Bluetooth...", Health.Searching, false),
            };
        switch (phase)
        {
            case RidePhase.StickLost: return new("Trainer: ANT+ stick lost, reconnecting (check its USB plug)", Health.Weak, false);
            case RidePhase.Connected: return new("Trainer: " + HealthRules.Text(trainer), trainer, trainer == Health.NotChosen);
            case RidePhase.Stopping: return new("Trainer: stopping (releasing the trainer)...", Health.Weak, false);
            // only the stick is missing, on a PC with Bluetooth: no red light (the note above says what to do)
            case RidePhase.Failed when noStickOnly: return new("Trainer: not connected (no ANT+ stick plugged in)", Health.Searching, true);
            case RidePhase.Failed: return new("Trainer: stopped, see the message above", Health.Lost, false);
        }
        if (searching) return new("Trainer: paused during the device search", Health.Searching, true);
        if (savedName is not null) return new("Trainer: " + savedName + ", not connected", Health.Searching, true);
        if (savedNumber is { } n) return new("Trainer: " + DeviceLabels.Short((ushort)n, savedMake) + ", not connected", Health.Searching, true);
        return new("Trainer: not chosen yet (see My devices)", Health.NotChosen, true);
    }
}

/// <summary>Where one job's device choice on the "My devices" tab stands (saved, found, unavailable and unsaved look different).</summary>
public enum ChoiceState { NotSet, Saved, SavedFound, SavedNotFound, Unsaved }

/// <summary>The rules behind the light under each device drop-down. Pure, so `selftest` covers them.</summary>
public static class DeviceChoiceRules
{
    /// <param name="chosen">what the drop-down shows (null = none / the trainer's own).</param>
    /// <param name="saved">what the settings file holds for this job.</param>
    /// <param name="heard">the chosen device answered in the last search.</param>
    /// <param name="searched">a search has finished since the window opened.</param>
    public static ChoiceState Classify(DeviceKey? chosen, DeviceKey? saved, bool heard, bool searched)
    {
        if (chosen != saved) return ChoiceState.Unsaved;
        if (chosen is null) return ChoiceState.NotSet;
        if (heard) return ChoiceState.SavedFound;
        return searched ? ChoiceState.SavedNotFound : ChoiceState.Saved;
    }

    /// <param name="trainerFallback">power and cadence: with no sensor of their own they come from the trainer.</param>
    /// <remarks>Short words for the light, the state only, as on the Ride tab's lights ("Not set up" and "Not found" are
    /// <see cref="HealthRules.Text"/>'s own words). The tooltip, <see cref="Advice"/>, says the rest.</remarks>
    public static string Text(ChoiceState s, bool trainerFallback = false) => s switch
    {
        ChoiceState.NotSet => trainerFallback ? "From the trainer" : HealthRules.Text(Health.NotChosen),
        ChoiceState.Saved => "Saved",
        ChoiceState.SavedFound => "Found",
        ChoiceState.SavedNotFound => HealthRules.Text(Health.NotFound),
        _ => "Not saved",
    };

    /// <summary>The light's colour: the Ride tab's colour for the same word (found = good, not found, not set up).</summary>
    public static Health Light(ChoiceState s) => s switch
    {
        ChoiceState.Saved => Health.Searching,
        ChoiceState.SavedFound => Health.Good,
        ChoiceState.SavedNotFound => Health.NotFound,
        ChoiceState.Unsaved => Health.Weak,
        _ => Health.NotChosen,
    };

    /// <summary>A sentence for the tooltip: what it means and what to do.</summary>
    public static string Advice(ChoiceState s, bool trainerFallback = false) => s switch
    {
        ChoiceState.NotSet => trainerFallback ? "No sensor of its own is chosen for this: the reading comes from the trainer." : "No device is chosen for this.",
        ChoiceState.Saved => "This device is saved. Press \"Search for devices\" to see whether it answers.",
        ChoiceState.SavedFound => "This device is saved and it answered in this search.",
        ChoiceState.SavedNotFound => "This device is saved, but it did not answer in this search. Make sure it is switched on; pedal or move to wake it up, then search again.",
        _ => "Changed: press Save to keep it.",
    };

    /// <summary>
    /// The devices one job's drop-down offers under its "None" line, in order: the saved device first when the search did not
    /// hear it, then every heard device that can do the job (<paramref name="heard"/> is in the order to show), then the devices used
    /// before (<paramref name="known"/>) that can do the job and were not heard, so a job set to none can get its old device back
    /// without a search. No device twice.
    /// </summary>
    public static List<(DeviceKey Key, ChoiceOrigin Origin)> Offered(Role role, DeviceKey? saved, IEnumerable<DeviceKey> heard, IEnumerable<DeviceKey> known)
    {
        var heardAll = heard.ToList();
        var list = new List<(DeviceKey Key, ChoiceOrigin Origin)>();
        if (saved is { } s && !heardAll.Contains(s)) list.Add((s, ChoiceOrigin.Saved));
        foreach (var h in heardAll)
            if (AntDeviceTypes.Roles(h.DeviceType).Contains(role) && !list.Any(i => i.Key == h)) list.Add((h, ChoiceOrigin.Heard));
        foreach (var k in known)
            if (AntDeviceTypes.Roles(k.DeviceType).Contains(role) && !heardAll.Contains(k) && !list.Any(i => i.Key == k)) list.Add((k, ChoiceOrigin.UsedBefore));
        return list;
    }

    /// <summary>
    /// What the devices of a search would be saved as for one job, in the order to offer them (the input of
    /// <see cref="Offered"/>): the ANT+ devices first, each kind most heard first. So a trainer heard both ways is two entries, the ANT+
    /// one first (it is the one suggested when nothing is saved yet); a Bluetooth trainer is no power or cadence sensor of its own.
    /// </summary>
    public static List<DeviceKey> HeardFor(Role role, IEnumerable<FoundDevice> heard) =>
        heard.OrderBy(h => h.IsBluetooth).ThenByDescending(h => h.Messages).Select(h => h.KeyFor(role)).OfType<DeviceKey>().Distinct().ToList();

    /// <summary>
    /// What follows a device's name in a drop-down. Only a device used before says so; a heard device shows no
    /// reading (the Ride tab does that), and the saved device carries neither "(saved)" nor "not found": the light under the
    /// drop-down says both.
    /// </summary>
    public static string Suffix(ChoiceOrigin origin) => origin == ChoiceOrigin.UsedBefore ? " (used before)" : "";
}

/// <summary>Why a device is in a job's drop-down: it is the saved one (not heard), the search heard it, or it was used before.</summary>
public enum ChoiceOrigin { Saved, Heard, UsedBefore }

/// <summary>Messages per second over the last <see cref="WindowSeconds"/>, from cumulative message counts sampled over time.</summary>
public sealed class RateMeter
{
    public const double WindowSeconds = 4.0, MinSpanSeconds = 1.5;
    private readonly Queue<(double T, long N)> _samples = new();

    public void Reset() => _samples.Clear();

    /// <summary>Add a sample (time in seconds, total messages so far); returns the rate, or null until <see cref="MinSpanSeconds"/> have been sampled.</summary>
    public double? Add(double t, long messages)
    {
        if (_samples.Count > 0 && messages < _samples.Last().N) _samples.Clear();   // a new ride / new device: counts restart
        _samples.Enqueue((t, messages));
        while (_samples.Count > 2 && _samples.Peek().T < t - WindowSeconds) _samples.Dequeue();
        var first = _samples.Peek();
        double span = t - first.T;
        return span < MinSpanSeconds ? null : (messages - first.N) / span;
    }
}

/// <summary>
/// How much past time the Ride tab's graphs show, `graphs.historySeconds` in TrainerBridgeConfig.json (hand-edited, no field in
/// the window). Absent or not a number = <see cref="DefaultSeconds"/>; out of range is clamped.
/// </summary>
public static class GraphHistory
{
    public const int DefaultSeconds = 300, MinSeconds = 10, MaxSeconds = 3600;

    public static int Clamp(double seconds) =>
        double.IsNaN(seconds) ? DefaultSeconds : (int)Math.Round(Math.Clamp(seconds, MinSeconds, MaxSeconds));
}

/// <summary>The last <see cref="WindowSeconds"/> of one reading, for the small graph. Null values are gaps (no reading), never zero.</summary>
public sealed class RollingHistory
{
    private double _window = GraphHistory.DefaultSeconds;
    private readonly List<(double T, double? V)> _points = new();

    /// <summary>How far back the history goes (from the settings file, clamped). Making it shorter drops the older points.</summary>
    public double WindowSeconds
    {
        get => _window;
        set
        {
            _window = GraphHistory.Clamp(value);
            if (_points.Count > 0) Trim(_points[^1].T);
        }
    }

    public IReadOnlyList<(double T, double? V)> Points => _points;

    public void Add(double t, double? v)
    {
        _points.Add((t, v));
        Trim(t);
    }

    private void Trim(double now)
    {
        int drop = 0;
        while (drop < _points.Count && _points[drop].T < now - _window) drop++;
        if (drop > 0) _points.RemoveRange(0, drop);
    }

    public void Clear() => _points.Clear();

    public double? Min => _points.Where(p => p.V.HasValue).Min(p => p.V);
    public double? Max => _points.Where(p => p.V.HasValue).Max(p => p.V);

    /// <summary>For screen readers and UI Automation tests: "last 300 s: 118 readings, 120 to 250".</summary>
    public string Summary(int decimals = 0)
    {
        string last = "last " + _window.ToString("0", CultureInfo.InvariantCulture) + " s: ";
        int n = _points.Count(p => p.V.HasValue);
        if (n == 0) return last + "no readings";
        string f = decimals == 0 ? "0" : "0." + new string('0', decimals);
        return $"{last}{n} readings, {Min!.Value.ToString(f, CultureInfo.InvariantCulture)} to {Max!.Value.ToString(f, CultureInfo.InvariantCulture)}";
    }
}

/// <summary>One moment of live readings for the window, from a ride (<see cref="AntLiveSource"/>) or the demo (<see cref="DemoLiveSource"/>).</summary>
public sealed record LiveReading(
    int? PowerW, double? CadenceRpm, int? HeartRateBpm, double? SpeedMps,
    LinkInfo? Trainer, LinkInfo? HeartRate, LinkInfo? Cadence, LinkInfo? Power)
{
    public static readonly LiveReading None = new(null, null, null, null, null, null, null, null);

    public LinkInfo? LinkFor(Role r) => r switch { Role.Trainer => Trainer, Role.HeartRate => HeartRate, Role.Cadence => Cadence, _ => Power };
}

/// <summary>Number formatting for the big readings: "--" when there is no reading (never a misleading 0).</summary>
public static class ReadingText
{
    public const string None = "--";
    public static string Whole(double? v) => v is { } x && !double.IsNaN(x) ? Math.Round(x).ToString("0", CultureInfo.InvariantCulture) : None;
    public static string SpeedKmh(double? mps) => mps is { } x && !double.IsNaN(x) ? (x * 3.6).ToString("0.0", CultureInfo.InvariantCulture) : None;
}
