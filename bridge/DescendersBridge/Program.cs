using DescendersBridge;
using DescendersBridge.Ant;

// turbotrainer-bridge scan [seconds]                 list every ANT+ device in range, live, then the Bluetooth trainers and sensors heard
// turbotrainer-bridge setup [seconds]                scan (ANT+ and Bluetooth), then choose a device per role and save it
// turbotrainer-bridge show                           print the saved choices
// turbotrainer-bridge set <role> <type> <number> [tx]   save one choice without scanning (scripting)
// turbotrainer-bridge set <role> bluetooth <address> [name]   the same for a Bluetooth trainer or sensor (HeartRate, Cadence, Power:)
// turbotrainer-bridge bluetooth [seconds]            what Windows says about this PC's Bluetooth, then an advertisement scan (default 10 s);
//                                                    exit 0 = usable, 2 = no usable Bluetooth
// turbotrainer-bridge clear <role>                   forget one choice
// turbotrainer-bridge run [seconds]                  ride: read the chosen devices, talk to the mod, drive trainer resistance (0 = until Ctrl+C)
// turbotrainer-bridge run [seconds] --erg <watts>    same, but hold a fixed ERG target (FE-C page 49) with no mod: bench test TE1
// turbotrainer-bridge run --auto [--parent <pid>]    how the mod starts it when none is running: no questions, chooses the trainer itself if none
//                                                    is saved, retries a missing stick, reports its state to the mod's HUD, logs to logs\bridge_*.log;
//                                                    outlives the game, stops after 10 min with no game connected
// turbotrainer-bridge run --sim [--watts N | --watts-profile "0:150,30:250"] [--cadence N] [--idle-between from,to]
//                     [--stop-sending-after N] [--seconds N] [--log <file.jsonl>] [--workout <file.zwo>]
//                                                    fake trainer, no stick: the real ride loop and workout session, for the smoke test and
//                                                    loop tests (replaces tools\BridgeSim.ps1); logs every packet to --log or logs\ride_*.jsonl;
//                                                    --workout starts that workout file at once (no in-game menu, and no window here)
//   --parent <pid> (with any run): stop, releasing the trainer, when that process exits (the mod no longer passes it)
//   --seconds <n> (with any run): stop after n seconds, like `run <n>`
// turbotrainer-bridge command <name> [value] [durationSec]   send the mod one command (docs/IPC.md: ping, press_start, quit, set_test_speed, ...);
//                                                    the ack goes to 9871, so a running bridge logs it
//   every run sends the rider profile to the mod every 5 s (`rider` message)
// setup, show, set, clear and run first load and print the per-user %APPDATA%\TrainerBridge\TrainerBridgeConfig.json
// (rider profile + chosen devices, TrainerBridgeConfig.cs)
// turbotrainer-bridge stop                           ask a bridge the game started (run --auto) to stop, releasing the trainer (the
//                                                    dev scripts use it before installing); the window and run / run --sim ignore it
// turbotrainer-bridge keysource                   where the ANT+ network key comes from: prints built-in, config.json or none
//                                                    (exit 0, or 2 for none); never prints the key
// turbotrainer-bridge version                       which code this exe was built from: commit, branch, (+changes), build time (
//                                                    BuildStamp.targets); the same text is the exe's ProductVersion
// turbotrainer-bridge                               (no arguments, e.g. double-clicked) opens the window (Gui\): same code, one process
// turbotrainer-bridge gui [--demo]                   the window from a command line; --demo shows made-up readings and devices (no stick needed)
//   --config <file> (with any command): use that settings file instead of %APPDATA%\TrainerBridge\TrainerBridgeConfig.json (tests)
int ci = Array.FindIndex(args, a => a.Equals("--config", StringComparison.OrdinalIgnoreCase));
if (ci >= 0)
{
    if (ci + 1 >= args.Length) { Console.WriteLine("usage: --config <settings file>"); return 1; }
    TrainerBridgeConfig.PathOverride = Path.GetFullPath(args[ci + 1]);
    args = args.Where((_, i) => i != ci && i != ci + 1).ToArray();
}
string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "gui";
const string Usage = "usage: turbotrainer-bridge scan|setup|show|set|clear|bluetooth|run|command|stop|keysource|version|gui|selftest [--config <file>]   (see docs/BRIDGE.md)\n"
    + "       no arguments (or double-click): open the window";
int Arg(int i, int dflt) => args.Length > i && int.TryParse(args[i], out int v) ? v : dflt;

try
{
    switch (cmd)
    {
        case "scan":
        {
            var (_, bluetooth) = Setup.ScanAll(Arg(1, 20), live: true);
            Console.WriteLine($"--- Bluetooth trainers and sensors: {bluetooth.Count}");
            foreach (var b in bluetooth) Console.WriteLine("  " + DescendersBridge.Bluetooth.BleRules.Describe(b));
            return 0;
        }
        case "bluetooth":
            return await Setup.BluetoothCheckAsync(Arg(1, 10));
        case "setup":
            return Setup.Run(Arg(1, 20));
        case "show":
            Setup.Show(TrainerBridgeConfig.Load().Devices);
            return 0;
        case "set":
        {
            // `set Trainer bluetooth C1:23:45:67:89:AB [name]` saves a Bluetooth trainer without scanning; any role
            if (args.Length >= 4 && args[2].Equals("bluetooth", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse<Role>(args[1], true, out var btRole) || !Enum.IsDefined(btRole) || DescendersBridge.Bluetooth.BleAddress.Parse(args[3]) is not { } address)
                { Console.WriteLine("usage: set <Trainer|HeartRate|Cadence|Power> bluetooth <address, e.g. C1:23:45:67:89:AB> [name]"); return 1; }
                var p = TrainerBridgeConfig.Load().Devices;
                p.Set(btRole, Setup.BluetoothKeyFor(btRole, address, p), name: args.Length > 4 ? string.Join(" ", args.Skip(4)) : null);
                p.Save();
                Setup.Show(p);
                return 0;
            }
            if (args.Length < 4 || !Enum.TryParse<Role>(args[1], true, out var role) || !byte.TryParse(args[2], out byte type) || !ushort.TryParse(args[3], out ushort num))
            { Console.WriteLine("usage: set <Trainer|HeartRate|Cadence|Power> <deviceType> <deviceNumber> [transmissionType]"); return 1; }
            byte tx = args.Length > 4 && byte.TryParse(args[4], out byte t) ? t : (byte)0;
            var prefs = TrainerBridgeConfig.Load().Devices; prefs.Set(role, new DeviceKey(type, num, tx)); prefs.Save();
            Setup.Show(prefs);
            return 0;
        }
        case "clear":
        {
            if (args.Length < 2 || !Enum.TryParse<Role>(args[1], true, out var role)) { Console.WriteLine("usage: clear <role>"); return 1; }
            var prefs = TrainerBridgeConfig.Load().Devices; prefs.Clear(role); prefs.Save();
            Setup.Show(prefs);
            return 0;
        }
        case "selftest":
        {
            // `selftest --snapshots <folder>`: also keep the pictures the window test paints, to look at the window without a desktop
            int si = Array.IndexOf(args, "--snapshots");
            if (si >= 0 && si + 1 < args.Length) WindowDesignTests.SnapshotDir = Path.GetFullPath(args[si + 1]);
            return SelfTest.Run();
        }
        case "command":
        {
            // a mod command (docs/IPC.md), e.g. `command set_test_speed 12 4`; its ack goes to whoever holds 9871 (a running bridge logs it)
            if (args.Length < 2) { Console.WriteLine("usage: command <name> [value] [durationSec]"); return 1; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("{\"type\":\"command\",\"name\":" + System.Text.Json.JsonSerializer.Serialize(args[1]));
            if (args.Length > 2 && double.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out double cv)) sb.Append(",\"value\":").Append(cv.ToString(inv));
            if (args.Length > 3 && double.TryParse(args[3], System.Globalization.NumberStyles.Float, inv, out double cd)) sb.Append(",\"durationSec\":").Append(cd.ToString(inv));
            string json = sb.Append('}').ToString();
            using (var udp = Loopback.Sender()) { byte[] b = System.Text.Encoding.UTF8.GetBytes(json); udp.Send(b, b.Length, "127.0.0.1", 9872); }
            Console.WriteLine("sent -> 127.0.0.1:9872: " + json);
            return 0;
        }
        case "stop":
        {
            // a bridge the game started (`run --auto`) outlives the game; the dev scripts stop it cleanly (trainer released first) before installing
            using (var udp = Loopback.Sender()) { byte[] b = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"shutdown\"}"); udp.Send(b, b.Length, "127.0.0.1", 9871); }
            Console.WriteLine("sent -> 127.0.0.1:9871: {\"type\":\"shutdown\"} (a bridge the game started stops, releasing the trainer; the window and run / run --sim ignore it)");
            return 0;
        }
        case "keysource":
        {
            string source = BridgeConfig.KeySource;
            Console.WriteLine(source);
            return source == "none" ? 2 : 0;
        }
        case "version" or "--version":
            Console.WriteLine(BuildStamp.Text);
            return 0;
        case "run":
        {
            if (!args.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine("Bridge build: " + BuildStamp.Text);   // --auto prints it into its log (AutoRun)
            double? erg = null;
            int ei = Array.FindIndex(args, a => a.Equals("--erg", StringComparison.OrdinalIgnoreCase));
            if (ei >= 0)
            {
                if (ei + 1 >= args.Length || !double.TryParse(args[ei + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w) || w <= 0)
                { Console.WriteLine("usage: run [seconds] --erg <watts>"); return 1; }
                erg = w;
            }
            int? parent = null;
            int pi = Array.FindIndex(args, a => a.Equals("--parent", StringComparison.OrdinalIgnoreCase));
            if (pi >= 0)
            {
                if (pi + 1 >= args.Length || !int.TryParse(args[pi + 1], out int pid)) { Console.WriteLine("usage: run [seconds] --parent <pid>"); return 1; }
                parent = pid;
            }
            int seconds = args.Length > 1 && int.TryParse(args[1], out int secs) ? secs : 0;
            int si = Array.FindIndex(args, a => a.Equals("--seconds", StringComparison.OrdinalIgnoreCase));
            if (si >= 0 && (si + 1 >= args.Length || !int.TryParse(args[si + 1], out seconds) || seconds < 0)) { Console.WriteLine("usage: run ... --seconds <n>"); return 1; }
            if (args.Any(a => a.Equals("--sim", StringComparison.OrdinalIgnoreCase)))
            {
                var sim = SimOptions.Parse(args, out string simErr);
                if (sim is null) { Console.WriteLine(simErr); Console.WriteLine(SimOptions.Usage); return 1; }
                int li = Array.FindIndex(args, a => a.Equals("--log", StringComparison.OrdinalIgnoreCase));
                if (li >= 0 && li + 1 >= args.Length) { Console.WriteLine(SimOptions.Usage); return 1; }
                int wi = Array.FindIndex(args, a => a.Equals("--workout", StringComparison.OrdinalIgnoreCase));
                if (wi >= 0 && (wi + 1 >= args.Length || !File.Exists(args[wi + 1]))) { Console.WriteLine("usage: run --sim ... --workout <workout file (.zwo / .xml)>   (file not found)"); return 1; }
                return await RunSim(sim, seconds, li >= 0 ? args[li + 1] : null, erg, parent, wi >= 0 ? args[wi + 1] : null);
            }
            if (args.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase)))
                return await AutoRun.RunAsync(parent);
            return await Run(seconds, erg, parent);
        }
        case "gui":
            return DescendersBridge.Gui.GuiMain.Run(demo: args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)));
        case "help" or "--help" or "-h" or "/?":
            Console.WriteLine(Usage);
            return 0;
        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (Exception ex)
{
    Console.WriteLine("ERROR: " + ex.Message);
    return 2;
}

static async Task<int> RunSim(SimOptions sim, int seconds, string? logPath, double? fixedErgW, int? parentPid, string? workoutPath)
{
    var (rider, _) = TrainerBridgeConfig.Load();   // the rider profile (FTP) is real, only the trainer is fake
    using var cts = new CancellationTokenSource();
    if (seconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(seconds));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    if (parentPid is { } pid) ParentWatch.Start(pid, cts);
    ModRider.Start(rider, cts.Token);
    await Ride.RunSimAsync(sim, rider, logPath, fixedErgW, cts.Token, workoutPath);
    return 0;
}

static async Task<int> Run(int seconds, double? fixedErgW, int? parentPid)
{
    var (rider, prefs) = TrainerBridgeConfig.Load();
    if (prefs.Get(Role.Trainer) is null && prefs.Get(Role.HeartRate) is null && prefs.Get(Role.Cadence) is null && prefs.Get(Role.Power) is null)
    {
        Console.WriteLine("Nothing chosen. Run `turbotrainer-bridge setup` first (or `run --auto` to choose the trainer automatically).");
        return 1;
    }

    using var cts = new CancellationTokenSource();
    if (seconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(seconds));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    if (parentPid is { } pid) ParentWatch.Start(pid, cts);
    ModRider.Start(rider, cts.Token);
    // the stick and the ANT+ key are only asked for when a chosen device is ANT+
    if (prefs.NeedsAnt) Console.WriteLine("ANT+ network key source: " + BridgeConfig.KeySource);
    await Ride.RunAsync(prefs, rider, fixedErgW, cts.Token);
    return 0;
}
