using DescendersCyclingPOC;

namespace DescendersBridge;

/// <summary>
/// The automatic census on load is a developer option (off by default) and every dump overwrites one file per
/// kind and scene. Compiled into the bridge and run by `turbotrainer-bridge selftest` (src\DumpFiles.cs is linked in too).
/// </summary>
public static class DumpFilesTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Dump files: census on load gated, one file per kind and scene");
        check(DumpFiles.CensusAt(false, 12f) < 0f, "AutoCensusOnLoad off: no census after a load");
        check(DumpFiles.CensusAt(true, 12f) == 16f, "AutoCensusOnLoad on: census 4 s after the load");

        string a = DumpFiles.FileName("course", "Forest", "csv");
        check(a == "course_Forest.csv", $"course dump name has no timestamp (got {a})");
        check(DumpFiles.FileName("course", "Forest", "csv") == a, "same kind + scene -> same file, so the next dump overwrites it");
        check(DumpFiles.FileName("trackpoints", "Forest", "csv") != a, "different kind -> different file");
        check(DumpFiles.FileName("course", "Hub", "csv") != a, "different scene -> different file");
        check(DumpFiles.FileName("terrain_census", "Forest", "txt") == "terrain_census_Forest.txt", "text dumps are .txt");

        string odd = DumpFiles.FileName("ray_probe", "a/b:c?", "txt");
        check(odd == "ray_probe_a_b_c_.txt", $"characters Windows refuses in file names become '_' (got {odd})");
        check(DumpFiles.FileName("vehicle_dump", "", "txt") == "vehicle_dump_noscene.txt", "empty scene name -> 'noscene'");
        check(DumpFiles.FileName("vehicle_dump", null!, "txt") == "vehicle_dump_noscene.txt", "null scene name -> 'noscene'");
    }
}
