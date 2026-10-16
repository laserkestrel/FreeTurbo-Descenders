using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DescendersBridge.Gui;

/// <summary>
/// Window mode: the exe started with no arguments (double-clicked), or `gui [--demo]`. One process: the window runs the same
/// bridge code as the command line, on an STA thread. The exe stays a console program so every command-line use (cmd.exe, the mod's
/// `run --auto`, scripts reading its output) is unchanged; when double-clicked, the console Windows opens for it is closed straight away.
/// </summary>
public static class GuiMain
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleProcessList(uint[] processList, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();

    public static int Run(bool demo)
    {
        // Only this process on the console = Windows made it for a double-click: drop it. Started from cmd.exe, it stays (and cmd waits).
        try { if (GetConsoleProcessList(new uint[4], 4) == 1) FreeConsole(); } catch { /* not Windows console: nothing to hide */ }

        int code = 0;
        var ui = new Thread(() =>
        {
            var sink = new LogSink();
            Console.SetOut(sink);
            using var log = AutoRun.StartLog();   // tees into logs\bridge_*.log, same as run --auto
            Console.WriteLine("Bridge build: " + BuildStamp.Text);
            Console.WriteLine("Window mode" + (demo ? " (demo)" : "") + ", settings file " + TrainerBridgeConfig.Path);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) =>
            {
                Console.WriteLine("ERROR (window): " + e.Exception);
                MessageBox.Show("Something went wrong in the window. The Logs tab has the technical text, if someone asks for it.", FreeTurboDescenders.Names.Bridge,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new BridgeWindow(demo, sink)); }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex); code = 2; }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        return code;
    }
}

/// <summary>Console output collected for the Logs tab, without the printout of the settings values (the log file, which the
/// output is copied to before it gets here, keeps it). Thread-safe: the ride and ANT callbacks write from other threads.</summary>
public sealed class LogSink : TextWriter
{
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();

    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) { lock (_gate) _pending.Append(value == '\n' ? Environment.NewLine : value.ToString()); }
    public override void Write(string? value) { if (value is null) return; lock (_gate) _pending.Append(value.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)); }
    public override void WriteLine(string? value) { if (TrainerBridgeConfig.IsDump(value)) return; lock (_gate) _pending.Append($"{DateTime.Now:HH:mm:ss} {value}{Environment.NewLine}"); }

    /// <summary>Everything written since the last call.</summary>
    public string Drain()
    {
        lock (_gate)
        {
            if (_pending.Length == 0) return "";
            string s = _pending.ToString();
            _pending.Clear();
            return s;
        }
    }
}
