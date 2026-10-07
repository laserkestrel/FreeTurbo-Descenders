using UnityEngine;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// Screen-space confetti shower for a completed workout: IMGUI only (tinted Texture2D.whiteTexture, rotated with GUI.matrix), no assets.
    /// Pieces are spawned over the first half of the shower, fall with a sideways wobble and flip (width follows cos of the spin), and fade out over the last second.
    /// </summary>
    internal static class Confetti
    {
        private struct Piece
        {
            public float X, Y, Vy, Sway, SwayFreq, SwayPhase, Angle, Spin, Flip, FlipSpeed, W, H;
            public Color C;
        }

        private static readonly Color[] Palette =
        {
            new Color(0.96f, 0.26f, 0.21f), new Color(1f, 0.76f, 0.03f), new Color(0.30f, 0.69f, 0.31f),
            new Color(0.13f, 0.59f, 0.95f), new Color(0.61f, 0.15f, 0.69f), new Color(1f, 0.44f, 0.72f), new Color(0f, 0.74f, 0.83f)
        };

        private const int MaxPieces = 400;
        private static readonly Piece[] _p = new Piece[MaxPieces];
        private static int _count;
        private static float _start, _end = -1f, _last, _spawnAcc;
        private static System.Random _rng = new System.Random();

        public static bool Active { get { return _end > 0f && Time.unscaledTime < _end; } }

        /// <summary>Start a shower lasting <paramref name="seconds"/> (0 or less = nothing).</summary>
        public static void Burst(float seconds)
        {
            if (seconds <= 0f) return;
            _count = 0; _spawnAcc = 0f;
            _start = _last = Time.unscaledTime;
            _end = _start + seconds;
            // a first handful already on screen so it starts with a pop, not a trickle
            for (int i = 0; i < 40; i++) Spawn(Rand(0f, Screen.height * 0.35f));
            Log.Info("Confetti: " + seconds.ToString("F1") + " s");
        }

        public static void Clear() { _end = -1f; _count = 0; }

        public static void OnGUI()
        {
            if (_end < 0f) return;
            float now = Time.unscaledTime;
            if (now >= _end) { Clear(); return; }
            if (Event.current.type != EventType.Repaint) return;

            float dt = Mathf.Min(now - _last, 0.1f);
            _last = now;
            float dur = _end - _start;
            float h = Screen.height;

            // spawn at a steady rate over the first half of the shower
            if (now - _start < dur * 0.5f)
            {
                _spawnAcc += dt * 160f * Mathf.Max(0.5f, h / 1080f);
                while (_spawnAcc >= 1f) { _spawnAcc -= 1f; Spawn(-Rand(10f, 40f)); }
            }

            float alpha = Mathf.Clamp01(_end - now);   // fade over the last second
            Matrix4x4 oldM = GUI.matrix;
            Color oldC = GUI.color;
            for (int i = 0; i < _count; )
            {
                Piece p = _p[i];
                p.Y += p.Vy * dt;
                p.SwayPhase += p.SwayFreq * dt;
                p.X += Mathf.Sin(p.SwayPhase) * p.Sway * dt;
                p.Angle += p.Spin * dt;
                p.Flip += p.FlipSpeed * dt;
                if (p.Y > h + 40f) { _p[i] = _p[--_count]; continue; }   // off the bottom: recycle the slot
                _p[i] = p;
                i++;

                float w = p.W * Mathf.Max(0.12f, Mathf.Abs(Mathf.Cos(p.Flip)));
                Color c = p.C * (0.75f + 0.25f * Mathf.Abs(Mathf.Cos(p.Flip)));   // a little darker edge-on
                c.a = alpha;
                GUI.matrix = oldM;
                GUIUtility.RotateAroundPivot(p.Angle, new Vector2(p.X, p.Y));
                GUI.color = c;
                GUI.DrawTexture(new Rect(p.X - w * 0.5f, p.Y - p.H * 0.5f, w, p.H), Texture2D.whiteTexture);
            }
            GUI.matrix = oldM;
            GUI.color = oldC;
        }

        private static void Spawn(float y)
        {
            if (_count >= MaxPieces) return;
            float s = Mathf.Max(0.6f, Screen.height / 1080f);
            _p[_count++] = new Piece
            {
                X = Rand(0f, Screen.width), Y = y,
                Vy = Rand(160f, 320f) * s,
                Sway = Rand(30f, 110f) * s, SwayFreq = Rand(1.5f, 4f), SwayPhase = Rand(0f, 6.28f),
                Angle = Rand(0f, 360f), Spin = Rand(-240f, 240f),
                Flip = Rand(0f, 6.28f), FlipSpeed = Rand(4f, 11f),
                W = Rand(8f, 14f) * s, H = Rand(12f, 22f) * s,
                C = Palette[_rng.Next(Palette.Length)]
            };
        }

        private static float Rand(float a, float b) { return a + (float)_rng.NextDouble() * (b - a); }
    }
}
