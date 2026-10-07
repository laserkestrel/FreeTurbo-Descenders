namespace DescendersBridge;

/// <summary>
/// The logs folder: where bridge_*.log and ride_*.jsonl go. The standard one is logs\ beside config.json
/// (<see cref="Ride.DefaultLogsDir"/>); the rider may choose another on the window's Logs tab (`logs.dir` in TrainerBridgeConfig.json,
/// "" = the standard one, so a settings file never pins an install's own folder). The rules and the window's words, pure, for `selftest`.
/// </summary>
public static class LogsFolder
{
    public const string SavedNote = "Ride recordings go here from the next ride, the bridge log from the next time this program starts.";

    /// <summary>The folder in use: the chosen one when it is a full path, else the standard one.</summary>
    public static string Resolve(string? configured, string standard)
    {
        string dir = (configured ?? "").Trim();
        return dir.Length > 0 && Path.IsPathRooted(dir) ? dir : standard;
    }

    /// <summary><paramref name="dir"/>, created; when it cannot be made (a drive that is gone), the standard folder and a log line. Never throws.</summary>
    public static string Usable(string dir, string standard, Action<string> log)
    {
        try { Directory.CreateDirectory(dir); return dir; }
        catch (Exception ex)
        {
            if (Same(dir, standard)) return standard;   // the caller's own error handling says why
            log("Logs folder " + dir + " cannot be used (" + ex.Message + "); using " + standard);
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
    public static string Status(string? configured, string standard) => Same(Resolve(configured, standard), standard)
        ? "The standard folder. The bridge log and the ride recordings are kept here."
        : "Your own folder. The bridge log and the ride recordings are kept here; empty the box and press Save to go back to the standard folder.";

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
