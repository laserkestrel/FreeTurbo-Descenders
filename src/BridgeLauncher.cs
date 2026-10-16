using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// Starts the trainer bridge (FreeTurbo-Bridge.exe: a separate 32-bit .NET 8 process, it cannot load inside the game) when the game starts,
    /// without a window, as `run --auto`: it chooses the trainer itself and reports its state with the `status` message. A convenience only: the
    /// bridge is the hub, the rider normally opens its window and launches the game from there. Started without `--parent`; when the
    /// game quits the mod says `bye` (Hello.Bye) and the headless bridge stops, releasing the trainer first. After a game crash it
    /// stops by itself once no game has been connected for a while (GamePresence.AutoIdleExitMinutes). The mod never kills it:
    /// that would skip the release. Not started when a bridge, or anything else holding the telemetry port (a `run --sim` fake trainer), is already running.
    /// </summary>
    internal sealed class BridgeLauncher
    {
        private Process _proc;
        private string _note = "";   // why it was not started / how it ended, for the HUD
        private bool _exitLogged;
        private float _nextCheck;

        public void Start(int telemetryPort)
        {
            if (!Config.BridgeAutoStart) { Log.Info("Bridge launcher: off (BridgeAutoStart=false)"); return; }
            string exe = Config.BridgePath;
            if (!Path.IsPathRooted(exe)) exe = Path.Combine(Path.GetDirectoryName(Application.dataPath), exe);   // relative to the game folder
            if (!File.Exists(exe))
            {
                _note = "Bridge not installed";
                Log.Warn("Bridge launcher: " + exe + " not found (BridgePath); start the bridge by hand");
                return;
            }
            try
            {
                // also the pre-rename exe names, in case an old install is still running
                bool running = Process.GetProcessesByName(Config.BridgeProcessName).Length > 0;
                for (int i = 0; i < Names.OldBridgeNames.Length && !running; i++) running = Process.GetProcessesByName(Names.OldBridgeNames[i]).Length > 0;
                if (running) { Log.Info("Bridge launcher: a bridge is already running, not starting another"); return; }
            }
            catch (Exception ex) { Log.Warn("Bridge launcher: process list unavailable (" + ex.Message + ")"); }
            bool inUse = PortProbe.UdpInUse(telemetryPort);
            if (PortProbe.LastError != null) Log.Warn("Bridge launcher: Windows' UDP table could not be read (" + PortProbe.LastError + "); checked UDP " + telemetryPort + " on 127.0.0.1 only");
            if (inUse) { Log.Info("Bridge launcher: UDP " + telemetryPort + " is already in use (a bridge, or one started by hand as `run --sim`), not starting one"); return; }
            try
            {
                var psi = new ProcessStartInfo(exe, "run --auto");   // no --parent: the mod's `bye` stops it when the game quits
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                if (Config.BridgeShowWindow) { psi.UseShellExecute = true; psi.WindowStyle = ProcessWindowStyle.Minimized; }
                else { psi.UseShellExecute = false; psi.CreateNoWindow = true; }
                _proc = Process.Start(psi);
                Log.Info("Bridge launcher: started " + exe + " (pid " + _proc.Id + "); its log is in the logs folder beside it");
            }
            catch (Exception ex)
            {
                _note = "Bridge failed to start";
                Log.Error("Bridge launcher: could not start " + exe, ex);
            }
        }

        /// <summary>Once a second: notice the bridge exiting (while the game runs it only exits by itself on a fatal error).</summary>
        public void Tick()
        {
            if (_proc == null || _exitLogged || Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1f;
            bool exited;
            try { exited = _proc.HasExited; } catch (Exception) { return; }
            if (!exited) return;
            _exitLogged = true;
            int code = -1;
            try { code = _proc.ExitCode; } catch (Exception) { }
            _note = "Bridge stopped";
            Log.Warn("Bridge launcher: the bridge exited (code " + code + "); see bridge_*.log in its logs folder");
        }

        private bool Running
        {
            get { try { return _proc != null && !_proc.HasExited; } catch (Exception) { return false; } }
        }

        /// <summary>The TRAINER line for the HUD: the bridge's own status while it is fresh, else what the launcher knows. "" = nothing to show.</summary>
        public string HudText(BridgeLink link, out bool ok)
        {
            ok = false;
            string state, text;
            if (link != null && link.TryGetStatus(3.0, out state, out text)) { ok = state == "connected"; return HudRules.TrainerText(state, text); }
            TrainerSample s;
            if (link != null && link.TryGetTrainer(out s) && s.Connected && DateTime.UtcNow.Ticks - s.RecvTicks < 3 * TimeSpan.TicksPerSecond)
            { ok = true; return "Trainer connected"; }   // a bridge that sends no status (an older one)
            if (Running) return "Starting bridge";
            return _note;
        }
    }
}
