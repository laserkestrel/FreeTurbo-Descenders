using System.Globalization;

namespace DescendersBridge.Ant;

/// <summary>Who made a device, as the device itself says: the ANT+ manufacturer ID, and the model number where the page has one.</summary>
public readonly record struct DeviceIdentity(ushort ManufacturerId, ushort? ModelNumber = null)
{
    /// <summary>For the log and the console listing: "manufacturer 89 (Tacx), model 2875". Numbers always, the brand when known.</summary>
    public string LogText
    {
        get
        {
            string s = "manufacturer " + ManufacturerId.ToString(CultureInfo.InvariantCulture);
            if (Manufacturers.Name(ManufacturerId) is { } brand) s += " (" + brand + ")";
            if (ModelNumber is { } m) s += ", model " + m.ToString(CultureInfo.InvariantCulture);
            return s;
        }
    }
}

/// <summary>
/// ANT+ devices send no name, but a manufacturer ID a few times a minute, mixed in with their normal data. Pure decoders over
/// the 8-byte payload:
///  - trainer FE-C (17), power meter (11): common page 80 (0x50), bytes 4-5 = manufacturer ID, 6-7 = model number (little endian);
///  - heart rate (120), cadence (122), speed (123): background page 2 (page number = byte 0 without its top bit, the page-change toggle),
///    byte 1 = manufacturer ID (8 bit), no model number;
///  - speed+cadence (121): has no background pages, so no manufacturer.
/// </summary>
public static class DeviceIdentityPages
{
    public const byte ManufacturerPage = 0x50, BackgroundPage = 2;

    /// <summary>The manufacturer this page carries; null = it is another page, or this device type never says.</summary>
    public static DeviceIdentity? Decode(byte deviceType, ReadOnlySpan<byte> p)
    {
        if (p.Length < 8) return null;
        switch (deviceType)
        {
            case AntDeviceTypes.FitnessEquipment:
            case AntDeviceTypes.PowerMeter:
                if (p[0] != ManufacturerPage) return null;
                return new DeviceIdentity((ushort)(p[4] | (p[5] << 8)), (ushort)(p[6] | (p[7] << 8)));

            case AntDeviceTypes.HeartRate:
            case AntDeviceTypes.BikeCadence:
            case AntDeviceTypes.BikeSpeed:
                return (p[0] & 0x7F) == BackgroundPage ? new DeviceIdentity(p[1]) : null;

            default:
                return null;
        }
    }
}

/// <summary>
/// Manufacturer ID -> short brand name, for the hardware brands a rider is likely to own (trainers, power meters, heart-rate straps /
/// watches, cadence sensors). The IDs are the `manufacturer` type of the FIT SDK profile (Profile.xlsx, Types tab). An ID that is not
/// here has no name: the window then shows the device by number only, and the log still prints the ID.
/// NOT YET CHECKED against Profile.xlsx (the run that wrote this table had no web access): check each line before relying on it.
/// </summary>
public static class Manufacturers
{
    public const ushort Garmin = 1, Tacx = 89;

    private static readonly Dictionary<int, string> Names = new()
    {
        [Garmin] = "Garmin",
        [6] = "SRM",
        [7] = "Quarq",
        [9] = "Saris",
        [23] = "Suunto",
        [32] = "Wahoo",
        [41] = "Shimano",
        [48] = "Pioneer",
        [51] = "4iiii",
        [69] = "Stages",
        [73] = "Wattbike",
        [86] = "Elite",
        [Tacx] = "Tacx",
        [107] = "Magene",
        [123] = "Polar",
        [263] = "Favero",
    };

    /// <summary>The brand, or null when the ID is unknown (or there is none).</summary>
    public static string? Name(int? manufacturerId) => manufacturerId is { } id && Names.TryGetValue(id, out var n) ? n : null;
}

/// <summary>
/// How the bridge window names a device: the brand when the device told us one we know, and no protocol name ("Trainer", not
/// "Trainer (FE-C)"; the log and the console keep <see cref="AntDeviceTypes.Name"/>). Pure, so `selftest` covers it
/// (tests\unit\DeviceIdentityTests.cs).
/// </summary>
public static class DeviceLabels
{
    /// <summary>What kind of device it is, in the rider's words.</summary>
    public static string Kind(byte deviceType) => deviceType == AntDeviceTypes.FitnessEquipment ? "Trainer" : AntDeviceTypes.Name(deviceType);

    /// <summary>The found list's "Make" cell: the brand, blank when unknown.</summary>
    public static string Make(int? manufacturerId) => Manufacturers.Name(manufacturerId) ?? "";

    /// <summary>A drop-down line: the brand when known and the device number, the same words as the Ride tab,
    /// "Tacx 44226" / "device 44226". No kind (the drop-down's own label says it) and no reading.</summary>
    public static string Describe(DeviceKey k, int? manufacturerId) => Short(k.DeviceNumber, manufacturerId);

    /// <summary>How a device is reached, in the window's words (the found list's "Found by" cell): "ANT+" or "Bluetooth".</summary>
    public static string Transport(DeviceKey k) => k.IsBluetooth ? "Bluetooth" : "ANT+";

    /// <summary>The found list's "Make" cell for either kind: an ANT+ device's brand (blank when unknown); a Bluetooth device
    /// has no manufacturer ID but advertises a name, usually the maker's model name, which goes here (no name: its address).</summary>
    public static string FoundMake(DeviceKey k, int? manufacturerId, string? advertisedName) =>
        !k.IsBluetooth ? Make(manufacturerId)
        : string.IsNullOrWhiteSpace(advertisedName) ? DescendersBridge.Bluetooth.BleAddress.Format(k.BluetoothAddress) : advertisedName.Trim();

    /// <summary>The found list's "Number" cell: the ANT+ device number; a Bluetooth device has none.</summary>
    public static string FoundNumber(DeviceKey k) => k.IsBluetooth ? "" : k.DeviceNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>A drop-down line: every device says how it is reached, so a trainer found both ways is two lines that
    /// can be told apart: "Tacx 44226 (ANT+)" / "KICKR CORE 1234 (Bluetooth)".</summary>
    public static string Choice(DeviceKey k, int? manufacturerId, string? name) =>
        k.IsBluetooth ? Bluetooth(k, name) : Describe(k, manufacturerId) + " (ANT+)";

    /// <summary>A Bluetooth device's drop-down line: the name it advertises, "KICKR CORE 1234 (Bluetooth)"; no name stored:
    /// its address, "Bluetooth C1:23:45:67:89:AB" (no kind either).</summary>
    public static string Bluetooth(DeviceKey k, string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Bluetooth " + DescendersBridge.Bluetooth.BleAddress.Format(k.BluetoothAddress) : name.Trim() + " (Bluetooth)";

    /// <summary>The Ride tab's short form for a Bluetooth device: its name, else its kind and address.</summary>
    public static string BluetoothShort(DeviceKey k, string? name) =>
        string.IsNullOrWhiteSpace(name) ? Kind(k.DeviceType) + " " + DescendersBridge.Bluetooth.BleAddress.Format(k.BluetoothAddress) : name.Trim();

    /// <summary>The Ride tab's short form: "Tacx 44226"; unknown brand: "device 44226".</summary>
    public static string Short(ushort deviceNumber, int? manufacturerId) =>
        (Manufacturers.Name(manufacturerId) ?? "device") + " "+ deviceNumber.ToString(CultureInfo.InvariantCulture);
}
