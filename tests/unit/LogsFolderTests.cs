using DescendersBridge.Ant;

namespace DescendersBridge;

/// <summary>
/// The logs folder can be chosen on the window's Logs tab (`logs.dir` in TrainerBridgeConfig.json); not set = the standard
/// folder, logs\ beside config.json, as before. The pure rules and the settings section. Uses a temp folder, never the real %APPDATA% file.
/// </summary>
public static class LogsFolderTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Logs folder: the standard folder unless the rider chooses another");
        string dir = Path.Combine(Path.GetTempPath(), "descenders_logs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string standard = Path.Combine(dir, "bridge", "logs"), own = Path.Combine(dir, "My logs");
            check(LogsFolder.Resolve("", standard) == standard && LogsFolder.Resolve(null, standard) == standard && LogsFolder.Resolve("  ", standard) == standard,
                "nothing chosen: the standard folder");
            check(LogsFolder.Resolve(own, standard) == own && LogsFolder.Resolve(" " + own + " ", standard) == own, "a chosen full path is used (trimmed)");
            check(LogsFolder.Resolve("relative\\logs", standard) == standard, "a hand-edited relative path is not used: the standard folder");

            check(LogsFolder.ToStore("", standard) == "" && LogsFolder.ToStore(standard, standard) == "" && LogsFolder.ToStore(standard.ToUpperInvariant() + "\\", standard) == "",
                "saving an empty box or the standard folder itself stores nothing, so the setting keeps following the install");
            check(LogsFolder.ToStore("  " + own + " ", standard) == own, "another folder is stored as typed (trimmed)");

            // the activities folder uses the same rules, with words of its own
            check(LogsFolder.IsStandard("", standard) && LogsFolder.IsStandard(standard + "\\", standard) && !LogsFolder.IsStandard(own, standard)
                && ActivityFiles.Status("", standard).EndsWith("for uploading to Strava or a similar site.", StringComparison.Ordinal)
                && ActivityFiles.Status(own, standard).Contains("in your own folder") && ActivityFiles.Status(own, standard).Contains("go back to the standard folder"),
                "the activities folder's line says what the files are for, and how to go back from an own folder");
            string cfgFile = Path.Combine(dir, TrainerBridgeConfig.FileName);
            File.WriteAllText(cfgFile, "{ \"rider\": { \"ftpW\": 250 }, \"logs\": { \"dir\": \"\" } }");
            check(TrainerBridgeConfig.LoadActivitiesDir(cfgFile) == "", "no activities section: nothing chosen");
            TrainerBridgeConfig.SaveActivitiesDir(cfgFile, " " + own + " ");
            check(TrainerBridgeConfig.LoadActivitiesDir(cfgFile) == own && File.ReadAllText(cfgFile).Contains("\"ftpW\": 250") && TrainerBridgeConfig.LoadLogsDir(cfgFile) == "",
                "the activities folder is stored in its own section (trimmed), the other sections are kept");
            var said = new List<string>();
            string gone = Path.Combine(dir, "bad\0name");
            check(LogsFolder.Usable(gone, standard, said.Add, "Activities folder") == standard && Directory.Exists(standard)
                && said.Count == 1 && said[0].StartsWith("Activities folder ", StringComparison.Ordinal) && said[0].Contains("cannot be used"),
                "an activities folder that cannot be made: the standard one is used and the log says so in its own words");

            check(LogsFolder.Refuse("") == null && LogsFolder.Refuse(own) == null && LogsFolder.Refuse(null) == null, "an empty box and a full path (it need not exist yet) are accepted");
            check(LogsFolder.Refuse("relative\\logs")?.StartsWith("Not saved", StringComparison.Ordinal) == true && LogsFolder.Refuse("C:\\bad\0name")?.StartsWith("Not saved", StringComparison.Ordinal) == true,
                "a relative path and a name Windows does not accept are refused, in words");
            check(LogsFolder.Status("", standard) == "Logs for troubleshooting and support." && LogsFolder.Status(standard, standard) == LogsFolder.Status("", standard)
                && LogsFolder.Status(own, standard).StartsWith("Logs for troubleshooting and support, in your own folder", StringComparison.Ordinal)
                && LogsFolder.Status(own, standard).Contains("go back to the standard folder"), "the line under the box says what the logs are for, and how to go back from an own folder");

            var log = new List<string>();
            check(LogsFolder.Usable(own, standard, log.Add) == own && Directory.Exists(own) && log.Count == 0, "the chosen folder is made when it is not there");
            string blocked = Path.Combine(dir, "a-file");
            File.WriteAllText(blocked, "x");
            check(LogsFolder.Usable(Path.Combine(blocked, "logs"), standard, log.Add) == standard && Directory.Exists(standard) && log.Count == 1 && log[0].Contains("cannot be used"),
                "a chosen folder that cannot be made: the standard folder is used and the log says so");

            // the `logs` section of the settings file
            string p = Path.Combine(dir, TrainerBridgeConfig.FileName);
            check(TrainerBridgeConfig.LoadLogsDir(p) == "", "no settings file: no logs folder chosen");
            UserConfig Load() => TrainerBridgeConfig.Load(p, "", "", () => null, log.Add);
            log.Clear();
            Load();
            check(TrainerBridgeConfig.LoadLogsDir(p) == "" && !log.Exists(l => l.StartsWith("Logs folder", StringComparison.Ordinal)), "a new settings file has none");
            TrainerBridgeConfig.SaveRider(p, new RiderConfig { FtpW = 250 });
            TrainerBridgeConfig.SaveLibraryDir(p, own);
            TrainerBridgeConfig.SaveLogsDir(p, "  " + own + "  ");
            check(TrainerBridgeConfig.LoadLogsDir(p) == own && TrainerBridgeConfig.LoadLibraryDir(p) == own && Load().Rider.FtpW == 250 && log.Contains("Logs folder: " + own),
                "saved and read back (trimmed); the rider and library sections are kept; loading prints it");
            TrainerBridgeConfig.SaveRider(p, new RiderConfig { FtpW = 260 });
            TrainerBridgeConfig.SaveLibraryDir(p, "");
            check(TrainerBridgeConfig.LoadLogsDir(p) == own, "rider and library saves keep the logs folder");
            TrainerBridgeConfig.SaveLogsDir(p, "");
            check(TrainerBridgeConfig.LoadLogsDir(p) == "" && Load().Rider.FtpW == 260, "saving nothing goes back to the standard folder");
            File.WriteAllText(p, "{ broken");
            check(TrainerBridgeConfig.LoadLogsDir(p) == "", "an unreadable settings file: the standard folder (never throws)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        RunPrune(check);
    }

    /// <summary>Ride recordings were never deleted (88 MB in 21 files on the build VM): only the newest few are kept, like the bridge logs.</summary>
    private static void RunPrune(Action<bool, string> check)
    {
        Console.WriteLine("Logs folder: only the newest ride recordings and bridge logs are kept");
        check(LogsFolder.KeepRideRecordings >= 4 && LogsFolder.KeepRideRecordings <= 10 && AutoRun.KeepLogs == 20,
            $"keep {LogsFolder.KeepRideRecordings} ride recordings (Collect logs sends 3) and 20 bridge logs");

        var rides = Enumerable.Range(1, 8).Select(d => $"ride_202610{d:00}_180000.jsonl").ToList();
        var others = new[] { "bridge_20261001_180000.log", "notes.txt", "ride_notes.txt", "my-ride_20261001.jsonl", "RIDE_20261009_180000.JSONL" };
        var gone = LogsFolder.OldFiles(rides.Concat(others).Reverse(), "ride_", ".jsonl", 5);
        check(gone.SequenceEqual(new[] { "ride_20261005_180000.jsonl", "ride_20261004_180000.jsonl", "ride_20261003_180000.jsonl",
                "ride_20261002_180000.jsonl", "ride_20261001_180000.jsonl" }),
            $"9 recordings (one in capitals) and a new one to come: the oldest 5 go, the newest 4 stay, whatever order the folder lists them in (got {string.Join(", ", gone)})");
        check(!gone.Intersect(others.Take(4)).Any(), "a bridge log, notes and a file that only looks like a recording are never touched");
        check(LogsFolder.OldFiles(rides.Take(4), "ride_", ".jsonl", 5).Count == 0, "4 recordings and a new one make 5: nothing goes");
        check(LogsFolder.OldFiles(rides.Take(5), "ride_", ".jsonl", 5).SequenceEqual(new[] { rides[0] }), "5 recordings: the oldest goes for the new one");
        check(LogsFolder.OldFiles(Array.Empty<string>(), "ride_", ".jsonl", 5).Count == 0, "an empty folder: nothing to delete");
        check(LogsFolder.OldFiles(rides, "bridge_", ".log", 20).Count == 0, "bridge-log pruning never takes a ride recording");

        string dir = Path.Combine(Path.GetTempPath(), "descenders_prune_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string n in rides.Concat(new[] { "bridge_20261001_180000.log", "notes.txt" })) File.WriteAllText(Path.Combine(dir, n), "{}");
            using (new FileStream(Path.Combine(dir, rides[0]), FileMode.Open, FileAccess.Read, FileShare.None))   // still open elsewhere: cannot be deleted
                LogsFolder.Prune(dir, "ride_", ".jsonl", 5);
            var left = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToList();
            check(left.SequenceEqual(new[] { "bridge_20261001_180000.log", "notes.txt", rides[0], rides[4], rides[5], rides[6], rides[7] }),
                $"in a real folder: the newest 4 stay, other files stay, a locked one is skipped without an error (left: {string.Join(", ", left)})");
            LogsFolder.Prune(Path.Combine(dir, "missing"), "ride_", ".jsonl", 5);
            check(true, "a folder that is gone: no exception");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
