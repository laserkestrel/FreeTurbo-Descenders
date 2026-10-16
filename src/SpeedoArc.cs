using System;
using System.Reflection;
using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// Power / heart rate / cadence laid out along the outer edge of the game's speedometer dial (docs/HUD_PLACEMENT.md).
    /// The dial is the quarter-circle image `speedometerbg` under UI_Speedometer (Radial90, origin bottom-right), so its arc is centred on that
    /// rectangle's bottom-right corner with a radius of about its side length. The game's Main Canvas scales with screen HEIGHT (ScaleWithScreenSize,
    /// 1920x1080 reference, match = 1) and anchors the panel to the bottom-right corner, so if the game's rectangle cannot be read the same numbers
    /// are rebuilt from Screen.height. Everything is recomputed when the screen size changes.
    /// </summary>
    internal sealed class SpeedoArc
    {
        // Measured on 2560x1080 (rect 231x222 px, 18 px from the right edge, 8 px above the bottom), expressed at 1080 px height.
        private const float FallbackRightMargin = 18f, FallbackBottomMargin = 8f, FallbackRadius = 226f;
        private const float BoxW = 104f, BoxH = 56f, Gap = 14f;
        private static readonly float[] AnglesDeg = { 25f, 52f, 78f };   // measured from straight left (0) to straight up (90) around the arc centre
        // Sampled from the game's own HUD: the big speed "0" and the score are RGB (53,108,164); the "km/h" lettering is (223,223,223).
        private static readonly Color ValueBlue = new Color32(53, 108, 164, 255), LabelWhite = new Color32(223, 223, 223, 255), LostColour = new Color(1f, 0.38f, 0.38f);
        private static readonly string[] Labels = { "POWER", "HEART RATE", "CADENCE" };

        private Vector2 _center;
        private float _radius, _scale = 1f;
        private int _w, _h;
        private float _nextCheck;
        private Component _speedo;
        private GUIStyle _label, _value;

        public void Draw(string[] values, bool lost)
        {
            Update();
            EnsureStyles();
            float s = _scale;
            for (int i = 0; i < 3; i++)
            {
                float a = AnglesDeg[i] * Mathf.Deg2Rad;
                Vector2 n = new Vector2(-Mathf.Cos(a), -Mathf.Sin(a));   // outward normal, IMGUI y points down
                float w = BoxW * s, h = BoxH * s;
                // push the box out along the normal until its nearest edge touches the ring of radius (R + Gap)
                float reach = (_radius + Gap * s) + Mathf.Abs(n.x) * w / 2f + Mathf.Abs(n.y) * h / 2f;
                Vector2 c = _center + n * reach;
                Rect r = new Rect(c.x - w / 2f, c.y - h / 2f, w, h);
                Color col = lost ? LostColour : ValueBlue;
                _label.fontSize = Mathf.Max(9, Mathf.RoundToInt(11f * s));
                _value.fontSize = Mathf.Max(12, Mathf.RoundToInt(28f * s));
                _label.normal.textColor = LabelWhite; _value.normal.textColor = col;
                GUI.Label(new Rect(r.x, r.y + 2f * s, r.width, 14f * s), Labels[i], _label);
                GUI.Label(new Rect(r.x, r.y + 14f * s, r.width, r.height - 14f * s), values[i], _value);
            }
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            bool sizeChanged = _w != Screen.width || _h != Screen.height;
            if (!sizeChanged && now < _nextCheck) return;
            _nextCheck = now + 3f;   // re-read now and then: the dial may not exist yet, or the game may rebuild its UI
            _w = Screen.width; _h = Screen.height;
            _scale = _h / 1080f;
            Vector2 c; float r; string source;
            if (TryReadGame(out c, out r)) source = "game";
            else
            {
                c = new Vector2(_w - FallbackRightMargin * _scale, _h - FallbackBottomMargin * _scale);
                r = FallbackRadius * _scale;
                source = "fallback (scale rule)";
            }
            bool changed = source != _source || (c - _center).sqrMagnitude > 4f || Mathf.Abs(r - _radius) > 2f;
            _center = c; _radius = r;
            if (changed || sizeChanged)
            {
                _source = source;
                Log.Info("SpeedoArc: " + _w + "x" + _h + " arc centre (" + c.x.ToString("F0") + "," + c.y.ToString("F0") + ") radius " + r.ToString("F0") + " px, source " + source);
            }
        }
        private string _source = "";

        /// <summary>Read the dial image's rectangle from the game's UI (works while the dial is hidden too, the hierarchy still has a layout).</summary>
        private bool TryReadGame(out Vector2 centre, out float radius)
        {
            centre = default(Vector2); radius = 0f;
            try
            {
                if (_speedo == null)
                {
                    Type t = FindType("UI_Speedometer");
                    if (object.ReferenceEquals(t, null)) return false;
                    foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(t))
                    {
                        Component c = o as Component;
                        if (c != null && c.gameObject.scene.IsValid()) { _speedo = c; break; }
                    }
                    if (_speedo == null) return false;
                }
                RectTransform dial = null;
                foreach (RectTransform rt in _speedo.GetComponentsInChildren<RectTransform>(true))
                    if (rt.name == "speedometerbg") { dial = rt; break; }
                if (dial == null) return false;
                Canvas canvas = _speedo.GetComponentInParent<Canvas>();
                if (canvas != null) canvas = canvas.rootCanvas;
                Camera cam = null;
                if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
                var corners = new Vector3[4];
                dial.GetWorldCorners(corners);
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 p = cam != null ? cam.WorldToScreenPoint(corners[i]) : corners[i];
                    float gy = _h - p.y;
                    x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); y0 = Mathf.Min(y0, gy); y1 = Mathf.Max(y1, gy);
                }
                float w = x1 - x0, h = y1 - y0, s = _h / 1080f;
                bool sane = w > 60f * s && w < 600f * s && h > 60f * s && h < 600f * s && x1 > _w * 0.5f && x1 < _w + 60f * s && y1 > _h * 0.5f && y1 < _h + 60f * s;
                if (!sane) return false;
                centre = new Vector2(x1, y1);   // Radial90 with origin bottom-right: the arc is centred on the rectangle's bottom-right corner
                radius = (w + h) / 2f;
                return true;
            }
            catch (Exception) { _speedo = null; return false; }
        }

        private static Type FindType(string name)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t = asm.GetType(name, false); if (!object.ReferenceEquals(t, null)) return t; }
                catch (Exception) { }
            }
            return null;
        }

        private void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, wordWrap = false, richText = false };
            _value = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = false, richText = false, fontStyle = FontStyle.Bold };
            _label.padding = new RectOffset(0, 0, 0, 0);
            _value.padding = new RectOffset(0, 0, 0, 0);
        }
    }
}
