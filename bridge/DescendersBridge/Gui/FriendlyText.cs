using System.Net.Sockets;

namespace DescendersBridge.Gui;

/// <summary>A message for the window's banner: what happened, then what to do. No technical terms (written for anyone).</summary>
public sealed record FriendlyMessage(string Title, string Advice)
{
    public string Text => Title + " " + Advice;
}

/// <summary>
/// The plain-language wording for every known failure. Pure so `selftest` can check that no stack trace, exception type or file-format jargon
/// reaches the rider. The technical detail still goes to the Logs tab and logs\bridge_*.log.
/// </summary>
public static class FriendlyText
{
    public static readonly FriendlyMessage NoStick = new(
        "The ANT+ USB stick was not found.",
        "Plug it into a USB port (an extension cable lets you put it close to the trainer), then press \"Try again\". "
        + "If it is already plugged in, another program may be using it (for example a training app, or the copy of this bridge that the game starts): close it and try again.");

    /// <summary>The saved devices are ANT+ ones, no stick is plugged in, and this PC has Bluetooth. Not an error (the
    /// window shows it as a note, not in red): the rider may simply have no stick, and Bluetooth is one search away.</summary>
    public static readonly FriendlyMessage NoStickBluetoothHere = new(
        "Your saved devices use an ANT+ USB stick, and none is plugged in.",
        "To use Bluetooth instead, go to \"My devices\", press \"Search for devices\", choose the devices marked (Bluetooth) and press \"Save\". "
        + "Or plug the stick in and press \"Try again\".");

    /// <summary>The connection failed for want of the stick: red only when this PC has no Bluetooth to use instead.</summary>
    public static FriendlyMessage NoStickFor(bool bluetoothUsable) => bluetoothUsable ? NoStickBluetoothHere : NoStick;

    /// <summary>The banner's colour: every failure is an error but the one that is only a missing stick on a PC with Bluetooth.</summary>
    public static bool IsError(FriendlyMessage m) => m != NoStickBluetoothHere;

    public static readonly FriendlyMessage TrainerNotFound = new(
        "We can't find your trainer.",
        "Check that the trainer is plugged in and switched on, pedal for a few seconds to wake it up, and make sure the USB stick is close to it. Then press \"Try again\".");

    public static readonly FriendlyMessage TrainerNotHeardInSearch = new(
        "No trainer was found in the search.",
        "Check that the trainer is plugged in and switched on, pedal for a few seconds to wake it up, and make sure the USB stick is close to it. Then press \"Search again\".");

    // A Bluetooth trainer talks to one app at a time, which is the usual reason it is not found. No app is named.
    private const string WakeTrainer = "Check that the trainer is plugged in and switched on and pedal for a few seconds to wake it up. ";
    private const string OneAppAtATime = "A trainer talks Bluetooth to one app at a time: close the trainer maker's phone app and any other training app. ";

    /// <summary>A Bluetooth trainer not heard during a ride: nothing about a USB stick.</summary>
    public static readonly FriendlyMessage BluetoothTrainerNotFound = new(TrainerNotFound.Title, WakeTrainer + OneAppAtATime + "Then press \"Try again\".");

    /// <summary>A search that looked on Bluetooth only (this PC has no ANT+ stick).</summary>
    public static readonly FriendlyMessage TrainerNotHeardOnBluetooth = new(TrainerNotHeardInSearch.Title, WakeTrainer + OneAppAtATime + "Then press \"Search again\".");

    /// <summary>A search that looked on both.</summary>
    public static readonly FriendlyMessage TrainerNotHeardOnEither = new(TrainerNotHeardInSearch.Title,
        WakeTrainer + "With an ANT+ USB stick, make sure the stick is close to the trainer. " + OneAppAtATime + "Then press \"Search again\".");

    /// <summary>The search's "no trainer found" help for what it searched: the stick advice only when ANT+ was searched,
    /// the one-app-at-a-time advice only when Bluetooth was.</summary>
    public static FriendlyMessage TrainerNotHeard(bool ant, bool bluetooth) =>
        !bluetooth ? TrainerNotHeardInSearch : ant ? TrainerNotHeardOnEither : TrainerNotHeardOnBluetooth;

    /// <summary>The ride's "can't find your trainer" help, by how the saved trainer is reached.</summary>
    public static FriendlyMessage TrainerMissing(bool bluetooth) => bluetooth ? BluetoothTrainerNotFound : TrainerNotFound;

    /// <summary>A search on a PC with neither an ANT+ stick nor Bluetooth. (No stick alone is no error: Bluetooth is searched.)</summary>
    public static readonly FriendlyMessage NoRadio = new(
        "No ANT+ stick and no Bluetooth were found.",
        "Plug in an ANT+ stick or switch on Bluetooth, then press \"Try again\".");

    public static readonly FriendlyMessage NoTrainerChosen = new(
        "No trainer has been chosen yet.",
        "Go to \"My devices\", press \"Search for devices\", pick your trainer and press \"Save\": this window then connects to it by itself.");

    public static readonly FriendlyMessage AlreadyRunning = new(
        "The bridge is already running in the background.",
        "A game started without this window opens its own copy, which keeps running for 10 minutes after the game closes. Close the game, "
        + "wait for that copy to stop, then press \"Try again\". Next time, open this window before the game.");

    public static readonly FriendlyMessage SteamMissing = new(
        "The game could not be started.",
        "Check that Steam is installed and Descenders is in your library, then start the game from Steam yourself: this window stays connected and your workout is ready, it starts when you pedal in the game.");

    public static readonly FriendlyMessage NoKey = new(
        "This copy of the bridge is incomplete: it is missing a part needed to talk to ANT+ devices.",
        "Please download the official release and use the bridge from it.");

    /// <summary>A Bluetooth trainer is chosen and this PC cannot do Bluetooth; the title is the reason itself ("This PC has no Bluetooth").</summary>
    public static FriendlyMessage NoBluetooth(string reason) => new(
        reason.TrimEnd('.') + ".",
        "Your trainer is saved as a Bluetooth trainer. Plug in a Bluetooth adapter (or switch Bluetooth on) and press \"Try again\", or use an ANT+ USB stick and choose the trainer again on the \"My devices\" tab.");

    public static readonly FriendlyMessage ConfigBroken = new(
        "Your settings file could not be read (it may have been changed by hand and has a mistake in it).",
        "Press \"Fix it for me\" to start again with standard settings. The old file is kept as a backup, so nothing is lost.");

    public static readonly FriendlyMessage Welcome = new(
        "Welcome!",
        "First enter your weight and your bike's weight on the \"Rider and bike\" tab, then find your trainer on the \"My devices\" tab.");

    /// <summary>Wording for an exception from a ride or a search.</summary>
    public static FriendlyMessage For(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException) return AlreadyRunning;   // UDP 9871 held by another bridge
            if (e is AntStickException) return NoStick;
            if (e is Bluetooth.BluetoothUnavailableException) return NoBluetooth(e.Message);
            if (e.Message.StartsWith("No ANT+ network key", StringComparison.Ordinal)) return NoKey;
            if (e.GetType().FullName?.StartsWith("ANT_Managed_Library.", StringComparison.Ordinal) == true) return NoStick;   // ANT_Device() with no stick
        }
        return new FriendlyMessage("Something went wrong.",
            "Press \"Try again\". If it keeps happening, the Logs tab shows what happened: send its text to whoever gave you this program.");
    }

    public const string NoStickLog = "No ANT+ USB stick is available: plug one in, or close the other program that is using it.";

    /// <summary>What the log says about an exception from a ride or a search (the rider reads the Logs tab). No stick is an everyday
    /// event, so it is one plain line with no exception type and no stack trace; a reason other than "nothing plugged in" is kept, on the same line.
    /// Anything else keeps its full detail, which is what a bug report needs.</summary>
    public static string LogText(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is not AntStickException) continue;
            string reason = (e.InnerException?.Message ?? "").Split('\r', '\n')[0].Trim();
            const string prefix = "ANTLibrary Exception:";
            if (reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) reason = reason.Substring(prefix.Length).Trim();
            return reason.Length == 0 || reason.StartsWith("No ANT devices detected", StringComparison.OrdinalIgnoreCase) ? NoStickLog : NoStickLog + " (" + reason + ")";
        }
        return ex.ToString();
    }
}

/// <summary>The window's words about the workout library folder (on the Logs tab). Pure, for `selftest`.</summary>
public static class LibraryText
{
    public const string NotSet = "Not set yet: choose the folder that holds your \"Workouts\" and \"TrainingPlans\" folders.";

    /// <summary>What the folder means for the lists right now (they are on the Workouts tab, not in the game).</summary>
    public static string Status(string dir)
    {
        dir = (dir ?? "").Trim();
        if (dir.Length == 0) return NotSet;
        if (!Directory.Exists(dir)) return "That folder does not exist. Choose another one with \"Browse...\".";
        bool workouts = Directory.Exists(Path.Combine(dir, "Workouts")), plans = Directory.Exists(Path.Combine(dir, "TrainingPlans"));
        return workouts || plans
            ? "The workouts" + (plans ? " and training plans" : "") + " in this folder are listed on the Workouts tab."
            : "This folder has no \"Workouts\" or \"TrainingPlans\" folder yet: put workout files (.zwo or .xml) in a \"Workouts\" folder inside it.";
    }

    /// <summary>Why a folder is not saved (null = save it). An empty box is allowed: it clears the setting.</summary>
    public static string? Refuse(string dir)
    {
        dir = (dir ?? "").Trim();
        if (dir.Length == 0) return null;
        if (!Path.IsPathRooted(dir)) return "Not saved: give the full path of the folder, for example C:\\Users\\you\\Documents\\Workouts, or use \"Browse...\".";
        if (!Directory.Exists(dir)) return "Not saved: that folder does not exist. Choose one with \"Browse...\".";
        return null;
    }
}
