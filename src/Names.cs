#nullable disable
using System;

// Pure logic (no Unity, no logging): also compiled into the bridge (bridge/DescendersBridge.csproj links this file) so `selftest` checks it
// (tests\unit\NamesTests.cs).
namespace FreeTurboDescenders
{
    /// <summary>
    /// What the two parts are called since the end of the POC stage: the mod is FreeTurbo-Descenders (was DescendersCyclingPOC),
    /// the bridge is FreeTurbo-Bridge (was turbotrainer-bridge, before that descenders-bridge).
    /// </summary>
    internal static class Names
    {
        /// <summary>The mod: its DLL (without .dll), its name in MelonLoader (the tag of its log lines), its section of MelonPreferences.cfg
        /// and its folder under UserData.</summary>
        public const string Mod = "FreeTurbo-Descenders";
        /// <summary>The bridge: its exe and process name (without .exe) and the title of its window.</summary>
        public const string Bridge = "FreeTurbo-Bridge";
        /// <summary>The folder in the game folder the bridge is installed in (not renamed).</summary>
        public const string BridgeFolder = "DescendersBridge";
        public const string DefaultBridgePath = BridgeFolder + "\\" + Bridge + ".exe";
        /// <summary>What the bridge's exe was called before: an old one may still be running, and a cfg may still name it.</summary>
        public static readonly string[] OldBridgeNames = { "turbotrainer-bridge", "descenders-bridge" };

        /// <summary>The cfg's BridgePath: a value still holding an old standard path moves to the new one, anything the rider chose stays.</summary>
        public static string MigrateBridgePath(string saved)
        {
            string s = (saved ?? "").Trim();
            for (int i = 0; i < OldBridgeNames.Length; i++)
                if (string.Equals(s, BridgeFolder + "\\" + OldBridgeNames[i] + ".exe", StringComparison.OrdinalIgnoreCase)) return DefaultBridgePath;
            return saved;
        }
    }
}
