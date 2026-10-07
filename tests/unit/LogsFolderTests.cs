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

            check(LogsFolder.Refuse("") == null && LogsFolder.Refuse(own) == null && LogsFolder.Refuse(null) == null, "an empty box and a full path (it need not exist yet) are accepted");
            check(LogsFolder.Refuse("relative\\logs")?.StartsWith("Not saved", StringComparison.Ordinal) == true && LogsFolder.Refuse("C:\\bad\0name")?.StartsWith("Not saved", StringComparison.Ordinal) == true,
                "a relative path and a name Windows does not accept are refused, in words");
            check(LogsFolder.Status("", standard).StartsWith("The standard folder", StringComparison.Ordinal) && LogsFolder.Status(standard, standard).StartsWith("The standard folder", StringComparison.Ordinal)
                && LogsFolder.Status(own, standard).StartsWith("Your own folder", StringComparison.Ordinal), "the line under the box says which folder is in use");

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
    }
}
