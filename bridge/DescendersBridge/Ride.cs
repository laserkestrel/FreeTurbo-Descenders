using System.Diagnostics;
using DescendersBridge.Ant;
using DescendersBridge.Bluetooth;

namespace DescendersBridge;

/// <summary>The ANT+ stick could not be opened (unplugged, driver not bound, or held by another program).</summary>
public sealed class AntStickException : Exception
{
    public AntStickException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>One ride: open the chosen devices, talk to the mod until <paramref name="ct"/> fires, then release the trainer.</summary>
public static class Ride
{
    /// <summary>
    /// Open what the chosen devices need, and nothing else. <paramref name="openAnt"/> (the stick and the ANT+ network key) is
    /// called only when at least one chosen device is ANT+; <paramref name="radio"/> (Bluetooth) only when a chosen device is Bluetooth.
    /// A Bluetooth trainer on a PC that cannot do Bluetooth gives <see cref="BluetoothUnavailableException"/> with one plain sentence;
    /// Bluetooth sensors alone never stop a ride: one WARNING, and the ride goes on without them. Bluetooth is checked
    /// first: it claims no hardware, so nothing has to be closed again when it is not there. ANT+ sensors beside a Bluetooth trainer
    /// with no stick plugged in are one WARNING too.
    /// </summary>
    public const string NoStickForSensors = "No ANT+ USB stick is plugged in";

    internal static async Task<RideSource> OpenSourceAsync(DevicePreferences prefs, Func<DevicePreferences, ITrainerSource> openAnt, Func<IBleRadio> radio, Action<string> log,
        double bluetoothRetrySeconds = FtmsSession.RetrySeconds)
    {
        bool btTrainer = prefs.Roles.TryGetValue(Role.Trainer.ToString(), out var trainer) && trainer.IsBluetooth;
        // the Bluetooth sensors, one connection per device (a power meter chosen for power and cadence is one). A role that
        // names the Bluetooth trainer itself is not a sensor: the trainer's connection supplies it.
        var sensorRoles = new List<(Role Role, StoredDevice Device)>();
        foreach (Role role in new[] { Role.HeartRate, Role.Cadence, Role.Power })
            if (prefs.Roles.TryGetValue(role.ToString(), out var d) && d.IsBluetooth && d.Usable
                && !(btTrainer && d.Key.BluetoothAddress == trainer!.Key.BluetoothAddress))
                sensorRoles.Add((role, d));

        FtmsSession? bt = null;
        var sensors = new Dictionary<Role, BleSensorSession>();
        string? sensorProblem = null;
        if (btTrainer || sensorRoles.Count > 0)
        {
            var r = radio();
            string? problem = BleRules.Problem(await r.AdapterAsync());
            if (problem is not null && btTrainer) throw new BluetoothUnavailableException(problem);
            if (btTrainer) bt = new FtmsSession(r, trainer!.Key.BluetoothAddress, trainer.Name, m => log("[bluetooth] " + m), bluetoothRetrySeconds);
            if (problem is not null)
            {
                // a sensor never stops a ride: without Bluetooth the ride goes on with the other devices, the sensor's reading is absent
                sensorProblem = problem;
                log("[bluetooth] WARNING: " + problem + ": the Bluetooth sensor(s) chosen for " + string.Join(", ", sensorRoles.Select(s => s.Role)) + " are not used in this ride");
            }
            else
                foreach (var group in sensorRoles.GroupBy(s => s.Device.Key.BluetoothAddress))
                {
                    BleFeed feeds = BleFeed.None;
                    foreach (var (role, d) in group) feeds |= BleSensors.Feed(role, d.Key.DeviceType);
                    var first = group.First().Device;
                    var session = new BleSensorSession(r, first.Key, group.Select(s => s.Device.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)), feeds,
                        m => log("[bluetooth] " + m), bluetoothRetrySeconds);
                    foreach (var (role, _) in group) sensors[role] = session;
                }
        }
        ITrainerSource? ant = null;
        string? antProblem = null;
        if (prefs.NeedsAnt)
        {
            try { ant = openAnt(prefs.AntOnly()); }
            // with a Bluetooth trainer, ANT+ sensors and no stick, the ride goes on without those sensors (the same
            // rule as Bluetooth sensors on a PC with no Bluetooth); an ANT+ trainer without its stick is still no ride
            catch (AntStickException) when (bt is not null)
            {
                antProblem = NoStickForSensors;
                var roles = Enum.GetValues<Role>().Where(r => prefs.Get(r) is { IsBluetooth: false });
                log("WARNING: " + antProblem + ": the ANT+ sensor(s) chosen for " + string.Join(", ", roles) + " are not used in this ride");
            }
        }
        return new RideSource(prefs, ant, bt, sensors, sensorProblem, antProblem);
    }

    /// <param name="sharedMod">`run --auto` listens for the game from the start and passes its link in; null = open one for this ride.</param>
    /// <param name="radio">tests: a fake Bluetooth radio; null = this PC's.</param>
    public static async Task RunAsync(DevicePreferences prefs, RiderConfig rider, double? fixedErgW, CancellationToken ct, ModLink? sharedMod = null, IBleRadio? radio = null)
    {
        AntSession? ant = null;
        ITrainerSource OpenAnt(DevicePreferences antPrefs)
        {
            byte[] networkKey = BridgeConfig.LoadNetworkKey();
            AntSession first;
            try { ant = first = new AntSession(networkKey, antPrefs); }
            catch (Exception ex) { throw new AntStickException("ANT+ stick not available: " + ex.Message, ex); }
            // A stick lost mid-ride (it drops when the game starts on the garage laptop) does not end the ride: the loop keeps
            // running on this source, which opens a new session when the stick is back, so the mode and the workout started in the window survive.
            async Task<ITrainerSource> Reopen()
            {
                var s = new AntSession(networkKey, antPrefs);   // throws while the stick is still gone
                try
                {
                    s.Status += m => Console.WriteLine("[ant] " + m);
                    s.Identified += Learn;
                    await s.OpenAsync(rider);
                    return s;
                }
                catch { s.Dispose(); throw; }
            }
            return new RecoveringSource(first, Reopen, Console.WriteLine);
        }
        using var src = await OpenSourceAsync(prefs, OpenAnt, () => radio ?? new WindowsBleRadio(), Console.WriteLine);
        try
        {
            _current = src;
            Console.WriteLine("Chosen devices:");
            Setup.Show(prefs);
            using var own = sharedMod is null ? new ModLink() : null;
            var mod = sharedMod ?? own!;
            mod.DropIntents();
            _currentMod = mod;
            string logPath = NewLogPath();
            mod.StartLog(logPath, nextPath: NewLogPath);   // a game back after being gone gets a new file
            Console.WriteLine("Logging mod telemetry to " + logPath);

            if (ant is { } a)
            {
                a.Status += m => Console.WriteLine("[ant] " + m);
                a.Identified += Learn;
                await a.OpenAsync(rider);
            }
            src.StartBluetooth();   // looks for the Bluetooth trainer and sensors from now on; one that is still switched off is waited for

            Console.WriteLine(fixedErgW is { } fw ? $"Holding ERG {fw:0} W (no mod needed). Ctrl+C to stop (the target is released first)." : "Riding. Ctrl+C to stop (resistance is released first).");
            await new RideLoop(src, mod, BridgeConfig.LoadResistance(), rider, fixedErgW).RunAsync(ct);

            await ReleaseAsync(src, Console.WriteLine);
            if (sharedMod is not null) sharedMod.EndLog("ride stopped");
        }
        finally { _current = null; _currentMod = sharedMod; }
    }

    /// <summary>The end of every ride, whichever transport the trainer is on: take the load off before the link is closed, and say
    /// "Resistance released." or a WARNING.</summary>
    internal static async Task ReleaseAsync(RideSource src, Action<string> log)
    {
        if (src.BluetoothSide is { } bt)
        {
            var r = await bt.ReleaseAsync(TimeSpan.FromSeconds(3));
            log(r.Outcome switch
            {
                ReleaseOutcome.Released => "Resistance released.",
                // never connected in this ride (the garage log of 2026-10-05): the bridge set no resistance, so there is nothing to warn about
                ReleaseOutcome.LinkAlreadyDead when bt.Connections == 0 => "The Bluetooth trainer was never connected in this ride: no resistance was set, nothing to release.",
                ReleaseOutcome.LinkAlreadyDead =>"WARNING: the Bluetooth trainer was not connected when the ride stopped: its resistance could not be released (switch the trainer off and on if it feels loaded).",
                _ => $"WARNING: resistance release {r.Outcome} (was holding {r.HeldGradePercent:0.0}%): check the trainer.",
            });
            return;
        }
        if (src.AntSide is not RecoveringSource stick) return;
        if (stick.Inner is not AntSession live || live.StickLost)   // nothing can reach the trainer, so no release is attempted
            log("WARNING: the ANT+ stick was still gone when the ride stopped: the trainer's resistance could not be released (switch the trainer off and on if it feels loaded).");
        else if (live.Trainer is { } tr)
        {
            var r = await tr.ReleaseAsync(TimeSpan.FromSeconds(3));
            log(r.Outcome == ReleaseOutcome.Released ? "Resistance released." : $"WARNING: resistance release {r.Outcome} (was holding {r.HeldGradePercent:0.0}%): check the trainer.");
        }
    }

    /// <summary>A device of this ride said who made it: the saved device gains the manufacturer in the settings file.</summary>
    private static void Learn(DeviceKey key, DeviceIdentity id) =>
        TrainerBridgeConfig.LearnManufacturers(new[] { (key, id.ManufacturerId) }, Console.WriteLine);

    /// <summary>A new ride recording in the logs folder; the oldest go first, so there are never more than <see cref="LogsFolder.KeepRideRecordings"/>
    /// (a `--log` file the developer named is never pruned by its own ride).</summary>
    private static string NewLogPath()
    {
        string dir = UsableLogsDir(Console.WriteLine);
        LogsFolder.Prune(dir, "ride_", ".jsonl", LogsFolder.KeepRideRecordings);
        return Path.Combine(dir, $"ride_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
    }

    /// <summary>
    /// `run --sim`: the same ride loop, session and mod link as a real ride, with <see cref="SimTrainer"/> in place of the ANT+ stick
    /// (no stick, no key needed). Every telemetry packet and every ack / intent goes to <paramref name="logPath"/> (the recording the smoke test reads).
    /// <paramref name="workoutPath"/> (`--workout`): start that workout file at once, as the window's Workouts tab would, so the
    /// game's workout panel and controls can be tested with no window.
    /// </summary>
    public static async Task RunSimAsync(SimOptions sim, RiderConfig rider, string? logPath, double? fixedErgW, CancellationToken ct, string? workoutPath = null)
    {
        using var mod = new ModLink();   // throws SocketException when another bridge holds 9871
        // --log names one file: a game back after being gone appends to it (the smoke test reads one file); else a new file per game session
        string first = logPath ?? NewLogPath();
        mod.StartLog(first, telemetryEvery: 1, nextPath: logPath is null ? NewLogPath : null);
        Console.WriteLine("Fake trainer (--sim): " + sim);
        Console.WriteLine("Logging mod telemetry to " + Path.GetFullPath(first));
        Console.WriteLine("Riding with the fake trainer. Ctrl+C to stop.");
        _currentMod = mod;
        var loop = new RideLoop(new SimTrainer(sim), mod, BridgeConfig.LoadResistance(), rider, fixedErgW);
        if (workoutPath is not null)
            _ = loop.Post((s, now) => { var r = Menu.StartWorkout(s, Path.GetFullPath(workoutPath), now); Console.WriteLine("--workout: " + r.Text); return r; });
        try { await loop.RunAsync(ct); }
        finally { _currentMod = null; }
        Console.WriteLine("Fake trainer stopped.");
    }

    /// <summary>Where config.json lives (logs\ sits beside it); the exe's folder when there is no config.json.</summary>
    public static string DataDir => Path.GetDirectoryName(BridgeConfig.Find("config.json")) ?? AppPaths.ExeDir;

    /// <summary>The standard logs folder: logs\ beside config.json.</summary>
    public static string DefaultLogsDir => Path.Combine(DataDir, "logs");

    /// <summary>Where bridge_*.log and ride_*.jsonl go: the folder set on the window's Logs tab, else the standard one.</summary>
    public static string LogsDir => LogsFolder.Resolve(TrainerBridgeConfig.LoadLogsDir(), DefaultLogsDir);

    /// <summary><see cref="LogsDir"/>, created; when the chosen folder cannot be made (a drive that is gone), the standard one and a log line.</summary>
    public static string UsableLogsDir(Action<string> log) => LogsFolder.Usable(LogsDir, DefaultLogsDir, log);

    /// <summary>The devices of the ride in progress (null between rides; its readings are null while the stick is gone mid-ride), read by the window's live panel (Gui\). Only this class sets it.</summary>
    public static RideSource? Current => _current;
    private static volatile RideSource? _current;

    /// <summary>A ride is running but its ANT+ stick is gone and being looked for (<see cref="RecoveringSource"/>): the window says so.</summary>
    public static bool StickGone => _current?.LinkLost ?? false;

    /// <summary>The link to the game while a ride (or `run --auto`) listens for one, null otherwise: the window's status strip reads the `hello` and
    /// whether a game is connected from it. Only this class sets it.</summary>
    public static ModLink? CurrentMod => _currentMod;
    private static volatile ModLink? _currentMod;

    /// <summary>`run --auto` sets its long-lived link here, so it counts even between rides (no stick yet).</summary>
    internal static void SetListening(ModLink? mod) => _currentMod = mod;
}

/// <summary>`--parent &lt;pid&gt;`: cancel when that process exits, so the ride loop releases the trainer and the bridge exits with it.
/// the mod no longer passes it (the bridge outlives the game); kept for scripts and tests.</summary>
public static class ParentWatch
{
    public static void Start(int pid, CancellationTokenSource cts)
    {
        Process parent;
        try { parent = Process.GetProcessById(pid); }
        catch (ArgumentException)
        {
            Console.WriteLine($"Parent process {pid} is not running: stopping.");
            cts.Cancel();
            return;
        }
        Console.WriteLine($"Watching parent process {pid} ({parent.ProcessName}): the bridge stops when it exits.");
        _ = Task.Run(async () =>
        {
            try { await parent.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { return; }
            Console.WriteLine("Parent process exited: stopping.");
            cts.Cancel();
        });
    }
}
