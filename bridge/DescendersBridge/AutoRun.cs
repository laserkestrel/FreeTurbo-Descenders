using System.Text;
using DescendersBridge.Ant;

namespace DescendersBridge;

/// <summary>
/// `run --auto [--parent &lt;pid&gt;]`: the mode the mod starts the bridge in when none is running (no window). Nothing here waits for a human;
/// every state is reported to the mod's HUD with the `status` message:
///  - no network key: report it and wait (nothing else can be done);
///  - no ANT+ stick: report it and retry every few seconds (the rider may plug it in after starting the game);
///  - no trainer chosen yet (none in TrainerBridgeConfig.json): scan, take the trainer heard most (the nearest one) and a heart-rate monitor, save them;
///  - then ride. The game no longer passes --parent; the game says `bye` when it quits and this bridge (it has
///    no window to close) stops, releasing the trainer. A game that crashed says nothing: the trainer goes flat on stale telemetry, the ride log
///    ends after <see cref="GamePresence.GoneSeconds"/>, and the bridge stops by itself once no game has been heard for
///    <see cref="GamePresence.AutoIdleExitMinutes"/>, or on Ctrl+C / --parent / `FreeTurbo-Bridge stop`.
/// The link to the game is opened first and kept for the whole run, so a game is heard in every phase. `setup` still works for anyone who wants to choose by hand.
/// Console output is copied to logs\bridge_*.log, since there is no window to read it in.
/// </summary>
public static class AutoRun
{
    public const int ScanSeconds = 10, RetrySeconds = 5, KeepLogs = 20;

    public static async Task<int> RunAsync(int? parentPid)
    {
        using var log = StartLog();
        Console.WriteLine("Bridge build: " + BuildStamp.Text);
        using var cts = new CancellationTokenSource();
        try { Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); }; } catch { /* no console */ }
        if (parentPid is { } pid) ParentWatch.Start(pid, cts);
        var ct = cts.Token;

        var (rider, prefs) = TrainerBridgeConfig.Load();   // printed, so the log shows which file and values are in use
        ModRider.Start(rider, ct);   // in every phase, so the mod has the profile even with no stick or no key

        ModLink mod;
        try { mod = new ModLink(); }
        catch (System.Net.Sockets.SocketException ex)   // another bridge (a `run --sim` fake trainer too) holds 9871: it serves the game, this one is not needed
        {
            Console.WriteLine("ERROR: " + ex.Message);
            await Hold("error", "UDP port 9871 is in use by another program", ct, RetrySeconds);
            return 2;
        }
        using var ownedMod = mod;
        Ride.SetListening(mod);
        IdleWatch(mod, cts);
        mod.ShutdownRequested = () =>
        {
            Console.WriteLine("Asked to stop (FreeTurbo-Bridge stop): stopping (the trainer is released first).");
            cts.Cancel();
        };
        mod.GameQuit = () =>   // no window to close, so a hidden bridge must not stay behind when the rider quits the game
        {
            Console.WriteLine("The game quit: stopping (the trainer is released first).");
            cts.Cancel();
        };
        try { return await RideAsync(mod, rider, prefs, ct); }
        finally { Ride.SetListening(null); }
    }

    /// <summary>Stop once no game has been heard for <see cref="GamePresence.AutoIdleExitMinutes"/> (the ride's release runs on the way out).</summary>
    private static void IdleWatch(ModLink mod, CancellationTokenSource cts)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        double limit = GamePresence.AutoIdleExitMinutes * 60;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(5000, cts.Token); } catch (OperationCanceledException) { return; }
                if (!GamePresence.IdleExit(mod.SecondsSinceHeard, started.Elapsed.TotalSeconds, limit)) continue;
                Console.WriteLine($"No game connected for {GamePresence.AutoIdleExitMinutes:0} minutes: stopping (the trainer is released first).");
                cts.Cancel();
                return;
            }
        });
    }

    private static async Task<int> RideAsync(ModLink mod, RiderConfig rider, DevicePreferences prefs, CancellationToken ct)
    {
        // The ANT+ key (and the stick) are only needed when a chosen device is ANT+, or when no trainer is chosen yet
        // (the automatic choice scans ANT+). A Bluetooth trainer chosen with `setup` rides with neither.
        byte[] key = Array.Empty<byte>();
        if (prefs.NeedsAnt || prefs.Get(Role.Trainer) is null)
        {
            try
            {
                Console.WriteLine("ANT+ network key source: " + BridgeConfig.KeySource);
                key = BridgeConfig.LoadNetworkKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                await Hold("error", "No usable ANT+ network key (none built in or in config.json)", ct);
                return 2;
            }
        }

        while (!ct.IsCancellationRequested)
        {
            if (prefs.Get(Role.Trainer) is null && !await ChooseAsync(key, prefs, ct)) continue;
            try
            {
                await Ride.RunAsync(prefs, rider, null, ct, mod);
                return 0;
            }
            catch (AntStickException ex)
            {
                Console.WriteLine(ex.Message);
                await Hold("nostick", "No ANT+ USB stick found", ct, RetrySeconds);
            }
            catch (Bluetooth.BluetoothUnavailableException ex)   // a Bluetooth trainer is chosen and this PC cannot do Bluetooth (now); sensors alone never throw it
            {
                Console.WriteLine(ex.Message);
                await Hold("error", ex.Message, ct, RetrySeconds);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex);
                await Hold("error", ex.Message, ct, RetrySeconds);
            }
        }
        return 0;
    }

    /// <summary>Scan, then store the trainer heard most and (if none is stored) the heart-rate monitor heard most. False = nothing usable yet.</summary>
    private static async Task<bool> ChooseAsync(byte[] key, DevicePreferences prefs, CancellationToken ct)
    {
        DeviceTracker[] found;
        using (var scanner = new AntScanner(key))
        {
            scanner.Status += m => Console.WriteLine("[ant] " + m);
            try { scanner.Start(); }
            catch (Exception ex)
            {
                Console.WriteLine("ANT+ stick not available: " + ex.Message);
                await Hold("nostick", "No ANT+ USB stick found", ct, RetrySeconds);
                return false;
            }
            for (int i = 0; i < ScanSeconds; i++)
            {
                ModStatus.Send("scanning", "Looking for your trainer (pedal to wake it)");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return false; }
            }
            found = scanner.Devices.ToArray();
        }

        var trainers = found.Where(d => d.Key.DeviceType == AntDeviceTypes.FitnessEquipment).OrderByDescending(d => d.Messages).ToList();
        if (trainers.Count == 0)
        {
            Console.WriteLine($"No trainer heard in {ScanSeconds} s ({found.Length} other device(s)); scanning again.");
            return false;
        }
        Console.WriteLine($"Scan heard {found.Length} device(s):");
        foreach (var d in found) Console.WriteLine("  " + Setup.Describe(d));
        prefs.Set(Role.Trainer, trainers[0].Key, trainers[0].Identity?.ManufacturerId);
        if (trainers.Count > 1)
            Console.WriteLine($"{trainers.Count} trainers heard; chose {trainers[0].Key} (heard most). Run `FreeTurbo-Bridge setup` to pick another.");

        // a watch with no skin contact broadcasts 0 bpm: prefer one with a reading
        var hr = found.Where(d => d.Key.DeviceType == AntDeviceTypes.HeartRate)
            .OrderByDescending(d => d.HeartRateBpm.HasValue).ThenByDescending(d => d.Messages).FirstOrDefault();
        if (hr is not null && prefs.Get(Role.HeartRate) is null) prefs.Set(Role.HeartRate, hr.Key, hr.Identity?.ManufacturerId);
        foreach (var d in found) if (d.Identity is { } id) prefs.Learn(d.Key, id.ManufacturerId);   // stored devices gain their manufacturer

        try
        {
            prefs.Save();
            Console.WriteLine("Chose automatically, saved to " + TrainerBridgeConfig.Path);
        }
        catch (Exception ex) { Console.WriteLine("Chose automatically, but could not save the choice (used for this run only): " + ex.Message); }
        Setup.Show(prefs);
        return true;
    }

    /// <summary>Send a status once a second for <paramref name="seconds"/> (0 = until cancelled).</summary>
    private static async Task Hold(string state, string text, CancellationToken ct, int seconds = 0)
    {
        for (int i = 0; seconds == 0 || i < seconds; i++)
        {
            ModStatus.Send(state, text);
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Copy console output to a new logs\bridge_*.log (also used by the window, Gui\GuiMain.cs).</summary>
    internal static StreamWriter? StartLog()
    {
        try
        {
            string dir = Ride.UsableLogsDir(Console.WriteLine);   // the folder set on the window's Logs tab, else logs\ beside config.json
            LogsFolder.Prune(dir, "bridge_", ".log", KeepLogs);
            var w = new StreamWriter(Path.Combine(dir, $"bridge_{DateTime.Now:yyyyMMdd_HHmmss}.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(new TeeWriter(Console.Out, w));
            return w;
        }
        catch (Exception ex) { Console.WriteLine("Could not open the bridge log: " + ex.Message); return null; }
    }

    /// <summary>Console output to the (possibly absent) console and to the log file, each file line time-stamped.</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console, _file;
        public TeeWriter(TextWriter console, TextWriter file) { _console = console; _file = file; }
        public override Encoding Encoding => _console.Encoding;
        public override void Write(char c) { _console.Write(c); try { _file.Write(c); } catch { } }
        public override void Write(string? s) { _console.Write(s); try { _file.Write(s); } catch { } }
        public override void WriteLine(string? s) { _console.WriteLine(s); try { _file.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); } catch { } }
    }
}
