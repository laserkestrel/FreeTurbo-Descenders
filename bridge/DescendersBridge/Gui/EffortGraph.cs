using FreeTurboDescenders;

namespace DescendersBridge.Gui;

// The pure model of the Workouts tab's effort graph: a workout's steps as blocks with a zone each, and the line of text under
// them. No window code here, so `selftest` drives it (tests\unit\WorkoutsTabTests.cs). The control that paints it is EffortGraphView.

/// <summary>How hard a block is, by its target as a share of FTP: below 60 %, 60-75 %, 76-90 %, 91-105 %, above 105 %.</summary>
public enum EffortZone { Easy, Endurance, Tempo, Threshold, Hard }

/// <summary>
/// One block of the graph. <see cref="X0"/> / <see cref="X1"/>: where it starts and ends, as a share (0..1) of the workout's time.
/// <see cref="Y0"/> / <see cref="Y1"/>: its height at the start and the end, as a share of the graph's height (different = a slope).
/// A free-ride block has no target: it is drawn as a dashed outline.
/// </summary>
public readonly record struct EffortBlock(float X0, float X1, float Y0, float Y1, EffortZone Zone, bool FreeRide);

internal sealed class EffortGraph
{
    /// <summary>Where one zone ends and the next begins, in % of FTP (targets are rounded to a whole % first).</summary>
    private static readonly float[] ZoneEdges = { 59.5f, 75.5f, 90.5f, 105.5f };

    /// <summary>The pixels left blank between one block and the next (2, it was a hairline of 1).</summary>
    public const float SegmentGap = 2f;

    /// <summary>
    /// Where a block drawn from <paramref name="x0"/> to <paramref name="x1"/> (pixels) ends once the gap to the next block is taken off
    /// its right; a block too narrow to lose it keeps its width.
    /// </summary>
    public static float BlockEnd(float x0, float x1) => x1 - x0 > SegmentGap + 2f ? x1 - SegmentGap : x1;

    /// <summary>Nothing chosen: no graph, no words.</summary>
    public static readonly EffortGraph Empty = Note("");

    public IReadOnlyList<EffortBlock> Blocks { get; private init; } = Array.Empty<EffortBlock>();
    /// <summary>The height of the dashed FTP line, as a share of the graph's height; 0 = no line (FTP not known).</summary>
    public float FtpY { get; private init; }
    /// <summary>The watts at the top of the graph.</summary>
    public float TopW { get; private init; }
    /// <summary>The line under the graph: the total time and the hardest target.</summary>
    public string Summary { get; private init; } = "";
    /// <summary>Shown in place of the graph (a workout file that is missing or does not load); null = there is a graph.</summary>
    public string? Message { get; private init; }
    public bool Ok => Message is null;

    /// <summary>What the graph's control says to a test or a screen reader: the message, else the summary.</summary>
    public string Text => Message ?? Summary;

    public static EffortGraph Note(string message) => new() { Message = message };

    public static EffortZone ZoneOf(float pctOfFtp)
    {
        double pct = Math.Round(pctOfFtp);
        return pct < 60 ? EffortZone.Easy : pct <= 75 ? EffortZone.Endurance : pct <= 90 ? EffortZone.Tempo : pct <= 105 ? EffortZone.Threshold : EffortZone.Hard;
    }

    /// <summary>The graph of the workout file at <paramref name="path"/>; one that is missing or broken gives a message, never an error.</summary>
    public static EffortGraph Load(string path, float ftpW)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Note("The workout file is missing from the workout library.");
        return From(WorkoutParser.Load(path, ftpW), ftpW);
    }

    public static EffortGraph From(Workout w, float ftpW)
    {
        if (!w.Ok || !(w.TotalS > 0f)) return Note("This workout cannot be loaded: " + (w.Error ?? "no steps"));

        float peak = 0f;
        foreach (WorkoutStep s in w.Steps) if (!s.FreeRide) peak = Math.Max(peak, Math.Max(s.StartW, s.EndW));
        float top = Math.Max(peak, ftpW) * 1.15f;
        if (!(top > 0f)) top = 1f;
        float ftpY = ftpW > 0f ? ftpW / top : 0f;

        var blocks = new List<EffortBlock>();
        foreach (WorkoutStep s in w.Steps)
        {
            float x0 = s.StartS / w.TotalS, x1 = s.EndS / w.TotalS;
            if (s.FreeRide) { float y = ftpY > 0f ? ftpY * 0.6f : 0.5f; blocks.Add(new EffortBlock(x0, x1, y, y, EffortZone.Easy, true)); continue; }
            if (!(ftpW > 0f)) { blocks.Add(new EffortBlock(x0, x1, s.StartW / top, s.EndW / top, EffortZone.Easy, false)); continue; }

            // a slope that crosses a zone edge is cut there, so a warm-up shows each zone it passes through
            float p0 = s.StartW / ftpW * 100f, p1 = s.EndW / ftpW * 100f;
            var cuts = new List<float> { 0f, 1f };
            foreach (float edge in ZoneEdges)
                if ((edge - p0) * (edge - p1) < 0f) cuts.Add((edge - p0) / (p1 - p0));
            cuts.Sort();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                float f0 = cuts[i], f1 = cuts[i + 1];
                float w0 = s.StartW + (s.EndW - s.StartW) * f0, w1 = s.StartW + (s.EndW - s.StartW) * f1;
                blocks.Add(new EffortBlock(x0 + (x1 - x0) * f0, x0 + (x1 - x0) * f1, w0 / top, w1 / top, ZoneOf((w0 + w1) / 2f / ftpW * 100f), false));
            }
        }

        string time = WorkoutRunner.Fmt(w.TotalS);
        string summary = !(peak > 0f) ? time + " · free ride, no target"
            : time + " · hardest target " + Math.Round(peak) + " W" + (ftpW > 0f ? " (" + Math.Round(peak / ftpW * 100f) + " % of FTP)" : "");
        return new EffortGraph { Blocks = blocks, FtpY = ftpY, TopW = top, Summary = summary };
    }
}

/// <summary>
/// The graphs already worked out, so stepping through a long list reads each workout file once. A file that changed on the
/// disk, or another FTP, is read again.
/// </summary>
internal sealed class EffortGraphCache
{
    public const int Max = 400;

    private readonly Dictionary<string, (DateTime Stamp, float FtpW, EffortGraph Graph)> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times a file was read, for `selftest`.</summary>
    public int Loads { get; private set; }

    public EffortGraph Get(string path, float ftpW)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); }
        catch { stamp = default; }
        if (_known.TryGetValue(path, out var hit) && hit.Stamp == stamp && hit.FtpW == ftpW) return hit.Graph;
        if (_known.Count >= Max) _known.Clear();
        var graph = EffortGraph.Load(path, ftpW);
        Loads++;
        _known[path] = (stamp, ftpW, graph);
        return graph;
    }
}
