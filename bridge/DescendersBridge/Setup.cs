using System.Text;
using DescendersBridge.Ant;
using DescendersBridge.Bluetooth;

namespace DescendersBridge;

/// <summary>Console device chooser: scan (ANT+ and, Bluetooth), then pick one device per role and store it in TrainerBridgeConfig.json.</summary>
public static class Setup
{
    /// <summary>
    /// `set &lt;role&gt; bluetooth &lt;address&gt;`: the device a role's Bluetooth address is saved as. Cadence is a cadence sensor
    /// (CSC Measurement), unless that address is already known as a power meter: then its crank data is the cadence.
    /// </summary>
    public static DeviceKey BluetoothKeyFor(Role role, ulong address, DevicePreferences prefs)
    {
        byte type = role switch
        {
            Role.Trainer => AntDeviceTypes.FitnessEquipment,
            Role.HeartRate => AntDeviceTypes.HeartRate,
            Role.Power => AntDeviceTypes.PowerMeter,
            _ => prefs.Known.Any(d => d.IsBluetooth && d.Key == DeviceKey.Bluetooth(AntDeviceTypes.PowerMeter, address)) ? AntDeviceTypes.PowerMeter : AntDeviceTypes.BikeCadence,
        };
        return DeviceKey.Bluetooth(type, address);
    }

    public static DeviceTracker[] ScanFor(int seconds, bool live)
    {
        using var scanner = new AntScanner(BridgeConfig.LoadNetworkKey());
        scanner.Status += m => Console.WriteLine("[ant] " + m);
        scanner.Start();
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(2000);
            if (live) PrintDevices(scanner.Devices, $"--- {scanner.Messages} messages, {scanner.Devices.Count} devices");
        }
        var found = scanner.Devices.OrderBy(d => d.Key.DeviceType).ThenBy(d => d.Key.DeviceNumber).ToArray();
        return found;
    }

    /// <summary>
    /// Search both radios at the same time. Each is optional: a PC with no ANT+ stick (or a build with no ANT+ key) still
    /// searches Bluetooth, a PC with no Bluetooth still searches ANT+, and each says in one line why it was left out. Never throws.
    /// </summary>
    public static (DeviceTracker[] Ant, List<BleAdvert> Bluetooth) ScanAll(int seconds, bool live, IBleRadio? radio = null)
    {
        var bluetooth = Task.Run(() => BluetoothDevicesAsync(radio ?? new WindowsBleRadio(), seconds, Console.WriteLine));
        DeviceTracker[] ant;
        try { ant = ScanFor(seconds, live); }
        catch (Exception ex)
        {
            Console.WriteLine("ANT+ not searched: " + ex.Message);
            ant = Array.Empty<DeviceTracker>();
        }
        return (ant, bluetooth.GetAwaiter().GetResult());
    }

    /// <summary>The Bluetooth trainers (advertising the Fitness Machine Service 0x1826) heard in <paramref name="seconds"/>, nearest first.
    /// A PC that cannot do Bluetooth gives an empty list and one line saying why. Never throws.</summary>
    public static async Task<List<BleAdvert>> BluetoothTrainersAsync(IBleRadio radio, int seconds, Action<string> log) =>
        BleRules.Trainers(await BluetoothDevicesAsync(radio, seconds, log));

    /// <summary>The Bluetooth trainers and sensors (advertising heart rate 0x180D, speed and cadence 0x1816 or cycling power
    /// 0x1818) heard in <paramref name="seconds"/>: the trainers first, each kind nearest first. A PC that cannot do Bluetooth gives an
    /// empty list and one line saying why. Never throws.</summary>
    public static async Task<List<BleAdvert>> BluetoothDevicesAsync(IBleRadio radio, int seconds, Action<string> log)
    {
        try
        {
            var adapter = await radio.AdapterAsync();
            if (BleRules.Problem(adapter) is { } problem)
            {
                log("Bluetooth not searched: " + problem + (adapter.Detail.Length > 0 ? " (" + adapter.Detail + ")" : ""));
                return new List<BleAdvert>();
            }
            var heard = await radio.ScanAsync(TimeSpan.FromSeconds(seconds), CancellationToken.None);
            var devices = BleRules.Devices(heard);
            int trainers = BleRules.Trainers(heard).Count;
            log($"Bluetooth: {heard.Count} device(s) heard, {trainers} trainer(s) and {devices.Count - trainers} sensor(s) among them");
            return devices;
        }
        catch (Exception ex)
        {
            log("Bluetooth not searched: " + ex.Message);
            return new List<BleAdvert>();
        }
    }

    /// <summary>
    /// `turbotrainer-bridge bluetooth [seconds]`: what Windows says about this PC's Bluetooth, then an advertisement scan (the
    /// check that the radio can be used from this session). Exit 0 = the scan started and stopped without an error, 2 = no usable Bluetooth.
    /// </summary>
    public static async Task<int> BluetoothCheckAsync(int seconds, IBleRadio? radio = null)
    {
        radio ??= new WindowsBleRadio();
        var a = await radio.AdapterAsync();
        Console.WriteLine($"Bluetooth adapter: {(a.Present ? "present" : "none")}{(a.Detail.Length > 0 ? " (" + a.Detail + ")" : "")}");
        if (a.Present) Console.WriteLine($"  IsLowEnergySupported {a.LowEnergy}, IsCentralRoleSupported {a.CentralRole}, radio on {(a.RadioOn is { } on ? on.ToString() : "unknown")}");
        if (BleRules.Problem(a) is { } problem) { Console.WriteLine(problem); return 2; }
        try
        {
            Console.WriteLine($"Scanning advertisements for {seconds} s...");
            var heard = await radio.ScanAsync(TimeSpan.FromSeconds(seconds), CancellationToken.None);
            var devices = BleRules.Devices(heard);
            int trainers = BleRules.Trainers(heard).Count;
            Console.WriteLine($"Scan started and stopped without an error: {heard.Sum(h => h.Count)} advertisement(s) from {heard.Count} device(s), {trainers} trainer(s) (Fitness Machine Service 0x1826)"
                + $", {devices.Count - trainers} sensor(s) (heart rate 0x180D, speed and cadence 0x1816, cycling power 0x1818)");
            foreach (var d in devices) Console.WriteLine("  " + BleRules.Describe(d));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Scan failed: " + ex.GetType().Name + ": " + ex.Message);
            return 2;
        }
    }

    public static void PrintDevices(IEnumerable<DeviceTracker> devices, string header)
    {
        Console.WriteLine(header);
        foreach (var d in devices.OrderBy(d => d.Key.DeviceType).ThenBy(d => d.Key.DeviceNumber))
            Console.WriteLine("  " + Describe(d));
    }

    public static string Describe(DeviceTracker d)
    {
        var sb = new StringBuilder();
        sb.Append($"{AntDeviceTypes.Name(d.Key.DeviceType),-22} #{d.Key.DeviceNumber,-6} tx{d.Key.TransmissionType,-3} {d.Rate,4:0.0} msg/s  last {d.SecondsSinceLast,4:0.0}s ago");
        if (d.HeartRateBpm.HasValue) sb.Append($"  HR {d.HeartRateBpm} bpm");
        if (d.CadenceRpm.HasValue) sb.Append($"  cad {d.CadenceRpm:0} rpm (max {d.MaxCadenceRpm:0})");
        if (d.PowerW.HasValue) sb.Append($"  {d.PowerW} W");
        if (d.Identity is { } id) sb.Append("  " + id.LogText);   // numbers always, so the logs tell us real model numbers
        return sb.ToString();
    }

    /// <summary>One line of a role's list in <see cref="Run"/>. Name: a Bluetooth device's advertised name, saved with the choice.</summary>
    public readonly record struct Candidate(DeviceKey Key, string Text, string? Name = null);

    /// <summary>
    /// What `setup` offers for one role (pure, tests\unit\BluetoothSettingsTests.cs): the ANT+ devices heard that can do the job, then the
    /// Bluetooth devices heard that can (trainers; heart rate, cadence, power; <see cref="BleRules.KeyFor"/>), then
    /// the devices used before that this scan did not hear.
    /// </summary>
    public static List<Candidate> Candidates(Role role, DevicePreferences prefs, IReadOnlyList<DeviceTracker> ant, IReadOnlyList<BleAdvert> bluetooth)
    {
        var current = prefs.Get(role);
        var list = ant.Where(d => AntDeviceTypes.Roles(d.Key.DeviceType).Contains(role)).Select(d => new Candidate(d.Key, Describe(d))).ToList();
        foreach (var b in bluetooth)
            if (BleRules.KeyFor(role, b) is { } key) list.Add(new Candidate(key, BleRules.Describe(b), b.Name));
        var heard = list.Select(c => c.Key).ToList();
        list.AddRange(prefs.KnownFor(role).Where(k => k != current && !heard.Contains(k))
            .Select(k => new Candidate(k, Label(k, prefs) + "  (used before, not heard in this scan)")));
        return list;
    }

    /// <summary>A stored device for the console: an ANT+ one by type, number and transmission type, a Bluetooth one by name and address.</summary>
    public static string Label(DeviceKey k, DevicePreferences prefs) =>
        k.IsBluetooth ? $"{prefs.NameOf(k) ?? DeviceLabels.Kind(k.DeviceType)} (Bluetooth {BleAddress.Format(k.BluetoothAddress)})" : $"{k} tx{k.TransmissionType}";

    /// <summary>Interactive: scan, then choose per role. With no console input (stdin closed) every role keeps its stored choice.</summary>
    public static int Run(int seconds)
    {
        var prefs = TrainerBridgeConfig.Load().Devices;
        Console.WriteLine($"Scanning {seconds} s (ANT+ and Bluetooth). Make sure the watch is broadcasting HR and SPIN THE CRANK so the trainer and cadence sensors wake and show their rpm.");
        var (found, bluetooth) = ScanAll(seconds, live: false);
        Console.WriteLine();
        Console.WriteLine($"Heard {found.Length} ANT+ device(s) and {bluetooth.Count} Bluetooth device(s):");
        foreach (var d in found) Console.WriteLine("  " + Describe(d));
        foreach (var b in bluetooth) Console.WriteLine("  " + BleRules.Describe(b));

        foreach (Role role in Enum.GetValues<Role>())
        {
            var current = prefs.Get(role);
            var cands = Candidates(role, prefs, found, bluetooth);
            bool heardCurrent = current is { } cur && (found.Any(d => d.Key == cur) || bluetooth.Any(b => BleRules.KeyFor(role, b) == cur));
            Console.WriteLine();
            Console.WriteLine($"== {role} ==   current: {(current.HasValue ? Label(current.Value, prefs) : "(none)")}"
                + (current.HasValue && !heardCurrent ? "  [saved device NOT heard in this scan]" : ""));
            if (role is Role.Power or Role.Cadence)
                Console.WriteLine(role == Role.Power ? "   (none = use the trainer's own power)" : "   (none = use the trainer's own cadence)");
            for (int i = 0; i < cands.Count; i++)
                Console.WriteLine($"   {i + 1}) {cands[i].Text}{(current == cands[i].Key ? "   <- current" : "")}");
            Console.Write(cands.Count == 0 ? "   nothing heard for this role; Enter to keep, 0 to clear: " : "   choose 1-" + cands.Count + ", Enter to keep, 0 to clear: ");
            string? line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Trim() == "0") { prefs.Clear(role); continue; }
            if (int.TryParse(line, out int n) && n >= 1 && n <= cands.Count) prefs.Set(role, cands[n - 1].Key, name: cands[n - 1].Name);
            else Console.WriteLine("   not a valid choice, keeping the current one");
        }
        // every stored device this scan heard gains its manufacturer, chosen again or not
        foreach (var d in found) if (d.Identity is { } id) prefs.Learn(d.Key, id.ManufacturerId);

        prefs.Save();
        Console.WriteLine();
        Console.WriteLine("Saved to " + TrainerBridgeConfig.Path);
        Show(prefs);
        return 0;
    }

    public static void Show(DevicePreferences prefs)
    {
        foreach (Role role in Enum.GetValues<Role>())
        {
            var k = prefs.Get(role);
            string text = k is not { } key ? (role is Role.Power or Role.Cadence ? "(trainer)" : "(none)")
                : key.IsBluetooth ? Label(key, prefs)
                : key.ToString() + $" (tx {key.TransmissionType}{(prefs.ManufacturerOf(key) is { } m ? ", " + new DeviceIdentity(m).LogText : "")})";
            Console.WriteLine($"  {role,-10} {text}");
        }
        // devices chosen earlier and not in a role now are kept, so they can be chosen again without a search
        var unused = prefs.Known.Select(d => d.Key).Where(k => !Enum.GetValues<Role>().Any(r => prefs.Get(r) == k)).ToList();
        if (unused.Count > 0) Console.WriteLine("  Used before, not in use now: " + string.Join(", ", unused.Select(k => k.IsBluetooth ? Label(k, prefs) : $"{k} (tx {k.TransmissionType})")));
    }
}
