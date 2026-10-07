using DescendersBridge.Ant;
using DescendersBridge.Bluetooth;

namespace DescendersBridge;

/// <summary>
/// The chosen devices of one ride as <see cref="RideLoop"/> sees them: an ANT+ side (the stick with its channels, kept across
/// a lost stick by <see cref="RecoveringSource"/>), a Bluetooth side (the FTMS trainer, kept across disconnects by <see cref="FtmsSession"/>,
/// and the heart-rate, cadence and power sensors, each kept by its own <see cref="BleSensorSession"/>), or both.
/// Which reading wins is ONE rule for both transports, the one ANT+ always had: a role's own chosen device, and only that device (absent
/// while it is away, never the trainer's value instead); power and cadence with no device of their own come from the trainer, whichever
/// side that is. Each side reports its own loss: the stick gone is <see cref="LinkLost"/>, a Bluetooth trainer gone reads as "not paired",
/// a Bluetooth sensor gone is just an absent reading. A side that is null was not asked for: with only Bluetooth devices chosen there is
/// no stick and no ANT+ key in the ride at all.
/// </summary>
public sealed class RideSource : ITrainerSource, IDisposable
{
    private readonly DevicePreferences _prefs;
    private readonly ITrainerSource? _ant;
    private readonly FtmsSession? _bt;
    private readonly IReadOnlyDictionary<Role, BleSensorSession> _sensors;
    private readonly string _sensorProblem, _antProblem;

    /// <param name="ant">null = no ANT+ device chosen (or, with <paramref name="antProblem"/>, no stick for the ANT+ sensors).</param>
    /// <param name="antProblem">why ANT+ sensors are chosen but not used (no stick, beside a Bluetooth trainer); null = no such problem.</param>
    /// <param name="bluetooth">null = the trainer is not a Bluetooth one.</param>
    /// <param name="sensors">the Bluetooth sensor filling each role (one session may fill two); null = none.</param>
    /// <param name="sensorProblem">why Bluetooth sensors are chosen but not used ("This PC has no Bluetooth"); null = no such problem.</param>
    public RideSource(DevicePreferences prefs, ITrainerSource? ant, FtmsSession? bluetooth,
        IReadOnlyDictionary<Role, BleSensorSession>? sensors = null, string? sensorProblem = null, string? antProblem = null)
    {
        _prefs = prefs; _ant = ant; _bt = bluetooth;
        _sensors = sensors ?? new Dictionary<Role, BleSensorSession>();
        _sensorProblem = sensorProblem ?? "";
        _antProblem = antProblem ?? "";
    }

    /// <summary>Why the ANT+ sensors chosen beside a Bluetooth trainer are not used ("No ANT+ USB stick is plugged in"); "" = no such problem.</summary>
    public string AntProblem => _antProblem;

    public ITrainerSource? AntSide => _ant;
    public FtmsSession? BluetoothSide => _bt;
    /// <summary>The Bluetooth sensors of the ride, one per device.</summary>
    public IEnumerable<BleSensorSession> BluetoothSensors => _sensors.Values.Distinct();
    /// <summary>The Bluetooth sensor filling this role, null = none.</summary>
    public BleSensorSession? Sensor(Role role) => _sensors.TryGetValue(role, out var s) ? s : null;

    /// <summary>Start looking for every Bluetooth device of the ride (returns at once); one that is still switched off is waited for.</summary>
    public void StartBluetooth()
    {
        _bt?.Start();
        foreach (var s in BluetoothSensors) s.Start();
    }

    /// <summary>The ANT+ session in use, null while the stick is gone (or no ANT+ device is chosen).</summary>
    private AntSession? Ant => (_ant as RecoveringSource)?.Inner as AntSession ?? _ant as AntSession;

    /// <summary>This role is filled by the Bluetooth trainer: it is the trainer, or the role has no device of its own (or names the
    /// trainer itself) and the trainer supplies it.</summary>
    private bool OnBluetoothTrainer(Role role)
    {
        if (_bt is null) return false;
        if (role == Role.Trainer) return true;
        if (role == Role.HeartRate) return false;
        return _prefs.Get(role) is not { } own || (own.IsBluetooth && own.BluetoothAddress == _bt.Key.BluetoothAddress);
    }

    /// <summary>A Bluetooth sensor is chosen for this role and cannot be used (this PC has no Bluetooth): the role has no reading.</summary>
    private bool Unusable(Role role) => role != Role.Trainer && !_sensors.ContainsKey(role) && !OnBluetoothTrainer(role)
        && _prefs.Get(role) is { } own && (own.IsBluetooth || _antProblem.Length > 0);

    public bool LinkLost => _ant?.LinkLost ?? false;
    public string LinkLostText => _ant?.LinkLostText ?? "";
    public ITrainerControl? Control => _bt is not null ? _bt : _ant?.Control;
    public bool TrainerPaired => _bt is not null ? _bt.Paired : _ant?.TrainerPaired ?? false;
    public int? PowerW => Sensor(Role.Power) is { } s ? s.PowerW : OnBluetoothTrainer(Role.Power) ? _bt!.PowerW : Unusable(Role.Power) ? null : _ant?.PowerW;
    public double? CadenceRpm => Sensor(Role.Cadence) is { } s ? s.CadenceRpm : OnBluetoothTrainer(Role.Cadence) ? _bt!.CadenceRpm : Unusable(Role.Cadence) ? null : _ant?.CadenceRpm;
    public int? HeartRateBpm => Sensor(Role.HeartRate) is { } s ? s.HeartRateBpm : Unusable(Role.HeartRate) ? null : _ant?.HeartRateBpm;
    public string HeartRateDiagnosis => Sensor(Role.HeartRate) is { } s ? s.HeartRateDiagnosis : Unusable(Role.HeartRate) ? (_prefs.Get(Role.HeartRate) is { IsBluetooth: true } ? _sensorProblem : _antProblem) : _ant?.HeartRateDiagnosis ?? "";
    public bool Muted => _ant?.Muted ?? false;

    /// <summary>The trainer's own speed (the window's Speed tile).</summary>
    public double? SpeedMps => _bt is not null ? _bt.SpeedMps : Ant?.SpeedMps;

    /// <summary>The link to the device filling this role, for the window's health lights (null = no device fills it, or its stick is gone).</summary>
    public LinkInfo? Link(Role role) =>
        Sensor(role) is { } s ? s.LinkInfo
        : OnBluetoothTrainer(role) ? _bt!.LinkInfo
        : Unusable(role) ? new LinkInfo(_prefs.Get(role)!.Value, 0, double.PositiveInfinity, NotFound: true)
        : Ant?.Link(role);

    public void Dispose()
    {
        foreach (var s in BluetoothSensors) try { s.Dispose(); } catch { }
        try { _bt?.Dispose(); } catch { }
        try { (_ant as IDisposable)?.Dispose(); } catch { }
    }
}
