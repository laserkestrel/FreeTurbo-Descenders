using System.Diagnostics;
using DescendersBridge.Ant;

namespace DescendersBridge.Bluetooth;

/// <summary>One connected Bluetooth trainer as <see cref="FtmsSession"/> holds it, whichever protocol it speaks: the standard Fitness
/// Machine Service (<see cref="FtmsTrainerLink"/>) or Tacx's own service (<see cref="TacxFecTrainerLink"/>).</summary>
public interface IBleTrainerLink : ITrainerControl
{
    /// <summary>For the log: "the Fitness Machine Service" / "Tacx's own trainer service".</summary>
    string Protocol { get; }
    bool Connected { get; }
    long Messages { get; }
    double SecondsSinceLast { get; }
    /// <summary>The grade the trainer last took, for the release warning.</summary>
    double HeldGradePercent { get; }
    int? PowerW { get; }
    double? CadenceRpm { get; }
    double? SpeedMps { get; }
    Task OpenAsync(CancellationToken ct = default);
    Task<ReleaseResult> ReleaseAsync(TimeSpan budget, CancellationToken ct = default);
}

/// <summary>
/// One connected FTMS trainer: its readings (Indoor Bike Data) and the control writes of <see cref="ITrainerControl"/> through
/// the Fitness Machine Control Point. Plain C# over <see cref="IBleConnection"/>. Rules:
///  - Request Control once after connecting, and again when the trainer answers "control not permitted" (the write is then tried once more);
///  - a write is true only when the trainer's response indication (0x80, the same op code) says success: a write it merely received, an
///    answer that does not come within <see cref="ResponseSeconds"/> or any other result is false, like an unacknowledged FE-C page;
///  - one command at a time (the control point allows no overlap);
///  - an ERG target is clamped to the trainer's Supported Power Range when it publishes one;
///  - a reading older than <see cref="StaleSeconds"/> is null: absent is never zero.
/// The link dies with its connection; <see cref="FtmsSession"/> makes a new one when the trainer is back.
/// </summary>
public sealed class FtmsTrainerLink : IBleTrainerLink
{
    public string Protocol => "the Fitness Machine Service";

    public const double ResponseSeconds = 3.0, StaleSeconds = 3.0;

    private readonly IBleConnection _conn;
    private readonly Action<string> _log;
    private readonly TimeSpan _responseWait;
    private readonly SemaphoreSlim _one = new(1, 1);                 // one control-point procedure at a time
    private readonly object _gate = new();
    private TaskCompletionSource<FtmsResult>? _pending;
    private byte _pendingOp;
    private readonly Dictionary<byte, string> _said = new();         // per op code: the outcome last logged (a repeat is not logged again)

    private IndoorBikeData _last;
    private long _powerTicks, _cadenceTicks, _speedTicks, _dataTicks;   // Stopwatch timestamps, 0 = never
    private long _messages;
    private double _heldGrade;
    private double _heldTargetW = double.NaN;

    public FtmsTrainerLink(IBleConnection conn, Action<string> log, double responseSeconds = ResponseSeconds)
    {
        _conn = conn; _log = log;
        _responseWait = TimeSpan.FromSeconds(responseSeconds);
    }

    /// <summary>The trainer's power range, null when it publishes none.</summary>
    public FtmsPowerRange? PowerRange { get; private set; }
    public bool Connected => _conn.Connected;
    /// <summary>Indoor Bike Data notifications heard.</summary>
    public long Messages => Interlocked.Read(ref _messages);
    public double SecondsSinceLast => Age(Interlocked.Read(ref _dataTicks));
    /// <summary>The grade the trainer last acknowledged, for the release warning.</summary>
    public double HeldGradePercent => Volatile.Read(ref _heldGrade);

    public int? PowerW { get { lock (_gate) return Age(_powerTicks) <= StaleSeconds ? _last.PowerW : null; } }
    public double? CadenceRpm { get { lock (_gate) return Age(_cadenceTicks) <= StaleSeconds ? _last.CadenceRpm : null; } }
    public double? SpeedMps { get { lock (_gate) return Age(_speedTicks) <= StaleSeconds ? _last.SpeedMps : null; } }

    private static double Age(long ticks) => ticks == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(ticks).TotalSeconds;

    /// <summary>Subscribe to the readings and the control point, read the power range, take control. Throws when the trainer has no usable
    /// Fitness Machine Service (the connection attempt then counts as failed).</summary>
    public async Task OpenAsync(CancellationToken ct = default)
    {
        await _conn.SubscribeAsync(Ftms.Service, Ftms.ControlPointChar, indicate: true, OnControlPoint, ct).ConfigureAwait(false);
        await _conn.SubscribeAsync(Ftms.Service, Ftms.IndoorBikeDataChar, indicate: false, OnIndoorBikeData, ct).ConfigureAwait(false);

        byte[]? range = null;
        try { range = await _conn.ReadAsync(Ftms.Service, Ftms.SupportedPowerRangeChar, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log("power range not read (" + ex.Message + "): ERG targets are not clamped to the trainer's range"); }
        PowerRange = range is null ? null : Ftms.DecodePowerRange(range);
        _log(PowerRange is { } r ? $"power range {r.MinW}..{r.MaxW} W in steps of {r.StepW} W" : "the trainer publishes no power range");

        if (!await RequestControlAsync(ct).ConfigureAwait(false))
            _log("WARNING: the trainer did not grant control yet (another app connected to it?): asked again with the next write");
    }

    private void OnIndoorBikeData(byte[] value)
    {
        if (Ftms.DecodeIndoorBikeData(value) is not { } d) return;
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            // each field keeps its own age: a trainer may split its data over two packets ("more data")
            if (d.PowerW.HasValue) { _last = _last with { PowerW = d.PowerW }; _powerTicks = now; }
            if (d.CadenceRpm.HasValue) { _last = _last with { CadenceRpm = d.CadenceRpm }; _cadenceTicks = now; }
            if (d.SpeedMps.HasValue) { _last = _last with { SpeedMps = d.SpeedMps }; _speedTicks = now; }
        }
        Interlocked.Exchange(ref _dataTicks, now);
        Interlocked.Increment(ref _messages);
    }

    private void OnControlPoint(byte[] value)
    {
        if (Ftms.DecodeResponse(value) is not { } r) return;
        TaskCompletionSource<FtmsResult>? waiting = null;
        lock (_gate)
            if (_pending is not null && _pendingOp == r.RequestOpCode) { waiting = _pending; _pending = null; }
        waiting?.TrySetResult(r.Result);
    }

    /// <summary>One procedure: write the command, wait for its response indication. Null = the write did not go through or nothing answered.</summary>
    private async Task<FtmsResult?> ProcedureAsync(byte[] command, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<FtmsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _pending = answer; _pendingOp = command[0]; }
        try
        {
            if (!_conn.Connected) return null;
            bool written;
            try { written = await _conn.WriteAsync(Ftms.Service, Ftms.ControlPointChar, command, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { written = false; }   // the device has gone
            if (!written) return null;
            try { return await answer.Task.WaitAsync(_responseWait, ct).ConfigureAwait(false); }
            catch (TimeoutException) { return null; }
        }
        finally { lock (_gate) if (ReferenceEquals(_pending, answer)) _pending = null; }
    }

    private async Task<bool> RequestControlAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try { return Said(Ftms.OpRequestControl, "request control", await ProcedureAsync(Ftms.RequestControl(), ct).ConfigureAwait(false)); }
        finally { _one.Release(); }
    }

    /// <summary>A command with the "control not permitted" rule: ask for control again, then send it once more.</summary>
    private async Task<bool> CommandAsync(byte[] command, string what, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await ProcedureAsync(command, ct).ConfigureAwait(false);
            if (r == FtmsResult.ControlNotPermitted)
            {
                _log($"{what}: control not permitted, asking the trainer for control again");
                if (Said(Ftms.OpRequestControl, "request control", await ProcedureAsync(Ftms.RequestControl(), ct).ConfigureAwait(false)))
                    r = await ProcedureAsync(command, ct).ConfigureAwait(false);
            }
            return Said(command[0], what, r);
        }
        finally { _one.Release(); }
    }

    /// <summary>Log an outcome when it differs from the last one for this op code (a write repeats every second); true = success.</summary>
    private bool Said(byte op, string what, FtmsResult? r)
    {
        string outcome = r is { } x ? Ftms.Words(x) : _conn.Connected ? "no answer" : "not connected";
        bool changed;
        lock (_said) { changed = !_said.TryGetValue(op, out var before) || before != outcome; _said[op] = outcome; }
        if (changed && r != FtmsResult.Success) _log($"WARNING: {what}: {outcome}; the trainer's resistance is unchanged");
        else if (changed && op == Ftms.OpRequestControl) _log("control granted");
        return r == FtmsResult.Success;
    }

    public async Task<bool> SetTrackResistanceAsync(double gradePercent, double crr, CancellationToken ct = default)
    {
        bool ok = await CommandAsync(Ftms.SetIndoorBikeSimulation(gradePercent, crr), "simulation grade (0x11)", ct).ConfigureAwait(false);
        if (ok) { Volatile.Write(ref _heldGrade, gradePercent); Volatile.Write(ref _heldTargetW, double.NaN); }   // simulation ends ERG
        return ok;
    }

    public async Task<bool> SetTargetPowerAsync(double watts, CancellationToken ct = default)
    {
        int w = Ftms.TargetWatts(watts, PowerRange);
        bool ok = await CommandAsync(Ftms.SetTargetPower(w), "target power (0x05)", ct).ConfigureAwait(false);
        if (ok) Volatile.Write(ref _heldTargetW, w);
        return ok;
    }

    /// <summary>The ERG target the trainer last acknowledged (after the range clamp), NaN when none is held.</summary>
    public double LastTargetPowerW => Volatile.Read(ref _heldTargetW);

    /// <summary>
    /// End of the ride, the same promise as ANT+: take the load off before disconnecting. Simulation grade 0, then Reset (0x01, which also
    /// hands control back). Released = the trainer acknowledged at least one of the two; retried until <paramref name="budget"/> is spent.
    /// </summary>
    public async Task<ReleaseResult> ReleaseAsync(TimeSpan budget, CancellationToken ct = default)
    {
        double held = HeldGradePercent;
        var clock = Stopwatch.StartNew();
        do
        {
            if (!_conn.Connected) return new(ReleaseOutcome.LinkAlreadyDead, held);
            bool flat, reset;
            try
            {
                flat = await SetTrackResistanceAsync(0.0, double.NaN, ct).ConfigureAwait(false);
                reset = await CommandAsync(Ftms.Reset(), "reset (0x01)", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            if (flat || reset) return new(ReleaseOutcome.Released, held);
            try { await Task.Delay(150, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        while (clock.Elapsed < budget);
        return new(_conn.Connected ? ReleaseOutcome.Failed : ReleaseOutcome.LinkAlreadyDead, held);
    }
}

/// <summary>
/// The Bluetooth trainer of a ride, across disconnects (what <see cref="RecoveringSource"/> is to the ANT+ stick; the trainer
/// speaks the Fitness Machine Service or, when it has none, Tacx's own service: <see cref="IBleTrainerLink"/>). It looks for
/// the trainer from the start and every <see cref="RetrySeconds"/> after a loss (out of range, switched off and on); meanwhile there is no
/// trainer and no reading, so the game is told the trainer is not connected, the workout clock holds and nothing is written. The ride, its
/// mode and its workout are untouched, and <see cref="RideLoop"/> writes its target again on the first tick the trainer is back (it forgets
/// what it sent whenever the trainer is not paired). Starting a ride with the trainer still switched off is not an error: it is searched for.
/// </summary>
public sealed class FtmsSession : ITrainerControl, IDisposable
{
    public const double RetrySeconds = 3;

    private readonly IBleRadio _radio;
    private readonly ulong _address;
    private readonly string _name;
    private readonly Action<string> _log;
    private readonly TimeSpan _retry;
    private readonly CancellationTokenSource _stop = new();
    private volatile IBleTrainerLink? _link;
    private Task _run = Task.CompletedTask;
    private int _connections;

    public FtmsSession(IBleRadio radio, ulong address, string? name, Action<string> log, double retrySeconds = RetrySeconds)
    {
        _radio = radio; _address = address; _log = log;
        _name = string.IsNullOrWhiteSpace(name) ? BleAddress.Format(address) : name.Trim() + " (" + BleAddress.Format(address) + ")";
        _retry = TimeSpan.FromSeconds(retrySeconds);
    }

    public DeviceKey Key => DeviceKey.Bluetooth(AntDeviceTypes.FitnessEquipment, _address);
    /// <summary>The live link, null while the trainer is not connected.</summary>
    public IBleTrainerLink? Link => _link is { Connected: true } l ? l : null;
    public bool Paired => Link is not null;
    /// <summary>How many times the trainer has been connected in this ride (1 = never lost).</summary>
    public int Connections => Volatile.Read(ref _connections);

    public int? PowerW => Link?.PowerW;
    public double? CadenceRpm => Link?.CadenceRpm;
    public double? SpeedMps => Link?.SpeedMps;

    /// <summary>For the window's health light: 0 messages = still searching.</summary>
    public LinkInfo LinkInfo => Link is { } l ? new LinkInfo(Key, l.Messages, l.SecondsSinceLast) : new LinkInfo(Key, 0, double.PositiveInfinity);

    /// <summary>Start looking for the trainer (returns at once).</summary>
    public void Start() => _run = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        _log($"looking for the trainer {_name}");
        var gone = Stopwatch.StartNew();
        for (int tries = 1; !ct.IsCancellationRequested; tries++)
        {
            IBleConnection? conn = null;
            try
            {
                conn = await _radio.ConnectAsync(_address, ct).ConfigureAwait(false);
                var link = await OpenLinkAsync(conn, ct).ConfigureAwait(false);
                bool first = Interlocked.Increment(ref _connections) == 1;
                _link = link;
                _log(first ? $"trainer {_name} connected through {link.Protocol}" : $"trainer back after {gone.Elapsed.TotalSeconds:0} s: the ride carries on, the target is written again");
                tries = 0;
                while (!ct.IsCancellationRequested && conn.Connected) await Task.Delay(100, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;
                _link = null;
                gone.Restart();
                _log($"trainer lost (out of range or switched off): the ride, its mode and its workout carry on; looking for it every {_retry.TotalSeconds:0.#} s");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _link = null;
                if (tries == 1 || tries % 10 == 0) _log($"trainer not connected after {gone.Elapsed.TotalSeconds:0} s: {BleRules.Why(ex)}");
            }
            finally
            {
                if (!ct.IsCancellationRequested && conn is not null)
                    try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* it has gone: nothing to close properly */ }
                else _last = conn;   // the ride is stopping: Dispose closes it, after the release
            }
            try { await Task.Delay(_retry, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private volatile IBleConnection? _last;

    /// <summary>
    /// (the garage's Tacx Neo 2 has no Fitness Machine Service): the standard service first; a trainer that
    /// does not have it is spoken to through Tacx's own service. One that has neither is not connected, and the error names the
    /// services it does have, so a log shows what the device is.
    /// </summary>
    private async Task<IBleTrainerLink> OpenLinkAsync(IBleConnection conn, CancellationToken ct)
    {
        var ftms = new FtmsTrainerLink(conn, _log);
        Exception noFtms;
        try { await ftms.OpenAsync(ct).ConfigureAwait(false); return ftms; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { noFtms = ex; }

        var tacx = new TacxFecTrainerLink(conn, _log);
        try { await tacx.OpenAsync(ct).ConfigureAwait(false); return tacx; }
        catch (OperationCanceledException) { throw; }
        catch { /* not a Tacx either: the first error is the one to tell */ }

        string has = "";
        try
        {
            var services = await conn.ServicesAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (services.Count > 0) has = "; its services: " + string.Join(", ", services.Select(BleRules.ServiceName));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* it would not say */ }
        throw new InvalidOperationException(noFtms.Message + has);
    }

    public Task<bool> SetTrackResistanceAsync(double gradePercent, double crr, CancellationToken ct = default) =>
        Link is { } l ? l.SetTrackResistanceAsync(gradePercent, crr, ct) : Task.FromResult(false);

    public Task<bool> SetTargetPowerAsync(double watts, CancellationToken ct = default) =>
        Link is { } l ? l.SetTargetPowerAsync(watts, ct) : Task.FromResult(false);

    /// <summary>Take the load off before the ride ends (<see cref="FtmsTrainerLink.ReleaseAsync"/>); call before <see cref="Dispose"/>.</summary>
    public Task<ReleaseResult> ReleaseAsync(TimeSpan budget, CancellationToken ct = default) =>
        Link is { } l ? l.ReleaseAsync(budget, ct) : Task.FromResult(new ReleaseResult(ReleaseOutcome.LinkAlreadyDead, _link?.HeldGradePercent ?? 0));

    /// <summary>Stop looking and disconnect.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        try { _run.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelled */ }
        _link = null;
        try { _last?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
