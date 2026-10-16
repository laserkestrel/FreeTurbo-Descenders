using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// The end of the POC stage: the mod is FreeTurbo-Descenders (was DescendersCyclingPOC), the bridge is FreeTurbo-Bridge (was
/// turbotrainer-bridge). src\Names.cs holds the names; a cfg still naming an old bridge exe moves to the new one. Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class NamesTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Names: FreeTurbo-Descenders and FreeTurbo-Bridge");
        check(Names.Mod == "FreeTurbo-Descenders" && Names.Bridge == "FreeTurbo-Bridge", "the mod is FreeTurbo-Descenders, the bridge is FreeTurbo-Bridge");
        check(Names.DefaultBridgePath == "DescendersBridge\\FreeTurbo-Bridge.exe", "the bridge is installed as DescendersBridge\\FreeTurbo-Bridge.exe in the game folder");
        check(typeof(NamesTests).Assembly.GetName().Name == Names.Bridge, "the bridge's own assembly (its exe) has that name");

        check(Names.MigrateBridgePath("DescendersBridge\\turbotrainer-bridge.exe") == Names.DefaultBridgePath
            && Names.MigrateBridgePath(" descendersbridge\\TurboTrainer-Bridge.exe ") == Names.DefaultBridgePath, "a cfg still holding the old standard path moves to the new exe");
        check(Names.MigrateBridgePath("DescendersBridge\\descenders-bridge.exe") == Names.DefaultBridgePath, "so does the name before that");
        check(Names.MigrateBridgePath("D:\\Tools\\turbotrainer-bridge.exe") == "D:\\Tools\\turbotrainer-bridge.exe"
            && Names.MigrateBridgePath("Bridge\\my.exe") == "Bridge\\my.exe" && Names.MigrateBridgePath(Names.DefaultBridgePath) == Names.DefaultBridgePath,
            "a path the rider chose stays, and so does the new standard path");
        check(Names.MigrateBridgePath(null) == null && Names.MigrateBridgePath("") == "", "nothing saved stays nothing (the mod then uses the standard path)");
        check(Array.IndexOf(Names.OldBridgeNames, "turbotrainer-bridge") >= 0 && Array.IndexOf(Names.OldBridgeNames, Names.Bridge) < 0,
            "an old bridge still running under its old name is known to the mod's launcher");
    }
}
