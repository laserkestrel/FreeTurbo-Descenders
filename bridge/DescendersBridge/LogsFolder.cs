namespace DescendersBridge;

/// <summary>
/// The logs folder: where bridge_*.log and ride_*.jsonl go. The standard one is logs\ beside config.json
/// (<see cref="Ride.DefaultLogsDir"/>); the rider may choose another on the window's Logs tab (`logs.dir` in TrainerBridgeConfig.json,
/// "" = the standard one, so a settings file never pins an install's own folder). The rules and the window's words, pure, for `selftest`.
/// </summary>
public static class LogsFolder
{
    public const string SavedNote = "Ride recordings go here from the next ride, the bridge log from the next time this program starts.";

    /// <summary>Ride recordings (ride_*.jsonl, about 0.5 MB a minute of riding) kept in the logs folder: only for troubleshooting, so the newest
    /// few, never a disk that fills up unseen. Collect logs sends the newest 3. Bridge logs keep <see cref="AutoRun.KeepLogs"/>.</summary>
    public const int KeepRideRecordings = 5;

    /// <summary>The files to delete before a new <paramref name="prefix"/>*<paramref name="ext"/> file is opened, so that with the new one
    /// there are <paramref name="keep"/>: all such names but the newest keep - 1 (the names carry yyyyMMdd_HHmmss, so name order is time
    /// order). Other names are never returned. Pure, for `selftest`.</summary>
    public static List<string> OldFiles(IEnumerable<string> names, string prefix, string ext, int keep) =>
        names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase).Skip(Math.Max(0, keep - 1)).ToList();

    /// <summary>Deletes <see cref="OldFiles"/> in <paramref name="dir"/>; a file that cannot be deleted (still open, read-only) is left. Never throws.</summary>
    public static void Prune(string dir, string prefix, string ext, int keep)
    {
        try
        {
            foreach (string name in OldFiles(Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>(), prefix, ext, keep))
                try { File.Delete(Path.Combine(dir, name)); } catch { }
        }
        catch { }
    }

    /// <summary>The folder in use: the chosen one when it is a full path, else the standard one.</summary>
    public static string Resolve(string? configured, string standard)
    {
        string dir = (configured ?? "").Trim();
        return dir.Length > 0 && Path.IsPathRooted(dir) ? dir : standard;
    }

    /// <summary><paramref name="dir"/>, created; when it cannot be made (a drive that is gone), the standard folder and a log line. Never throws.</summary>
    public static string Usable(string dir, string standard, Action<string> log, string what = "Logs folder")
    {
        try { Directory.CreateDirectory(dir); return dir; }
        catch (Exception ex)
        {
            if (Same(dir, standard)) return standard;   // the caller's own error handling says why
            log(what + " " + dir + " cannot be used (" + ex.Message + "); using " + standard);
            try { Directory.CreateDirectory(standard); } catch { }
            return standard;
        }
    }

    /// <summary>What the settings file keeps for what the rider typed: "" for an empty box or the standard folder itself.</summary>
    public static string ToStore(string? typed, string standard)
    {
        string dir = (typed ?? "").Trim();
        return dir.Length == 0 || Same(dir, standard) ? "" : dir;
    }

    /// <summary>Why a folder is not saved (null = save it; it is made when it does not exist). An empty box is allowed: back to the standard folder.</summary>
    public static string? Refuse(string? typed)
    {
        string dir = (typed ?? "").Trim();
        if (dir.Length == 0) return null;
        if (!Path.IsPathRooted(dir)) return "Not saved: give the full path of the folder, for example C:\\Users\\you\\Documents\\BridgeLogs, or use \"Browse...\".";
        try { Path.GetFullPath(dir); }
        catch (Exception) { return "Not saved: Windows does not accept that as a folder name. Choose one with \"Browse...\"."; }
        return null;
    }

    /// <summary>The line under the box: which folder is in use.</summary>
    public static string Status(string? configured, string standard) => IsStandard(configured, standard)
        ? "Logs for troubleshooting and support."
        : "Logs for troubleshooting and support, in your own folder; empty the box and press Save to go back to the standard folder.";

    /// <summary>True when the folder in use is the standard one (nothing chosen, or the standard folder itself typed in).</summary>
    public static bool IsStandard(string? configured, string standard) => Same(Resolve(configured, standard), standard);

    private static bool Same(string a, string b)
    {
        static string Norm(string p)
        {
            try { p = Path.GetFullPath(p); } catch (Exception) { }
            return p.TrimEnd('\\', '/');
        }
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }
}
