using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DescendersBridge;

/// <summary>
/// Every build carries where it came from (BuildStamp.targets): commit, branch, +changes, build time, and the same text as the
/// assembly's informational version, which becomes the exe's Win32 ProductVersion that tools\WhatsInstalled.ps1 reads. Run by `selftest`.
/// </summary>
public static class BuildStampTests
{
    // Same shapes as ConvertFrom-BuildStamp in tools\WhatsInstalledRules.ps1.
    private static readonly Regex Shape = new(
        @"^(?:(?<ver>[0-9A-Za-z.\-]+) \()?(?<commit>[0-9a-f]{7,40}|unknown)\)?(?: on (?<branch>\S+))?(?<dirty> \(\+changes\))?, built \d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC$");

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Build stamp: commit / branch / +changes / build time");
        string s = BuildStamp.Text;
        check(Shape.IsMatch(s), $"stamp has the documented shape (got '{s}')");

        string? info = typeof(BuildStampTests).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        check(info == s, $"AssemblyInformationalVersion is the stamp, nothing appended (got '{info}')");

        // Run from the built exe (dotnet run / the installed bridge): its version resource must carry the stamp, or WhatsInstalled.ps1 is blind.
        string? exe = Environment.ProcessPath;
        if (exe is not null && Path.GetFileName(exe).Equals(FreeTurboDescenders.Names.Bridge + ".exe",StringComparison.OrdinalIgnoreCase))
        {
            string? pv = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
            check(pv == s, $"the exe's ProductVersion is the stamp (got '{pv}')");
        }

        // The window's title bar shows the name and version number only; the whole stamp stays in the log and the exe's ProductVersion
        string bridge = FreeTurboDescenders.Names.Bridge;
        foreach (var (stamp, demo, want) in new[]
        {
            ("1.0.258 (f707dd4) on main, built 2026-10-16 16:44 UTC", false, bridge + " 1.0.258"),
            ("1.0.258 (f707dd4) on day-night-cycle (+changes), built 2026-10-16 16:44 UTC", false, bridge + " 1.0.258"),
            ("1.0.258 (f707dd4) on main, built 2026-10-16 16:44 UTC", true, bridge + " 1.0.258 (demo: made-up readings)"),
            ("f707dd4 on main, built 2026-10-16 16:44 UTC", false, bridge),
            ("unknown, built 2026-10-16 16:44 UTC", false, bridge),
            ("", false, bridge),
        })
        {
            string got = Gui.BridgeWindow.Title(stamp, demo);
            check(got == want, $"window title for '{stamp}'{(demo ? " (demo)" : "")} is '{want}' (got '{got}')");
        }
        string live = Gui.BridgeWindow.Title(s, false);
        check(!live.Contains("built", StringComparison.Ordinal) && !live.Contains(" on ", StringComparison.Ordinal),
            $"this build's window title has no commit, branch or build time (got '{live}')");
    }
}
