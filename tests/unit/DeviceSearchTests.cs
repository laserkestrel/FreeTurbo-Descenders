using DescendersBridge.Ant;
using DescendersBridge.Bluetooth;
using DescendersBridge.Gui;

namespace DescendersBridge;

/// <summary>
/// The bridge window finds and shows ANT+ and Bluetooth devices together. The pure rules: what a search says for each of the
/// four cases (stick + Bluetooth, Bluetooth only, stick only, neither), how a found row and a drop-down line name each kind, the status
/// strip's words for a Bluetooth trainer, and that choosing the same trainer the other way replaces the role. Then the real
/// <see cref="DeviceSearch"/> on a fake Bluetooth radio and a made-up (or missing) ANT+ stick. No stick, no radio, a temp folder only.
/// Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class DeviceSearchTests
{
    /// <summary>A radio that is there but cannot scan (Windows only says "switched off" when asked to).</summary>
    private sealed class DeadRadio : IBleRadio
    {
        private readonly string _why;
        public DeadRadio(string why) { _why = why; }
        public Task<BleAdapterInfo> AdapterAsync() => Task.FromResult(new BleAdapterInfo(true, true, true, null, "fake radio"));
        public Task<IReadOnlyList<BleAdvert>> ScanAsync(TimeSpan duration, CancellationToken ct) => throw new InvalidOperationException(_why);
        public Task<IBleConnection> ConnectAsync(ulong address, CancellationToken ct) => throw new InvalidOperationException("not used");
    }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Device search: one search for ANT+ and Bluetooth, on whatever the PC has");
        DeviceKey antTrainer = new(AntDeviceTypes.FitnessEquipment, 44226, 5), watch = new(AntDeviceTypes.HeartRate, 7000, 1);
        var trainerAdvert = new BleAdvert(0xC123456789AB, "KICKR CORE 1234", -60, 12, new[] { Ftms.Service, BleSensors.PowerService });
        var strapAdvert = new BleAdvert(0xC1234567AAAA, "HRM 55", -80, 9, new[] { BleSensors.HeartRateService });
        var meterAdvert = new BleAdvert(0xC1234567BBBB, "", -90, 4, new[] { BleSensors.PowerService });
        DeviceKey btTrainer = trainerAdvert.TrainerKey;

        // ---- the four cases -> the line shown ----
        check(SearchRules.Note(ant: true, bluetoothProblem: null) is null && SearchRules.Failure(true, null) is null, "stick + Bluetooth: both are searched, nothing to say");
        check(SearchRules.Note(ant: false, bluetoothProblem: null) == "No ANT+ stick found: searching Bluetooth only" && SearchRules.Failure(false, null) is null,
            "Bluetooth only: no error, a quiet line \"No ANT+ stick found: searching Bluetooth only\"");
        check(SearchRules.Note(ant: true, BleRules.NoBluetooth) == "This PC has no Bluetooth: searching ANT+ only" && SearchRules.Failure(true, BleRules.NoBluetooth) is null,
            "stick only: as before, with a quiet line \"This PC has no Bluetooth: searching ANT+ only\"");
        check(SearchRules.Note(ant: false, BleRules.NoBluetooth) is null && SearchRules.Failure(false, BleRules.NoBluetooth) == FriendlyText.NoRadio
            && FriendlyText.NoRadio.Advice.StartsWith("Plug in an ANT+ stick or switch on Bluetooth", StringComparison.Ordinal),
            "neither: one message naming both (\"Plug in an ANT+ stick or switch on Bluetooth\")");
        check(SearchRules.Note(true, BleRules.RadioOff) is { } off && off.StartsWith("Bluetooth is switched off", StringComparison.Ordinal) && off.EndsWith(": searching ANT+ only", StringComparison.Ordinal),
            "Bluetooth switched off, stick in: the quiet line says switched off, not \"no Bluetooth\"");
        check(SearchRules.Note(false, null, keyMissing: true) == SearchRules.NoKeyBluetoothOnly && SearchRules.Failure(false, BleRules.NoBluetooth, keyMissing: true) == FriendlyText.NoKey,
            "a build without the ANT+ key: Bluetooth is still searched; with no Bluetooth either, the message is the missing-key one (a stick would not help)");

        // ---- the row for each transport ----
        var antRow = new FoundDevice(antTrainer, 60, 4, 0.1, "195 watts", new DeviceIdentity(Manufacturers.Tacx));
        var btRow = FoundDevice.From(trainerAdvert)!;
        check(DeviceLabels.Transport(antTrainer) == "ANT+" && DeviceLabels.Transport(btTrainer) == "Bluetooth", "a found row says how it was found: \"ANT+\" or \"Bluetooth\"");
        check(antRow.Kind == "Trainer" && btRow.Kind == "Trainer" && !antRow.IsBluetooth && btRow.IsBluetooth && btRow.Key == btTrainer,
            "a trainer is \"Trainer\" either way (no protocol name), and the Bluetooth row is saved by its address");
        check(DeviceLabels.FoundMake(antTrainer, Manufacturers.Tacx, null) == "Tacx" && DeviceLabels.FoundNumber(antTrainer) == "44226"
            && DeviceLabels.FoundMake(btTrainer, null, btRow.Name) == "KICKR CORE 1234" && DeviceLabels.FoundNumber(btTrainer) == "",
            "Make and Number: an ANT+ device's brand and number; a Bluetooth device's advertised name, and no number");
        var noName = FoundDevice.From(meterAdvert)!;
        check(noName.Name is null && DeviceLabels.FoundMake(noName.Key, null, noName.Name) == "C1:23:45:67:BB:BB" && noName.Kind == "Power meter",
            "a Bluetooth device that advertises no name is shown by its address");
        check(antRow.Signal == "Strong" && btRow.Signal == "Strong" && FoundDevice.From(strapAdvert)!.Signal == "OK" && noName.Signal == "Weak"
            && SearchRules.BluetoothSignal(-70) == "Strong" && SearchRules.BluetoothSignal(-85) == "OK" && SearchRules.BluetoothSignal(-86) == "Weak",
            "Signal: the same three words for both kinds (a Bluetooth device's from its signal strength)");
        check(FoundDevice.From(new BleAdvert(0xA1B2C3D4, "Headphones", -40, 30, Array.Empty<Guid>())) is null, "a Bluetooth device that is no trainer or sensor is not listed");
        check(DeviceLabels.Choice(antTrainer, Manufacturers.Tacx, null) == "Tacx 44226 (ANT+)" && DeviceLabels.Choice(antTrainer, null, null) == "device 44226 (ANT+)"
            && DeviceLabels.Choice(btTrainer, null, "KICKR CORE 1234") == "KICKR CORE 1234 (Bluetooth)" && DeviceLabels.Choice(btTrainer, null, null) == "Bluetooth C1:23:45:67:89:AB",
            "a drop-down line (saved, found or used before) says its kind too: \"Tacx 44226 (ANT+)\" / \"KICKR CORE 1234 (Bluetooth)\"");

        // ---- a trainer that offers both appears twice; the newer choice replaces the older ----
        var heard = new[] { btRow, antRow, FoundDevice.From(strapAdvert)!, new FoundDevice(watch, 20, 2, 0.3, "118 bpm") };
        var forTrainer = DeviceChoiceRules.HeardFor(Role.Trainer, heard);
        check(forTrainer.SequenceEqual(new[] { antTrainer, btTrainer }), "the same trainer heard both ways is offered twice for the Trainer job, the ANT+ one first");
        check(DeviceChoiceRules.HeardFor(Role.Power, heard).SequenceEqual(new[] { antTrainer }) && DeviceChoiceRules.HeardFor(Role.Cadence, heard).SequenceEqual(new[] { antTrainer })
            && DeviceChoiceRules.HeardFor(Role.HeartRate, heard).SequenceEqual(new[] { watch, DeviceKey.Bluetooth(AntDeviceTypes.HeartRate, strapAdvert.Address) }),
            "a Bluetooth trainer is not offered as a power or cadence sensor of its own; both straps are offered for heart rate");
        check(btRow.Is(btTrainer) && !btRow.Is(antTrainer) && antRow.Is(antTrainer) && noName.Is(DeviceKey.Bluetooth(AntDeviceTypes.PowerMeter, meterAdvert.Address)),
            "a saved device counts as found only when the search heard it the same way");
        var prefs = new DevicePreferences();
        prefs.Set(Role.Trainer, antTrainer, Manufacturers.Tacx);
        var offered = DeviceChoiceRules.Offered(Role.Trainer, prefs.Get(Role.Trainer), forTrainer, prefs.KnownFor(Role.Trainer));
        check(offered.Select(o => o.Key).SequenceEqual(new[] { antTrainer, btTrainer }) && offered.All(o => o.Origin == ChoiceOrigin.Heard), "with the ANT+ one saved, the drop-down offers both");
        prefs.Set(Role.Trainer, btTrainer, name: btRow.Name);
        check(prefs.Get(Role.Trainer) == btTrainer && !prefs.NeedsAnt && prefs.NeedsBluetooth && prefs.Roles.Count == 1 && prefs.NameOf(btTrainer) == "KICKR CORE 1234",
            "choosing the same trainer by Bluetooth replaces the ANT+ choice for the role: no stick is needed any more");
        check(prefs.KnownFor(Role.Trainer).SequenceEqual(new[] { antTrainer, btTrainer }), "and the ANT+ one stays on offer as used before");
        prefs.Set(Role.Trainer, antTrainer);
        check(prefs.Get(Role.Trainer) == antTrainer && prefs.NeedsAnt && !prefs.NeedsBluetooth && prefs.ManufacturerOf(antTrainer) == Manufacturers.Tacx, "and back again the other way");
        check(!ConnectRules.AnyDevice(new DevicePreferences()) && DeviceChoiceRules.Offered(Role.Trainer, null, Array.Empty<DeviceKey>(), new DevicePreferences().KnownFor(Role.Trainer)).Count == 0,
            "every job set to none: nothing is offered and nothing breaks");

        // ---- the status strip for a Bluetooth trainer ----
        const RideController.State Stopped = RideController.State.Stopped, Running = RideController.State.Running;
        StripLine Bt(RideController.State state, Health h, bool was = false, bool stick = false, bool searching = false) =>
            StripRules.Trainer(state, stick, searching, h, 0, null, "KICKR CORE 1234", was);
        check(Bt(Running, Health.Searching) == new StripLine("Trainer: connecting over Bluetooth...", Health.Searching, false), "Bluetooth trainer, not answered yet: connecting");
        check(Bt(Running, Health.Good, was: true) == new StripLine("Trainer: Connected (Bluetooth)", Health.Good, false)
            && Bt(Running, Health.Weak, was: true) == new StripLine("Trainer: Weak signal (Bluetooth)", Health.Weak, false), "Bluetooth trainer: connected, and a weak signal");
        check(Bt(Running, Health.Searching, was: true) == new StripLine("Trainer: Bluetooth connection lost, reconnecting", Health.Weak, false)
            && Bt(Running, Health.Lost, was: true).Text == "Trainer: Bluetooth connection lost, reconnecting", "Bluetooth trainer gone after it was connected: lost, reconnecting (a warning light)");
        check(Bt(Running, Health.Good, was: true, stick: true).Text == "Trainer: Connected (Bluetooth)",
            "a stick lost under a Bluetooth trainer (an ANT+ strap is on it) does not put stick words on the trainer");
        check(Bt(Stopped, Health.NotChosen).Text == "Trainer: KICKR CORE 1234, not connected" && Bt(Stopped, Health.NotChosen, searching: true).Text == "Trainer: paused during the device search",
            "Bluetooth trainer, not connected: its name; paused during a search");
        var btTexts = new List<string>();
        foreach (var state in Enum.GetValues<RideController.State>())
            foreach (bool stick in new[] { false, true })
                foreach (bool was in new[] { false, true })
                    foreach (var h in Enum.GetValues<Health>())
                        btTexts.Add(Bt(state, h, was, stick).Text);
        var badTexts = btTexts.Distinct().Where(t => !t.StartsWith("Trainer: ", StringComparison.Ordinal) || t.Length > 60 || t.Contains("stick", StringComparison.OrdinalIgnoreCase)).ToList();
        check(badTexts.Count == 0, "every strip text of a Bluetooth trainer: at most 60 characters, never the word \"stick\"" + (badTexts.Count > 0 ? " (" + string.Join(" | ", badTexts) + ")" : ""));
        check(StripRules.Trainer(Running, true, false, Health.Good, 44226).Text.Contains("ANT+ stick lost") && StripRules.Trainer(Running, false, false, Health.Good, 44226).Text == "Trainer: Connected",
            "an ANT+ trainer keeps its words, the stick ones included");

        // ---- the help ----
        var both = FriendlyText.TrainerNotHeard(ant: true, bluetooth: true);
        var bluetoothOnly = FriendlyText.TrainerNotHeard(ant: false, bluetooth: true);
        check(FriendlyText.TrainerNotHeard(ant: true, bluetooth: false) == FriendlyText.TrainerNotHeardInSearch && !FriendlyText.TrainerNotHeardInSearch.Text.Contains("Bluetooth"),
            "no trainer in an ANT+ only search: the help is as before");
        check(new[] { both, bluetoothOnly, FriendlyText.BluetoothTrainerNotFound }.All(m => m.Advice.Contains("one app at a time") && m.Advice.Contains("phone app") && m.Advice.Contains("any other training app")),
            "no trainer where Bluetooth was searched: a trainer talks Bluetooth to one app at a time, close the maker's phone app and any other training app");
        check(both.Advice.Contains("stick") && !bluetoothOnly.Advice.Contains("stick") && both.Title == FriendlyText.TrainerNotHeardInSearch.Title && both.Advice.Contains("Search again") && bluetoothOnly.Advice.Contains("Search again"),
            "the stick advice only when ANT+ was searched; then search again");
        check(FriendlyText.TrainerMissing(bluetooth: false) == FriendlyText.TrainerNotFound && FriendlyText.TrainerMissing(bluetooth: true) == FriendlyText.BluetoothTrainerNotFound
            && !FriendlyText.BluetoothTrainerNotFound.Text.Contains("stick"), "a Bluetooth trainer not found during a ride: no stick advice");
        check(Enum.GetValues<Health>().All(h => !HealthRules.Advice(h, bluetooth: true).Contains("stick", StringComparison.OrdinalIgnoreCase)) && HealthRules.Advice(Health.Weak).Contains("USB stick"),
            "a Bluetooth device's light never talks about the USB stick; an ANT+ device's still does");

        // ---- short scans add up ----
        var merged = BleRules.Merge(new[] { new BleAdvert(1, "", -70, 3, new[] { Ftms.Service }), strapAdvert },
            new[] { new BleAdvert(1, "Trainer one", -55, 4, new[] { BleSensors.PowerService }), new BleAdvert(1, "", -50, 1, Array.Empty<Guid>()), meterAdvert });
        check(merged.Count == 3 && merged[0] is { Name: "Trainer one", RssiDbm: -50, Count: 8 } m0 && m0.Services.Count == 2 && merged[1] == strapAdvert && merged[2] == meterAdvert,
            "a device heard in several short scans is one entry: its name kept once heard, the newest signal, every service, the counts added up");

        // ---- the demo ----
        check(FoundDevice.From(DemoLiveSource.BluetoothTrainer) is { } demoTrainer && demoTrainer.KeyFor(Role.Trainer) is not null && demoTrainer.KeyFor(Role.Power) is null
            && FoundDevice.From(DemoLiveSource.BluetoothPower)?.KeyFor(Role.Power) == DemoLiveSource.PowerMeter && DemoLiveSource.PowerMeter.IsBluetooth,
            "gui --demo: the trainer is found over Bluetooth too, and the power meter it shows as saved is a Bluetooth one the search finds");

        Searches(check, antTrainer, watch);
    }

    /// <summary>The real <see cref="DeviceSearch"/> (0.3 s instead of 15) on a fake radio and a made-up or missing stick.</summary>
    private static void Searches(Action<bool, string> check, DeviceKey antTrainer, DeviceKey watch)
    {
        Console.WriteLine("Device search: the real search on a fake radio and a made-up stick");
        var realOut = Console.Out;
        (DeviceSearch Search, string Log) Run(Func<AntScan> openAnt, IBleRadio radio)
        {
            var log = new StringWriter();
            Console.SetOut(log);
            try
            {
                var s = new DeviceSearch(demo: false, openAnt, () => radio, seconds: 0.3);
                s.RunAsync().GetAwaiter().GetResult();
                return (s, log.ToString());
            }
            finally { Console.SetOut(realOut); }
        }
        static AntScan NoStick() => throw new AntStickException("ANT+ stick not available: test", new Exception("ANTLibrary Exception: No ANT devices detected"));
        int closed = 0;
        AntScan Stick() => new(() => new[] { new DeviceTracker(watch), new DeviceTracker(antTrainer) }, () => closed++);
        var noBluetooth = new FakeFtmsRadio { Adapter = new BleAdapterInfo(false) };

        var (both, _) = Run(Stick, new FakeFtmsRadio());
        var rows = both.Devices;
        check(both.Failure is null && both.Note is null && both.SearchedAnt && both.SearchedBluetooth && !both.Running && closed == 1, "stick + Bluetooth: both searched, no line, the stick is closed again afterwards");
        check(rows.Count == 3 && rows[0].Key == antTrainer && rows[1].Key == watch && rows[2] is { IsBluetooth: true, Name: FakeFtmsRadio.TrainerName } && rows[2].KeyFor(Role.Trainer) is not null,
            $"its list: the ANT+ devices, then the Bluetooth trainer by its name; the headphones are left out (got {string.Join(", ", rows.Select(r => r.Key))})");

        var (btOnly, btLog) = Run(NoStick, new FakeFtmsRadio());
        check(btOnly.Failure is null && btOnly.Note == SearchRules.BluetoothOnly && !btOnly.SearchedAnt && btOnly.SearchedBluetooth && btOnly.Devices.Count == 1 && btOnly.Devices[0].IsBluetooth,
            "no stick is no error by itself: the Bluetooth trainer is found, with the quiet line");
        check(btLog.Contains("ANT+ not searched: " + FriendlyText.NoStickLog) && !btLog.Contains("Exception") && !btLog.Contains("Search failed"),
            "and the log has one plain line about the stick, no exception");
        var (nothing, _) = Run(NoStick, new FakeFtmsRadio { InRange = false });
        check(nothing.Failure is null && nothing.Note == SearchRules.BluetoothOnly && nothing.Devices.Count == 0,
            "Bluetooth only and nothing in range (the build VM): no error, the quiet line, an empty list");

        closed = 0;
        var (antOnly, antLog) = Run(Stick, noBluetooth);
        check(antOnly.Failure is null && antOnly.Note == "This PC has no Bluetooth: searching ANT+ only" && antOnly.SearchedAnt && !antOnly.SearchedBluetooth && antOnly.Devices.Count == 2 && closed == 1
            && antLog.Contains("Bluetooth not searched: This PC has no Bluetooth"), "stick only: the ANT+ devices as before, with the quiet line");

        var (neither, neitherLog) = Run(NoStick, noBluetooth);
        check(neither.Failure == FriendlyText.NoRadio && neither.Note is null && neither.Devices.Count == 0 && !neither.Running && neitherLog.Contains("Search failed: " + FriendlyText.NoRadio.Title),
            "neither: the search stops at once with the one message naming both");

        var (off, _) = Run(Stick, new DeadRadio(BleRules.RadioOff));
        check(off.Failure is null && off.Note is { } offNote && offNote.StartsWith("Bluetooth is switched off", StringComparison.Ordinal) && off.Devices.Count == 2 && !off.SearchedBluetooth,
            "Bluetooth switched off (Windows says so only when the scan starts), stick in: ANT+ alone, and the line says why");
        var (offNoStick, _) = Run(NoStick, new DeadRadio(BleRules.RadioOff));
        check(offNoStick.Failure == FriendlyText.NoRadio, "Bluetooth switched off and no stick: the message naming both");
        var (odd, oddLog) = Run(Stick, new DeadRadio("Windows stopped the Bluetooth scan (OtherError)"));
        check(odd.Note == SearchRules.BluetoothFailed + SearchRules.AntOnly && oddLog.Contains("OtherError"), "a scan Windows stops for another reason: plain words on screen, the reason in the log");
        var (noKey, _) = Run(() => throw new InvalidOperationException("No ANT+ network key: this build has none"), new FakeFtmsRadio());
        check(noKey.Failure is null && noKey.Note == SearchRules.NoKeyBluetoothOnly && noKey.Devices.Count == 1, "a build without the ANT+ key still searches Bluetooth");

        Rework(check, Run, NoStick);
    }

    /// <summary>A radio whose devices leave the Fitness Machine Service out of what they advertise: the search has to ask them.</summary>
    private sealed class QuietRadio : IBleRadio
    {
        public const ulong Trainer = 0xC1234567C001, Pedals = 0xC1234567C002, Maker = 0xC1234567C003, Strap = 0xC1234567C004;
        public readonly List<ulong> Asked = new();

        public Task<BleAdapterInfo> AdapterAsync() => Task.FromResult(new BleAdapterInfo(true, true, true, null, "fake radio"));

        public async Task<IReadOnlyList<BleAdvert>> ScanAsync(TimeSpan duration, CancellationToken ct)
        {
            await Task.Delay(duration, ct);
            return new BleAdvert[]
            {
                new(Trainer, "Quiet trainer", -55, 9, new[] { BleSensors.PowerService, BleSensors.CscService }),
                new(Pedals, "Power pedals", -60, 9, new[] { BleSensors.PowerService }),
                new(Maker, "Maker trainer", -65, 9, new[] { BleRules.TacxTrainerService }),
                new(Strap, "Strap", -70, 9, new[] { BleSensors.HeartRateService }),
                new(0xA1B2C3D4, "Headphones", -40, 30, new[] { Ftms.Uuid(0x110B) }),
            };
        }

        public Task<IBleConnection> ConnectAsync(ulong address, CancellationToken ct)
        {
            lock (Asked) Asked.Add(address);
            if (address == Maker) throw new InvalidOperationException("not heard (is it switched on and awake?)");
            return Task.FromResult<IBleConnection>(new Answer(address == Trainer));
        }

        private sealed class Answer : IBleConnection
        {
            private readonly bool _ftms;
            public Answer(bool ftms) { _ftms = ftms; }
            public bool Connected => true;
            public Task<byte[]?> ReadAsync(Guid service, Guid characteristic, CancellationToken ct) =>
                Task.FromResult(_ftms && service == Ftms.Service && characteristic == Ftms.FeatureChar ? new byte[8] : null);
            public Task SubscribeAsync(Guid service, Guid characteristic, bool indicate, Action<byte[]> onValue, CancellationToken ct) => throw new InvalidOperationException("not used");
            public Task<bool> WriteAsync(Guid service, Guid characteristic, byte[] value, CancellationToken ct) => Task.FromResult(false);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The rework after the garage test: a trainer that does not advertise the Fitness Machine Service is still found as a trainer (a
    /// Tacx Neo 2T was not), and no stick on a PC with Bluetooth is not the red "stick not found" error.
    /// </summary>
    private static void Rework(Action<bool, string> check, Func<Func<AntScan>, IBleRadio, (DeviceSearch Search, string Log)> run, Func<AntScan> noStick)
    {
        Console.WriteLine("Device search: a trainer that does not advertise as one, and no stick on a PC with Bluetooth");
        var tacx = new BleAdvert(0xC1234567D001, "Tacx Neo 2T 12345", -60, 5, new[] { BleRules.TacxTrainerService, BleSensors.PowerService });
        check(BleRules.IsTrainer(tacx) && BleRules.Kind(tacx) == "Trainer" && BleRules.KeyFor(Role.Trainer, tacx) == tacx.TrainerKey
            && BleRules.KeyFor(Role.Power, tacx) is null && BleRules.KeyFor(Role.Cadence, tacx) is null && BleRules.Trainers(new[] { tacx }).Count == 1,
            "a trainer that advertises its maker's trainer service (Tacx) and not 0x1826 is a trainer, and the trainer only");
        var pedals = new BleAdvert(1, "Pedals", -60, 5, new[] { BleSensors.PowerService });
        check(BleRules.WorthAsking(pedals) && BleRules.WorthAsking(tacx) && !BleRules.WorthAsking(new BleAdvert(2, "Strap", -60, 5, new[] { BleSensors.HeartRateService }))
            && !BleRules.WorthAsking(new BleAdvert(3, "T", -60, 5, new[] { Ftms.Service, BleSensors.PowerService })) && !BleRules.WorthAsking(new BleAdvert(4, "Headphones", -60, 5, Array.Empty<Guid>())),
            "worth asking whether it is a trainer: a device that advertises power, cadence or a maker's trainer service; not a strap, not one that says 0x1826 already");
        check(!BleRules.IsTrainer(pedals) && BleRules.IsTrainer(BleRules.WithFtms(pedals)) && BleRules.KeyFor(Role.Power, BleRules.WithFtms(pedals)) is null
            && BleRules.WithFtms(pedals).Services.Count == 2 && BleRules.WithFtms(BleRules.WithFtms(pedals)).Services.Count == 2,
            "a device that answered \"I have the Fitness Machine Service\" is a trainer from then on");
        check(BleRules.ServiceName(Ftms.Service) == "1826" && BleRules.ServiceName(BleRules.TacxTrainerService) == "6e40fec1-b5a3-f393-e0a9-e50e24dcca9e"
            && BleRules.DescribeUnlisted(new BleAdvert(0xA1B2C3D4, "Headphones", -40, 30, new[] { Ftms.Uuid(0x110B) })).Contains("Headphones") && BleRules.DescribeUnlisted(pedals).EndsWith("services: 1818", StringComparison.Ordinal),
            "the log names a service by its short number, a maker's own by its whole UUID");

        var radio = new QuietRadio();
        var (s, log) = run(noStick, radio);
        var rows = s.Devices;
        FoundDevice? Row(ulong address) => rows.FirstOrDefault(r => r.Key.BluetoothAddress == address);
        check(s.Failure is null && Row(QuietRadio.Trainer) is { Kind: "Trainer" } quiet && quiet.KeyFor(Role.Trainer) is not null && quiet.KeyFor(Role.Power) is null,
            "the search asks a device that advertises only power and cadence, and lists it as a trainer when it has the Fitness Machine Service");
        check(Row(QuietRadio.Pedals) is { Kind: "Power meter" } p && p.KeyFor(Role.Trainer) is null && p.KeyFor(Role.Power) is not null, "real power pedals answer no and stay a power meter");
        check(Row(QuietRadio.Maker) is { Kind: "Trainer" } && log.Contains("Maker trainer") && log.Contains("no answer"), "a maker's trainer that does not answer the question is still listed as a trainer");
        check(radio.Asked.Count == 3 && !radio.Asked.Contains(QuietRadio.Strap) && rows.Count == 4 && rows[0].Key.BluetoothAddress == QuietRadio.Maker && rows[1].Key.BluetoothAddress == QuietRadio.Trainer,
            $"three devices were asked (not the strap, not the headphones); the trainers are listed first (asked {radio.Asked.Count}, {rows.Count} rows)");
        check(log.Contains("whether it is a trainer: yes") && log.Contains("whether it is a trainer: no (no Fitness Machine Service)")
            && log.Contains("Other Bluetooth devices heard") && log.Contains("Headphones") && log.Contains("services: 110B"),
            "the log says what each device answered, and names the Bluetooth devices heard and not listed with what they advertise");

        // ---- no stick on a PC with Bluetooth ----
        check(FriendlyText.NoStickFor(bluetoothUsable: false) == FriendlyText.NoStick && FriendlyText.IsError(FriendlyText.NoStick), "no stick and no Bluetooth: the red \"stick not found\" message, as before");
        var soft = FriendlyText.NoStickFor(bluetoothUsable: true);
        check(soft == FriendlyText.NoStickBluetoothHere && !FriendlyText.IsError(soft) && soft.Advice.Contains("My devices") && soft.Advice.Contains("(Bluetooth)") && soft.Advice.Contains("Try again")
            && !soft.Title.Contains("not found"), "saved ANT+ devices, no stick, a PC with Bluetooth: a note (not red) that says how to use Bluetooth instead");
        check(new[] { FriendlyText.NoRadio, FriendlyText.NoKey, FriendlyText.TrainerNotFound, FriendlyText.AlreadyRunning }.All(FriendlyText.IsError), "every other failure is still an error");
        const RideController.State Failed = RideController.State.Failed;
        var strip = StripRules.Trainer(Failed, false, false, Health.NotChosen, 44226, noStickOnly: true);
        check(strip == new StripLine("Trainer: not connected (no ANT+ stick plugged in)", Health.Searching, true) && strip.Text.Length <= 60
            && StripRules.Trainer(Failed, false, false, Health.NotChosen, 44226) == new StripLine("Trainer: stopped, see the message above", Health.Lost, false),
            "the status strip then has no red light: \"not connected (no ANT+ stick plugged in)\"; any other failure keeps the red one");

        // ---- a Bluetooth trainer, ANT+ sensors saved from before, no stick ----
        DeviceKey strap = new(AntDeviceTypes.HeartRate, 7000, 1), btTrainer = DeviceKey.Bluetooth(AntDeviceTypes.FitnessEquipment, FakeFtmsRadio.TrainerAddress);
        var prefs = new DevicePreferences();
        prefs.Set(Role.Trainer, btTrainer, name: FakeFtmsRadio.TrainerName);
        prefs.Set(Role.HeartRate, strap);
        var said = new List<string>();
        static ITrainerSource Gone(DevicePreferences _) => throw new AntStickException("ANT+ stick not available: test", null);
        using (var src = Ride.OpenSourceAsync(prefs, Gone, () => new FakeFtmsRadio(), said.Add).GetAwaiter().GetResult())
            check(src.BluetoothSide is not null && src.AntSide is null && src.AntProblem == Ride.NoStickForSensors && !src.LinkLost && src.HeartRateBpm is null
                && src.Link(Role.HeartRate) is { NotFound: true } && src.HeartRateDiagnosis == Ride.NoStickForSensors
                && said.Count(l => l.StartsWith("WARNING: No ANT+ USB stick is plugged in", StringComparison.Ordinal) && l.Contains("HeartRate")) == 1,
                "a Bluetooth trainer with an ANT+ strap saved and no stick: the ride goes on without the strap, one WARNING, no stick error");
        var antTrainerPrefs = new DevicePreferences();
        antTrainerPrefs.Set(Role.Trainer, new DeviceKey(AntDeviceTypes.FitnessEquipment, 44226, 5));
        bool threw = false;
        try { Ride.OpenSourceAsync(antTrainerPrefs, Gone, () => new FakeFtmsRadio(), _ => { }).GetAwaiter().GetResult().Dispose(); }
        catch (AntStickException) { threw = true; }
        check(threw, "an ANT+ trainer without its stick is still no ride");
    }
}
