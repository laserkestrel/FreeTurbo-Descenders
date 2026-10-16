using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// Indoor-cycling-app-style workout list (docs/REQUIREMENTS.md "Workout HUD", V2.18): header with time left and progress bar, the current section large on top,
    /// the next few below in intensity-coloured bands, a bias footer. Drawn with filled rectangles and one label per row (no GUILayout).
    /// Everything comes from the bridge's `session` message (the bridge runs the workout); strings are rebuilt once per message.
    /// </summary>
    internal sealed class WorkoutPanel
    {
        private static readonly Color[] Zones =
        {
            new Color(0.50f, 0.53f, 0.55f), new Color(0.27f, 0.56f, 0.90f), new Color(0.32f, 0.72f, 0.37f),
            new Color(0.95f, 0.80f, 0.22f), new Color(0.95f, 0.56f, 0.17f), new Color(0.86f, 0.27f, 0.24f),
        };
        private static readonly Color Neutral = new Color(0.24f, 0.28f, 0.25f);
        private static readonly Color Back = new Color(0.04f, 0.05f, 0.04f, 0.85f);

        // Backdrop and bands share WorkoutHudOpacity; bands stay more opaque than the backdrop so the colour still reads against bright scenery.
        private static Color Backdrop { get { return new Color(Back.r, Back.g, Back.b, Back.a * Config.WorkoutHudOpacity); } }
        private static Color Tint(Color c) { c.a = Mathf.Lerp(0.35f, 1f, Config.WorkoutHudOpacity); return c; }

        private GUIStyle _left, _right;
        private SessionView _cacheView;
        private string _curTitle = "", _curRange = "";
        private string[] _titles = new string[0], _ranges = new string[0];
        private string _finishIn = "", _stepLeft = "", _now = "";
        private string _completeText = "", _completeFor = "";

        public bool Wanted(ErgControl erg) { return erg.Workout != null || erg.ShowComplete; }

        private static int RowsShown(SessionWorkout r) { return System.Math.Max(0, System.Math.Min(Config.WorkoutHudRows, r.Upcoming.Count)); }
        private static int MoreRows(SessionWorkout r) { return r.Steps - r.Step - RowsShown(r); }

        // Every label is drawn this many points above its nominal size; the panel is widened to match so section titles do not clip.
        private const int FontBump = 2;
        private static float Scale { get { return Screen.height / 1080f * Config.WorkoutHudScale; } }
        public static float Width { get { return 330f * Scale; } }

        public float Height(ErgControl erg)
        {
            float s = Scale;
            SessionWorkout r = erg.Workout;
            if (r == null) return 96f * s;
            int n = RowsShown(r);
            bool more = MoreRows(r) > 0;
            return (66f + (erg.PlanLine.Length > 0 ? 20f : 0f) + (r.Started && !r.Held ? 0f : 22f) + 58f + n * 40f + (more ? 24f : 0f) + 30f) * s;
        }

        public void Draw(ErgControl erg, float x, float y)
        {
            EnsureStyles();
            float s = Scale, w = Width, h = Height(erg);
            Fill(new Rect(x, y, w, h), Backdrop);
            SessionWorkout r = erg.Workout;
            if (r == null) { DrawComplete(erg, x, y, w, s); return; }

            Refresh(erg.Session, r);
            float pad = 10f * s, iw = w - 2f * pad, cy = y + 6f * s;

            Text(new Rect(x + pad, cy, iw, 22f * s), r.Name, false, 16, Color.white);
            cy += 22f * s;
            string planLine = erg.PlanLine;
            if (planLine.Length > 0)
            {
                Text(new Rect(x + pad, cy, iw, 20f * s), planLine, false, 13, new Color(0.7f, 0.85f, 1f));
                cy += 20f * s;
            }
            Text(new Rect(x + pad, cy, iw, 20f * s), "Finish in " + _finishIn, false, 14, Hud.Accent);
            cy += 22f * s;
            float frac = r.TotalS > 0f ? Mathf.Clamp01(r.ElapsedS / r.TotalS) : 0f;
            Fill(new Rect(x + pad, cy, iw, 6f * s), new Color(1f, 1f, 1f, 0.15f));
            Fill(new Rect(x + pad, cy, iw * frac, 6f * s), Hud.Accent);
            cy += 12f * s;
            if (r.Held) { Text(new Rect(x + pad, cy, iw, 20f * s), "On hold: RB + up to come back", false, 13, new Color(1f, 0.8f, 0f)); cy += 22f * s; }
            else if (!r.Started) { Text(new Rect(x + pad, cy, iw, 20f * s), "Start pedalling to begin", false, 13, new Color(1f, 0.8f, 0f)); cy += 22f * s; }

            // current section
            WorkoutStep cur = r.Current;
            float ch = 56f * s;
            Rect cr = new Rect(x + 4f * s, cy + 2f * s, w - 8f * s, ch);
            Color cc = Band(cur, out Color textOn);
            Fill(cr, Tint(Color.Lerp(cc, Color.white, 0.35f)));
            float sf = cur.DurationS > 0f ? Mathf.Clamp01(1f - r.StepRemainingS / cur.DurationS) : 1f;
            Fill(new Rect(cr.x, cr.y, cr.width * sf, cr.height), Tint(cc));
            Text(new Rect(cr.x + 8f * s, cr.y + 4f * s, cr.width * 0.66f, 30f * s), _curTitle, false, 20, textOn);
            Text(new Rect(cr.x, cr.y + 4f * s, cr.width - 8f * s, 30f * s), _stepLeft, true, 20, textOn);
            string sub = _curRange;
            if (sub.Length > 0 && _now.Length > 0) sub += "   now " + _now;
            Text(new Rect(cr.x + 8f * s, cr.y + 34f * s, cr.width - 16f * s, 20f * s), sub, false, 13, textOn);
            cy += ch + 4f * s;

            // upcoming
            int n = RowsShown(r);
            for (int i = 0; i < n; i++)
            {
                Rect rr = new Rect(x + 4f * s, cy + 2f * s, w - 8f * s, 36f * s);
                Color rc = Band(r.Upcoming[i], out Color rt);
                Fill(rr, Tint(rc));
                Text(new Rect(rr.x + 8f * s, rr.y + 2f * s, rr.width * 0.62f, 32f * s), _titles[i], false, 15, rt);
                Text(new Rect(rr.x, rr.y + 2f * s, rr.width - 8f * s, 32f * s), _ranges[i], true, 12, rt);
                cy += 40f * s;
            }
            int left = MoreRows(r);
            if (left > 0) { Text(new Rect(x + pad, cy, iw, 22f * s), "+ " + left + " more", false, 13, Hud.Accent); cy += 24f * s; }

            int pct = Mathf.RoundToInt(100f + r.BiasPct);
            Text(new Rect(x + pad, cy + 4f * s, iw, 22f * s), "Bias " + pct + "%      RB + left / right", false, 12, Hud.Accent);
        }

        private void DrawComplete(ErgControl erg, float x, float y, float w, float s)
        {
            if (_completeFor != erg.CompleteName || _completeText.Length == 0)
            {
                _completeFor = erg.CompleteName;
                _completeText = "Total time " + WorkoutRunner.Fmt(erg.CompleteTotalS);
            }
            float pad = 10f * s;
            Text(new Rect(x + pad, y + 6f * s, w - 2f * pad, 22f * s), erg.CompleteName, false, 16, Color.white);
            Text(new Rect(x + pad, y + 30f * s, w - 2f * pad, 34f * s), "Workout complete", false, 24, Hud.Accent);
            Text(new Rect(x + pad, y + 66f * s, w - 2f * pad, 22f * s), _completeText, false, 14, Color.white);
        }

        /// <summary>Rebuild the strings once per new session message (4 Hz; the view never changes after it arrives).</summary>
        private void Refresh(SessionView v, SessionWorkout r)
        {
            if (object.ReferenceEquals(_cacheView, v)) return;
            _cacheView = v;
            float bias = r.BiasFactor;
            _curTitle = WorkoutRows.Title(r.Current, bias);
            _curRange = WorkoutRows.Range(r.Current, bias);
            int c = r.Upcoming.Count;
            if (_titles.Length != c) { _titles = new string[c]; _ranges = new string[c]; }
            for (int i = 0; i < c; i++)
            {
                _titles[i] = WorkoutRows.Title(r.Upcoming[i], bias);
                _ranges[i] = WorkoutRows.Range(r.Upcoming[i], bias);
            }
            _finishIn = WorkoutRunner.Fmt(r.TotalRemainingS);
            _stepLeft = WorkoutRunner.Fmt(r.StepRemainingS);
            _now = float.IsNaN(v.TargetW) || r.Held ? "" : v.TargetW.ToString("F0") + "w";   // on hold the target is the manual one, not this row's
        }

        private static Color Band(WorkoutStep st, out Color textOn)
        {
            int z = WorkoutRows.Zone(st, Config.Rider.FtpW);   // FTP 0 (no rider message yet) = neutral rows
            Color c = z < 0 ? Neutral : Zones[z];
            float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            textOn = lum > 0.6f ? new Color(0.06f, 0.07f, 0.06f) : Color.white;
            return c;
        }

        private static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private void Text(Rect r, string s, bool right, int size, Color c)
        {
            if (s.Length == 0) return;
            GUIStyle st = right ? _right : _left;
            st.fontSize = Mathf.Max(9, Mathf.RoundToInt((size + FontBump) * Scale));
            st.normal.textColor = c;
            GUI.Label(r, s, st);
        }

        private void EnsureStyles()
        {
            if (_left != null) return;
            _left = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false, alignment = TextAnchor.UpperLeft, clipping = TextClipping.Clip };
            _right = new GUIStyle(_left) { alignment = TextAnchor.UpperRight };
            _left.padding = new RectOffset(0, 0, 0, 0);
            _right.padding = new RectOffset(0, 0, 0, 0);
        }
    }
}
