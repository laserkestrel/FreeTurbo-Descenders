using DescendersBridge.Ant;
using DescendersBridge.Gui;

namespace DescendersBridge;

/// <summary>
/// The bridge window connects by itself (no Start / Stop button). <see cref="ConnectRules.Decide"/> is the "should the window
/// connect now?" rule: a device saved, the ANT+ key present, no device search running, not connected / stopping already, and a failed
/// connection only on the rider's "Try again" (or another press that asks for a new try). Plus the words shown while not connected, which
/// must not name the button that is gone. Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class ConnectRulesTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Connect rules: the window connects by itself");
        const RideController.State Stopped = RideController.State.Stopped, Running = RideController.State.Running,
            Stopping = RideController.State.Stopping, Failed = RideController.State.Failed;

        check(ConnectRules.Decide(Stopped, deviceSaved: true, keyPresent: true, searching: false, asked: false) == ConnectStep.Connect,
            "the window opens with a device saved and the key present: connect");
        check(ConnectRules.Decide(Stopped, deviceSaved: false, keyPresent: true, searching: false, asked: false) == ConnectStep.NoDevice
            && ConnectRules.Decide(Stopped, deviceSaved: false, keyPresent: false, searching: false, asked: true) == ConnectStep.NoDevice,
            "no device saved (first run): stay disconnected, the banner sends the rider to My devices");
        check(ConnectRules.Decide(Stopped, deviceSaved: true, keyPresent: false, searching: false, asked: false) == ConnectStep.NoKey,
            "a device saved but no ANT+ key: stay disconnected, the banner says the build is incomplete");
        check(ConnectRules.Decide(Stopped, true, true, searching: true, asked: false) == ConnectStep.Wait
            && ConnectRules.Decide(Stopped, true, true, searching: true, asked: true) == ConnectStep.Wait
            && ConnectRules.Decide(Failed, true, true, searching: true, asked: true) == ConnectStep.Wait,
            "a device search has the stick: wait, even when asked (the search connects again when it is over)");
        check(ConnectRules.Decide(Running, true, true, false, asked: false) == ConnectStep.Wait && ConnectRules.Decide(Running, true, true, false, asked: true) == ConnectStep.Wait,
            "already connected: nothing to do (no second ride)");
        check(ConnectRules.Decide(Stopping, true, true, false, asked: true) == ConnectStep.Wait, "still releasing the trainer: wait");
        check(ConnectRules.Decide(Failed, true, true, false, asked: false) == ConnectStep.Wait,
            "a failed connection (no stick, another bridge copy) is not tried again by itself: its banner offers Try again");
        check(ConnectRules.Decide(Failed, true, true, false, asked: true) == ConnectStep.Connect, "Try again (or a Save, or a search that ended) after a failure: connect");
        check(ConnectRules.Decide(Stopped, true, true, false, asked: true) == ConnectStep.Connect, "after a search or a Save, with a device saved: connect again");
        check(ConnectRules.Decide(Stopped, deviceSaved: false, keyPresent: false, searching: false, asked: false, demo: true) == ConnectStep.Connect,
            "gui --demo: the demo readings start when the window opens, no device or key needed");
        check(ConnectRules.Decide(Stopped, false, false, searching: true, asked: false, demo: true) == ConnectStep.Wait
            && ConnectRules.Decide(Running, false, false, false, asked: true, demo: true) == ConnectStep.Wait,
            "gui --demo: a search still pauses it, and it does not start twice");

        var none = new DevicePreferences();
        var strapOnly = new DevicePreferences();
        strapOnly.Set(Role.HeartRate, new DeviceKey(AntDeviceTypes.HeartRate, 678, 1));
        check(!ConnectRules.AnyDevice(none) && ConnectRules.AnyDevice(strapOnly), "a device saved = any job has one (a heart-rate strap alone is enough to connect)");

        var texts = new List<string> { GamePresence.Describe(false, null, double.PositiveInfinity), GamePresence.Describe(true, null, double.PositiveInfinity), GameLauncher.OpeningText };
        foreach (var state in Enum.GetValues<RideController.State>())
            foreach (bool searching in new[] { false, true })
            {
                texts.Add(ConnectRules.IdleText(state, searching));
                texts.Add(ConnectRules.CannotStartText(state, deviceSaved: true, searching));
                texts.Add(ConnectRules.CannotStartText(state, deviceSaved: false, searching));
            }
        foreach (var m in new[] { FriendlyText.NoStick, FriendlyText.TrainerNotFound, FriendlyText.NoTrainerChosen, FriendlyText.AlreadyRunning, FriendlyText.SteamMissing, FriendlyText.NoKey, FriendlyText.Welcome })
            texts.Add(m.Text);
        var naming = texts.Where(t => t.Length == 0 || t.Contains("Start riding", StringComparison.OrdinalIgnoreCase) || t.Contains("Stop riding", StringComparison.OrdinalIgnoreCase)
            || t.Contains("stop the ride", StringComparison.OrdinalIgnoreCase) || t.Contains("Launch Descenders", StringComparison.OrdinalIgnoreCase)).ToList();
        check(naming.Count == 0, "no message names the Start / Stop button or the Launch Descenders button, both gone" + (naming.Count > 0 ? " (" + string.Join(" | ", naming) + ")" : ""));

        check(ConnectRules.IdleText(Stopped, searching: true).Contains("search") && ConnectRules.IdleText(Stopped, false) == "Not connected"
            && ConnectRules.IdleText(Failed, false) == "Not connected" && ConnectRules.IdleText(Stopping, false) != "Not connected",
            "under a reading while not connected: paused during a search, reconnecting, or not connected");
        check(ConnectRules.CannotStartText(Stopped, deviceSaved: false, searching: false).Contains("My devices")
            && ConnectRules.CannotStartText(Failed, true, false).Contains("message at the top")
            && ConnectRules.CannotStartText(Stopped, true, searching: true).Contains("search"),
            "a Start press on the Workouts tab while not connected says why: no device yet, the failure's message, or the search");
    }
}
