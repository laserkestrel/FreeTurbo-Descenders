#nullable disable

// Pure logic (no Unity, no logging): also compiled into the bridge (bridge/DescendersBridge.csproj links this file) so `selftest` checks the
// message the mod sends against the bridge's parser.
namespace DescendersCyclingPOC
{
    /// <summary>The `hello` message (docs/IPC.md): which game this is, its build, and what the bridge may use it for.</summary>
    internal static class Hello
    {
        public const string Game = "descenders";
        /// <summary>gradient: telemetry `fitness.roadGradient`; speed: the game runs the speed model; hud: it shows the session
        /// (no menu capability: workouts and plans are chosen in the bridge window).</summary>
        public static readonly string[] Capabilities = { "gradient", "speed", "hud" };
        public const int IntervalSeconds = 5;   // resent, so a bridge started after the game still learns who is connected

        public static string Message(string version)
        {
            var w = new JsonWriter().Obj().Prop("type", "hello").Prop("game", Game).Prop("version", version ?? "").Arr("capabilities");
            for (int i = 0; i < Capabilities.Length; i++) w.Val(Capabilities[i]);
            return w.EndArr().End().ToString();
        }
    }
}
