namespace DescendersBridge.Ant;

/// <summary>
/// Rolling record of resistance-write outcomes. A single dropped ack on a 4 Hz
/// ANT link is normal; a sustained run of them is a link problem the rider needs
/// to know about. Raises the alarm once when the failure fraction over the
/// window crosses the threshold, and rate-limits re-alarms.
/// </summary>
public sealed class TxOutcomeWindow
{
    private readonly TimeSpan _span;
    private readonly double _failFraction;
    private readonly int _minSamples;
    private readonly TimeSpan _reAlarmGap;
    private readonly string _subject;
    private readonly Queue<(DateTime At, bool Ok)> _q = new();

    private DateTime _lastAlarm = DateTime.MinValue;

    public TxOutcomeWindow(
        TimeSpan? span = null, double failFraction = 0.10,
        int minSamples = 10, TimeSpan? reAlarmGap = null,
        string subject = "resistance writes")
    {
        _span = span ?? TimeSpan.FromSeconds(30);
        _failFraction = failFraction;
        _minSamples = minSamples;
        _reAlarmGap = reAlarmGap ?? TimeSpan.FromSeconds(30);
        _subject = subject;
    }

    /// <summary>
    /// Record one outcome. Returns a warning string if this pushes the window
    /// over the failure threshold (respecting the re-alarm gap), else null.
    /// </summary>
    public string? Record(bool ok, DateTime now)
    {
        _q.Enqueue((now, ok));
        while (_q.Count > 0 && now - _q.Peek().At > _span) _q.Dequeue();

        int total = _q.Count;
        int failed = 0;
        foreach (var (_, o) in _q) if (!o) failed++;

        if (total < _minSamples || failed <= total * _failFraction) return null;
        if (now - _lastAlarm < _reAlarmGap) return null;

        _lastAlarm = now;
        return $"{failed}/{total} {_subject} failed in the last {_span.TotalSeconds:0} s "
             + "— check the ANT link / trainer";
    }
}
