using System.Globalization;

namespace DescendersBridge;

/// <summary>
/// The `training` section of the per-user TrainerBridgeConfig.json (moved out of the mod's MelonPreferences.cfg): ERG limits, step and
/// start target, the workout bias step and pause time, and the trainer difficulty. They describe the rider and the trainer, not the game, so a second
/// game needs none of them again. Edited in the bridge window's Resistance tab (all but the pause time: file only); a running ride picks a change up within a second (<see cref="TrainingWatch"/>).
/// </summary>
public sealed record TrainingConfig
{
    /// <summary>Freeride resistance = raw road gradient x this (docs/FORCE_MODEL.md "Gradient scale: split"). 0.35 feels as the mod's old GradientScale 0.35 did;
    /// 1 = the real gradient, 0 = always flat. The game's speed never depends on it (that is the mod's ClimbPhysicsScale).</summary>
    public double TrainerDifficulty { get; init; } = 0.35;
    public double ErgMinW { get; init; } = 30;
    /// <summary>Also limited by config.json's resistance.ErgMaxW (the lower wins, <see cref="SessionConfig.From"/>).</summary>
    public double ErgMaxW { get; init; } = 400;
    public double ErgStepW { get; init; } = 10;
    /// <summary>The manual ERG target a ride starts at.</summary>
    public double ErgDefaultW { get; init; } = 120;
    /// <summary>RB + Left / Right in a workout moves every target by this many percent.</summary>
    public double WorkoutBiasStepPct { get; init; } = 5;
    /// <summary>No power and no cadence for this long pauses the workout clock.</summary>
    public double WorkoutPauseSeconds { get; init; } = 3;
}

/// <summary>Range checks for <see cref="TrainingConfig"/>, shared by loading, saving and the window's fields.</summary>
public static class Training
{
    public const double MinDifficulty = 0, MaxDifficulty = 2;
    public const double MinErgW = 0, MaxErgW = 2000, MinStepW = 1, MaxStepW = 100;
    public const double MinBiasPct = 1, MaxBiasPct = 25, MinPauseS = 1, MaxPauseS = 60;

    /// <summary>Range-check every field (a NaN takes its default) and keep ERG min &lt;= start &lt;= max; <paramref name="clamped"/> names the fields changed ("" = none).</summary>
    public static TrainingConfig Clamp(TrainingConfig t, out string clamped)
    {
        var names = new List<string>();
        double C(double v, double lo, double hi, double dflt, string name)
        {
            double o = double.IsNaN(v) ? dflt : Math.Clamp(v, lo, hi);
            if (o != v) names.Add(name);
            return o;
        }
        var d = new TrainingConfig();
        double min = C(t.ErgMinW, MinErgW, MaxErgW, d.ErgMinW, "ergMinW");
        double max = C(t.ErgMaxW, MinErgW, MaxErgW, d.ErgMaxW, "ergMaxW");
        if (min > max) { max = min; if (!names.Contains("ergMaxW")) names.Add("ergMaxW"); }   // a max below the min is raised to it
        double start = C(t.ErgDefaultW, min, max, Math.Clamp(d.ErgDefaultW, min, max), "ergDefaultW");
        var c = new TrainingConfig
        {
            TrainerDifficulty = C(t.TrainerDifficulty, MinDifficulty, MaxDifficulty, d.TrainerDifficulty, "trainerDifficulty"),
            ErgMinW = min, ErgMaxW = max, ErgDefaultW = start,
            ErgStepW = C(t.ErgStepW, MinStepW, MaxStepW, d.ErgStepW, "ergStepW"),
            WorkoutBiasStepPct = C(t.WorkoutBiasStepPct, MinBiasPct, MaxBiasPct, d.WorkoutBiasStepPct, "workoutBiasStepPct"),
            WorkoutPauseSeconds = C(t.WorkoutPauseSeconds, MinPauseS, MaxPauseS, d.WorkoutPauseSeconds, "workoutPauseSeconds"),
        };
        clamped = string.Join(", ", names);
        return c;
    }

    /// <summary>What the window's Resistance tab saves: the fields it shows, plus the one it does not (the workout pause time,
    /// settings file only), kept as the file has it.</summary>
    public static TrainingConfig KeepHidden(TrainingConfig shown, TrainingConfig onFile) => shown with { WorkoutPauseSeconds = onFile.WorkoutPauseSeconds };

    public static string Describe(TrainingConfig t) =>
        string.Create(CultureInfo.InvariantCulture,
            $"difficulty {t.TrainerDifficulty:0.##}, ERG {t.ErgMinW:0}-{t.ErgMaxW:0} W step {t.ErgStepW:0} W start {t.ErgDefaultW:0} W, workout bias step {t.WorkoutBiasStepPct:0.#} %, pause after {t.WorkoutPauseSeconds:0.#} s");
}

/// <summary>
/// The `training` section as a running ride sees it: read at start, then read again whenever the settings file's write time changes (checked at most
/// once a second), so a change saved in the window, even from a second bridge process, applies mid-ride without a restart. An unreadable file keeps
/// the last good values.
/// </summary>
public sealed class TrainingWatch
{
    private readonly string _path;
    private readonly Action<string> _log;
    private DateTime _stamp, _nextCheck;

    public TrainingConfig Current { get; private set; }

    public TrainingWatch(string path, Action<string> log)
    {
        _path = path; _log = log;
        _stamp = Stamp();
        Current = TrainerBridgeConfig.LoadTraining(path, log);
    }

    /// <summary>True when the section changed since the last call (<see cref="Current"/> then holds the new values).</summary>
    public bool Poll(DateTime now)
    {
        if (now < _nextCheck) return false;
        _nextCheck = now.AddSeconds(1);
        var stamp = Stamp();
        if (stamp == _stamp) return false;
        _stamp = stamp;
        if (!TrainerBridgeConfig.IsReadable(_path, out _)) return false;
        var t = TrainerBridgeConfig.LoadTraining(_path, _log);
        if (t == Current) return false;
        Current = t;
        _log("Training settings changed: " + Training.Describe(t));
        return true;
    }

    private DateTime Stamp()
    {
        try { return File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue; }
        catch (Exception) { return DateTime.MinValue; }
    }
}
