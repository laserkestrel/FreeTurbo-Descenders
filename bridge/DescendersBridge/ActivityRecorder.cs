using System.Text;
using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// Where activity files go and what they are called: a folder activities\ beside the user's settings file
/// (%APPDATA%\TrainerBridge\activities\, or beside the `--config` file) unless the rider chooses another on the window's Logs tab, one file per workout
/// or free ride, named with the date, the time and the workout's name: 2026-10-10_1830_Sweet-Spot-3x10.fit. Pure rules, for `selftest`.
/// </summary>
public static class ActivityFiles
{
    public const string FolderName = "activities";
    /// <summary>The name in the file name of a ride with no workout (freeride, plain ERG): 2026-10-10_1830_Free-ride.fit.</summary>
    public const string FreeRideName = "Free ride";
    /// <summary>A free ride is over when the game has not been riding for this long (a workout stays loaded and is never ended by it).</summary>
    public const double FreeRideIdleSeconds = 600;

    public const string SavedNote = "Activity files go here from the next ride that ends.";

    /// <summary>The standard activities folder: activities\ beside the settings file in use.</summary>
    public static string StandardDir => DirFor(TrainerBridgeConfig.Path);

    /// <summary>The activities folder in use: the one chosen on the window's Logs tab (`activities.dir` in the settings file) when it is
    /// a full path, else the standard one. Read fresh at every call, so a change applies to the next file written.</summary>
    public static string Dir => LogsFolder.Resolve(TrainerBridgeConfig.LoadActivitiesDir(), StandardDir);

    /// <summary><see cref="Dir"/>, or the standard folder (and a log line) when the chosen one cannot be made, so a ride is never lost
    /// to a drive that is gone.</summary>
    public static string UsableDir(Action<string> log) => LogsFolder.Usable(Dir, StandardDir, log, "Activities folder");

    /// <summary>The line under the box on the Logs tab: what the folder is for, and how to go back from an own folder.</summary>
    public static string Status(string? configured, string standard) => LogsFolder.IsStandard(configured, standard)
        ? "Activity files (.fit) saved after each ride, for uploading to Strava or a similar site."
        : "Activity files (.fit) saved after each ride, in your own folder; empty the box and press Save to go back to the standard folder.";

    public static string DirFor(string configPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", FolderName);

    /// <summary>The workout's name as part of a file name: characters Windows does not allow are taken out, spaces become "-".</summary>
    public static string SafeName(string? workoutName)
    {
        var sb = new StringBuilder();
        char[] bad = Path.GetInvalidFileNameChars();
        foreach (char c in (workoutName ?? "").Trim())
        {
            if (char.IsWhiteSpace(c)) { if (sb.Length > 0 && sb[^1] != '-') sb.Append('-'); }
            else if (c >= ' ' && Array.IndexOf(bad, c) < 0) sb.Append(c);
        }
        string s = sb.ToString().Trim('-', '.');
        if (s.Length > 60) s = s.Substring(0, 60).TrimEnd('-', '.');
        return s.Length > 0 ? s : "workout";
    }

    /// <summary>"2026-10-10_1830_Sweet-Spot-3x10.fit" for a workout started at <paramref name="startLocal"/>.</summary>
    public static string FileName(DateTime startLocal, string? workoutName) =>
        startLocal.ToString("yyyy-MM-dd_HHmm", System.Globalization.CultureInfo.InvariantCulture) + "_" + SafeName(workoutName) + ".fit";

    /// <summary>The full path to write: <see cref="FileName"/> in <paramref name="dir"/>, with _2, _3, .. before the ending when that file is
    /// already there (the same workout twice in a minute), so no ride is ever overwritten.</summary>
    public static string NewPath(string dir, DateTime startLocal, string? workoutName, Func<string, bool> exists)
    {
        string name = FileName(startLocal, workoutName), path = Path.Combine(dir, name);
        for (int n = 2; exists(path); n++) path = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "_" + n + ".fit");
        return path;
    }

    /// <summary>Whether an activity that is over gets a file: whenever it was ridden, however short, whether the workout
    /// reached its end (also skipped to the end) or ended early. Nothing recorded, or ended early with no riding time = no file: an
    /// empty file is worse than none.</summary>
    public static bool Keep(bool completed, double ridingSeconds, int records) => records > 0 && (completed || ridingSeconds > 0);

    public const string NoGameSpeedWarning = "WARNING: the game sends no speedometer speed (gameSpeedKmh, an older mod): the activity file uses the game's real speed, which is higher than the speedometer showed. Update the mod.";

    /// <summary>The speed that goes into the file, m/s: the game's speedometer scale, so the file says what the rider saw
    /// on screen; a mod that does not send it = the game's real speed as before (<paramref name="fellBack"/>), so no file has nothing.</summary>
    public static double? Speed(double? gameSpeedMps, double? realSpeedMps, out bool fellBack)
    {
        fellBack = gameSpeedMps is null && realSpeedMps is not null;
        return gameSpeedMps ?? realSpeedMps;
    }
}

/// <summary>
/// Collects the ride's activities and writes each one's .fit file when it is over (every ride, however short,
/// free rides too). Called once per tick of <see cref="RideLoop"/>, on its thread, after the session's own tick; it only watches the
/// session (which workout is loaded, started, finished) and never changes it.
///  - A workout's activity starts at its first pedal stroke and ends when the session lets go of the workout: finished or skipped to the
///    end, replaced by another workout or by manual ERG, or the ride stopped (<see cref="Stop"/>: the window closed, Save on My devices /
///    Rider and bike, the bridge stopped). It always gets its file; one that did not reach its end says so in the log line.
///  - Everything else (freeride, plain ERG, a workout loaded and not yet started) is a free ride: its activity starts at the first pedal
///    stroke with the game riding and ends when a workout starts, when the ride stops, or after
///    <see cref="ActivityFiles.FreeRideIdleSeconds"/> with the game not riding (the bridge window outlives the game: tomorrow's ride is
///    not part of today's file).
///  - Never pedalled = no activity, so no file (<see cref="ActivityFiles.Keep"/>).
///  - One record per second while the game is riding. Speed is the game's speedometer speed from telemetry (what the rider
///    saw on screen, not the real rigidbody speed, which is about 1.8 x higher), distance that speed added up; a mod that sends no
///    speedometer speed = the real speed and one WARNING (<see cref="ActivityFiles.Speed"/>).
///  - `riding` false (the game paused, a menu, no fresh telemetry) stops the activity's timer and true starts it again: no records
///    meanwhile, and the time is not riding time. A workout on hold (the mode button) is still a ride and is recorded, and so is
///    coasting with the workout's own clock paused: the file's riding time may be longer than the workout clock.
/// The bytes are <see cref="Fit"/>'s; a file that cannot be written is one ERROR line, never an exception into the ride.
/// </summary>
public sealed class ActivityRecorder
{
    private readonly Func<string> _dir;
    private readonly Action<string> _log;
    private WorkoutRunner? _run;      // the session's started workout as of the last tick, null = a free ride
    private FitActivity? _act;        // the activity being recorded, null until the first pedal stroke
    private string _name = "";
    private double _distanceM;
    private uint _lastSecond;
    private DateTime _stoppedAt;      // when the timer last stopped
    private bool _warnedNoGameSpeed;  // the fall-back to the real speed is logged once

    /// <summary>The files written by this recorder, oldest first.</summary>
    public List<string> Written { get; } = new();

    /// <param name="dir">the activities folder, asked each time a file is written</param>
    public ActivityRecorder(Func<string> dir, Action<string> log) { _dir = dir; _log = log; }

    /// <summary>A workout or a free ride is being recorded right now.</summary>
    public bool Recording => _act is not null;

    /// <param name="riding">fresh telemetry and the game says the rider is riding (<see cref="RideLoop.Riding"/>)</param>
    /// <param name="gameSpeedMps">the speed on the game's speedometer from telemetry, m/s, null = the mod does not send it</param>
    /// <param name="realSpeedMps">the game's real speed from telemetry, used only when there is no speedometer speed; null = not known</param>
    /// <param name="dt">seconds since the last tick, as the session counts them</param>
    public void Tick(Session session, bool riding, int? powerW, double? cadenceRpm, int? heartRateBpm, double? gameSpeedMps, double? realSpeedMps, double dt, DateTime now)
    {
        double? speedMps = ActivityFiles.Speed(gameSpeedMps, realSpeedMps, out bool fellBack);
        var run = session.Run is { Started: true } started ? started : null;   // armed = not ridden yet: still the free ride
        if (!ReferenceEquals(run, _run))
        {
            Finish(now);
            _run = run;
        }

        if (_act is null)
        {
            // a workout's activity starts with its clock, a free ride's at the first pedal stroke in the game
            if (run is null && !(riding && Session.IsPedalling(powerW, cadenceRpm))) return;
            _act = new FitActivity(now);
            _name = run?.W.Name ?? ActivityFiles.FreeRideName;
            _distanceM = 0;
            _lastSecond = 0;
            _log(run is null ? "Activity: recording this free ride for its activity file" : "Activity: recording '" + _name + "' for its activity file");
        }
        else if (riding != _act.Running)
        {
            _act.Timer(now, riding);
            if (!riding) _stoppedAt = now;
        }
        else if (riding) { if (speedMps is { } v && !double.IsNaN(v) && v > 0) _distanceM += v * dt; }
        else if (run is null && (now - _stoppedAt).TotalSeconds >= ActivityFiles.FreeRideIdleSeconds)
        {
            Finish(_stoppedAt);   // the free ride ended when the game stopped riding
            return;
        }

        uint second = Fit.Timestamp(now);
        if (!riding || second == _lastSecond) return;
        _lastSecond = second;
        if (fellBack && !_warnedNoGameSpeed) { _warnedNoGameSpeed = true; _log(ActivityFiles.NoGameSpeedWarning); }
        _act.Add(new FitRecord(now, powerW, cadenceRpm, heartRateBpm, speedMps, _distanceM));
    }

    /// <summary>The ride is over (the window closed, the bridge stopped): what is being recorded ends here, a workout early.</summary>
    public void Stop(DateTime now)
    {
        Finish(now);
        _run = null;
    }

    /// <summary>End the activity being recorded (none = nothing) and write its file.</summary>
    private void Finish(DateTime now)
    {
        if (_act is not { } a) return;
        _act = null;
        bool early = _run is { Finished: false };   // a workout that did not reach its end; a free ride has no end to reach
        a.End(now);
        if (!ActivityFiles.Keep(!early, a.TimerSeconds(a.EndUtc), a.Records))
        {
            _log("Activity: '" + _name + "' had no riding: no activity file");
            return;
        }
        try
        {
            string dir = _dir();
            Directory.CreateDirectory(dir);
            string path = ActivityFiles.NewPath(dir, a.StartUtc.ToLocalTime(), _name, File.Exists);
            File.WriteAllBytes(path, Fit.Encode(a, DateTime.UtcNow));
            Written.Add(path);
            var t = a.Totals();
            _log("Activity file: " + path + " (" + WorkoutRunner.Fmt((float)t.TimerS) + " of riding, " + (t.DistanceM / 1000).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " km"
                + (t.AvgPowerW is { } w ? ", avg " + w.ToString("F0") + " W" : "") + (early ? ", the workout ended early" : "") + "): upload it by hand, for example to Strava");
        }
        catch (Exception ex) { _log("ERROR: the activity file for '" + _name + "' could not be written: " + ex.Message); }
    }
}
