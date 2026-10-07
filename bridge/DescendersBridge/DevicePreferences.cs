using System.Text.Json.Serialization;
using DescendersBridge.Ant;
using DescendersBridge.Bluetooth;

namespace DescendersBridge;

/// <summary>One stored choice. An ANT+ device is matched on the air by type + number + transmission type; the name is for humans only.
/// ManufacturerId: who made it, once the device has said so; left out of the file while unknown, so an older file reads and
/// writes as before. `transport` "bluetooth" makes it a Bluetooth device, matched by `address` ("C1:23:45:67:89:AB"), with
/// the name it advertises in `name`; deviceType still says what kind of device it is (17 = trainer; 120 = heart rate,
/// 122 = cadence sensor, 11 = power meter). No `transport` (every file written before #112) or "ant" = ANT+, and neither field is
/// written for an ANT+ device.</summary>
public sealed record StoredDevice(byte DeviceType, ushort DeviceNumber, byte TransmissionType, string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ushort? ManufacturerId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Transport = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Address = null)
{
    public const string Bluetooth = "bluetooth", Ant = "ant";

    [JsonIgnore]
    public bool IsBluetooth => string.Equals(Transport?.Trim(), Bluetooth, StringComparison.OrdinalIgnoreCase);

    /// <summary>False for a Bluetooth entry whose address cannot be read (a hand-edit gone wrong): it is not loaded.</summary>
    [JsonIgnore]
    public bool Usable => !IsBluetooth || BleAddress.Parse(Address) is not null;

    /// <summary>A hand-written Bluetooth entry may leave deviceType out: it counts as a trainer. In a sensor's role the ride still reads
    /// it as that role's sensor (<see cref="BleSensors.Feed"/> goes by the role).</summary>
    [JsonIgnore]
    public DeviceKey Key => IsBluetooth
        ? DeviceKey.Bluetooth(DeviceType == 0 ? AntDeviceTypes.FitnessEquipment : DeviceType, BleAddress.Parse(Address) ?? 0)
        : new(DeviceType, DeviceNumber, TransmissionType);

    public static StoredDevice ForBluetooth(DeviceKey key, string? name) =>
        new(key.DeviceType, 0, 0, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), null, Bluetooth, BleAddress.Format(key.BluetoothAddress));
}

/// <summary>
/// The user's chosen device per role: the `devices` section of the per-user %APPDATA%\TrainerBridge\TrainerBridgeConfig.json
/// (<see cref="TrainerBridgeConfig"/>, loaded with the rider profile). A trainer broadcasts several profiles under one device number,
/// so the type is part of the identity. Also every device ever chosen (<see cref="Known"/>, the `knownDevices` section):
/// setting a role to none keeps its device there, so it can be chosen again later without a search. Each role's device is ANT+ or
/// Bluetooth; a mix is fine.
/// </summary>
public sealed class DevicePreferences
{
    public Dictionary<string, StoredDevice> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every device ever chosen for a role, in use or not, oldest first. Only grows (edit the file to drop one).</summary>
    [JsonIgnore]
    public List<StoredDevice> Known { get; } = new();

    /// <summary>Writes the `devices` and `knownDevices` sections; the rider section is left as it is. Throws when the file is unreadable.</summary>
    public void Save() => TrainerBridgeConfig.SaveDevices(this);

    public DeviceKey? Get(Role role) => Roles.TryGetValue(role.ToString(), out var d) ? d.Key : null;

    /// <summary>At least one chosen device is ANT+. Only then does a ride need the USB stick and the ANT+ network key.</summary>
    [JsonIgnore]
    public bool NeedsAnt => Roles.Values.Any(d => !d.IsBluetooth);

    /// <summary>At least one chosen device is Bluetooth.</summary>
    [JsonIgnore]
    public bool NeedsBluetooth => Roles.Values.Any(d => d.IsBluetooth);

    /// <summary>The ANT+ choices alone: what the ANT+ session opens channels for (a Bluetooth trainer or sensor is not its business).</summary>
    public DevicePreferences AntOnly()
    {
        var p = new DevicePreferences();
        foreach (var (role, d) in Roles) if (!d.IsBluetooth) p.Roles[role] = d;
        foreach (var d in Known) if (!d.IsBluetooth) p.Known.Add(d);
        return p;
    }

    /// <summary>The name stored with this device (a Bluetooth device's advertised name), null = none.</summary>
    public string? NameOf(DeviceKey key)
    {
        foreach (var d in Roles.Values) if (d.Key == key && !string.IsNullOrWhiteSpace(d.Name)) return d.Name;
        foreach (var d in Known) if (d.Key == key && !string.IsNullOrWhiteSpace(d.Name)) return d.Name;
        return null;
    }

    /// <param name="manufacturerId">who made it, when the search heard that; null keeps what is already known about this device.</param>
    /// <param name="name">a Bluetooth device's advertised name, when the search heard it; null keeps the name already stored.</param>
    public void Set(Role role, DeviceKey key, ushort? manufacturerId = null, string? name = null)
    {
        var d = key.IsBluetooth
            ? StoredDevice.ForBluetooth(key, name ?? NameOf(key))
            : new StoredDevice(key.DeviceType, key.DeviceNumber, key.TransmissionType, AntDeviceTypes.Name(key.DeviceType), manufacturerId ?? ManufacturerOf(key));
        Roles[role.ToString()] = d;
        Remember(d);
        if (d.IsBluetooth && d.Name is not null)   // a name heard later replaces the one remembered
            for (int i = 0; i < Known.Count; i++) if (Known[i].Key == key) Known[i] = d;
        if (d.ManufacturerId is { } m) Learn(key, m);
    }

    /// <summary>The manufacturer stored for this device (in a role or among the known ones), null = not known.</summary>
    public ushort? ManufacturerOf(DeviceKey key)
    {
        foreach (var d in Roles.Values) if (d.Key == key && d.ManufacturerId is { } m) return m;
        foreach (var d in Known) if (d.Key == key && d.ManufacturerId is { } m) return m;
        return null;
    }

    /// <summary>A stored device told us who made it. Every entry for that device (roles and known) gets the manufacturer;
    /// a device that is not stored is not added. True = something changed (save it).</summary>
    public bool Learn(DeviceKey key, ushort manufacturerId)
    {
        bool changed = false;
        foreach (string role in Roles.Keys.ToList())
            if (Roles[role] is { } d && d.Key == key && d.ManufacturerId != manufacturerId) { Roles[role] = d with { ManufacturerId = manufacturerId }; changed = true; }
        for (int i = 0; i < Known.Count; i++)
            if (Known[i].Key == key && Known[i].ManufacturerId != manufacturerId) { Known[i] = Known[i] with { ManufacturerId = manufacturerId }; changed = true; }
        return changed;
    }

    /// <summary>The role has no device any more; the device itself stays in <see cref="Known"/>.</summary>
    public void Clear(Role role)
    {
        if (Roles.TryGetValue(role.ToString(), out var d)) Remember(d);
        Roles.Remove(role.ToString());
    }

    /// <summary>Add to <see cref="Known"/> unless it is there already.</summary>
    public void Remember(StoredDevice d)
    {
        if (d is not null && !Known.Any(k => k.Key == d.Key)) Known.Add(d);
    }

    /// <summary>The known devices that can do this job (a trainer can also give power and cadence), oldest first. A Bluetooth trainer is
    /// offered as the trainer only (its power and cadence come with that job); Bluetooth sensors are offered
    /// for their jobs like ANT+ ones.</summary>
    public IEnumerable<DeviceKey> KnownFor(Role role) =>
        Known.Select(d => d.Key).Where(k => AntDeviceTypes.Roles(k.DeviceType).Contains(role)
            && !(k.IsBluetooth && k.DeviceType == AntDeviceTypes.FitnessEquipment && role != Role.Trainer));
}
