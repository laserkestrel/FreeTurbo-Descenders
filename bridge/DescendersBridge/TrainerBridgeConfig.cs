using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DescendersBridge.Ant;

namespace DescendersBridge;

/// <summary>What <see cref="TrainerBridgeConfig.Load()"/> returns: the rider profile, the chosen devices and the training settings.</summary>
public sealed record UserConfig(RiderConfig Rider, DevicePreferences Devices)
{
    public TrainingConfig Training { get; init; } = new();
}

/// <summary>
/// The per-user settings file, %APPDATA%\TrainerBridge\TrainerBridgeConfig.json: the rider profile (`rider` section) and the chosen
/// device per role (`devices` section; ANT+, or Bluetooth with `transport` / `address`) in one place, outside the game folder, so each Windows user of the PC has their own and every
/// copy or install of the bridge shares it. Loaded once at startup and printed to the console (and the bridge log) so it is clear which file
/// and which values are in use. Created when missing, seeded once from the old files: rider.json in the same folder (or an old config.json
/// `rider` block) and devices.json next to config.json / the exe. Those old files are then ignored. The ANT+ network key never goes in here.
/// Also the workout library folder (`library` section, {"dir": "..."}, set in the window) and the training plan progress
/// (`progress` section, src\Progress.cs ProgressFile, written by the ride's <see cref="PlanBook"/>), and the `training` section
/// (<see cref="TrainingConfig"/>: ERG limits, workout bias / pause, trainer difficulty; no migration, it starts from the defaults), and
/// the `knownDevices` list: every device ever chosen, kept when its role is set to none, and the logs folder
/// (`logs` section, {"dir": "..."}, set in the window; absent or "" = the standard one), the activities folder likewise (`activities` section), and how far back the window's graphs go
/// (`graphs` section, {"historySeconds": 300}, edited by hand), and the window's accent colour
/// (`window` section, {"accentColour": "#00A0B1"}, edited by hand). The bridge is the only writer of this
/// file: every save re-reads it and replaces only its own section (<see cref="SaveSection"/>, ProgressFile.Save), so the others are kept.
/// </summary>
public static class TrainerBridgeConfig
{
    public const string FileName = "TrainerBridgeConfig.json";

    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly JsonDocumentOptions DocOpts = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string Dir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrainerBridge");
    public static string Path => PathOverride ?? System.IO.Path.Combine(Dir, FileName);

    /// <summary>`--config &lt;file&gt;` (any command): use that file instead of the per-user one, e.g. for the automated window tests. Null = normal.</summary>
    public static string? PathOverride { get; set; }

    /// <summary>The pre-#24 rider profile, in the same folder.</summary>
    public static string LegacyRiderPath => System.IO.Path.Combine(Dir, "rider.json");

    /// <summary>The pre-#24 device choices: next to config.json, else next to the exe.</summary>
    public static string LegacyDevicesPath
    {
        get
        {
            string? cfg = BridgeConfig.Find("config.json");
            string dir = cfg is null ? AppPaths.ExeDir : System.IO.Path.GetDirectoryName(cfg)!;
            return System.IO.Path.Combine(dir, "devices.json");
        }
    }

    /// <summary>The file layout. Section names are read case-insensitively.</summary>
    private sealed class FileModel
    {
        public RiderConfig? Rider { get; set; }
        public Dictionary<string, StoredDevice>? Devices { get; set; }
        public List<StoredDevice>? KnownDevices { get; set; }
        public TrainingConfig? Training { get; set; }
    }

    private static FileModel ModelOf(UserConfig cfg) =>
        new() { Rider = cfg.Rider, Devices = cfg.Devices.Roles, KnownDevices = cfg.Devices.Known, Training = cfg.Training };

    /// <summary>Load (or create / migrate) <see cref="Path"/> and print it. Never throws: a broken file gives the defaults and a log line.
    /// The window loads the file at every connect, save and undo, so the lines are printed the first time and after that only when
    /// they differ from the last ones printed (<see cref="PrintOnce"/>).</summary>
    public static UserConfig Load()
    {
        var said = new List<string>();
        var cfg = PathOverride is { } p
            ? Load(p, "", "", () => null, said.Add)   // a --config file is never seeded from the real old files
            : Load(Path, LegacyRiderPath, LegacyDevicesPath, BridgeConfig.LegacyRider, said.Add);
        if (Printed.IsNew(said)) foreach (string line in said) Console.WriteLine(line);
        return cfg;
    }

    private static readonly PrintOnce Printed = new();

    /// <summary>Testable core: <paramref name="legacyConfigRider"/> returns config.json's old `rider` block (null = none).</summary>
    internal static UserConfig Load(string path, string legacyRiderPath, string legacyDevicesPath, Func<RiderConfig?> legacyConfigRider, Action<string> log)
    {
        UserConfig cfg;
        if (File.Exists(path))
        {
            try
            {
                var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), ReadOpts) ?? throw new JsonException("empty file");
                RiderConfig rider;
                if (m.Rider is null)
                {
                    rider = new RiderConfig();
                    log($"Bridge config: no rider section in {path}, using the defaults");
                }
                else
                {
                    rider = RiderProfile.Clamp(m.Rider, out string clamped);
                    if (clamped.Length > 0) log($"Bridge config: {clamped} out of range in {path}, clamped");
                }
                cfg = new UserConfig(rider, ToPrefs(m.Devices, m.KnownDevices)) { Training = TrainingOf(m, path, log) };
            }
            catch (Exception ex)
            {
                log($"Bridge config: {path} is unreadable ({ex.Message}); using defaults and no devices, leaving the file as it is");
                cfg = new UserConfig(new RiderConfig(), new DevicePreferences());
            }
        }
        else
        {
            cfg = new UserConfig(SeedRider(path, legacyRiderPath, legacyConfigRider, log), SeedDevices(path, legacyDevicesPath, log));
            try { Write(path, ModelOf(cfg)); }
            catch (Exception ex) { log("Bridge config: could not write " + path + " (" + ex.Message + "); using the values below for this run"); }
        }
        log(Dump(path, cfg));
        string lib = LoadLibraryDir(path);
        log("Workout library: " + (lib.Length > 0 ? lib : "not set. " + Menu.NoLibraryText));
        string logs = LoadLogsDir(path);
        if (logs.Length > 0) log("Logs folder: " + logs);
        return cfg;
    }

    /// <summary>The `training` section (clamped), the defaults when it is missing (no migration from the mod's cfg:).</summary>
    private static TrainingConfig TrainingOf(FileModel m, string path, Action<string> log)
    {
        if (m.Training is null)
        {
            log($"Bridge config: no training section in {path}, using the defaults (change them in the bridge window's Resistance tab)");
            return new TrainingConfig();
        }
        var t = Training.Clamp(m.Training, out string clamped);
        if (clamped.Length > 0) log($"Bridge config: {clamped} out of range in {path}, clamped");
        return t;
    }

    /// <summary>Only the `training` section of <paramref name="path"/>, for the ride (<see cref="TrainingWatch"/>). Quiet unless something is wrong:
    /// a missing file or section gives the defaults; an unreadable file gives the defaults and a log line.</summary>
    internal static TrainingConfig LoadTraining(string path, Action<string> log)
    {
        if (!File.Exists(path)) return new TrainingConfig();
        try
        {
            var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), ReadOpts) ?? throw new JsonException("empty file");
            if (m.Training is null) return new TrainingConfig();
            var t = Training.Clamp(m.Training, out string clamped);
            if (clamped.Length > 0) log($"Bridge config: {clamped} out of range in {path}, clamped");
            return t;
        }
        catch (Exception ex)
        {
            log($"Bridge config: training settings unreadable in {path} ({ex.Message}); using the defaults");
            return new TrainingConfig();
        }
    }

    /// <summary>Store the training settings (clamped first) in <see cref="Path"/>, leaving the other sections as they are. Returns what was stored.</summary>
    public static TrainingConfig SaveTraining(TrainingConfig t) => SaveTraining(Path, t);

    /// <summary>Throws when the existing file is not valid JSON, like <see cref="SaveDevices(string, DevicePreferences)"/>.</summary>
    internal static TrainingConfig SaveTraining(string path, TrainingConfig t)
    {
        var c = Training.Clamp(t, out _);
        SaveSection(path, "training", JsonSerializer.SerializeToNode(c, WriteOpts), "enter the training settings again");
        return c;
    }

    /// <summary>The workout library folder (`library.dir`), "" when not set or the file cannot be read. Read fresh at every call (the window
    /// may have changed it while a ride runs). A relative path is taken relative to the settings file's folder.</summary>
    public static string LoadLibraryDir() => LoadLibraryDir(Path);

    internal static string LoadLibraryDir(string path) => LoadDir(path, "library");

    /// <summary>The logs folder the rider chose (`logs.dir`), "" when not set (= the standard one, <see cref="Ride.DefaultLogsDir"/>)
    /// or the file cannot be read. Read like <see cref="LoadLibraryDir()"/>.</summary>
    public static string LoadLogsDir() => LoadLogsDir(Path);

    internal static string LoadLogsDir(string path) => LoadDir(path, "logs");

    /// <summary>Store the logs folder in <see cref="Path"/> ("" = the standard one), leaving the other sections as they are.</summary>
    public static void SaveLogsDir(string dir) => SaveLogsDir(Path, dir);

    /// <summary>Throws when the existing file is not valid JSON, like <see cref="SaveDevices(string, DevicePreferences)"/>.</summary>
    internal static void SaveLogsDir(string path, string dir) =>
        SaveSection(path, "logs", new JsonObject { ["dir"] = (dir ?? "").Trim() }, "set the logs folder again");

    /// <summary>The activities folder the rider chose (`activities.dir`), "" when not set (= the standard one,
    /// <see cref="ActivityFiles.StandardDir"/>) or the file cannot be read. Read like <see cref="LoadLibraryDir()"/>.</summary>
    public static string LoadActivitiesDir() => LoadActivitiesDir(Path);

    internal static string LoadActivitiesDir(string path) => LoadDir(path, "activities");

    /// <summary>Store the activities folder in <see cref="Path"/> ("" = the standard one), leaving the other sections as they are.</summary>
    public static void SaveActivitiesDir(string dir) => SaveActivitiesDir(Path, dir);

    /// <summary>Throws when the existing file is not valid JSON, like <see cref="SaveDevices(string, DevicePreferences)"/>.</summary>
    internal static void SaveActivitiesDir(string path, string dir) =>
        SaveSection(path, "activities", new JsonObject { ["dir"] = (dir ?? "").Trim() }, "set the activities folder again");

    /// <summary>How many seconds the Ride tab's graphs show (`graphs.historySeconds`,; edited by hand, no field in the window).
    /// Absent, not a number or an unreadable file = <see cref="Gui.GraphHistory.DefaultSeconds"/>; clamped. Never throws.</summary>
    public static int LoadGraphHistorySeconds() => LoadGraphHistorySeconds(Path);

    internal static int LoadGraphHistorySeconds(string path) => ReadGraphHistorySeconds(path) ?? Gui.GraphHistory.DefaultSeconds;

    /// <summary>Like <see cref="LoadGraphHistorySeconds()"/>, and a readable file that does not have the setting gains it with the default
    /// (the other sections are kept), so the rider finds it there. A missing or unreadable file is left alone.</summary>
    public static int EnsureGraphHistorySeconds(Action<string> log) => EnsureGraphHistorySeconds(Path, log);

    internal static int EnsureGraphHistorySeconds(string path, Action<string> log)
    {
        if (ReadGraphHistorySeconds(path) is { } s) return s;
        try
        {
            if (File.Exists(path) && IsReadable(path, out _))
            {
                SaveSection(path, "graphs", new JsonObject { ["historySeconds"] = Gui.GraphHistory.DefaultSeconds }, "");
                log($"Bridge config: graphs.historySeconds added to {path} ({Gui.GraphHistory.DefaultSeconds} s, the time the Ride tab's graphs show)");
            }
        }
        catch (Exception ex) { log("Bridge config: could not add graphs.historySeconds to " + path + " (" + ex.Message + ")"); }
        return Gui.GraphHistory.DefaultSeconds;
    }

    /// <summary>Null when the file, the `graphs` section or a numeric `historySeconds` is not there.</summary>
    private static int? ReadGraphHistorySeconds(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (JsonNode.Parse(File.ReadAllText(path), null, DocOpts) is not JsonObject root) return null;
            var graphs = root.FirstOrDefault(p => p.Key.Equals("graphs", StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
            var secs = graphs?.FirstOrDefault(p => p.Key.Equals("historySeconds", StringComparison.OrdinalIgnoreCase)).Value;
            return secs is JsonValue v && v.TryGetValue(out double d) ? Gui.GraphHistory.Clamp(d) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The window's accent colour (`window.accentColour`, "#RRGGBB",; edited by hand, no field in the window).
    /// Absent, not a colour or an unreadable file = <see cref="Gui.AccentRules.DefaultHex"/>. Never throws.</summary>
    public static string LoadAccentColour() => LoadAccentColour(Path);

    internal static string LoadAccentColour(string path) =>
        Gui.AccentRules.TryParse(ReadAccentColour(path), out var c) ? Gui.AccentRules.ToHex(c) : Gui.AccentRules.DefaultHex;

    /// <summary>Like <see cref="LoadAccentColour()"/>, and a readable file that does not have the setting gains it with the default
    /// (the other sections are kept), so the rider finds it there. A value that is not a colour is logged and left as written;
    /// a missing or unreadable file is left alone.</summary>
    public static string EnsureAccentColour(Action<string> log) => EnsureAccentColour(Path, log);

    internal static string EnsureAccentColour(string path, Action<string> log)
    {
        string? written = ReadAccentColour(path);
        if (Gui.AccentRules.TryParse(written, out var c)) return Gui.AccentRules.ToHex(c);
        try
        {
            if (written is not null)
                log($"Bridge config: window.accentColour \"{written}\" in {path} is not a colour like {Gui.AccentRules.DefaultHex}; using {Gui.AccentRules.DefaultHex}");
            else if (File.Exists(path) && IsReadable(path, out _))
            {
                SaveSection(path, "window", new JsonObject { ["accentColour"] = Gui.AccentRules.DefaultHex }, "");
                log($"Bridge config: window.accentColour added to {path} ({Gui.AccentRules.DefaultHex}, the window's accent colour; the blue before it was {Gui.AccentRules.OldBlueHex})");
            }
        }
        catch (Exception ex) { log("Bridge config: could not add window.accentColour to " + path + " (" + ex.Message + ")"); }
        return Gui.AccentRules.DefaultHex;
    }

    /// <summary>What is written as `window.accentColour` (a non-text value as its JSON text); null when the file, the `window` section or
    /// the setting is not there.</summary>
    private static string? ReadAccentColour(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (JsonNode.Parse(File.ReadAllText(path), null, DocOpts) is not JsonObject root) return null;
            var window = root.FirstOrDefault(p => p.Key.Equals("window", StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
            var colour = window?.FirstOrDefault(p => p.Key.Equals("accentColour", StringComparison.OrdinalIgnoreCase)).Value;
            if (colour is null) return null;
            return colour is JsonValue v && v.TryGetValue(out string? t) ? t ?? "" : colour.ToJsonString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>The `dir` of a folder section (`library`, `logs`, `activities`), "" when there is none. Never throws.</summary>
    private static string LoadDir(string path, string section)
    {
        try
        {
            if (!File.Exists(path)) return "";
            if (JsonNode.Parse(File.ReadAllText(path), null, DocOpts) is not JsonObject root) return "";
            var lib = root.FirstOrDefault(p => p.Key.Equals(section, StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
            var dir = lib?.FirstOrDefault(p => p.Key.Equals("dir", StringComparison.OrdinalIgnoreCase)).Value;
            string s = dir is JsonValue v && v.TryGetValue(out string? t) ? (t ?? "").Trim() : "";
            if (s.Length == 0) return "";
            return System.IO.Path.IsPathRooted(s) ? s : System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, s));
        }
        catch (Exception) { return ""; }
    }

    /// <summary>Store the workout library folder in <see cref="Path"/>, leaving the other sections as they are.</summary>
    public static void SaveLibraryDir(string dir) => SaveLibraryDir(Path, dir);

    /// <summary>Throws when the existing file is not valid JSON, like <see cref="SaveDevices(string, DevicePreferences)"/>.</summary>
    internal static void SaveLibraryDir(string path, string dir) =>
        SaveSection(path, "library", new JsonObject { ["dir"] = (dir ?? "").Trim() }, "set the workout library folder again");

    private static RiderConfig SeedRider(string path, string legacyRiderPath, Func<RiderConfig?> legacyConfigRider, Action<string> log)
    {
        if (File.Exists(legacyRiderPath))
        {
            try
            {
                var r = JsonSerializer.Deserialize<RiderConfig>(File.ReadAllText(legacyRiderPath), ReadOpts) ?? throw new JsonException("empty file");
                var c = RiderProfile.Clamp(r, out _);
                log($"Bridge config: {path} did not exist; rider section copied from {legacyRiderPath} ({RiderProfile.Describe(c)}). That file is now ignored and can be deleted");
                return c;
            }
            catch (Exception ex) { log($"Bridge config: could not read {legacyRiderPath} ({ex.Message}); not copied"); }
        }
        RiderConfig? old = null;
        try { old = legacyConfigRider(); }
        catch (Exception ex) { log("Bridge config: could not read the rider block in config.json (" + ex.Message + ")"); }
        if (old is not null)
        {
            var c = RiderProfile.Clamp(old, out _);
            log($"Bridge config: {path} did not exist; rider section created from the rider block in config.json ({RiderProfile.Describe(c)}). That block is now ignored");
            return c;
        }
        log($"Bridge config: {path} did not exist; rider section created with the defaults");
        return new RiderConfig();
    }

    private static DevicePreferences SeedDevices(string path, string legacyDevicesPath, Action<string> log)
    {
        if (!File.Exists(legacyDevicesPath)) return new DevicePreferences();
        try
        {
            var old = JsonSerializer.Deserialize<DevicePreferences>(File.ReadAllText(legacyDevicesPath), ReadOpts) ?? throw new JsonException("empty file");
            var d = ToPrefs(old.Roles, null);
            log($"Bridge config: devices section copied from {legacyDevicesPath} ({d.Roles.Count} device(s)). That file is now ignored and can be deleted");
            return d;
        }
        catch (Exception ex)
        {
            log($"Bridge config: could not read {legacyDevicesPath} ({ex.Message}); no devices copied");
            return new DevicePreferences();
        }
    }

    /// <summary>Role names are matched case-insensitively, so a hand-edited "trainer" still counts. The known devices are the
    /// `knownDevices` list plus whatever the roles hold, so a file from before #72 starts remembering at its first load.</summary>
    /// A Bluetooth entry whose address cannot be read is left out, like a role that was never set.
    private static DevicePreferences ToPrefs(Dictionary<string, StoredDevice>? roles, List<StoredDevice>? known)
    {
        var p = new DevicePreferences();
        if (roles is not null) foreach (var (role, d) in roles) if (d is not null && d.Usable) p.Roles[role] = d;
        if (known is not null) foreach (var d in known) if (d is not null && d.Usable) p.Remember(d);
        foreach (var d in p.Roles.Values) p.Remember(d);
        return p;
    }

    /// <summary>
    /// The effective values (after clamping), as JSON: printed at startup so it is clear which file is read. Built from the parsed values,
    /// so nothing else a user may have pasted into the file is echoed.
    /// </summary>
    internal static string Dump(string path, UserConfig cfg) =>
        $"{DumpHead}{path}:{Environment.NewLine}{JsonSerializer.Serialize(ModelOf(cfg), WriteOpts)}";

    private const string DumpHead = "Bridge config ";

    /// <summary>True for the text <see cref="Dump"/> makes. The window's Logs tab leaves it out (the tabs show the same
    /// values); the console and logs\bridge_*.log keep it.</summary>
    internal static bool IsDump(string? line) =>
        line is not null && line.StartsWith(DumpHead, StringComparison.Ordinal) && line.Contains(":" + Environment.NewLine + "{", StringComparison.Ordinal);

    /// <summary>Store the device choices and the known devices (a device taken out of a role stays known) in <see cref="Path"/>,
    /// leaving the rest of the file (the rider section) as it is.</summary>
    public static void SaveDevices(DevicePreferences prefs) => SaveDevices(Path, prefs);

    /// <summary>Throws when the existing file is not valid JSON (it is not overwritten: the rider's values would be lost).</summary>
    internal static void SaveDevices(string path, DevicePreferences prefs)
    {
        foreach (var d in prefs.Roles.Values) prefs.Remember(d);   // a role filled without Set (Roles is public)
        SaveSections(path, "choose the devices again",
            ("devices", JsonSerializer.SerializeToNode(prefs.Roles, WriteOpts)), ("knownDevices", JsonSerializer.SerializeToNode(prefs.Known, WriteOpts)));
    }

    /// <summary>
    /// Devices told us who made them (a search, or a manufacturer page heard during a ride). The saved devices among them gain the
    /// manufacturer in <see cref="Path"/> without the rider choosing them again; a device that is not saved is not added. The file is read
    /// afresh and only written when something changed, so a choice saved meanwhile is kept. Returns how many devices changed. Never throws.
    /// </summary>
    public static int LearnManufacturers(IEnumerable<(DeviceKey Key, ushort ManufacturerId)> heard, Action<string> log) => LearnManufacturers(Path, heard, log);

    internal static int LearnManufacturers(string path, IEnumerable<(DeviceKey Key, ushort ManufacturerId)> heard, Action<string> log)
    {
        Monitor.Enter(LearnLock);
        try
        {
            if (!File.Exists(path)) return 0;
            var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), ReadOpts) ?? throw new JsonException("empty file");
            var prefs = ToPrefs(m.Devices, m.KnownDevices);
            int n = 0;
            foreach (var (key, id) in heard)
            {
                if (!prefs.Learn(key, id)) continue;
                n++;
                log($"Bridge config: {key} is made by {Manufacturers.Name(id) ?? "an unlisted manufacturer"} (manufacturer {id}): saved with the device");
            }
            if (n > 0) SaveDevices(path, prefs);
            return n;
        }
        catch (Exception ex)
        {
            log("Bridge config: could not save a device's manufacturer (" + ex.Message + ")");
            return 0;
        }
        finally { Monitor.Exit(LearnLock); }
    }

    private static readonly object LearnLock = new();

    /// <summary>Store the rider profile (clamped first) in <see cref="Path"/>, leaving the devices section as it is. Returns what was stored.</summary>
    public static RiderConfig SaveRider(RiderConfig rider) => SaveRider(Path, rider);

    /// <summary>Throws when the existing file is not valid JSON, like <see cref="SaveDevices(string, DevicePreferences)"/>.</summary>
    internal static RiderConfig SaveRider(string path, RiderConfig rider)
    {
        var c = RiderProfile.Clamp(rider, out _);
        SaveSection(path, "rider", JsonSerializer.SerializeToNode(c, WriteOpts), "enter the rider values again");
        return c;
    }

    private static void SaveSection(string path, string section, JsonNode? value, string redo) => SaveSections(path, redo, (section, value));

    /// <summary>Re-read the file, replace only these sections (matched case-insensitively), write it back in one go.</summary>
    private static void SaveSections(string path, string redo, params (string Name, JsonNode? Value)[] sections)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            try { root = JsonNode.Parse(File.ReadAllText(path), null, DocOpts) as JsonObject ?? throw new JsonException("not a JSON object"); }
            catch (Exception ex) { throw new InvalidOperationException($"{path} is unreadable ({ex.Message}); fix or delete it, then {redo}"); }
        }
        else root = new JsonObject { ["rider"] = JsonSerializer.SerializeToNode(new RiderConfig(), WriteOpts), ["devices"] = new JsonObject() };

        foreach (var (section, value) in sections)
        {
            foreach (string k in root.Select(p => p.Key).Where(k => k.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList()) root.Remove(k);
            root[section] = value;
        }
        WriteText(path, root.ToJsonString(WriteOpts));
    }

    /// <summary>
    /// False when the file exists but cannot be used (not JSON, wrong shape): <see cref="Load()"/> would then quietly use the defaults, so the
    /// window offers to repair it (<see cref="ResetBroken"/>). A missing file is fine: loading creates it.
    /// </summary>
    public static bool IsReadable(string path, out string reason)
    {
        reason = "";
        if (!File.Exists(path)) return true;
        try
        {
            string text = File.ReadAllText(path);
            if (JsonNode.Parse(text, null, DocOpts) is not JsonObject) throw new JsonException("not a JSON object");
            _ = JsonSerializer.Deserialize<FileModel>(text, ReadOpts);
            return true;
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    /// <summary>Keep an unreadable file as TrainerBridgeConfig.broken-&lt;time&gt;.json beside it and start a fresh one with the defaults. Returns the backup's path.</summary>
    public static string ResetBroken(string path)
    {
        string backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!,
            $"{System.IO.Path.GetFileNameWithoutExtension(path)}.broken-{DateTime.Now:yyyyMMdd_HHmmss}.json");
        if (File.Exists(path)) File.Move(path, backup, overwrite: true);
        Write(path, new FileModel { Rider = new RiderConfig(), Devices = new(), KnownDevices = new(), Training = new TrainingConfig() });
        return backup;
    }

    private static void Write(string path, FileModel m) => WriteText(path, JsonSerializer.Serialize(m, WriteOpts));

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}

/// <summary>Remembers the last block of lines printed, so the same block is not printed twice in a row. Pure, for `selftest`.</summary>
public sealed class PrintOnce
{
    private readonly object _gate = new();
    private string? _last;

    /// <summary>True the first time and whenever <paramref name="lines"/> differ from the last block this returned true for.</summary>
    public bool IsNew(IReadOnlyList<string> lines)
    {
        string block = string.Join("\n", lines);
        lock (_gate)
        {
            if (block == _last) return false;
            _last = block;
            return true;
        }
    }
}
