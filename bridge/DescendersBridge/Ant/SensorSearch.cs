namespace DescendersBridge.Ant;

/// <summary>Where one sensor channel is: closed and waiting for its next turn, searching for the sensor, or receiving it.</summary>
public enum SensorState { Waiting, Searching, Tracking }

/// <summary>What the session should do with a sensor channel now.</summary>
public enum SensorStep { None, Open, Close }

/// <summary>
/// A chosen sensor that is not there (a heart-rate strap left switched off) must not harm the trainer channel. A sensor channel
/// used to search for ever at high priority, which interrupts the other open channels: every trainer write timed out for the whole ride.
/// Now a sensor search is short and low priority (it does not interrupt other channels), the channel is closed when it ends, and it is
/// opened again at intervals, so a sensor switched on later is still picked up. On a stick without low-priority search the search is
/// shorter and the pauses grow. Pure numbers and rules (no radio), tested in <c>tests\unit\SensorSearchTests.cs</c>.
/// </summary>
public static class SensorSearchRules
{
    /// <summary>One low-priority search: long enough to find a sensor that is on (heart rate takes 5-7 s to lock).</summary>
    public const double SearchSeconds = 10;
    /// <summary>Pause between low-priority searches for a sensor that was not found.</summary>
    public const double RetrySeconds = 30;
    /// <summary>A stick without low-priority search: the search interrupts the trainer, so it is short...</summary>
    public const double FallbackSearchSeconds = 5;
    /// <summary>...and the pause doubles from this up to <see cref="FallbackMaxRetrySeconds"/>.</summary>
    public const double FallbackRetrySeconds = 60, FallbackMaxRetrySeconds = 300;
    /// <summary>A sensor that was being received and dropped out gets one more search almost at once.</summary>
    public const double DropRetrySeconds = 2;
    /// <summary>The stick ends a search by itself; if it has not this long after the search time, the channel is closed by the bridge.</summary>
    public const double CloseGraceSeconds = 3;
    /// <summary>A closed channel is not reopened sooner than this (the stick needs a moment to close it).</summary>
    public const double MinReopenSeconds = 1;
    /// <summary>A channel that should be receiving but has been silent this long, with no word from the stick, is closed and searched for again.</summary>
    public const double SilentTrackingSeconds = 20;
    /// <summary>While the trainer is searching (at the start or after a link loss) no sensor searches, for at most this long.</summary>
    public const double TrainerHoldSeconds = 30;
    /// <summary>The trainer channel's search priority; sensor channels keep the default 0, so the trainer is searched for first.</summary>
    public const byte TrainerSearchPriority = 10;

    public static double SearchWindow(bool lowPriority) => lowPriority ? SearchSeconds : FallbackSearchSeconds;

    /// <summary>A search time in the stick's 2.5 s units (1..254: 0 means "no search" and 255 "for ever").</summary>
    public static byte TimeoutUnits(double seconds) => (byte)Math.Clamp((int)Math.Ceiling(seconds / 2.5), 1, 254);

    /// <summary>The pause after a search that found nothing. <paramref name="misses"/>: searches in a row without the sensor (1 = the first).</summary>
    public static double RetryGap(int misses, bool lowPriority, bool heardBefore)
    {
        if (heardBefore && misses <= 1) return DropRetrySeconds;
        if (lowPriority) return RetrySeconds;
        int doublings = Math.Clamp(misses - 1, 0, 8);
        return Math.Min(FallbackMaxRetrySeconds, FallbackRetrySeconds * (1 << doublings));
    }

    /// <summary>The trainer has the radio to itself: chosen, not paired, and not searching for longer than <see cref="TrainerHoldSeconds"/>.</summary>
    public static bool TrainerHolds(bool trainerChosen, bool paired, double secondsUnpaired) =>
        trainerChosen && !paired && secondsUnpaired < TrainerHoldSeconds;

    /// <summary>Which misses go to the log: the first, then every tenth (a ride without the sensor must not fill the log).</summary>
    public static bool ReportMiss(int misses) => misses == 1 || (misses > 0 && misses % 10 == 0);

    /// <summary>The log line for a search that found nothing.</summary>
    public static string MissText(DeviceKey key, int misses, bool lowPriority, bool heardBefore)
    {
        string again = $"looking again in {RetryGap(misses, lowPriority, heardBefore):0} s";
        if (heardBefore) return $"{key} has gone quiet: the ride carries on without it, {again} (search {misses})";
        return $"{key} not found: the ride carries on without it, {again} (search {misses}). Is it switched on?";
    }
}

/// <summary>
/// One sensor channel's search state over time (seconds on any steady clock, passed in). The channel reports what the radio did
/// (<see cref="Opened"/>, <see cref="Data"/>, <see cref="WentToSearch"/>, <see cref="Closed"/>); <see cref="Next"/> says what to do.
/// Not thread safe: the owner locks.
/// </summary>
public sealed class SensorSearch
{
    private double _since;        // when the current state began (Searching: the search; Waiting: the close)
    private double _lastData;
    private double _openAt;       // Waiting: not before this

    public SensorState State { get; private set; } = SensorState.Waiting;
    /// <summary>Searches in a row that ended without the sensor.</summary>
    public int Misses { get; private set; }
    public bool HeardBefore { get; private set; }
    /// <summary>The stick has low-priority search (set by the channel once it has configured the radio).</summary>
    public bool LowPriority { get; set; } = true;
    /// <summary>Chosen but not there: at least one search ended without it and it is not being received now.</summary>
    public bool NotFound => Misses > 0 && State != SensorState.Tracking;

    public SensorStep Next(double now, bool anotherSearching, bool trainerHolds)
    {
        switch (State)
        {
            case SensorState.Waiting:
                return now >= _openAt && !anotherSearching && !trainerHolds ? SensorStep.Open : SensorStep.None;
            case SensorState.Searching:
                if (trainerHolds) return SensorStep.Close;   // the trainer re-pairs first
                return now - _since >= SensorSearchRules.SearchWindow(LowPriority) + SensorSearchRules.CloseGraceSeconds ? SensorStep.Close : SensorStep.None;
            default:
                return now - _lastData >= SensorSearchRules.SilentTrackingSeconds ? SensorStep.Close : SensorStep.None;
        }
    }

    /// <summary>The channel was opened: a search has begun.</summary>
    public void Opened(double now) { State = SensorState.Searching; _since = now; }

    /// <summary>The channel could not be opened: try again after the normal pause.</summary>
    public void OpenFailed(double now) { State = SensorState.Waiting; _since = now; _openAt = now + SensorSearchRules.RetrySeconds; }

    /// <summary>A message from the sensor. True when this is news (it was being searched for). Ignored on a closed channel (a late message).</summary>
    public bool Data(double now)
    {
        if (State == SensorState.Waiting) return false;
        bool found = State == SensorState.Searching;
        State = SensorState.Tracking; _lastData = now; Misses = 0; HeardBefore = true;
        return found;
    }

    /// <summary>The stick lost the sensor and is searching for it again.</summary>
    public void WentToSearch(double now)
    {
        if (State != SensorState.Tracking) return;
        State = SensorState.Searching; _since = now;
    }

    /// <summary>
    /// The channel is closed (the stick's search ran out, or the bridge closed it). <paramref name="miss"/>: the search had its full time
    /// (false when it was cut short for the trainer: then it is opened again as soon as allowed). True when it changed anything
    /// (a second report of the same close does not).
    /// </summary>
    public bool Closed(double now, bool miss)
    {
        if (State == SensorState.Waiting) return false;
        State = SensorState.Waiting; _since = now;
        double gap = SensorSearchRules.MinReopenSeconds;
        if (miss)
        {
            Misses++;
            gap = Math.Max(gap, SensorSearchRules.RetryGap(Misses, LowPriority, HeardBefore));
        }
        _openAt = now + gap;
        return true;
    }
}
