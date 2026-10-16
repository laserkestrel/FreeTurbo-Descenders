using System.Diagnostics;
using DescendersBridge.Gui;
using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// The pure parts of the Workouts tab's new look: the left / right picker (index, neighbours, stop at the ends, the position
/// line, names cut with "…"), the up / down focus order, and the effort graph (steps -> blocks with a zone each, slopes cut at the zone
/// edges, the FTP line, the summary line, free ride, a missing or broken file) with its cache. The focus order without the
/// Start buttons, the hint line, where a click on a picker row starts or steps, how a card shares its spare height (the graph no longer
/// takes all of it), and the 2 px between the graph's blocks. The window itself is driven in
/// WindowDesignTests. Temp folder only. Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class WorkoutsTabTests
{
    private const string Mixed = "<workout_file><name>Mixed</name><workout>"
        + "<Warmup Duration='600' PowerLow='0.4' PowerHigh='0.8'/><SteadyState Duration='300' Power='0.7'/>"
        + "<IntervalsT Repeat='2' OnDuration='60' OffDuration='60' OnPower='1.2' OffPower='0.5'/><FreeRide Duration='120'/>"
        + "<SteadyState Duration='300' Power='1.0'/><Cooldown Duration='300' PowerLow='0.7' PowerHigh='0.3'/></workout></workout_file>";

    public static void Run(Action<bool, string> check)
    {
        Picker(check);
        Focus(check);
        Layout(check);
        Graph(check);
        Files(check);
    }

    private static void Picker(Action<bool, string> check)
    {
        Console.WriteLine("Workouts tab: the left / right picker");
        var p = new PickerModel("Session");
        check(p.Count == 0 && p.Index == -1 && p.Current is null && p.Previous is null && p.Next is null && !p.CanPrevious && !p.CanNext && p.Position == "Session" && !p.Step(1) && !p.Step(-1),
            "nothing to choose: no entry, no neighbours, no arrows, the line above is the label alone, a step does nothing");
        var three = new[] { new PickerEntry("A", "first"), new PickerEntry("B", "second"), new PickerEntry("C") };
        p.Fill(three, 1);
        check(p.Index == 1 && p.Current == three[1] && p.Previous == three[0] && p.Next == three[2] && p.CanPrevious && p.CanNext && p.Position == "Session · 2 of 3",
            $"filled on the middle entry: both neighbours shown, \"Session · 2 of 3\" above (got '{p.Position}')");
        check(p.Step(-1) && p.Index == 0 && p.Previous is null && !p.CanPrevious && p.Next == three[1] && p.Position == "Session · 1 of 3", "left steps one entry; at the first entry that side is blank and its arrow hidden");
        check(!p.Step(-1) && p.Index == 0, "left at the first entry: the list stops (no wrap)");
        check(p.Step(1) && p.Step(1) && p.Index == 2 && p.Next is null && !p.CanNext && p.Previous == three[1], "right steps one entry at a time to the last one, where the right side is blank");
        check(!p.Step(1) && p.Index == 2 && p.Position == "Session · 3 of 3", "right at the last entry: the list stops (no wrap)");
        check(p.Select(0) && p.Step(7) && p.Index == 2 && !p.Select(2) && p.Select(-4) && p.Index == 0, "a step or a choice past an end lands on that end; choosing the entry already chosen is no move");
        p.Fill(three, 99);
        bool high = p.Index == 2;
        p.Fill(three, -3);
        check(high && p.Index == 0, "a refill keeps the chosen index inside the list");
        p.Fill(Array.Empty<PickerEntry>(), 0);
        check(p.Index == -1 && p.Current is null && p.Position == "Session", "a refill with nothing: back to no entry");
        var one = new PickerModel("Folder");
        one.Fill(new[] { new PickerEntry("Workouts", "3 workouts") }, 0);
        check(one.Position == "Folder · 1 of 1" && !one.CanPrevious && !one.CanNext && !one.Step(1), "a single entry: shown, no arrows either side");

        check(PickerText.Cut("Sweet Spot 3x10", 8) == "Sweet S…" && PickerText.Cut("Sweet Spot 3x10", 7) == "Sweet…" && PickerText.Cut("abc", 3) == "abc" && PickerText.Cut("abcd", 1) == "…" && PickerText.Cut("abc", 0) == "",
            $"a long name is cut with \"…\", never ending in a space; one that fits is left alone (got '{PickerText.Cut("Sweet Spot 3x10", 8)}', '{PickerText.Cut("Sweet Spot 3x10", 7)}')");
    }

    private static void Focus(Action<bool, string> check)
    {
        Console.WriteLine("Workouts tab: up / down between the four picker rows (no Start buttons)");
        bool[] all = { true, true, true, true };   // plan, session, folder, workout
        check(NavRules.Move(0, 1, all) == 1 && NavRules.Move(1, 1, all) == 2 && NavRules.Move(2, 1, all) == 3 && NavRules.Move(3, -1, all) == 2,
            "down / up move one row: from the session row straight to the folder row, no button in between");
        check(NavRules.Move(3, 1, all) == 3 && NavRules.Move(0, -1, all) == 0, "at the last / first row the focus stays (no wrap)");
        check(NavRules.Move(-1, 1, all) == 0 && NavRules.Move(-1, -1, all) == -1, "coming from outside (the tab strip): down enters at the first row");
        bool[] some = { true, false, true, false };   // no plan followed (no sessions), a folder with no workouts
        check(NavRules.Move(0, 1, some) == 2 && NavRules.Move(2, -1, some) == 0 && NavRules.Move(2, 1, some) == 2, "an empty picker is skipped");
        check(NavRules.Move(-1, 1, new[] { false, false }) == -1 && NavRules.Move(0, 0, all) == 0, "nothing usable: the focus stays where it is");
        check(NavRules.Hint(true, true) == "Enter or click to start" && NavRules.Hint(false, true) == "" && NavRules.Hint(true, false) == "" && NavRules.Hint(false, false) == "",
            "the hint line: \"Enter or click to start\" under the card that has the focus and something to start, nothing otherwise");
    }

    private static void Layout(Action<bool, string> check)
    {
        Console.WriteLine("Workouts tab: a click on a picker row, the card's spare height, the gap between the graph's blocks");
        // a row 400 wide with 22 px arrows: each side is 22 + 20 % of 356 = 93 px
        check(PickerLayout.Side(400, 22) == 93 && PickerLayout.Hit(0, 400, 22) == PickerHit.Previous && PickerLayout.Hit(92, 400, 22) == PickerHit.Previous
            && PickerLayout.Hit(307, 400, 22) == PickerHit.Next && PickerLayout.Hit(399, 400, 22) == PickerHit.Next,
            $"a click on an arrow or a neighbour is a step (side {PickerLayout.Side(400, 22)} px of 400)");
        check(PickerLayout.Hit(93, 400, 22) == PickerHit.Centre && PickerLayout.Hit(200, 400, 22) == PickerHit.Centre && PickerLayout.Hit(306, 400, 22) == PickerHit.Centre,
            "a click on the chosen entry in the middle is the start");
        check(PickerLayout.Hit(-1, 400, 22) == PickerHit.None && PickerLayout.Hit(400, 400, 22) == PickerHit.None, "outside the row: nothing");
        check(400 - 2 * PickerLayout.Side(400, 22) > 2 * (PickerLayout.Side(400, 22) - 22), "the chosen entry has more room than both neighbours' names together");
        check(PickerLayout.Side(30, 22) == 22 && PickerLayout.Hit(15, 30, 22) == PickerHit.Previous, "a row too narrow for names still has its arrows");

        // 280 px spare, two rows, four gaps above the graph, rows may grow 28 px
        var (graph, row, gap) = CardRules.Share(280, 2, 4, 28);
        check(graph == 126 && row == 28 && gap == 24 && graph + 2 * row + 4 * gap <= 280, $"spare height: the graph takes under half, the rows grow to their limit, the rest is room between the lines (got {graph}, {row}, {gap})");
        check(CardRules.Share(280, 2, 4, 28).Graph < 280 / 2 && CardRules.Share(1000, 2, 4, 28).Graph < 500, "the graph never takes all the spare height (it did earlier), nor half of it");
        var (g2, r2, p2) = CardRules.Share(80, 2, 3, 28);
        check(g2 == 36 && r2 == 10 && p2 == 8 && g2 + 2 * r2 + 3 * p2 <= 80, $"little to spare (the minimum window): each gets its share, never more than there is (got {g2}, {r2}, {p2})");
        check(CardRules.Share(0, 2, 4, 28) == (0, 0, 0) && CardRules.Share(-40, 2, 4, 28) == (0, 0, 0), "nothing to spare: every line keeps its least height");
        check(CardRules.Share(100, 0, 0, 28) == (45, 0, 0) && CardRules.Share(100, 2, 0, 28) == (45, 12, 0), "no rows or no gaps to give to: the others still get their share");

        check(EffortGraph.SegmentGap == 2f && EffortGraph.BlockEnd(10f, 60f) == 58f, "the graph's blocks are 2 px apart (one more than before)");
        check(EffortGraph.BlockEnd(10f, 14f) == 14f && EffortGraph.BlockEnd(10f, 14.5f) == 12.5f && EffortGraph.BlockEnd(10f, 11f) == 11f, "a block too narrow to lose the gap keeps its width, so it still shows");
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;

    private static void Graph(Action<bool, string> check)
    {
        Console.WriteLine("Workouts tab: the effort graph of a workout");
        check(new[] { 0f, 59f, 59.4f }.All(v => EffortGraph.ZoneOf(v) == EffortZone.Easy) && new[] { 60f, 75f, 75.2f }.All(v => EffortGraph.ZoneOf(v) == EffortZone.Endurance)
            && new[] { 76f, 90f }.All(v => EffortGraph.ZoneOf(v) == EffortZone.Tempo) && new[] { 91f, 100f, 105f }.All(v => EffortGraph.ZoneOf(v) == EffortZone.Threshold)
            && new[] { 106f, 150f }.All(v => EffortGraph.ZoneOf(v) == EffortZone.Hard),
            "zones by % of FTP: below 60 easy, 60-75 endurance, 76-90 tempo, 91-105 threshold, above 105 hard");
        check(Enum.GetValues<EffortZone>().Select(Theme.ZoneColor).Distinct().Count() == 5 && Enum.GetValues<EffortZone>().All(z => Theme.Contrast(Theme.ZoneColor(z), Theme.Surface) >= 3),
            "each zone has its own colour, and each stands out from the card (3:1 or better)");

        var g = EffortGraph.From(WorkoutParser.Parse(Mixed, "mixed.zwo", 200f), 200f);
        var b = g.Blocks;
        check(g.Ok && b.Count == 12, $"a mixed workout: 12 blocks (warm-up cut in 3 at the zone edges, steady, 2 x on / off, free ride, steady, cool-down cut in 2; got {b.Count})");
        if (b.Count != 12) return;
        bool joined = Near(b[0].X0, 0f) && Near(b[^1].X1, 1f);
        for (int i = 0; i + 1 < b.Count; i++) joined &= Near(b[i].X1, b[i + 1].X0) && b[i].X1 > b[i].X0;
        check(joined, "the blocks follow each other from the start to the end of the workout, each as wide as its share of the time");
        check(b.Take(3).Select(x => x.Zone).SequenceEqual(new[] { EffortZone.Easy, EffortZone.Endurance, EffortZone.Tempo }) && b.Take(3).All(x => x.Y1 > x.Y0 && !x.FreeRide)
            && Near(b[0].Y1, b[1].Y0) && Near(b[1].Y1, b[2].Y0) && Near(b[2].X1, 600f / 1860f) && Near(b[0].Y0, 80f / g.TopW) && Near(b[2].Y1, 160f / g.TopW),
            "the warm-up (40 to 80 % of FTP) is one unbroken slope, coloured easy, endurance, tempo as it rises");
        check(b[3] is { Zone: EffortZone.Endurance, FreeRide: false } && Near(b[3].Y0, b[3].Y1) && Near(b[3].Y0, 140f / g.TopW) && Near(b[3].X1 - b[3].X0, 300f / 1860f),
            "a steady step is a flat block: its width is its duration, its height its target");
        check(b[4].Zone == EffortZone.Hard && b[5].Zone == EffortZone.Easy && b[6].Zone == EffortZone.Hard && b[7].Zone == EffortZone.Easy && Near(b[4].Y0, 240f / g.TopW),
            "intervals: hard (120 %) and easy (50 %) blocks in turn");
        check(b[8].FreeRide && Near(b[8].X1 - b[8].X0, 120f / 1860f) && b.Count(x => x.FreeRide) == 1, "a free-ride step is flagged (drawn as a dashed outline, labelled \"free ride\")");
        check(b[9].Zone == EffortZone.Threshold && Near(b[9].Y0, g.FtpY), "a step at 100 % of FTP is threshold, exactly at the FTP line");
        check(b[10].Zone == EffortZone.Endurance && b[11].Zone == EffortZone.Easy && b[10].Y0 > b[10].Y1 && b[11].Y0 > b[11].Y1 && Near(b[10].Y1, b[11].Y0),
            "the cool-down slopes down, endurance then easy");
        check(Near(g.FtpY, 200f / g.TopW) && g.FtpY is > 0f and < 1f && b.All(x => x.Y0 <= 1f && x.Y1 <= 1f && x.Y0 >= 0f && x.Y1 >= 0f), "the FTP line and every block fit the graph's height");
        check(g.Summary == "31:00 · hardest target 240 W (120 % of FTP)" && g.Text == g.Summary && g.Message is null, $"the line under it: total time, hardest target in watts and % of FTP (got '{g.Summary}')");

        var other = EffortGraph.From(WorkoutParser.Parse(Mixed, "mixed.zwo", 300f), 300f);
        check(other.Summary == "31:00 · hardest target 360 W (120 % of FTP)" && other.Blocks.Select(x => x.Zone).SequenceEqual(b.Select(x => x.Zone)), $"another FTP: other watts, the same zones (got '{other.Summary}')");

        var low = EffortGraph.From(WorkoutParser.Parse("<workout_file><workout><SteadyState Duration='600' Power='0.5'/></workout></workout_file>", "low.zwo", 200f), 200f);
        check(low.Blocks.Count == 1 && Near(low.TopW, 230f) && low.Blocks[0].Y0 < low.FtpY && low.Summary == "10:00 · hardest target 100 W (50 % of FTP)", "an easy workout: the graph still reaches up to the FTP line");
        var free = EffortGraph.From(WorkoutParser.Parse("<workout_file><workout><FreeRide Duration='120'/></workout></workout_file>", "free.zwo", 200f), 200f);
        check(free.Ok && free.Blocks is [{ FreeRide: true }] && free.Summary == "2:00 · free ride, no target", $"a workout that is all free ride: one outline, no target named (got '{free.Summary}')");
        var noFtp = EffortGraph.From(WorkoutParser.Parse(Mixed, "mixed.zwo", 0f), 0f);
        check(noFtp.Ok && noFtp.FtpY == 0f && !noFtp.Summary.Contains('%'), "no FTP known: no FTP line, no % in the words, no error");

        var broken = EffortGraph.From(WorkoutParser.Parse("<not xml", "b.zwo", 200f), 200f);
        check(!broken.Ok && broken.Blocks.Count == 0 && broken.Message!.StartsWith("This workout cannot be loaded") && broken.Text == broken.Message, "a broken workout file: a short message in place of the graph");
        var none = EffortGraph.From(WorkoutParser.Parse("<workout_file><workout/></workout_file>", "none.zwo", 200f), 200f);
        check(!none.Ok && none.Message!.StartsWith("This workout cannot be loaded"), "a workout file with no steps: the same");
        check(EffortGraph.Empty is { Ok: false, Message: "", Text: "" } && EffortGraph.Note("why").Text == "why", "nothing chosen: no graph and no words");
    }

    private static void Files(Action<bool, string> check)
    {
        Console.WriteLine("Workouts tab: the graphs are read from the disk once each");
        string dir = Path.Combine(Path.GetTempPath(), "descenders_graph_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "mixed.zwo");
            File.WriteAllText(file, Mixed);
            var missing = EffortGraph.Load(Path.Combine(dir, "gone.zwo"), 200f);
            check(!missing.Ok && missing.Message!.Contains("missing") && !EffortGraph.Load("", 200f).Ok, "a workout file that is not there: a message, never an error");

            var cache = new EffortGraphCache();
            var first = cache.Get(file, 200f);
            check(first.Ok && first.Blocks.Count == 12 && ReferenceEquals(cache.Get(file, 200f), first) && ReferenceEquals(cache.Get(file.ToUpperInvariant(), 200f), first) && cache.Loads == 1,
                "the same workout again (stepping back onto it) is not read again");
            check(cache.Get(file, 250f).Summary.Contains("300 W") && cache.Loads == 2, "another FTP (changed on the Rider tab): worked out again");
            File.WriteAllText(file, Mixed.Replace("Power='1.0'", "Power='1.5'"));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(10));
            check(cache.Get(file, 250f).Summary.Contains("375 W") && cache.Loads == 3, "a file changed on the disk: read again");
            check(!cache.Get(Path.Combine(dir, "gone.zwo"), 200f).Ok, "a missing file through the cache: its message");

            // 60 workouts, stepped through twice: must feel instant (a held key steps about 30 times a second)
            var files = new List<string>();
            for (int i = 0; i < 60; i++)
            {
                files.Add(Path.Combine(dir, "w" + i.ToString("00") + ".zwo"));
                File.WriteAllText(files[i], Mixed);
            }
            var long1 = new EffortGraphCache();
            var clock = Stopwatch.StartNew();
            foreach (string f in files) long1.Get(f, 200f);
            foreach (string f in files) long1.Get(f, 200f);
            clock.Stop();
            check(long1.Loads == 60 && clock.ElapsedMilliseconds < 2000, $"stepping through 60 workouts and back: each read once, in well under a held key's pace ({clock.ElapsedMilliseconds} ms)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
