namespace DescendersBridge.Ant;

/// <summary>What a device does for the ride. One device is chosen per role.</summary>
public enum Role { Trainer, HeartRate, Cadence, Power }

/// <summary>ANT+ device profile numbers (the "device type" in the channel ID).</summary>
public static class AntDeviceTypes
{
    public const byte PowerMeter = 11;         // 0x0B
    public const byte FitnessEquipment = 17;   // 0x11  FE-C (smart trainer)
    public const byte HeartRate = 120;         // 0x78
    public const byte BikeSpeedCadence = 121;  // 0x79  combined speed + cadence
    public const byte BikeCadence = 122;       // 0x7A
    public const byte BikeSpeed = 123;         // 0x7B

    /// <summary>For the log and the console. The window shows <see cref="DeviceLabels.Kind"/> (no protocol name).</summary>
    public static string Name(byte t) => t switch
    {
        PowerMeter => "Power meter",
        FitnessEquipment => "Trainer (FE-C)",
        HeartRate => "Heart rate",
        BikeSpeedCadence => "Speed+cadence sensor",
        BikeCadence => "Cadence sensor",
        BikeSpeed => "Speed sensor",
        _ => $"type {t}",
    };

    /// <summary>The roles a device of this type can fill. A trainer can supply power and (unreliably on some rigs) cadence.</summary>
    public static Role[] Roles(byte t) => t switch
    {
        FitnessEquipment => new[] { Role.Trainer, Role.Power, Role.Cadence },
        PowerMeter => new[] { Role.Power, Role.Cadence },
        HeartRate => new[] { Role.HeartRate },
        BikeSpeedCadence or BikeCadence => new[] { Role.Cadence },
        _ => Array.Empty<Role>(),
    };
}

/// <summary>A device's identity on the air: type + number (+ transmission type). This is what a stored preference matches.
/// A Bluetooth device is its 48-bit address instead (<see cref="BluetoothAddress"/> not 0; number and transmission type 0);
/// its DeviceType still says what kind of device it is (17 = trainer), so the jobs it can do are found the same way.</summary>
public readonly record struct DeviceKey(byte DeviceType, ushort DeviceNumber, byte TransmissionType, ulong BluetoothAddress = 0)
{
    public bool IsBluetooth => BluetoothAddress != 0;

    public static DeviceKey Bluetooth(byte deviceType, ulong address) => new(deviceType, 0, 0, address);

    public override string ToString() => IsBluetooth
        ? $"{DeviceLabels.Kind(DeviceType)} (Bluetooth) {DescendersBridge.Bluetooth.BleAddress.Format(BluetoothAddress)}"
        : $"{AntDeviceTypes.Name(DeviceType)} #{DeviceNumber}";
}

/// <summary>
/// What has been heard from one device: message count and seconds since the last one (infinity = never). Input to the window's health lights.
/// NotFound: a sensor that was searched for and is not there: the ride carries on without it.
/// ManufacturerId: who made it, when the device has said so in this ride.
/// </summary>
public readonly record struct LinkInfo(DeviceKey Key, long Messages, double SecondsSinceLast, bool NotFound = false, ushort? ManufacturerId = null);
