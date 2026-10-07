using System.Diagnostics;

namespace DescendersBridge.Ant;

/// <summary>
/// The ride's trainer source across a lost ANT+ stick. The garage laptop drops the stick for a moment when Descenders starts (seen on every
/// build since 2026-09-30, garage logs), and a ride that ended there took its session with it: the workout started in the window was
/// gone and the rider had to connect again (the "Start riding" button of the time) and start it again. So the ride loop keeps this one source for the whole ride: when the
/// session under it reports <see cref="ITrainerSource.LinkLost"/> it is disposed and a new one is opened every <see cref="RetrySeconds"/>
/// until the stick answers again. Meanwhile there is no trainer and no reading (<see cref="LinkLost"/> true, values null), so the game is
/// told the trainer is not connected, the workout clock holds (nobody pedals) and nothing is written; mode, workout and plan session are
/// untouched. No ANT types here, so `selftest` drives it with fakes (tests\unit\StickRecoveryTests.cs).
/// </summary>
public sealed class RecoveringSource : ITrainerSource, IDisposable
{
    public const double RetrySeconds = 3;

    private readonly Func<Task<ITrainerSource>> _reopen;
    private readonly Action<string> _log;
    private readonly Action<ITrainerSource?>? _changed;
    private readonly TimeSpan _retry;
    private readonly CancellationTokenSource _stop = new();
    private volatile ITrainerSource? _inner;
    private volatile Task _recovery = Task.CompletedTask;
    private readonly object _gate = new();

    /// <param name="first">the session the ride opened</param>
    /// <param name="reopen">opens a new one; throws while the stick is not back</param>
    /// <param name="changed">called with null when the stick is lost and with the new session when it is back (the window reads the live one)</param>
    public RecoveringSource(ITrainerSource first, Func<Task<ITrainerSource>> reopen, Action<string> log, Action<ITrainerSource?>? changed = null, double retrySeconds = RetrySeconds)
    {
        _inner = first; _reopen = reopen; _log = log; _changed = changed;
        _retry = TimeSpan.FromSeconds(retrySeconds);
    }

    /// <summary>The session in use, null while the stick is gone.</summary>
    public ITrainerSource? Inner => _inner;
    /// <summary>How many times the stick has come back.</summary>
    public int Recoveries { get; private set; }
    /// <summary>Ends when the stick is back (or this is disposed); completed while nothing is being recovered. For tests.</summary>
    internal Task Recovery => _recovery;

    /// <summary>The session to read, null while the stick is gone. Noticing a lost stick starts the recovery, once.</summary>
    private ITrainerSource? Live()
    {
        var s = _inner;
        if (s is null || !s.LinkLost) return s;
        lock (_gate)
            if (ReferenceEquals(_inner, s)) { _inner = null; _recovery = Task.Run(() => RecoverAsync(s)); }
        return null;
    }

    private async Task RecoverAsync(ITrainerSource lost)
    {
        _log($"ANT+ stick lost: the ride, its mode and its workout carry on; looking for the stick every {_retry.TotalSeconds:0.#} s (check its USB plug)");
        _changed?.Invoke(null);
        try { (lost as IDisposable)?.Dispose(); } catch { /* the stick is gone: nothing to close properly */ }
        var gone = Stopwatch.StartNew();
        for (int tries = 1; !_stop.IsCancellationRequested; tries++)
        {
            try { await Task.Delay(_retry, _stop.Token); } catch (OperationCanceledException) { return; }
            ITrainerSource next;
            try { next = await _reopen(); }
            catch (Exception ex)
            {
                if (tries == 1 || tries % 10 == 0) _log($"ANT+ stick not back yet after {gone.Elapsed.TotalSeconds:0} s: {ex.Message}");
                continue;
            }
            if (_stop.IsCancellationRequested) { (next as IDisposable)?.Dispose(); return; }   // the ride stopped while it opened
            Recoveries++;
            _inner = next;
            _changed?.Invoke(next);
            _log($"ANT+ stick back after {gone.Elapsed.TotalSeconds:0} s: the trainer and sensors are reconnecting, the ride carries on");
            return;
        }
    }

    /// <summary>What the game's HUD says while the stick is gone.</summary>
    public const string StickLostText = "ANT+ stick lost: reconnecting";

    public bool LinkLost => Live() is null;
    public string LinkLostText => LinkLost ? StickLostText : "";
    public ITrainerControl? Control => Live()?.Control;
    public bool TrainerPaired => Live()?.TrainerPaired ?? false;
    public int? PowerW => Live()?.PowerW;
    public double? CadenceRpm => Live()?.CadenceRpm;
    public int? HeartRateBpm => Live()?.HeartRateBpm;
    public string HeartRateDiagnosis => Live()?.HeartRateDiagnosis ?? "";
    public bool Muted => Live()?.Muted ?? false;

    /// <summary>Stop looking for the stick and close the session in use.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        try { _recovery.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelled */ }
        ITrainerSource? s;
        lock (_gate) { s = _inner; _inner = null; }
        try { (s as IDisposable)?.Dispose(); } catch { }
    }
}
