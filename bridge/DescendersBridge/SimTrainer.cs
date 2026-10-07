using System.Diagnostics;
using System.Globalization;
using DescendersBridge.Ant;

namespace DescendersBridge;

/// <summary>`run --sim` options (what tools\BridgeSim.ps1 used to offer). Parse is pure so `selftest` can check it.</summary>
public sealed record SimOptions
{
    public double Watts { get; init; } = 200;
    /// <summary>"0:150,30:250" = watts from that second on (overrides Watts); empty = none.</summary>
    public IReadOnlyList<(double AtS, double W)> Profile { get; init; } = Array.Empty<(double, double)>();
    /// <summary>Sent while the fake watts are above 0; at 0 W the fake rider stops pedalling and sends 0 rpm.</summary>
    public double Cadence { get; init; } = 85;
    /// <summary>The fake rider stops pedalling (0 W, 0 rpm) from..to seconds (tests the workout pause).</summary>
    public (double From, double To)? IdleBetween { get; init; }
    /// <summary>Stop the `trainer` messages after this many seconds (tests the mod's failsafe).</summary>
    public double? StopSendingAfter { get; init; }

    public const string Usage = "usage: run --sim [--watts N | --watts-profile \"0:150,30:250\"] [--cadence N] [--idle-between from,to] [--stop-sending-after N] [--seconds N] [--log <file.jsonl>] [--workout <file.zwo>]";

    /// <summary>Reads the --sim options out of <paramref name="args"/>; null plus <paramref name="error"/> on a bad value.</summary>
    public static SimOptions? Parse(string[] args, out string error)
    {
        error = "";
        var o = new SimOptions();
        string? Val(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : i >= 0 ? "" : null;
        }
        static bool Num(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v);

        if (Val("--watts") is { } w) { if (!Num(w, out double v) || v < 0) { error = "--watts needs a number >= 0"; return null; } o = o with { Watts = v }; }
        if (Val("--cadence") is { } c) { if (!Num(c, out double v) || v < 0) { error = "--cadence needs a number >= 0"; return null; } o = o with { Cadence = v }; }
        if (Val("--stop-sending-after") is { } s) { if (!Num(s, out double v) || v < 0) { error = "--stop-sending-after needs seconds"; return null; } o = o with { StopSendingAfter = v }; }
        if (Val("--idle-between") is { } ib)
        {
            var p = ib.Split(',');
            if (p.Length != 2 || !Num(p[0], out double a) || !Num(p[1], out double b) || b < a) { error = "--idle-between needs from,to seconds"; return null; }
            o = o with { IdleBetween = (a, b) };
        }
        if (Val("--watts-profile") is { } wp)
        {
            var list = new List<(double, double)>();
            foreach (var part in wp.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');
                if (kv.Length != 2 || !Num(kv[0], out double at) || !Num(kv[1], out double pw) || pw < 0) { error = "--watts-profile needs \"second:watts,...\""; return null; }
                list.Add((at, pw));
            }
            if (list.Count == 0) { error = "--watts-profile is empty"; return null; }
            o = o with { Profile = list.OrderBy(x => x.Item1).ToList() };
        }
        return o;
    }

    /// <summary>The fake rider's own watts at <paramref name="t"/> seconds (before any ERG target), 0 while idle.</summary>
    public double WattsAt(double t)
    {
        double w = Watts;
        if (Profile.Count > 0) { w = 0; foreach (var (at, pw) in Profile) if (t >= at) w = pw; }
        if (IdleBetween is { } idle && t >= idle.From && t < idle.To) w = 0;
        return w;
    }

    public override string ToString() =>
        (Profile.Count > 0 ? "profile " + string.Join(",", Profile.Select(p => $"{p.AtS:0}:{p.W:0}")) : $"{Watts:0} W") + $", {Cadence:0} rpm"
        + (IdleBetween is { } i ? $", idle {i.From:0}-{i.To:0} s" : "") + (StopSendingAfter is { } s ? $", silent after {s:0} s" : "");
}

/// <summary>
/// The fake trainer of `run --sim`: no ANT+ stick, the rider's watts come from <see cref="SimOptions"/>. It behaves like an ERG trainer:
/// while the ride loop holds a target (page 0x31) and the fake rider pedals, the power it reports is the target. So the smoke test and
/// the loop recipe run the real <see cref="Session"/> and <see cref="RideLoop"/>, only the radio is fake.
/// </summary>
public sealed class SimTrainer : ITrainerSource, ITrainerControl
{
    private readonly SimOptions _o;
    private readonly Func<double> _clock;
    private double? _ergW;

    public SimTrainer(SimOptions o, Func<double>? clock = null)
    {
        _o = o;
        if (clock is null) { var sw = Stopwatch.StartNew(); clock = () => sw.Elapsed.TotalSeconds; }
        _clock = clock;
    }

    private bool Stopped => _o.StopSendingAfter is { } s && _clock() >= s;

    public bool LinkLost => false;
    public string LinkLostText => "";
    public ITrainerControl? Control => this;
    public bool TrainerPaired => !Stopped;
    public bool Muted => Stopped;
    public int? HeartRateBpm => null;
    public string HeartRateDiagnosis => "";

    public int? PowerW
    {
        get
        {
            if (Stopped) return null;
            double w = _o.WattsAt(_clock());
            if (w > 0 && _ergW is { } e) w = e;
            return (int)Math.Round(w);
        }
    }

    public double? CadenceRpm => Stopped ? null : _o.WattsAt(_clock()) > 0 ? _o.Cadence : 0;

    public Task<bool> SetTrackResistanceAsync(double gradePercent, double crr, CancellationToken ct = default) { _ergW = null; return Task.FromResult(true); }
    public Task<bool> SetTargetPowerAsync(double watts, CancellationToken ct = default) { _ergW = watts; return Task.FromResult(true); }
}
