using System.Diagnostics;
using DescendersBridge.Ant;

namespace DescendersBridge.Bluetooth;

/// <summary>
/// One connected Bluetooth sensor: a heart-rate strap, a cadence sensor or a power meter, read through its notifications
/// (<see cref="BleSensors"/>). Plain C# over <see cref="IBleConnection"/>; nothing is ever written to a sensor. A reading older than
/// <see cref="StaleSeconds"/> is null: absent is never zero. The link dies with its connection; <see cref="BleSensorSession"/> makes a
/// new one when the sensor is back.
/// </summary>
public sealed class BleSensorLink
{
    public const double StaleSeconds = 3.0;

    private readonly IBleConnection _conn;
    private readonly BleFeed _feeds;
    private readonly object _gate = new();
    private readonly CrankCadence _cadence = new();
    private int _bpm, _powerW;
    private bool? _contact;
    private long _hrTicks, _powerTicks, _dataTicks;   // Stopwatch timestamps, 0 = never
    private long _messages;

    public BleSensorLink(IBleConnection conn, BleFeed feeds)
    {
        _conn = conn; _feeds = feeds;
    }

    public bool Connected => _conn.Connected;
    /// <summary>Notifications heard.</summary>
    public long Messages => Interlocked.Read(ref _messages);
    public double SecondsSinceLast => Age(Interlocked.Read(ref _dataTicks));

    public int? HeartRateBpm { get { lock (_gate) return Age(_hrTicks) <= StaleSeconds ? _bpm : null; } }
    public int? PowerW { get { lock (_gate) return Age(_powerTicks) <= StaleSeconds ? _powerW : null; } }
    public double? CadenceRpm { get { lock (_gate) return _cadence.Rpm(Now()); } }
    /// <summary>What the strap last said about skin contact; null = it does not say (or has gone quiet).</summary>
    public bool? Contact { get { lock (_gate) return SecondsSinceLast <= StaleSeconds ? _contact : null; } }

    private static double Age(long ticks) => ticks == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(ticks).TotalSeconds;
    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Subscribe to what this sensor is chosen for. Throws when the device does not have the service (the connection attempt
    /// then counts as failed).</summary>
    public async Task OpenAsync(CancellationToken ct = default)
    {
        if ((_feeds & BleFeed.HeartRate) != 0)
            await _conn.SubscribeAsync(BleSensors.HeartRateService, BleSensors.HeartRateMeasurementChar, indicate: false, OnHeartRate, ct).ConfigureAwait(false);
        if ((_feeds & BleFeed.Power) != 0)
            await _conn.SubscribeAsync(BleSensors.PowerService, BleSensors.PowerMeasurementChar, indicate: false, OnPower, ct).ConfigureAwait(false);
        if ((_feeds & BleFeed.Csc) != 0)
            await _conn.SubscribeAsync(BleSensors.CscService, BleSensors.CscMeasurementChar, indicate: false, OnCsc, ct).ConfigureAwait(false);
    }

    private void Heard(long now)
    {
        Interlocked.Exchange(ref _dataTicks, now);
        Interlocked.Increment(ref _messages);
    }

    private void OnHeartRate(byte[] value)
    {
        if (BleSensors.DecodeHeartRate(value) is not { } m) return;
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _contact = m.Contact;
            if (BleSensors.Valid(m)) { _bpm = m.Bpm; _hrTicks = now; }
            else _hrTicks = 0;   // "no contact" or 0 bpm: the last good value is not held
        }
        Heard(now);
    }

    private void OnPower(byte[] value)
    {
        if (BleSensors.DecodePower(value) is not { } m) return;
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _powerW = m.PowerW; _powerTicks = now;
            // with a CSC subscription too (one device chosen both ways), the CSC crank data is the cadence: never two counters in one sum
            if (m.Crank is { } c && (_feeds & BleFeed.Csc) == 0) _cadence.Add(c, Now());
        }
        Heard(now);
    }

    private void OnCsc(byte[] value)
    {
        if (BleSensors.DecodeCsc(value) is not { } m) return;
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
            if (m.Crank is { } c) _cadence.Add(c, Now());
        Heard(now);
    }
}

/// <summary>
/// One Bluetooth sensor of a ride, across disconnects (the sensors' version of <see cref="FtmsSession"/>). It looks for the
/// sensor from the start and every <see cref="RetrySeconds"/> after a loss (out of range, battery, strap taken off); meanwhile its
/// readings are absent (null: never zero, never the last value held). A sensor that is lost or never there does not end, pause or
/// otherwise touch the ride.
/// </summary>
public sealed class BleSensorSession : IDisposable
{
    public const double RetrySeconds = 3;

    private readonly IBleRadio _radio;
    private readonly string _name, _kind;
    private readonly Action<string> _log;
    private readonly TimeSpan _retry;
    private readonly CancellationTokenSource _stop = new();
    private volatile BleSensorLink? _link;
    private volatile bool _notFound;
    private Task _run = Task.CompletedTask;
    private int _connections, _started;

    public BleSensorSession(IBleRadio radio, DeviceKey key, string? name, BleFeed feeds, Action<string> log, double retrySeconds = RetrySeconds)
    {
        _radio = radio; Key = key; Feeds = feeds; _log = log;
        _kind = BleSensors.Kind(feeds);
        _name = string.IsNullOrWhiteSpace(name) ? BleAddress.Format(key.BluetoothAddress) : name.Trim() + " (" + BleAddress.Format(key.BluetoothAddress) + ")";
        _retry = TimeSpan.FromSeconds(retrySeconds);
    }

    public DeviceKey Key { get; }
    public BleFeed Feeds { get; }
    /// <summary>The live link, null while the sensor is not connected.</summary>
    public BleSensorLink? Link => _link is { Connected: true } l ? l : null;
    public bool Connected => Link is not null;
    /// <summary>How many times the sensor has been connected in this ride (1 = never lost).</summary>
    public int Connections => Volatile.Read(ref _connections);

    public int? HeartRateBpm => Link?.HeartRateBpm;
    public int? PowerW => Link?.PowerW;
    public double? CadenceRpm => Link?.CadenceRpm;

    public string HeartRateDiagnosis
    {
        get
        {
            var l = Link;
            return BleSensors.HeartRateWords(l is not null, Connections > 0, _notFound, l?.HeartRateBpm is not null, l?.Contact);
        }
    }

    /// <summary>For the window's health light: 0 messages = still searching; NotFound = tried and not there (the ride carries on, it is
    /// looked for again).</summary>
    public LinkInfo LinkInfo => Link is { } l ? new LinkInfo(Key, l.Messages, l.SecondsSinceLast) : new LinkInfo(Key, 0, double.PositiveInfinity, NotFound: _notFound);

    /// <summary>Start looking for the sensor (returns at once; a second call does nothing).</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _run = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        _log($"looking for the {_kind} {_name}");
        var gone = Stopwatch.StartNew();
        for (int tries = 1; !ct.IsCancellationRequested; tries++)
        {
            IBleConnection? conn = null;
            try
            {
                conn = await _radio.ConnectAsync(Key.BluetoothAddress, ct).ConfigureAwait(false);
                var link = new BleSensorLink(conn, Feeds);
                await link.OpenAsync(ct).ConfigureAwait(false);
                bool first = Interlocked.Increment(ref _connections) == 1;
                _link = link;
                _notFound = false;
                _log(first ? $"{_kind} {_name} connected" : $"{_kind} back after {gone.Elapsed.TotalSeconds:0} s");
                tries = 0;
                while (!ct.IsCancellationRequested && conn.Connected) await Task.Delay(100, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;
                _link = null;
                gone.Restart();
                _log($"{_kind} lost (out of range or switched off): the ride carries on without it; looking for it every {_retry.TotalSeconds:0.#} s");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _link = null;
                _notFound = true;
                if (tries == 1 || tries % 10 == 0) _log($"{_kind} not connected after {gone.Elapsed.TotalSeconds:0} s: {BleRules.Why(ex)}");
            }
            finally
            {
                _link = null;
                if (conn is not null)
                    try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* it has gone: nothing to close properly */ }
            }
            try { await Task.Delay(_retry, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Stop looking and disconnect.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        try { _run.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelled */ }
        _link = null;
    }
}
