using System.IO;
using System.Text;

namespace FreeTurboDescenders
{
    /// <summary>
    /// Pure rules for the developer dumps, no Unity code, so the bridge `selftest` can check them.
    /// Every dump overwrites ONE file per kind and scene (e.g. course_Forest.csv) instead of a timestamped file per
    /// dump: the census used to run on every level load and filled UserData\FreeTurbo-Descenders\ with hundreds of files
    /// next to progress.json and the workout library. The text dumps also go to MelonLoader\Latest.log line by line.
    /// </summary>
    internal static class DumpFiles
    {
        public const float CensusDelaySec = 4f;   // terrain colliders may not exist the instant the player does

        /// <summary>When the automatic census after a level load should run (unscaled time), or -1 for never.</summary>
        public static float CensusAt(bool autoCensusOnLoad, float now)
        {
            return autoCensusOnLoad ? now + CensusDelaySec : -1f;
        }

        /// <summary>File name for a dump: kind_scene.ext, with characters Windows won't take in a file name replaced by '_'.</summary>
        public static string FileName(string kind, string scene, string ext)
        {
            return Safe(kind, "dump") + "_" + Safe(scene, "noscene") + "." + Safe(ext, "txt");
        }

        private static string Safe(string s, string fallback)
        {
            if (s == null || s.Trim().Length == 0) return fallback;
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Trim().Length);
            foreach (char c in s.Trim()) sb.Append(System.Array.IndexOf(bad, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
