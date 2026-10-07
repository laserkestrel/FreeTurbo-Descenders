using System.Globalization;
using DescendersBridge.Ant;

namespace DescendersBridge.Bluetooth;

/// <summary>
/// The seam between the Bluetooth code and Windows: everything above it is plain C#, so `selftest` rides a fake trainer with no
/// radio (tests\unit\BluetoothRideTests.cs). The real one is <see cref="WindowsBleRadio"/>, the only file that touches Windows.Devices.Bluetooth.
/// </summary>
public interface IBleRadio
{
    /// <summary>This PC's Bluetooth, as Windows reports it. Never throws.</summary>
    Task<BleAdapterInfo> AdapterAsync();

    /// <summary>Listen to advertisements for <paramref name="duration"/>: every device heard, with the services it advertises.</summary>
    Task<IReadOnlyList<BleAdvert>> ScanAsync(TimeSpan duration, CancellationToken ct);

    /// <summary>Connect to one device. Throws when it does not answer (switched off, out of range).</summary>
    Task<IBleConnection> ConnectAsync(ulong address, CancellationToken ct);
}

/// <summary>One connected device (the trainer, or a sensor: each device of a ride has its own connection). Windows allows a
/// trainer one such connection at a time (a training app connected to it blocks this one).</summary>
public interface IBleConnection : IAsyncDisposable
{
    /// <summary>False once the device has gone (out of range, switched off): this connection is dead then, make a new one.</summary>
    bool Connected { get; }

    /// <summary>Read a characteristic; null = the device does not have it.</summary>
    Task<byte[]?> ReadAsync(Guid service, Guid characteristic, CancellationToken ct);

    /// <summary>Ask for notifications (or indications) and hand every value to <paramref name="onValue"/>. Throws when it cannot be done.</summary>
    Task SubscribeAsync(Guid service, Guid characteristic, bool indicate, Action<byte[]> onValue, CancellationToken ct);

    /// <summary>Write with response. True = the device accepted the write (its answer to a command comes separately, as an indication).</summary>
    Task<bool> WriteAsync(Guid service, Guid characteristic, byte[] value, CancellationToken ct);

    /// <summary>Every service the device has, for the log when it turns out not to be what it was chosen as (empty = not known).</summary>
    Task<IReadOnlyList<Guid>> ServicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
}

/// <summary>What Windows says about this PC's Bluetooth. RadioOn null = Windows would not say (it then counts as on: a radio that is off
/// fails the scan or the connect with its own message). <see cref="Detail"/>: the radio's own words, or the error that hid it.</summary>
public sealed record BleAdapterInfo(bool Present, bool LowEnergy = false, bool CentralRole = false, bool? RadioOn = null, string Detail = "");

/// <summary>One device heard in a scan.</summary>
public sealed record BleAdvert(ulong Address, string Name, int RssiDbm, int Count, IReadOnlyList<Guid> Services)
{
    public DescendersBridge.Ant.DeviceKey TrainerKey => DescendersBridge.Ant.DeviceKey.Bluetooth(DescendersBridge.Ant.AntDeviceTypes.FitnessEquipment, Address);
}

/// <summary>Bluetooth is asked for and this PC cannot do it; the message is one plain sentence for the rider.</summary>
public sealed class BluetoothUnavailableException : Exception
{
    public BluetoothUnavailableException(string message) : base(message) { }
}

/// <summary>The pure rules around the radio.</summary>
public static class BleRules
{
    public const string NoBluetooth = "This PC has no Bluetooth";
    public const string RadioOff = "Bluetooth is switched off on this PC (switch it on in Windows' settings)";

    /// <summary>Why Bluetooth cannot be used on this PC, in one plain sentence; null = it can.</summary>
    public static string? Problem(BleAdapterInfo a)
    {
        if (!a.Present) return NoBluetooth;
        if (!a.LowEnergy || !a.CentralRole) return "This PC's Bluetooth is too old to talk to a trainer or a sensor (it needs Bluetooth 4.0 Low Energy)";
        if (a.RadioOn == false) return RadioOff;
        return null;
    }

    /// <summary>The service Tacx trainers advertise for their own trainer protocol. A Tacx Neo 2T was not found as a
    /// trainer; it has the Fitness Machine Service but its short advertisement may name only this one (and Cycling Power). Second
    /// rework: a Tacx Neo 2 has ONLY this one, and is ridden through it (<see cref="TacxFec"/>).</summary>
    public static readonly Guid TacxTrainerService = new("6e40fec1-b5a3-f393-e0a9-e50e24dcca9e");

    /// <summary>A smart trainer, as far as its advertisement says: the Fitness Machine Service 0x1826 (also added by the window's search
    /// when a device that did not advertise it turns out to have it, <see cref="WithFtms"/>), or a trainer maker's own service.</summary>
    public static bool IsTrainer(BleAdvert a) => a.Services.Contains(Ftms.Service) || a.Services.Contains(TacxTrainerService);

    /// <summary>
    /// An advertisement is short, and some trainers leave the Fitness Machine Service out of it (they name Cycling
    /// Power, speed and cadence, or the maker's own service). Such a device is worth asking: the window's search connects to it for a
    /// moment and looks for the service itself. Not a device that already says it is a trainer by 0x1826, and not a plain heart-rate strap.
    /// </summary>
    public static bool WorthAsking(BleAdvert a) => !a.Services.Contains(Ftms.Service)
        && (a.Services.Contains(BleSensors.PowerService) || a.Services.Contains(BleSensors.CscService) || a.Services.Contains(TacxTrainerService));

    /// <summary>The device answered that it has the Fitness Machine Service: from now on it is a trainer like one that advertises it.</summary>
    public static BleAdvert WithFtms(BleAdvert a) =>
        a.Services.Contains(Ftms.Service) ? a : new BleAdvert(a.Address, a.Name, a.RssiDbm, a.Count, a.Services.Append(Ftms.Service).ToList());

    /// <summary>The smart trainers of a scan, nearest first.</summary>
    public static List<BleAdvert> Trainers(IEnumerable<BleAdvert> heard) =>
        heard.Where(IsTrainer).OrderByDescending(a => a.RssiDbm).ToList();

    /// <summary>
    /// What a device heard in a scan would be saved as for this role, null = it cannot do the job. By the services it
    /// advertises: a trainer (<see cref="IsTrainer"/>) has the Fitness Machine Service 0x1826; heart rate 0x180D; a power meter the Cycling Power Service 0x1818;
    /// cadence the Cycling Speed and Cadence Service 0x1816, else a power meter's crank data. A trainer is not offered as a separate power
    /// or cadence sensor (many also advertise 0x1818): its power and cadence come with the trainer job, over its one connection.
    /// </summary>
    public static DeviceKey? KeyFor(Role role, BleAdvert a)
    {
        bool trainer = IsTrainer(a);
        byte? type = role switch
        {
            Role.Trainer => trainer ? AntDeviceTypes.FitnessEquipment : null,
            Role.HeartRate => a.Services.Contains(BleSensors.HeartRateService) ? AntDeviceTypes.HeartRate : null,
            Role.Power => !trainer && a.Services.Contains(BleSensors.PowerService) ? AntDeviceTypes.PowerMeter : null,
            Role.Cadence => trainer ? null
                : a.Services.Contains(BleSensors.CscService) ? AntDeviceTypes.BikeCadence
                : a.Services.Contains(BleSensors.PowerService) ? AntDeviceTypes.PowerMeter : null,
            _ => null,
        };
        return type is { } t ? DeviceKey.Bluetooth(t, a.Address) : null;
    }

    /// <summary>The sensors of a scan (heart rate, cadence, power meter), nearest first.</summary>
    public static List<BleAdvert> Sensors(IEnumerable<BleAdvert> heard) =>
        heard.Where(a => SensorRoles.Any(r => KeyFor(r, a) is not null)).OrderByDescending(a => a.RssiDbm).ToList();

    /// <summary>Every device of a scan the bridge can use: the trainers, then the sensors (a device that is both is listed once).</summary>
    public static List<BleAdvert> Devices(IEnumerable<BleAdvert> heard)
    {
        var all = heard.ToList();
        var list = Trainers(all);
        list.AddRange(Sensors(all).Where(s => !list.Contains(s)));
        return list;
    }

    /// <summary>
    /// The window searches in short scans so its list fills while the search runs. One device heard in several of them is
    /// one entry: the newest signal and name (a name once heard is kept), every service it ever advertised, the counts added up.
    /// </summary>
    public static List<BleAdvert> Merge(IEnumerable<BleAdvert> earlier, IEnumerable<BleAdvert> later)
    {
        var list = earlier.ToList();
        foreach (var b in later)
        {
            int i = list.FindIndex(a => a.Address == b.Address);
            if (i < 0) { list.Add(b); continue; }
            var a = list[i];
            list[i] = new BleAdvert(a.Address, b.Name.Length > 0 ? b.Name : a.Name, b.RssiDbm, a.Count + b.Count, a.Services.Union(b.Services).ToList());
        }
        return list;
    }

    private static readonly Role[] SensorRoles = { Role.HeartRate, Role.Cadence, Role.Power };

    /// <summary>What a device is, from its advertisement: "Trainer", "Heart rate", "Power meter", "Cadence sensor" (several joined by " + ").</summary>
    public static string Kind(BleAdvert a)
    {
        var kinds = new List<string>();
        bool trainer = IsTrainer(a);
        if (trainer) kinds.Add("Trainer");
        if (a.Services.Contains(BleSensors.HeartRateService)) kinds.Add("Heart rate");
        if (!trainer)
        {
            if (a.Services.Contains(BleSensors.PowerService)) kinds.Add("Power meter");
            if (a.Services.Contains(BleSensors.CscService)) kinds.Add("Cadence sensor");
        }
        return kinds.Count == 0 ? "Device" : string.Join(" + ", kinds);
    }

    /// <summary>A service as the log names it: "1826" for a Bluetooth SIG number, the whole UUID for a maker's own.</summary>
    public static string ServiceName(Guid service)
    {
        string s = service.ToString();
        return s.StartsWith("0000", StringComparison.Ordinal) && s.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.Ordinal) ? s.Substring(4, 4).ToUpperInvariant() : s;
    }

    /// <summary>Why a connection attempt failed, for the log. An error of Windows' own may have no words at all (the garage log of
    /// 2026-10-05 has "trainer not connected after 31 s: " and nothing after it): then the error's type and number.</summary>
    public static string Why(Exception ex) =>
        !string.IsNullOrWhiteSpace(ex.Message) ? ex.Message : $"{ex.GetType().Name} 0x{ex.HResult:X8} (Windows gave no reason)";

    /// <summary>A log line for a device the search heard and does not list(when a trainer is missing from the list,
    /// this says what it advertised).</summary>
    public static string DescribeUnlisted(BleAdvert a) =>
        $"{(a.Name.Length > 0 ? a.Name : "(no name)")}  {BleAddress.Format(a.Address)}  signal {a.RssiDbm} dBm  services: {(a.Services.Count == 0 ? "none" : string.Join(", ", a.Services.Select(ServiceName)))}";

    /// <summary>A console line for a trainer or sensor found in a scan.</summary>
    public static string Describe(BleAdvert a) =>
        $"{Kind(a) + " (Bluetooth)",-22} {(a.Name.Length > 0 ? a.Name : "(no name)")}  {BleAddress.Format(a.Address)}  signal {a.RssiDbm} dBm  heard {a.Count}x";
}

/// <summary>A Bluetooth device address (48 bits) as text: "C1:23:45:67:89:AB".</summary>
public static class BleAddress
{
    public static string Format(ulong address)
    {
        var parts = new string[6];
        for (int i = 0; i < 6; i++) parts[i] = ((address >> (8 * (5 - i))) & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
        return string.Join(":", parts);
    }

    /// <summary>Reads "C1:23:45:67:89:AB", "c1-23-45-67-89-ab" or "C123456789AB"; null = not an address (0 is not one either).</summary>
    public static ulong? Parse(string? text)
    {
        string hex = (text ?? "").Trim().Replace(":", "").Replace("-", "");
        if (hex.Length != 12 || !ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong v) || v == 0) return null;
        return v;
    }
}
