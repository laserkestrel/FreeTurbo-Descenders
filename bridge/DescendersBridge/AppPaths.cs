namespace DescendersBridge;

/// <summary>Where the exe itself lives. In a single-file publish AppContext.BaseDirectory is the folder the bundle is extracted to, not the exe's.</summary>
public static class AppPaths
{
    public static string ExeDir
    {
        get
        {
            string? p = Environment.ProcessPath;
            // `dotnet turbotrainer-bridge.dll` runs as dotnet.exe: fall back to the assembly folder
            if (p is null || System.IO.Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return AppContext.BaseDirectory;
            return System.IO.Path.GetDirectoryName(p) ?? AppContext.BaseDirectory;
        }
    }
}
