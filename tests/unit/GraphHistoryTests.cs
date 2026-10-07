using DescendersBridge.Ant;
using DescendersBridge.Gui;

namespace DescendersBridge;

/// <summary>
/// The Ride tab's graphs show the last `graphs.historySeconds` of TrainerBridgeConfig.json (300 s when absent; edited by hand,
/// no field in the window). The pure rules, the history and the settings section. Uses a temp folder, never the real %APPDATA% file.
/// </summary>
public static class GraphHistoryTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Graph history: the graphs show the last graphs.historySeconds, 300 s by default");
        check(GraphHistory.DefaultSeconds == 300, "the default is five minutes");
        check(GraphHistory.Clamp(120) == 120 && GraphHistory.Clamp(1) == GraphHistory.MinSeconds && GraphHistory.Clamp(-5) == GraphHistory.MinSeconds
            && GraphHistory.Clamp(1e9) == GraphHistory.MaxSeconds && GraphHistory.Clamp(double.NaN) == 300 && GraphHistory.Clamp(90.4) == 90,
            "a value is used as written; too short / too long is clamped (10 s .. 1 h); whole seconds");

        // the history itself: 4 readings a second, like the window's timer
        var h = new RollingHistory();
        check(h.WindowSeconds == 300, "a graph keeps 300 s unless told otherwise");
        for (int i = 0; i <= 2400; i++) h.Add(i * 0.25, i);
        check(h.Points[0].T == 300 && h.Points[^1].T == 600 && h.Points.Count == 1201, $"after 10 min it holds exactly the last 5 min (from {h.Points[0].T} s, {h.Points.Count} points)");
        check(h.Summary() == "last 300 s: 1201 readings, 1200 to 2400", $"the summary names the time shown (got '{h.Summary()}')");
        h.WindowSeconds = 60;
        check(h.Points[0].T == 540 && h.Summary().StartsWith("last 60 s: 241 readings", StringComparison.Ordinal), "a shorter time drops the older points at once");
        h.WindowSeconds = 0;
        check(h.WindowSeconds == GraphHistory.MinSeconds, "the history clamps what it is given");

        // the `graphs` section of the settings file
        string dir = Path.Combine(Path.GetTempPath(), "descenders_graphs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string p = Path.Combine(dir, TrainerBridgeConfig.FileName);
            var log = new List<string>();
            check(TrainerBridgeConfig.LoadGraphHistorySeconds(p) == 300 && TrainerBridgeConfig.EnsureGraphHistorySeconds(p, log.Add) == 300 && !File.Exists(p) && log.Count == 0,
                "no settings file: 300 s, and none is made for it");

            TrainerBridgeConfig.SaveRider(p, new RiderConfig { FtpW = 250 });
            TrainerBridgeConfig.SaveLogsDir(p, Path.Combine(dir, "My logs"));
            check(TrainerBridgeConfig.LoadGraphHistorySeconds(p) == 300 && !File.ReadAllText(p).Contains("historySeconds"),"a file without the setting: 300 s; reading does not change the file");
            check(TrainerBridgeConfig.EnsureGraphHistorySeconds(p, log.Add) == 300 && File.ReadAllText(p).Contains("\"historySeconds\": 300") && log.Count == 1 && log[0].Contains("graphs.historySeconds added"),
                "the window writes the setting into the file with the default, once, and logs it");
            check(TrainerBridgeConfig.EnsureGraphHistorySeconds(p, log.Add) == 300 && log.Count == 1, "the next start leaves the file alone");
            check(TrainerBridgeConfig.Load(p, "", "", () => null, _ => { }).Rider.FtpW == 250 && TrainerBridgeConfig.LoadLogsDir(p) == Path.Combine(dir, "My logs"),
                "the rider and logs sections are kept");

            File.WriteAllText(p, File.ReadAllText(p).Replace("\"historySeconds\": 300", "\"historySeconds\": 120"));
            check(TrainerBridgeConfig.LoadGraphHistorySeconds(p) == 120 && TrainerBridgeConfig.EnsureGraphHistorySeconds(p, log.Add) == 120 && log.Count == 1, "a hand-edited 120 is used");
            TrainerBridgeConfig.SaveRider(p, new RiderConfig { FtpW = 260 });
            TrainerBridgeConfig.SaveTraining(p, new TrainingConfig());
            TrainerBridgeConfig.SaveLogsDir(p, "");
            check(TrainerBridgeConfig.LoadGraphHistorySeconds(p) == 120, "rider, training and logs saves keep it");

            File.WriteAllText(p, "{ \"GRAPHS\": { \"HistorySeconds\": 99999 } }");
            check(TrainerBridgeConfig.LoadGraphHistorySeconds(p) == GraphHistory.MaxSeconds, "names are case-insensitive; a value out of range is clamped");
            File.WriteAllText(p, "{ \"graphs\": { \"historySeconds\": \"long\" } }");
            check(TrainerBridgeConfig.LoadGraphHistorySeconds(p) == 300, "not a number: 300 s");
            File.WriteAllText(p, "{ broken");
            check(TrainerBridgeConfig.LoadGraphHistorySeconds(p) == 300 && TrainerBridgeConfig.EnsureGraphHistorySeconds(p, log.Add) == 300 && File.ReadAllText(p) == "{ broken",
                "an unreadable settings file: 300 s, and it is left alone (never throws)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
