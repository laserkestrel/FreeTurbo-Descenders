using System.Reflection;
using System.Text.Json;
using DescendersBridge.Ant;

namespace DescendersBridge;

/// <summary>Trainer resistance rules (see <see cref="RideLoop"/>). Optional "resistance" block in config.json.</summary>
public sealed record ResistanceConfig
{
    public bool Enabled { get; init; } = true;
    /// <summary>Upper clamp, percent. kcd2-turbo found 12 % a sensible ceiling for a Neo 2T; applied after the training section's trainerDifficulty.</summary>
    public double MaxGradePercent { get; init; } = 12.0;
    /// <summary>Moves smaller than this are not written.</summary>
    public double DeadbandPercent { get; init; } = 0.5;
    public int WriteIntervalMs { get; init; } = 1000;
    /// <summary>Mod telemetry older than this counts as "mod gone": resistance goes flat.</summary>
    public double StaleSeconds { get; init; } = 1.5;
    /// <summary>ERG: never send a target above this, whatever the rider's training.ergMaxW says (the lower wins; this is the install's last line).</summary>
    public double ErgMaxW { get; init; } = 500.0;
    /// <summary>ERG: a new target is approached at this rate instead of stepped (100 W/s = about 2 s for a 200 W jump).</summary>
    public double ErgRampWPerSec { get; init; } = 100.0;
    /// <summary>ERG: the current target is resent this often so a dropped ANT message repairs itself.</summary>
    public int ErgRefreshMs { get; init; } = 1000;
}

/// <summary>
/// bridge/config.json (gitignored, optional): install-specific settings only, i.e. the ANT+ network key override and an optional resistance
/// block (the rider profile and devices live in the per-user TrainerBridgeConfig.json, <see cref="TrainerBridgeConfig"/>; an old `rider` block only seeds it). Looked for next to the exe,
/// then up the tree in bridge/. Release builds carry the key compiled in (AssemblyMetadata "AntNetworkKey", see the csproj).
/// </summary>
public static class BridgeConfig
{
    private const string Placeholder = "PASTE_ADOPTER_KEY_HERE", MetadataKey = "AntNetworkKey";
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Null when there is no config.json; a malformed one throws.</summary>
    private static JsonDocument? Doc()
    {
        string? path = Find("config.json");
        return path is null ? null : JsonDocument.Parse(File.ReadAllText(path));
    }

    /// <summary>config.json's key when set (lets a from-source user override the built-in one), else the built-in key.</summary>
    public static byte[] LoadNetworkKey()
    {
        string key = ConfigKey() ?? BuiltInKey()
            ?? throw new InvalidOperationException("No ANT+ network key: this build has none built in and config.json has none. "
                + "Use a release build (or build with ANT_NETWORK_KEY set), or put ant.networkKey in config.json next to FreeTurbo-Bridge.exe (see bridge/config.example.json).");
        return AntNetworkKey.Parse(key);
    }

    /// <summary>Where <see cref="LoadNetworkKey"/> gets the key: "config.json", "built-in" or "none". Never the key itself.</summary>
    public static string KeySource => ConfigKey() is not null ? "config.json" : BuiltInKey() is not null ? "built-in" : "none";

    private static string? ConfigKey()
    {
        using var doc = Doc();
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("ant", out var ant) || ant.ValueKind != JsonValueKind.Object
            || !ant.TryGetProperty("networkKey", out var k) || k.ValueKind != JsonValueKind.String)
            return null;
        return Usable(k.GetString());
    }

    private static string? BuiltInKey() =>
        Usable((Assembly.GetEntryAssembly() ?? typeof(BridgeConfig).Assembly)
            .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataKey)?.Value);

    private static string? Usable(string? key)
    {
        key = key?.Trim();
        return string.IsNullOrEmpty(key) || key == Placeholder ? null : key;
    }

    /// <summary>The old `rider` block, read only to seed TrainerBridgeConfig.json once (<see cref="TrainerBridgeConfig"/>); null when there is none.</summary>
    public static RiderConfig? LegacyRider() => Section<RiderConfig>("rider");
    public static ResistanceConfig LoadResistance() => Section<ResistanceConfig>("resistance") ?? new();

    private static T? Section<T>(string name)
    {
        using var doc = Doc();
        if (doc is null) return default;
        return doc.RootElement.TryGetProperty(name, out var el) ? el.Deserialize<T>(Opts) : default;
    }

    public static string? Find(string file)
    {
        for (var dir = new DirectoryInfo(AppPaths.ExeDir); dir != null; dir = dir.Parent)
        {
            string a = Path.Combine(dir.FullName, file);
            if (File.Exists(a)) return a;
            string b = Path.Combine(dir.FullName, "bridge", file);
            if (File.Exists(b)) return b;
        }
        return null;
    }
}
