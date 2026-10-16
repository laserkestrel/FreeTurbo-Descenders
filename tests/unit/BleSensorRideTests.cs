using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DescendersBridge.Ant;
using DescendersBridge.Bluetooth;

namespace DescendersBridge;

/// <summary>
/// A fake Bluetooth sensor on the seam: one service with one notifying characteristic, sending <see cref="Value"/> every
/// 50 ms while connected. <see cref="Drop"/> is the sensor going out of range (or its battery dying); it keeps counting its crank
/// revolutions meanwhile, like a real one.
/// </summary>
internal sealed class FakeBleSensor
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Link? _link;
    private double _crankAt, _crankPart;
    private int _revs, _eventTime;

    public readonly ulong Address;
    public readonly string Name;
    public readonly Guid Service, Characteristic;
    public volatile bool InRange = true;
    public int Connects, Disconnects;

    /// <summary>The notification to send now; null = the sensor is connected and sends nothing.</summary>
    public Func<FakeBleSensor, byte[]?> Value = _ => null;
    /// <summary>How fast the fake crank turns (0 = stopped: the counters stand still).</summary>
    public double CrankRpm;

    public FakeBleSensor(ulong address, string name, Guid service, Guid characteristic)
    {
        Address = address; Name = name; Service = service; Characteristic = characteristic;
    }

    public static FakeBleSensor HeartRate(ulong address, string name, int bpm) =>
        new(address, name, BleSensors.HeartRateService, BleSensors.HeartRateMeasurementChar) { Value = _ => HeartRateBytes(bpm, contact: true) };

    /// <summary>Flags 0x06 / 0x04: contact supported, detected or not; an 8-bit value.</summary>
    public static byte[] HeartRateBytes(int bpm, bool contact) => new[] { (byte)(contact ? 0x06 : 0x04), (byte)bpm };

    /// <summary>The cumulative crank revolutions and last crank event time (1/1024 s) of a crank turning at <see cref="CrankRpm"/>.</summary>
    public (ushort Revolutions, ushort EventTime) Crank()
    {
        lock (_gate)
        {
            double now = _clock.Elapsed.TotalSeconds, rate = Volatile.Read(ref CrankRpm) / 60.0;
            _crankPart += (now - _crankAt) * rate;
            _crankAt = now;
            if (_crankPart >= 1.0)
            {
                int whole = (int)_crankPart;
                _crankPart -= whole;
                _revs += whole;
                _eventTime = (int)Math.Round((now - _crankPart / rate) * 1024.0);   // when the last whole revolution was completed
            }
            return ((ushort)_revs, (ushort)_eventTime);
        }
    }

    /// <summary>Cycling Power Measurement with crank revolution data (flags 0x0020).</summary>
    public byte[] PowerBytes(int watts)
    {
        var (revs, time) = Crank();
        return new byte[] { 0x20, 0x00, (byte)(watts & 0xFF), (byte)((watts >> 8) & 0xFF), (byte)(revs & 0xFF), (byte)(revs >> 8), (byte)(time & 0xFF), (byte)(time >> 8) };
    }

    /// <summary>CSC Measurement with wheel data (stepped over by the reader) and crank data (flags 0x03).</summary>
    public byte[] CscBytes()
    {
        var (revs, time) = Crank();
        return new byte[] { 0x03, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, (byte)(revs & 0xFF), (byte)(revs >> 8), (byte)(time & 0xFF), (byte)(time >> 8) };
    }

    public IBleConnection Connect()
    {
        var l = new Link(this);
        lock (_gate) _link = l;
        Interlocked.Increment(ref Connects);
        return l;
    }

    /// <summary>The sensor goes away: the connection in use is dead.</summary>
    public void Drop() { Link? l; lock (_gate) l = _link; l?.Lose(); }

    private sealed class Link : IBleConnection
    {
        private readonly FakeBleSensor _s;
        private readonly Timer _data;
        private Action<byte[]>? _to;
        private volatile bool _up = true;

        public Link(FakeBleSensor s)
        {
            _s = s;
            _data = new Timer(_ =>
            {
                var to = Volatile.Read(ref _to);
                if (_up && to is not null && _s.Value(_s) is { } v) to(v);
            }, null, 20, 50);
        }

        public bool Connected => _up;
        public void Lose() { if (_up) { _up = false; Interlocked.Increment(ref _s.Disconnects); } }

        public Task<byte[]?> ReadAsync(Guid service, Guid characteristic, CancellationToken ct) => Task.FromResult<byte[]?>(null);

        public Task SubscribeAsync(Guid service, Guid characteristic, bool indicate, Action<byte[]> onValue, CancellationToken ct)
        {
            if (!_up) throw new InvalidOperationException("the device did not answer (Unreachable)");
            if (service != _s.Service || characteristic != _s.Characteristic) throw new InvalidOperationException("this device does not have the service asked for");
            if (indicate) throw new InvalidOperationException("a measurement notifies");
            Volatile.Write(ref _to, onValue);
            return Task.CompletedTask;
        }

        public Task<bool> WriteAsync(Guid service, Guid characteristic, byte[] value, CancellationToken ct) => Task.FromResult(false);

        public ValueTask DisposeAsync()
        {
            Lose();
            _data.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The fake radio of the sensor tests: the fake FTMS trainer and any number of fake sensors, each in range or not.</summary>
internal sealed class FakeSensorRadio : IBleRadio
{
    public BleAdapterInfo Adapter = new(true, true, true, true, "fake radio");
    public readonly FakeFtmsTrainer Trainer = new();
    public readonly List<FakeBleSensor> Sensors = new();
    public int Attempts;

    public Task<BleAdapterInfo> AdapterAsync() => Task.FromResult(Adapter);

    public async Task<IReadOnlyList<BleAdvert>> ScanAsync(TimeSpan duration, CancellationToken ct)
    {
        await Task.Delay(duration, ct);
        var list = new List<BleAdvert>
        {
            new(0x0000A1B2C3D4, "Headphones", -48, 31, Array.Empty<Guid>()),
            new(FakeFtmsRadio.TrainerAddress, FakeFtmsRadio.TrainerName, -61, 12, new[] { Ftms.Service }),
        };
        int rssi = -50;
        foreach (var s in Sensors) if (s.InRange) list.Add(new(s.Address, s.Name, rssi -= 5, 8, new[] { s.Service }));
        return list;
    }

    public Task<IBleConnection> ConnectAsync(ulong address, CancellationToken ct)
    {
        Interlocked.Increment(ref Attempts);
        if (address == FakeFtmsRadio.TrainerAddress) return Task.FromResult(Trainer.Connect());
        if (Sensors.Find(s => s.Address == address) is { InRange: true } sensor) return Task.FromResult(sensor.Connect());
        throw new InvalidOperationException("not heard (is it switched on and awake?)");
    }
}

/// <summary>
/// Fake Bluetooth sensors on the seam, read through the real <see cref="RideSource"/> and <see cref="BleSensorSession"/>.
/// The real <see cref="RideLoop"/> rides a Bluetooth trainer with a Bluetooth heart-rate strap: the strap loses contact, drops out and
/// comes back, and the ride never notices more than an absent heart rate. Then the one rule for which reading wins (a chosen power
/// meter / cadence sensor against the trainer's own, on either transport), and a PC with no Bluetooth. No radio, no stick, no game: a
/// real <see cref="ModLink"/> on ephemeral UDP ports with the test as the game. Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class BleSensorRideTests
{
    private const ulong StrapAddress = 0x0000A1A1A1A1, MeterAddress = 0x0000C3C3C3C3, CadenceAddress = 0x0000B2B2B2B2;
    private static readonly DeviceKey Kickr = DeviceKey.Bluetooth(AntDeviceTypes.FitnessEquipment, FakeFtmsRadio.TrainerAddress),
        Strap = DeviceKey.Bluetooth(AntDeviceTypes.HeartRate, StrapAddress), Meter = DeviceKey.Bluetooth(AntDeviceTypes.PowerMeter, MeterAddress),
        Cadence = DeviceKey.Bluetooth(AntDeviceTypes.BikeCadence, CadenceAddress), AntTrainer = new(17, 44226, 5);

    private static bool Wait(Func<bool> until, double seconds = 5)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds) { if (until()) return true; Thread.Sleep(5); }
        return until();
    }

    private static bool Near(double? a, double b, double within = 0.005) => a is { } x && Math.Abs(x - b) < within;

    public static void Run(Action<bool, string> check)
    {
        RideChecks(check);
        WhichReadingWins(check);
        NoRadio(check);
    }

    private static void RideChecks(Action<bool, string> check)
    {
        Console.WriteLine("Bluetooth sensors: the real ride loop rides a Bluetooth trainer with a Bluetooth heart-rate strap");
        try
        {
            using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var mod = new ModLink(telemetryPort: 0, commandPort: ((IPEndPoint)game.Client.LocalEndPoint!).Port);
            var radio = new FakeSensorRadio();
            var t = radio.Trainer;
            var strap = FakeBleSensor.HeartRate(StrapAddress, "FAKE HRM", 142);
            radio.Sensors.Add(strap);
            var log = new List<string>();
            void Log(string s) { lock (log) log.Add(s); }
            bool Logged(string part) { lock (log) return log.Exists(l => l.Contains(part)); }

            var found = Setup.BluetoothDevicesAsync(radio, 0, Log).GetAwaiter().GetResult();
            check(found.Count == 2 && found[0].Name == FakeFtmsRadio.TrainerName && found[1].Name == "FAKE HRM" && Logged("1 trainer(s) and 1 sensor(s)"),
                "the search finds the trainer and the strap by the services they advertise (the headphones are left out)");

            var prefs = new DevicePreferences();
            prefs.Set(Role.Trainer, Kickr, name: FakeFtmsRadio.TrainerName);
            prefs.Set(Role.HeartRate, Strap, name: "FAKE HRM");
            using var src = Ride.OpenSourceAsync(prefs, _ => throw new AntStickException("the stick must not be opened", null), () => radio, Log, bluetoothRetrySeconds: 0.1).GetAwaiter().GetResult();
            check(src.AntSide is null && src.BluetoothSide is not null && src.Sensor(Role.HeartRate) is { Feeds: BleFeed.HeartRate } && src.BluetoothSensors.Count() == 1,
                "only Bluetooth devices chosen: no stick, no key; the strap has its own connection");

            double gradient = 0.05;
            using var gameStop = new CancellationTokenSource();
            var gameTask = Task.Run(async () =>
            {
                while (!gameStop.IsCancellationRequested)
                {
                    byte[] b = Encoding.UTF8.GetBytes("{\"type\":\"telemetry\",\"playerFound\":true,\"riding\":true,\"fitness\":{\"active\":true,\"roadGradient\":"
                        + Volatile.Read(ref gradient).ToString("0.###", CultureInfo.InvariantCulture) + "}}");
                    try { game.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, mod.ListenPort)); } catch { return; }
                    try { await Task.Delay(100, gameStop.Token); } catch { return; }
                }
            });

            var cfg = new ResistanceConfig { WriteIntervalMs = 250 };
            var training = new TrainingConfig { TrainerDifficulty = 1 };
            var loop = new RideLoop(src, mod, cfg, null, training, new Session(SessionConfig.From(training, cfg), () => 200, Log));
            using var cts = new CancellationTokenSource();
            Task run = Task.Run(() => loop.RunAsync(cts.Token));

            check(src.HeartRateBpm is null && src.HeartRateDiagnosis == "Bluetooth sensor not found yet" && src.Link(Role.HeartRate) is { Messages: 0, NotFound: false },
                "before the strap is found: no heart rate (absent is never zero), the light is searching");
            src.StartBluetooth();
            check(Wait(() => src.TrainerPaired && src.PowerW == 150) && Wait(() => src.HeartRateBpm == 142), "trainer and strap connect: 150 W from the trainer, 142 bpm from the strap");
            check(src.HeartRateDiagnosis == "" && src.Link(Role.HeartRate) is { Messages: > 0, NotFound: false } li && li.Key == Strap && src.Link(Role.Trainer)?.Key == Kickr && src.Link(Role.Power)?.Key == Kickr,
                "the window's lights: heart rate is the strap, trainer and power are the trainer");
            check(Wait(() => Near(t.GradePercent, 5.0)), "the road's 5 % reaches the trainer");

            strap.Value = _ => new byte[] { 0x01, 0x2C, 0x01 };
            check(Wait(() => src.HeartRateBpm == 300), "a 16-bit heart rate value is read");
            strap.Value = _ => FakeBleSensor.HeartRateBytes(0, contact: false);
            check(Wait(() => src.HeartRateBpm is null) && src.HeartRateDiagnosis == "connected, NO CONTACT: check the strap" && src.Sensor(Role.HeartRate)!.Connected,
                "the strap reports no skin contact: no heart rate, and the status line says why");
            strap.Value = _ => FakeBleSensor.HeartRateBytes(142, contact: true);
            check(Wait(() => src.HeartRateBpm == 142), "contact again: 142 bpm");

            // the strap drops out mid-ride
            strap.InRange = false;
            strap.Drop();
            check(Wait(() => src.HeartRateBpm is null, 1), "the strap drops out: its heart rate is absent at once (not zero, not the last value held)");
            int attempts = radio.Attempts;
            check(Wait(() => radio.Attempts >= attempts + 2 && src.Link(Role.HeartRate) is { Messages: 0, NotFound: true }) && src.HeartRateDiagnosis == "Bluetooth sensor lost: reconnecting"
                && Logged("heart-rate sensor lost"), "it is looked for again and again by itself; the light says not found, the log says lost");
            Volatile.Write(ref gradient, 0.08);
            check(Wait(() => Near(t.GradePercent, 8.0)) && !run.IsCompleted && src.TrainerPaired && src.PowerW == 150 && !src.LinkLost && t.Disconnects == 0,
                "the ride carries on meanwhile: the trainer stays connected, its power is read and the road's 8 % is written");
            strap.Value = _ => FakeBleSensor.HeartRateBytes(151, contact: true);
            strap.InRange = true;
            check(Wait(() => src.HeartRateBpm == 151) && src.Sensor(Role.HeartRate)!.Connections == 2 && Logged("heart-rate sensor back after") && src.Link(Role.HeartRate) is { NotFound: false },
                "back in range: reconnected under the running ride, 151 bpm");
            check(ReferenceEquals(RideLoop.Current, loop) && radio.Trainer.Count(Ftms.OpRequestControl) == 1, "same loop, and the trainer was never touched by it (control requested once)");

            cts.Cancel();
            check(run.Wait(5000), "Stop ends the loop");
            Ride.ReleaseAsync(src, Log).GetAwaiter().GetResult();
            check(Logged("Resistance released."), "the end of the ride releases the trainer as before");
            src.Dispose();
            check(strap.Disconnects == 2 && t.Disconnects == 1 && src.HeartRateBpm is null, "then the strap and the trainer are disconnected");
            check(!Logged("ANT+") && !Logged("stick"), "nothing about ANT+ or a stick was said in the whole ride");
            gameStop.Cancel();
            gameTask.Wait(1000);
        }
        catch (SocketException ex) { check(false, "could not open a UDP port for the test: " + ex.Message); }
    }

    /// <summary>One rule for both transports: a role's own chosen device, and only that device; none chosen = the trainer's own.</summary>
    private static void WhichReadingWins(Action<bool, string> check)
    {
        Console.WriteLine("Bluetooth sensors: a chosen power meter / cadence sensor against the trainer's own reading");
        var log = new List<string>();
        void Log(string s) { lock (log) log.Add(s); }
        bool Logged(string part) { lock (log) return log.Exists(l => l.Contains(part)); }

        // an ANT+ trainer (200 W, 85 rpm; SimTrainer stands in for the stick's side) with a Bluetooth power meter for power AND cadence
        {
            var radio = new FakeSensorRadio();
            var meter = new FakeBleSensor(MeterAddress, "FAKE PM", BleSensors.PowerService, BleSensors.PowerMeasurementChar) { CrankRpm = 90, Value = s => s.PowerBytes(233) };
            radio.Sensors.Add(meter);
            var prefs = new DevicePreferences();
            prefs.Set(Role.Trainer, AntTrainer); prefs.Set(Role.Power, Meter, name: "FAKE PM"); prefs.Set(Role.Cadence, Meter);
            DevicePreferences? given = null;
            using var src = Ride.OpenSourceAsync(prefs, a => { given = a; return new SimTrainer(new SimOptions()); }, () => radio, Log, bluetoothRetrySeconds: 0.1).GetAwaiter().GetResult();
            check(given is not null && given.Get(Role.Trainer) == AntTrainer && given.Get(Role.Power) is null && src.BluetoothSide is null
                && ReferenceEquals(src.Sensor(Role.Power), src.Sensor(Role.Cadence)) && src.BluetoothSensors.Count() == 1 && src.Sensor(Role.Power)!.Feeds == BleFeed.Power,
                "a mix: the ANT+ side gets the trainer only; the power meter chosen for power and cadence is ONE connection");
            check(src.PowerW is null && src.CadenceRpm is null && src.TrainerPaired, "before the power meter is found: no power and no cadence (the trainer's 200 W is not used instead)");
            src.StartBluetooth();
            check(Wait(() => src.PowerW == 233) && Wait(() => Near(src.CadenceRpm, 90, 1.5)), "connected: the power meter's 233 W wins over the trainer's 200 W, and its crank data gives 90 rpm (not the trainer's 85)");
            check(meter.Connects == 1 && src.Link(Role.Power)?.Key == Meter && src.Link(Role.Cadence)?.Key == Meter && src.Link(Role.Trainer) is null,
                "one connection to it; the window's power and cadence lights are the power meter");
            meter.InRange = false;
            meter.Drop();
            check(Wait(() => src.PowerW is null && src.CadenceRpm is null, 1) && src.TrainerPaired, "the power meter drops out: power and cadence are absent, the same rule as an ANT+ power meter out of range");
            meter.InRange = true;
            check(Wait(() => src.PowerW == 233) && Wait(() => Near(src.CadenceRpm, 90, 1.5)) && Logged("power meter back after"), "and back by itself");
        }

        // a Bluetooth trainer (150 W, 85 rpm) with a Bluetooth cadence sensor; power has no device of its own
        {
            var radio = new FakeSensorRadio();
            var cadence = new FakeBleSensor(CadenceAddress, "FAKE CAD", BleSensors.CscService, BleSensors.CscMeasurementChar) { CrankRpm = 70, Value = s => s.CscBytes() };
            radio.Sensors.Add(cadence);
            var prefs = new DevicePreferences();
            prefs.Set(Role.Trainer, Kickr, name: FakeFtmsRadio.TrainerName); prefs.Set(Role.Cadence, Cadence, name: "FAKE CAD");
            using var src = Ride.OpenSourceAsync(prefs, _ => throw new AntStickException("the stick must not be opened", null), () => radio, Log, bluetoothRetrySeconds: 0.1).GetAwaiter().GetResult();
            src.StartBluetooth();
            check(Wait(() => src.PowerW == 150) && Wait(() => Near(src.CadenceRpm, 70, 1.5)) && src.Sensor(Role.Cadence)!.Feeds == BleFeed.Csc,
                "Bluetooth trainer + Bluetooth cadence sensor: power is the trainer's (no device of its own), cadence is the sensor's 70 rpm (wheel data stepped over), not the trainer's 85");
            Volatile.Write(ref cadence.CrankRpm, 0);
            check(Wait(() => src.CadenceRpm == 0.0, 6) && src.Sensor(Role.Cadence)!.Connected, "the crank stops and the sensor keeps sending: 0 rpm after a few seconds");
            Volatile.Write(ref cadence.CrankRpm, 80);
            check(Wait(() => Near(src.CadenceRpm, 80, 1.5), 6), "pedalling again: 80 rpm");
            cadence.Value = _ => null;
            check(Wait(() => src.CadenceRpm is null, 8) && src.Sensor(Role.Cadence)!.Connected && src.PowerW == 150, "the sensor goes quiet (asleep): cadence is absent, the ride is untouched");
        }

        // the wrong kind of device, and a role that names the trainer itself
        {
            var radio = new FakeSensorRadio();
            radio.Sensors.Add(FakeBleSensor.HeartRate(StrapAddress, "FAKE HRM", 120));
            var prefs = new DevicePreferences();
            prefs.Set(Role.Trainer, Kickr); prefs.Set(Role.Power, DeviceKey.Bluetooth(AntDeviceTypes.PowerMeter, StrapAddress));
            prefs.Set(Role.Cadence, DeviceKey.Bluetooth(AntDeviceTypes.FitnessEquipment, FakeFtmsRadio.TrainerAddress));
            var said = new List<string>();
            using var src = Ride.OpenSourceAsync(prefs, _ => throw new AntStickException("no", null), () => radio, s => { lock (said) said.Add(s); }, bluetoothRetrySeconds: 0.05).GetAwaiter().GetResult();
            src.StartBluetooth();
            check(Wait(() => { lock (said) return said.Exists(l => l.Contains("power meter not connected after")); }) && src.Link(Role.Power) is { NotFound: true } && src.PowerW is null,
                "a device that has no Cycling Power Service chosen as the power meter: never connected, said in the log, no power");
            check(src.Sensor(Role.Cadence) is null && src.BluetoothSensors.Count() == 1 && Wait(() => Near(src.CadenceRpm, 85)) && src.Link(Role.Cadence)?.Key == Kickr,
                "a role that names the Bluetooth trainer itself opens no second connection to it: the trainer supplies it");
        }
    }

    private static void NoRadio(Action<bool, string> check)
    {
        Console.WriteLine("Bluetooth sensors: a PC with no Bluetooth");
        var log = new List<string>();
        var none = new FakeSensorRadio { Adapter = new BleAdapterInfo(false) };
        var prefs = new DevicePreferences();
        prefs.Set(Role.Trainer, AntTrainer); prefs.Set(Role.HeartRate, Strap, name: "FAKE HRM");
        int antOpens = 0;
        using var src = Ride.OpenSourceAsync(prefs, _ => { antOpens++; return new SimTrainer(new SimOptions()); }, () => none, log.Add).GetAwaiter().GetResult();
        src.StartBluetooth();
        check(antOpens == 1 && src.TrainerPaired && src.PowerW == 200 && !src.BluetoothSensors.Any(), "an ANT+ trainer with a Bluetooth strap saved: the ride starts and rides the trainer (a sensor never stops a ride)");
        check(log.Exists(l => l.Contains("WARNING: This PC has no Bluetooth") && l.Contains("HeartRate")) && src.HeartRateBpm is null && src.HeartRateDiagnosis == "This PC has no Bluetooth"
            && src.Link(Role.HeartRate) is { NotFound: true } li && li.Key == Strap, "one WARNING says the strap is not used; no heart rate, its light says not found");
        check(none.Attempts == 0, "and nothing is asked of the missing radio");

        int radios = 0;
        var antOnly = new DevicePreferences(); antOnly.Set(Role.Trainer, AntTrainer); antOnly.Set(Role.HeartRate, new DeviceKey(120, 7000, 1));
        using var src2 = Ride.OpenSourceAsync(antOnly, _ => new SimTrainer(new SimOptions()), () => { radios++; return none; }, log.Add).GetAwaiter().GetResult();
        check(radios == 0 && !src2.BluetoothSensors.Any(), "only ANT+ devices chosen: Bluetooth is never touched, as before");
    }
}
