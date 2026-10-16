using System.Text;
using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>Top-left IMGUI diagnostic panel (same OnGUI approach as Sandbox's SessionHUD).</summary>
    internal sealed class Hud
    {
        public bool Visible;
        public BridgeLauncher Launcher;   // TRAINER line: the bridge's status
        public WorldGate World;   // rider column and speedo arc only once the game world is up
        public Spectators Crowd;   // PARTY MODE line: is the crowd out
        /// <summary>The HUD's normal text: the bridge window's teal. Warnings keep their own colours.</summary>
        public static readonly Color Accent = new Color32(HudRules.AccentR, HudRules.AccentG, HudRules.AccentB, 255);

        private string _text = "", _diagText = "";   // rider stats, then (below the workout panel) the developer diagnostics
        private readonly WorkoutPanel _panel = new WorkoutPanel();
        private readonly SpeedoArc _arc = new SpeedoArc();   // power / heart rate / cadence around the game's speedometer dial
        private readonly string[] _vals = { "--", "--", "--" };
        private bool _lost;
        private ErgControl _erg;
        private float _nextBuild;
        private GUIStyle _style;
        private Texture2D _bg;
        private readonly StringBuilder _sb = new StringBuilder(1024);

        public Hud() { Visible = Config.HudVisibleAtStart; }

        public void Rebuild(RideStats stats, GameState s, TerrainSampler ter, WriteTest wt, PedalTest pt, FitnessMode fm, ErgControl erg, BridgeLink bridge, AutoSteer auto, string key9, string key7, string key8)
        {
            float now = Time.unscaledTime;
            if (now < _nextBuild) return;
            _nextBuild = now + 0.1f;

            _erg = erg;
            var b = _sb; b.Length = 0;
            bool trainerOk = false;
            string trainer = Launcher != null ? Launcher.HudText(bridge, out trainerOk) : "";
            AppendRider(b, erg, auto, trainer, trainerOk);
            _text = b.ToString();
            _lost = fm.FailSafe;
            _vals[0] = fm.Active ? fm.Avg3W.ToString("F0") + " W" : "--";
            _vals[1] = fm.Active && fm.HeartRate > 0f ? fm.HeartRate.ToString("F0") + " bpm" : "--";
            _vals[2] = fm.Active && fm.Cadence > 0f ? fm.Cadence.ToString("F0") + " rpm" : "--";
            if (!Config.HudDiagnostics) { _diagText = ""; return; }
            b.Length = 0;
            b.AppendLine("<b>DIAGNOSTICS</b>");
            b.AppendLine("Build: " + BuildStamp.Text);   // developer panel only; the rider reads the build in the bridge window
            b.AppendLine();
            b.AppendLine("Player: " + Flag(s.PlayerFound));
            b.AppendLine("Vehicle: " + Flag(s.VehicleFound));
            b.AppendLine("Rigidbody: " + Flag(s.RigidbodyFound));
            b.AppendLine();
            b.AppendLine("Position:");
            b.AppendLine("  X: " + s.Position.x.ToString("F1") + "   Y: " + s.Position.y.ToString("F1") + "   Z: " + s.Position.z.ToString("F1"));
            b.AppendLine("Velocity:");
            b.AppendLine("  X: " + s.Velocity.x.ToString("F1") + "   Y: " + s.Velocity.y.ToString("F1") + "   Z: " + s.Velocity.z.ToString("F1"));
            b.AppendLine("Speed: <b>" + s.SpeedKmh.ToString("F1") + " km/h</b>  (" + s.SpeedMs.ToString("F1") + " m/s)");
            b.AppendLine("  game-scale: " + s.GameSpeedKmh.ToString("F1") + " km/h");
            b.AppendLine("Grounded: " + (s.Grounded.HasValue ? (s.Grounded.Value ? "YES" : "NO") : "UNKNOWN"));
            b.AppendLine("Scene: " + s.Scene);
            b.AppendLine();
            b.AppendLine("Ground elevation: " + Num(ter.GroundY, "F1", " m") + "   [" + ter.DirSource + " dir]");
            b.AppendLine("Current gradient: " + Pct(ter.CurrentGradientPct));
            b.AppendLine("Samples ahead:");
            for (int i = 0; i < TerrainSampler.AheadDistances.Length; i++)
                b.AppendLine("  +" + TerrainSampler.AheadDistances[i].ToString("F0") + "m: " + Pct(ter.AheadPct[i]));
            b.AppendLine();
            AppendPath(b, auto);
            b.Append("Test cap: ");
            b.AppendLine(wt.Active ? "<color=#ffcc00>ACTIVE " + wt.CapKmh.ToString("F0") + " km/h, " + wt.RemainingSec.ToString("F1") + "s</color>" : "off");
            b.AppendLine("Fitness: " + (fm.Active ? "<color=#ffcc00>" + fm.State.ToUpper() + "</color>  " + fm.Avg3W.ToString("F0") + " W (3 s)  cad " + fm.Cadence.ToString("F0") + "  model " + (fm.VModel * 3.6f).ToString("F1") + " km/h" + (fm.FailSafe ? "  <color=#ff6060>TRAINER DATA LOST</color>" : "") + (auto.Turn != null && auto.Turn.Phase != "idle" ? "  <color=#ff6060>U-TURN " + auto.Turn.Phase.ToUpper() + "</color>" : "") : fm.WaitingForRider ? "waiting for the rider profile from the bridge" : "off (no trainer data)"));
            b.AppendLine("Pedal test: " + (pt.Active ? (pt.LeadInRemainingSec > 0f ? "<color=#ffcc00>STARTS IN " + pt.LeadInRemainingSec.ToString("F0") + " s</color>" : "<color=#ffcc00>FORCING " + pt.Target.ToString("F2") + "  (" + (pt.LevelIndex + 1) + "/" + pt.LevelCount + ", " + pt.StepRemainingSec.ToString("F0") + " s)</color>  pad " + (float.IsNaN(pt.PadRaw) ? "n/a" : pt.PadRaw.ToString("F2"))) : "off"));
            if (bridge != null)
            {
                double since = bridge.SecondsSinceLastRx;
                b.AppendLine("Bridge UDP: tx " + bridge.TxCount + (bridge.TxErrors > 0 ? " (err " + bridge.TxErrors + ")" : "")
                    + ", rx " + bridge.RxCount + (since >= 0 ? " (" + since.ToString("F0") + "s ago)" : "") + (bridge.RxBound ? "" : "  [cmd port unbound]"));
            }
            else b.AppendLine("Bridge UDP: disabled");
            b.AppendLine();
            b.Append("<size=11>" + key9 + " HUD   " + (key7 == "None" ? "" : key7 + " speed-cap test   ") + (key8 == "None" ? "" : key8 + " dump   ") + Config.AutoSteerKey + " autosteer</size>");
            _diagText = b.ToString();
        }

        public void OnGUI()
        {
            EnsureStyle();
            if (Visible && _text.Length > 0)
            {
                // Right-hand column, top to bottom: rider stats, workout panel, diagnostics (V2.18).
                // Menus / loading screens: only the developer diagnostics.
                bool world = World == null || World.Ready;
                bool left = Config.HudSide == "left";
                float y = HudRules.TopY(Screen.height);   // below the game's own status box in that corner
                if (world)
                {
                    var content = new GUIContent(_text);
                    Vector2 size = _style.CalcSize(content);
                    GUI.Label(new Rect(left ? 12f : Screen.width - size.x - 12f, y, size.x, size.y), content, _style);
                    y += size.y + 6f;
                    _arc.Draw(_vals, _lost);
                }
                if (world && _erg != null && _panel.Wanted(_erg))
                {
                    _panel.Draw(_erg, left ? 12f : Screen.width - WorkoutPanel.Width - 12f, y);
                    y += _panel.Height(_erg) + 6f;
                }
                if (_diagText.Length > 0)
                {
                    var dc = new GUIContent(_diagText);
                    Vector2 ds = _style.CalcSize(dc);
                    GUI.Label(new Rect(left ? 12f : Screen.width - ds.x - 12f, y, ds.x, ds.y), dc, _style);
                }
            }
        }

        private void EnsureStyle()
        {
            if (_style == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, new Color(0.04f, 0.05f, 0.04f, 0.78f));
                _bg.Apply();
                _style = new GUIStyle(GUI.skin.label);
                _style.richText = true;
                _style.normal.textColor = Accent;
                _style.normal.background = _bg;
                _style.padding = new RectOffset(10, 10, 8, 8);
                _style.wordWrap = false;
            }
            _style.fontSize = Mathf.Max(11, Mathf.RoundToInt(Screen.height / 1080f * 15f));
        }

        /// <summary>The rider block: the words and the layout are the pure HudRules; climbed metres and the build are in the bridge window.</summary>
        private void AppendRider(StringBuilder b, ErgControl erg, AutoSteer auto, string trainer, bool trainerOk)
        {
            b.Append(HudRules.RiderBlock(trainer, trainerOk, erg.ModeLabel, erg.ErgRequested, Config.ErgToggleKey.ToString(),
                auto == null ? (bool?)null : auto.Engaged, Config.AutoSteerKey.ToString(),
                Crowd == null ? (bool?)null : Crowd.Active, Config.SpectatorKey.ToString(), erg.Note));
        }

        private static void AppendPath(StringBuilder b, AutoSteer a)
        {
            if (a == null || !a.Track.Loaded) { b.AppendLine("Path: none found (generated course only)"); return; }
            b.AppendLine("Path: " + a.Track.Points.Length + " pts, " + a.Track.Length.ToString("F0") + " m"
                + (a.OnPath ? "   at " + a.Progress.ToString("F0") + " m, " + (a.Dir > 0 ? "to end" : "to start")
                            : "   <color=#ff6060>" + a.DistToPath.ToString("F0") + " m off path</color>"));
            if (a.OnPath)
            {
                b.AppendLine("  off-line: " + (a.XTrack >= 0 ? "+" : "") + a.XTrack.ToString("F1") + " m ( + = right)   path grade: " + Pct(a.PathGradPct));
                b.AppendLine("  path ahead 5/10/20/30m: " + Pct(a.PathAheadPct[0]) + " " + Pct(a.PathAheadPct[1]) + " " + Pct(a.PathAheadPct[2]) + " " + Pct(a.PathAheadPct[3]));
            }
            b.AppendLine("AutoSteer: " + (a.Engaged ? "<color=#ffcc00>ON</color>  steer " + (a.Cmd >= 0 ? "+" : "") + a.Cmd.ToString("F2") + "  lookahead " + a.Lookahead.ToString("F1") + " m" + (a.PastEnd > 0f ? "  <color=#ff6060>FINISH: GAME HAS CONTROL</color>" : (a.EndDist <= a.WarnAt ? "  <color=#ff6060>FINISH IN " + a.EndDist.ToString("F0") + " m</color>" : "")) : "off"));
        }

        private static string Flag(bool ok) { return ok ? "<color=#7CFC00>FOUND</color>" : "<color=#ff6060>NOT FOUND</color>"; }
        private static string Num(float v, string fmt, string unit) { return float.IsNaN(v) ? "n/a" : v.ToString(fmt) + unit; }
        private static string Pct(float v) { return float.IsNaN(v) ? "n/a" : (v >= 0 ? "+" : "") + v.ToString("F1") + "%"; }
    }
}
