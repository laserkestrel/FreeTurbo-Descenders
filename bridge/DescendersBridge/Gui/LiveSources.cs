using System.Diagnostics;
using System.Globalization;
using System.Text;
using DescendersBridge.Ant;
using DescendersBridge.Bluetooth;

namespace DescendersBridge.Gui;

/// <summary>Where the window's live readings come from.</summary>
public interface ILiveSource
{
    LiveReading Read();
}

/// <summary>The real ride: reads the session <see cref="Ride"/> has open (the same values the ride loop sends to the game).</summary>
public sealed class AntLiveSource : ILiveSource
{
    public LiveReading Read()
    {
        if (Ride.Current is not { } s) return LiveReading.None;
        return new LiveReading(s.PowerW, s.CadenceRpm, s.HeartRateBpm, s.SpeedMps,
            s.Link(Role.Trainer), s.Link(Role.HeartRate), s.Link(Role.Cadence), s.Link(Role.Power));
    }
}

/// <summary>
/// `gui --demo`: made-up readings and devices, no USB stick needed, so the window can be shown and tested by UI Automation on any PC.
/// The heart-rate strap has a weak signal and no cadence sensor is chosen, so every health light state but "lost" is on screen.
/// </summary>
public sealed class DemoLiveSource : ILiveSource
{
    public static readonly DeviceKey Trainer = new(AntDeviceTypes.FitnessEquipment, 12345, 5), Hr = new(AntDeviceTypes.HeartRate, 678, 1),
        Cadence = new(AntDeviceTypes.BikeCadence, 4321, 1);
    /// <summary>The demo trainer and strap say who made them; the cadence sensor does not (its Make cell stays blank).</summary>
    public const ushort TrainerMake = Manufacturers.Tacx, HrMake = Manufacturers.Garmin;

    /// <summary>The demo has Bluetooth devices too. The trainer is also found over Bluetooth (so the search lists it twice,
    /// once for each), and a Bluetooth power meter is the saved device for Power when the settings file has none.</summary>
    public static readonly BleAdvert BluetoothTrainer = new(0xC1234567A001, "Tacx Neo 2T 12345", -58, 30,
            new[] { Ftms.Service, BleSensors.PowerService }),
        BluetoothPower = new(0xC1234567A002, "Power pedals 77", -66, 24, new[] { BleSensors.PowerService });
    public static readonly DeviceKey PowerMeter = DeviceKey.Bluetooth(AntDeviceTypes.PowerMeter, BluetoothPower.Address);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public LiveReading Read()
    {
        double t = _clock.Elapsed.TotalSeconds;
        var trainer = new LinkInfo(Trainer, (long)(t * 4), 0.1, ManufacturerId: TrainerMake);
        return new LiveReading(
            PowerW: (int)Math.Round(200 + 40 * Math.Sin(t / 5)),
            CadenceRpm: 85 + 5 * Math.Sin(t / 7),
            HeartRateBpm: (int)Math.Round(120 + 10 * Math.Sin(t / 20)),
            SpeedMps: 8.0 + Math.Sin(t / 5),
            Trainer: trainer, HeartRate: new LinkInfo(Hr, (long)(t * 1.5), 0.5, ManufacturerId: HrMake), Cadence: null,
            Power: new LinkInfo(PowerMeter, (long)t + 1, 0.4));
    }
}

/// <summary>One device heard in a search, as the "My devices" tab lists it. <paramref name="Advert"/>: what a Bluetooth
/// device advertised (its name, its signal, the services that say which jobs it can do); null = an ANT+ device.</summary>
public sealed record FoundDevice(DeviceKey Key, long Messages, double Rate, double SecondsSinceLast, string Reading, DeviceIdentity? Identity = null, BleAdvert? Advert = null)
{
    private static readonly Role[] Roles = Enum.GetValues<Role>();

    /// <summary>Who made it, when the search heard its manufacturer page.</summary>
    public ushort? ManufacturerId => Identity?.ManufacturerId;

    public bool IsBluetooth => Advert is not null;

    /// <summary>A Bluetooth device's advertised name; null = none (or an ANT+ device, which sends no name).</summary>
    public string? Name => Advert is { Name.Length: > 0 } a ? a.Name : null;

    /// <summary>"Trainer", "Heart rate", ...: an ANT+ device by its type, a Bluetooth one by what it advertises.</summary>
    public string Kind => Advert is { } a ? BleRules.Kind(a) : DeviceLabels.Kind(Key.DeviceType);

    /// <summary>"Strong" / "OK" / "Weak": an ANT+ device from how often it was heard (ANT+ sensors send about 4 times a second), a
    /// Bluetooth one from its signal strength.</summary>
    public string Signal => Advert is { } a ? SearchRules.BluetoothSignal(a.RssiDbm)
        : SecondsSinceLast > AntSession.StaleSeconds ? "Gone quiet" : Rate >= 3.0 ? "Strong" : Rate >= HealthRules.WeakRate ? "OK" : "Weak";

    /// <summary>What this device would be saved as for a job; null = it cannot do that job. A Bluetooth trainer is the trainer only,
    /// and a Bluetooth device that does two jobs may be saved under a different kind for each (<see cref="BleRules.KeyFor"/>).</summary>
    public DeviceKey? KeyFor(Role role) =>
        Advert is { } a ? BleRules.KeyFor(role, a) : AntDeviceTypes.Roles(Key.DeviceType).Contains(role) ? Key : null;

    /// <summary>The search heard this saved / chosen device.</summary>
    public bool Is(DeviceKey key) => Key == key || (Advert is not null && Roles.Any(r => KeyFor(r) == key));

    /// <summary>A Bluetooth device of a scan; null = it advertises nothing the bridge can use.</summary>
    public static FoundDevice? From(BleAdvert a)
    {
        foreach (Role role in Roles)
            if (BleRules.KeyFor(role, a) is { } key) return new FoundDevice(key, a.Count, 0, 0, "", null, a);
        return null;
    }

    public static FoundDevice From(DeviceTracker d)
    {
        lock (d)
        {
            var sb = new StringBuilder();
            if (d.PowerW.HasValue) sb.Append(d.PowerW.Value).Append(" watts  ");
            if (d.CadenceRpm.HasValue) sb.Append(d.CadenceRpm.Value.ToString("0", CultureInfo.InvariantCulture)).Append(" rpm  ");
            if (d.HeartRateBpm.HasValue) sb.Append(d.HeartRateBpm.Value).Append(" bpm  ");
            return new FoundDevice(d.Key, d.Messages, d.Rate, d.SecondsSinceLast, sb.ToString().Trim(), d.Identity);
        }
    }
}

/// <summary>An open ANT+ scan as <see cref="DeviceSearch"/> sees it: what it has heard so far, and how to close it (the stick does one
/// thing at a time). The real one is <see cref="DeviceSearch.OpenStick"/>; `selftest` gives a made-up one, or one that has no stick.</summary>
public sealed record AntScan(Func<IReadOnlyCollection<DeviceTracker>> Devices, Action Close);

/// <summary>
/// A device search ("My devices" tab), or the demo list. One search looks on ANT+ (scan mode, the same scan as `setup`) and on
/// Bluetooth (advertisements, in short scans so the list fills while it runs) at the same time, for <see cref="Seconds"/>, on whatever
/// this PC has: no stick, or no Bluetooth, leaves the other one searching and a quiet line saying so (<see cref="Note"/>); with neither
/// there is nothing to search with (<see cref="Failure"/>).
/// </summary>
public sealed class DeviceSearch
{
    public const int Seconds = 15;
    /// <summary>One Bluetooth scan; its devices are in the list when it ends.</summary>
    public const double BluetoothScanSeconds = 3;

    private readonly bool _demo;
    private readonly double _seconds;
    private readonly Func<AntScan> _openAnt;
    private readonly Func<IBleRadio> _radio;
    private volatile AntScan? _ant;
    private volatile IReadOnlyList<BleAdvert> _bluetooth = Array.Empty<BleAdvert>();
    private volatile IReadOnlyList<FoundDevice>? _final;
    private volatile string? _note;
    private readonly Stopwatch _clock = new();

    /// <param name="openAnt">tests: a made-up ANT+ scan (or one that throws: no stick); null = this PC's stick.</param>
    /// <param name="radio">tests: a fake Bluetooth radio; null = this PC's.</param>
    public DeviceSearch(bool demo, Func<AntScan>? openAnt = null, Func<IBleRadio>? radio = null, double seconds = Seconds)
    {
        _demo = demo;
        _seconds = seconds;
        _openAnt = openAnt ?? OpenStick;
        _radio = radio ?? (() => new WindowsBleRadio());
    }

    public bool Running { get; private set; }
    /// <summary>Why the search could not run at all (neither an ANT+ stick nor Bluetooth, or something unexpected); null = it ran.</summary>
    public FriendlyMessage? Failure { get; private set; }
    /// <summary>The quiet line while only one of the two is searched (<see cref="SearchRules.Note"/>); null = nothing to say.</summary>
    public string? Note => _note;
    /// <summary>What this search looked on (for the "no trainer found" advice).</summary>
    public bool SearchedAnt { get; private set; }
    public bool SearchedBluetooth { get; private set; }
    public double Progress => Math.Clamp(_clock.Elapsed.TotalSeconds / _seconds, 0, 1);

    /// <summary>The ANT+ devices (by kind, then number), then the Bluetooth ones (trainers first, then by name).</summary>
    public IReadOnlyList<FoundDevice> Devices => _demo ? DemoDevices(_clock.Elapsed.TotalSeconds) : _final ?? Live();

    private IReadOnlyList<FoundDevice> Live()
    {
        var list = new List<FoundDevice>();
        if (_ant is { } ant) list.AddRange(ant.Devices().Select(FoundDevice.From).OrderBy(d => d.Key.DeviceType).ThenBy(d => d.Key.DeviceNumber));
        list.AddRange(_bluetooth.Select(FoundDevice.From).OfType<FoundDevice>()
            .OrderBy(d => d.KeyFor(Role.Trainer) is null).ThenBy(d => d.Name ?? "￿", StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Key.BluetoothAddress));
        return list;
    }

    /// <summary>This PC's ANT+ stick in scan mode. Throws <see cref="AntStickException"/> when there is none (or it is in use).</summary>
    public static AntScan OpenStick()
    {
        var scanner = new AntScanner(BridgeConfig.LoadNetworkKey());
        scanner.Status += m => Console.WriteLine("[ant] " + m);
        try { scanner.Start(); }
        catch (Exception ex)
        {
            scanner.Dispose();
            throw new AntStickException("ANT+ stick not available: " + ex.Message, ex);
        }
        return new AntScan(() => scanner.Devices, scanner.Dispose);
    }

    /// <summary>Runs the whole search; the window polls <see cref="Devices"/> and <see cref="Note"/> meanwhile. Never throws: a search
    /// that could not run says why in <see cref="Failure"/>.</summary>
    public async Task RunAsync()
    {
        Running = true; Failure = null; _note = null; _final = null; _clock.Restart();
        try
        {
            if (_demo) { SearchedAnt = SearchedBluetooth = true; await Task.Delay(TimeSpan.FromSeconds(3)); return; }
            await Task.Run(SearchAsync);
        }
        catch (Exception ex) { Failure = FriendlyText.For(ex); Console.WriteLine("Search failed: " + FriendlyText.LogText(ex)); }
        finally
        {
            if (!_demo) _final = Live();
            var ant = _ant;
            _ant = null;
            try { ant?.Close(); } catch { }
            _clock.Stop();
            Running = false;
        }
    }

    private async Task SearchAsync()
    {
        // Bluetooth is asked first: asking claims no hardware
        IBleRadio? radio = null;
        string? bluetoothProblem;
        try
        {
            radio = _radio();
            var adapter = await radio.AdapterAsync();
            bluetoothProblem = BleRules.Problem(adapter);
            if (bluetoothProblem is not null) Console.WriteLine("Bluetooth not searched: " + bluetoothProblem + (adapter.Detail.Length > 0 ? " (" + adapter.Detail + ")" : ""));
        }
        catch (Exception ex)
        {
            bluetoothProblem = SearchRules.BluetoothFailed;
            Console.WriteLine("Bluetooth not searched: " + ex.Message);
        }

        // no stick is an everyday thing: one plain line, and Bluetooth is searched alone
        bool keyMissing = false;
        try { _ant = _openAnt(); }
        catch (Exception ex)
        {
            keyMissing = ex.Message.StartsWith("No ANT+ network key", StringComparison.Ordinal);
            Console.WriteLine("ANT+ not searched: " + (ex is AntStickException ? FriendlyText.LogText(ex) : ex.Message));
        }
        SearchedAnt = _ant is not null;

        while (true)
        {
            SearchedBluetooth = bluetoothProblem is null;
            _note = SearchRules.Note(SearchedAnt, bluetoothProblem, keyMissing);
            if (SearchRules.Failure(SearchedAnt, bluetoothProblem, keyMissing) is { } failure)
            {
                Failure = failure;
                Console.WriteLine("Search failed: " + failure.Title);
                return;
            }
            double left = _seconds - _clock.Elapsed.TotalSeconds;
            if (left <= 0) break;
            if (bluetoothProblem is not null) { await Task.Delay(TimeSpan.FromSeconds(left)); break; }
            try { _bluetooth = BleRules.Merge(_bluetooth, await radio!.ScanAsync(TimeSpan.FromSeconds(Math.Min(left, BluetoothScanSeconds)), CancellationToken.None)); }
            catch (Exception ex)
            {
                // a radio that is switched off only says so when it is asked to scan
                bluetoothProblem = ex.Message == BleRules.RadioOff ? BleRules.RadioOff : SearchRules.BluetoothFailed;
                Console.WriteLine("Bluetooth not searched: " + ex.Message);
            }
        }

        if (SearchedBluetooth && radio is not null) await AskAsync(radio);

        var found = Live();
        Console.WriteLine($"Search finished: {found.Count} device(s)" + (_note is { } note ? " (" + note + ")" : ""));
        foreach (var d in found)
            Console.WriteLine(d.Advert is { } a ? "  " + BleRules.Describe(a)
                : $"  {d.Key} tx {d.Key.TransmissionType}  {d.Rate:0.0} msg/s  {d.Reading}{(d.Identity is { } id ? "  " + id.LogText : "")}");
        // what else Bluetooth heard, so a log shows why a device is missing from the list (named devices, nearest first)
        var unlisted = _bluetooth.Where(a => a.Name.Length > 0 && FoundDevice.From(a) is null).OrderByDescending(a => a.RssiDbm).Take(MaxUnlistedLogged).ToList();
        if (unlisted.Count > 0) Console.WriteLine("Other Bluetooth devices heard (no trainer or sensor the bridge knows, not listed):");
        foreach (var a in unlisted) Console.WriteLine("  " + BleRules.DescribeUnlisted(a));
    }

    /// <summary>How long a device may take to answer "do you have the Fitness Machine Service?", how many are asked, and how many
    /// unlisted devices the log names.</summary>
    public const double AskSeconds = 6;
    public const int MaxAsked = 4, MaxUnlistedLogged = 15;

    /// <summary>
    /// (a Tacx Neo 2T was not offered as a trainer): a trainer may leave the Fitness Machine Service out of its
    /// advertisement. The devices that could be one (<see cref="BleRules.WorthAsking"/>, the nearest <see cref="MaxAsked"/>) are connected
    /// to for a moment, all at once, and asked; one that has the service is a trainer from then on. A device that does not answer in
    /// <see cref="AskSeconds"/> stays what its advertisement said. Never throws.
    /// </summary>
    private async Task AskAsync(IBleRadio radio)
    {
        var ask = _bluetooth.Where(BleRules.WorthAsking).OrderByDescending(a => a.RssiDbm).Take(MaxAsked).ToList();
        if (ask.Count == 0) return;
        using var cts = new CancellationTokenSource();
        var asked = ask.Select(a => (Advert: a, Answer: HasFtmsAsync(radio, a.Address, cts.Token))).ToList();
        try { await Task.WhenAny(Task.WhenAll(asked.Select(q => q.Answer)), Task.Delay(TimeSpan.FromSeconds(Math.Min(AskSeconds, Math.Max(_seconds, 1))))); } catch { }
        cts.Cancel();
        foreach (var (advert, answer) in asked)
        {
            string name = (advert.Name.Length > 0 ? advert.Name : "(no name)") + " " + BleAddress.Format(advert.Address);
            bool? has = answer.Status == TaskStatus.RanToCompletion ? answer.Result : null;
            Console.WriteLine("Bluetooth: asked " + name + " whether it is a trainer: " + has switch
            {
                true => "yes (it has the Fitness Machine Service)",
                false => "no (no Fitness Machine Service)" + (BleRules.IsTrainer(advert) ? ": it is listed as a trainer by its maker's service, and is ridden through that one" : ""),
                _ => "no answer" + (answer.Exception?.GetBaseException().Message is { Length: > 0 } why ? " (" + why + ")" : ""),
            });
            if (has == true) _bluetooth = _bluetooth.Select(a => a.Address == advert.Address ? BleRules.WithFtms(a) : a).ToList();
        }
    }

    /// <summary>Connect, ask for the Fitness Machine Feature (every fitness machine has it), disconnect.</summary>
    private static async Task<bool> HasFtmsAsync(IBleRadio radio, ulong address, CancellationToken ct)
    {
        await using var conn = await radio.ConnectAsync(address, ct).ConfigureAwait(false);
        return await conn.ReadAsync(Ftms.Service, Ftms.FeatureChar, ct).ConfigureAwait(false) is not null;
    }

    private static IReadOnlyList<FoundDevice> DemoDevices(double t)
    {
        var list = new List<FoundDevice> { new(DemoLiveSource.Trainer, 40, 4.0, 0.1, "195 watts  84 rpm", new DeviceIdentity(DemoLiveSource.TrainerMake)) };
        if (t > 1) list.Add(new(DemoLiveSource.Hr, 12, 2.0, 0.4, "118 bpm", new DeviceIdentity(DemoLiveSource.HrMake)));
        if (t > 2) list.Add(new(DemoLiveSource.Cadence, 20, 4.0, 0.2, "86 rpm"));
        // the trainer answers over Bluetooth too (one row for each), and a Bluetooth power meter
        if (t > 1.5) list.Add(FoundDevice.From(DemoLiveSource.BluetoothTrainer)!);
        if (t > 2.5) list.Add(FoundDevice.From(DemoLiveSource.BluetoothPower)!);
        return list;
    }
}

/// <summary>The window's connection (made by the window itself, <see cref="ConnectRules"/>; no button): the same
/// <see cref="Ride.RunAsync"/> as `run`, in this process, on a background task.</summary>
public sealed class RideController
{
    public enum State { Stopped, Running, Stopping, Failed }

    private readonly bool _demo;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public RideController(bool demo) { _demo = demo; }

    private volatile State _state = State.Stopped;

    /// <summary>Each change is logged once ("Ride state: connected / stopping / stopped / failed").</summary>
    public State Current
    {
        get => _state;
        private set
        {
            var old = _state;
            _state = value;
            if (StripRules.LogLine(StripRules.Phase(old, false), StripRules.Phase(value, false)) is { } line) Console.WriteLine(line);
        }
    }

    public FriendlyMessage? Failure { get; private set; }
    public DevicePreferences? Devices { get; private set; }
    /// <summary>When the ride started (for "trainer not found after N seconds").</summary>
    public DateTime StartedUtc { get; private set; }

    /// <summary>Connect with the saved settings (re-read, so edits made in the window apply). Returns a message when it cannot start at all.</summary>
    public FriendlyMessage? Start()
    {
        if (Current is State.Running or State.Stopping) return null;
        var (rider, prefs) = TrainerBridgeConfig.Load();
        Devices = prefs;
        Failure = null;
        StartedUtc = DateTime.UtcNow;
        if (_demo) { Current = State.Running; return null; }
        if (prefs.Get(Role.Trainer) is null && prefs.Get(Role.HeartRate) is null && prefs.Get(Role.Cadence) is null && prefs.Get(Role.Power) is null)
            return FriendlyText.NoTrainerChosen;
        if (prefs.NeedsAnt && BridgeConfig.KeySource == "none") return FriendlyText.NoKey;   // Bluetooth devices need no key

        var cts = _cts = new CancellationTokenSource();
        Current = State.Running;
        _task = Task.Run(async () =>
        {
            try
            {
                ModRider.Start(rider, cts.Token);
                if (prefs.NeedsAnt) Console.WriteLine("ANT+ network key source: " + BridgeConfig.KeySource);
                await Ride.RunAsync(prefs, rider, null, cts.Token);
                if (ReferenceEquals(_cts, cts)) Current = State.Stopped;   // a ride that ended late must not mark the one started after it
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + FriendlyText.LogText(ex));
                cts.Cancel();   // stops the rider messages too
                if (!ReferenceEquals(_cts, cts)) return;
                var failure = FriendlyText.For(ex);
                // no stick on a PC that has Bluetooth is not the red error (the rider may have no stick at all)
                if (failure == FriendlyText.NoStick) failure = FriendlyText.NoStickFor(await BluetoothUsableAsync());
                if (!ReferenceEquals(_cts, cts)) return;
                Failure = failure;
                Current = State.Failed;
            }
        });
        return null;
    }

    /// <summary>This PC's Bluetooth can be used (asking claims no hardware). Never throws.</summary>
    private static async Task<bool> BluetoothUsableAsync()
    {
        try
        {
            var adapter = await new WindowsBleRadio().AdapterAsync();
            string? problem = BleRules.Problem(adapter);
            Console.WriteLine("No ANT+ stick: this PC's Bluetooth " + (problem is null ? "can be used instead (search on \"My devices\")" : "cannot be used either (" + problem + ")"));
            return problem is null;
        }
        catch { return false; }
    }

    /// <summary>Stop and wait until the trainer has been released (a few seconds at most). A stop already under way is waited for too,
    /// so two callers (a Save and a device search, say) both go on only when the stick is free.</summary>
    public async Task StopAsync()
    {
        if (_demo) { Current = State.Stopped; return; }
        if (Current == State.Running && _cts is not null)
        {
            Current = State.Stopping;
            _cts.Cancel();
        }
        if (Current != State.Stopping) return;
        if (_task is not null) await Task.WhenAny(_task, Task.Delay(TimeSpan.FromSeconds(8)));
        if (Current == State.Stopping) Current = State.Stopped;
    }
}
