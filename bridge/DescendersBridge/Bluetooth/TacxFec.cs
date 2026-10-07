using System.Diagnostics;
using DescendersBridge.Ant;

namespace DescendersBridge.Bluetooth;

/// <summary>
/// Tacx's own Bluetooth trainer service, "FE-C over Bluetooth" (the garage's Tacx Neo 2 answered "no Fitness
/// Machine Service", so the FTMS link could never connect to it). The trainer speaks the same ANT+ FE-C pages as over the stick, each
/// wrapped in an ANT serial frame: sync 0xA4, length 9, message id (0x4E broadcast from the trainer, 0x4F acknowledged to it), channel 5,
/// the 8-byte page, then the XOR of every byte before. Pure bytes; the pages themselves are <see cref="FecParser"/> / <see cref="FecControlPages"/>.
/// </summary>
public static class TacxFec
{
    public static readonly Guid Service = BleRules.TacxTrainerService;
    /// <summary>The trainer's pages (notify).</summary>
    public static readonly Guid ReadChar = new("6e40fec2-b5a3-f393-e0a9-e50e24dcca9e");
    /// <summary>The pages written to the trainer.</summary>
    public static readonly Guid WriteChar = new("6e40fec3-b5a3-f393-e0a9-e50e24dcca9e");

    public const byte Sync = 0xA4, PageLength = 9, Broadcast = 0x4E, Acknowledged = 0x4F, Channel = 0x05;
    public const int FrameLength = 13;

    private static byte Xor(byte[] bytes, int count)
    {
        byte x = 0;
        for (int i = 0; i < count; i++) x ^= bytes[i];
        return x;
    }

    /// <summary>One 8-byte FE-C page as the 13 bytes written to the trainer.</summary>
    public static byte[] Frame(byte[] page)
    {
        if (page.Length != 8) throw new ArgumentException("an FE-C page is 8 bytes", nameof(page));
        var f = new byte[FrameLength];
        f[0] = Sync; f[1] = PageLength; f[2] = Acknowledged; f[3] = Channel;
        Array.Copy(page, 0, f, 4, 8);
        f[12] = Xor(f, 12);
        return f;
    }

    /// <summary>The 8-byte page of one notification; null = not a page frame, or its checksum is wrong.</summary>
    public static byte[]? Unframe(byte[]? value)
    {
        if (value is null || value.Length < 12 || value[0] != Sync || value[1] != PageLength) return null;
        if (value[2] != Broadcast && value[2] != Acknowledged) return null;
        if (value.Length >= FrameLength && value[12] != Xor(value, 12)) return null;
        return value[4..12];
    }
}

/// <summary>
/// One connected Tacx trainer over its own service (<see cref="TacxFec"/>): the readings from FE-C pages 16 / 25 and the two control
/// writes as pages 0x33 (gradient) / 0x31 (ERG target), the same pages the ANT+ link sends. Rules:
///  - this protocol has no answer to a command: a write is true when the trainer accepted the Bluetooth write, false when it did not
///    or the trainer has gone;
///  - one write at a time;
///  - a reading older than <see cref="StaleSeconds"/> is null: absent is never zero.
/// Nothing is sent on connecting (the trainer keeps its own rider weight, 75 kg by default). The link dies with its connection.
/// </summary>
public sealed class TacxFecTrainerLink : IBleTrainerLink
{
    public const double StaleSeconds = 3.0;

    private readonly IBleConnection _conn;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<byte, bool> _said = new();   // per page: the outcome last logged (a repeat is not logged again)

    private int _powerW;
    private double _cadenceRpm, _speedMps;
    private long _powerTicks, _cadenceTicks, _speedTicks, _dataTicks;   // Stopwatch timestamps, 0 = never
    private long _messages;
    private double _heldGrade;
    private double _heldTargetW = double.NaN;

    public TacxFecTrainerLink(IBleConnection conn, Action<string> log)
    {
        _conn = conn; _log = log;
    }

    public string Protocol => "Tacx's own trainer service";
    public bool Connected => _conn.Connected;
    /// <summary>FE-C pages heard.</summary>
    public long Messages => Interlocked.Read(ref _messages);
    public double SecondsSinceLast => Age(Interlocked.Read(ref _dataTicks));
    public double HeldGradePercent => Volatile.Read(ref _heldGrade);
    /// <summary>The ERG target last written, NaN when none is held.</summary>
    public double LastTargetPowerW => Volatile.Read(ref _heldTargetW);

    public int? PowerW { get { lock (_gate) return Age(_powerTicks) <= StaleSeconds ? _powerW : null; } }
    public double? CadenceRpm { get { lock (_gate) return Age(_cadenceTicks) <= StaleSeconds ? _cadenceRpm : null; } }
    public double? SpeedMps { get { lock (_gate) return Age(_speedTicks) <= StaleSeconds ? _speedMps : null; } }

    private static double Age(long ticks) => ticks == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(ticks).TotalSeconds;

    /// <summary>Subscribe to the trainer's pages. Throws when the device does not have Tacx's service.</summary>
    public Task OpenAsync(CancellationToken ct = default) =>
        _conn.SubscribeAsync(TacxFec.Service, TacxFec.ReadChar, indicate: false, OnFrame, ct);

    private void OnFrame(byte[] value)
    {
        if (TacxFec.Unframe(value) is not { } page) return;
        long now = Stopwatch.GetTimestamp();
        if (FecParser.Parse(page) is { } t)
            lock (_gate)
            {
                // an "invalid" field makes that reading absent at once, the last good value is not held
                if (t.Page == FecParser.PageSpecificTrainerData)
                {
                    if (t.InstantaneousPowerWatts is { } w) { _powerW = w; _powerTicks = now; } else _powerTicks = 0;
                    if (t.CadenceRpm is { } rpm) { _cadenceRpm = rpm; _cadenceTicks = now; } else _cadenceTicks = 0;
                }
                else if (t.Page == FecParser.PageGeneralFeData)
                {
                    if (t.SpeedMetersPerSecond is { } v) { _speedMps = v; _speedTicks = now; } else _speedTicks = 0;
                }
            }
        Interlocked.Exchange(ref _dataTicks, now);
        Interlocked.Increment(ref _messages);
    }

    private async Task<bool> WriteAsync(byte[] page, string what, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool written = false;
            if (_conn.Connected)
                try { written = await _conn.WriteAsync(TacxFec.Service, TacxFec.WriteChar, TacxFec.Frame(page), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { written = false; }   // the device has gone
            bool changed;
            lock (_said) { changed = !_said.TryGetValue(page[0], out bool before) || before != written; _said[page[0]] = written; }
            if (changed && !written) _log($"WARNING: {what}: {(_conn.Connected ? "the trainer did not accept the write" : "not connected")}; the trainer's resistance is unchanged");
            return written;
        }
        finally { _one.Release(); }
    }

    public async Task<bool> SetTrackResistanceAsync(double gradePercent, double crr, CancellationToken ct = default)
    {
        bool ok = await WriteAsync(FecControlPages.TrackResistance(gradePercent, crr), "track resistance (page 0x33)", ct).ConfigureAwait(false);
        if (ok) { Volatile.Write(ref _heldGrade, gradePercent); Volatile.Write(ref _heldTargetW, double.NaN); }   // simulation ends ERG
        return ok;
    }

    public async Task<bool> SetTargetPowerAsync(double watts, CancellationToken ct = default)
    {
        bool ok = await WriteAsync(FecControlPages.TargetPower(watts), "target power (page 0x31)", ct).ConfigureAwait(false);
        if (ok) Volatile.Write(ref _heldTargetW, FecControlPages.TargetPowerField.Sent(watts));
        return ok;
    }

    /// <summary>End of the ride: gradient 0 (which also ends an ERG target), retried until <paramref name="budget"/> is spent.</summary>
    public async Task<ReleaseResult> ReleaseAsync(TimeSpan budget, CancellationToken ct = default)
    {
        double held = HeldGradePercent;
        var clock = Stopwatch.StartNew();
        do
        {
            if (!_conn.Connected) return new(ReleaseOutcome.LinkAlreadyDead, held);
            try
            {
                if (await SetTrackResistanceAsync(0.0, double.NaN, ct).ConfigureAwait(false)) return new(ReleaseOutcome.Released, held);
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
        while (clock.Elapsed < budget);
        return new(_conn.Connected ? ReleaseOutcome.Failed : ReleaseOutcome.LinkAlreadyDead, held);
    }
}
