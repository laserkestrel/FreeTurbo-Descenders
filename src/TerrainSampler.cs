using System;
using System.Collections.Generic;
using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// Ground-height and gradient sampler that works on Unity Terrain, mesh ramps/bumps and mixtures, and is not
    /// fooled by canopies, roofs or other overhead meshes.
    ///
    /// Method: the surface under the rider is the HIGHEST collider at or just above the rider's own position
    /// (so a tent roof above a rider standing on the ground is ignored, and a ramp under a jumping rider is used).
    /// From there the surface is followed outward along the direction of travel in 1 m steps: at each step take the
    /// highest surface within a plausible rise/drop of the previous height. Ramps rise continuously out of the ground
    /// so they are followed; a roof hanging metres above the ground is never reachable. Results are median-filtered
    /// then exponentially smoothed. Direction is horizontal VELOCITY when moving, else the bike's forward (never the camera).
    /// </summary>
    internal sealed class TerrainSampler
    {
        public static readonly float[] AheadDistances = { 5f, 10f, 20f, 30f };

        private const float SampleInterval = 0.05f;   // 20 Hz
        private const float RayStartAbove = 40f;       // metres above the reference height
        private const float RayLength = 300f;
        private const float MinDirSpeed = 1.5f;        // m/s of horizontal velocity needed to trust velocity direction
        private const float SmoothTau = 0.35f;         // seconds
        private const float LocalHalfBase = 2f;        // metres either side for "current ground gradient"
        private const float TeleportJump = 15f;        // m between samples => reset filters
        private const float StartAllowance = 0.4f;     // surface may be this far above the rider's own transform
        private const int ChainSteps = 30;             // 1 m steps ahead
        private const float StepM = 1f;
        // Allowed change in surface height per step (per metre travelled, plus a constant).
        private const float UpPerM = 2.5f, UpAbs = 0.3f;    // up to ~68 degree ramps
        private const float DownPerM = 4f, DownAbs = 1.0f;

        // Outputs (world metres / percent). NaN => no valid sample.
        public float GroundY = float.NaN;
        public float CurrentGradientPct = float.NaN;
        public readonly float[] AheadPct = new float[AheadDistances.Length];
        public string DirSource = "-";
        public Vector3 Dir = Vector3.forward;
        public string LastGroundKind = "-";

        private readonly float[] _aheadSmooth = new float[AheadDistances.Length];
        private readonly Median[] _aheadMed = new Median[AheadDistances.Length];
        private readonly Median _curMed = new Median();
        private float _curSmooth = float.NaN;
        private float _next;
        private Vector3 _lastPos;
        private bool _havePos;
        private readonly Dictionary<string, int> _hitKinds = new Dictionary<string, int>();
        private readonly RaycastHit[] _buf = new RaycastHit[32];
        private readonly float[] _fwd = new float[ChainSteps + 1];
        private readonly float[] _back = new float[(int)LocalHalfBase + 1];

        // The rider/bike colliders live on their own layers and are not always under Player_Human, so mask them out.
        private static readonly int GroundMask = BuildGroundMask();

        private static int BuildGroundMask()
        {
            int mask = Physics.DefaultRaycastLayers;
            foreach (string name in new[] { "Bike", "Cyclist" })
            {
                int l = LayerMask.NameToLayer(name);
                if (l >= 0) mask &= ~(1 << l);
            }
            return mask;
        }

        public TerrainSampler()
        {
            for (int i = 0; i < AheadDistances.Length; i++) _aheadMed[i] = new Median();
            Reset();
        }

        public void Reset()
        {
            GroundY = float.NaN; CurrentGradientPct = float.NaN; _curSmooth = float.NaN; _havePos = false;
            _curMed.Reset();
            for (int i = 0; i < AheadPct.Length; i++) { AheadPct[i] = float.NaN; _aheadSmooth[i] = float.NaN; _aheadMed[i].Reset(); }
        }

        public void Update(GameState s, Transform playerRoot)
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            float dt = Mathf.Max(SampleInterval, now - (_next - SampleInterval));
            _next = now + SampleInterval;

            if (!s.PlayerFound || playerRoot == null) { Reset(); return; }

            Vector3 pos = s.Position;
            if (_havePos && (pos - _lastPos).magnitude > TeleportJump) Reset();
            _lastPos = pos; _havePos = true;

            PickDirection(s);

            float y0;
            if (!PickStart(pos, playerRoot, out y0)) { Reset(); _havePos = true; _lastPos = pos; return; }
            GroundY = y0;

            Chain(pos, Dir, y0, playerRoot, _fwd, ChainSteps);
            Chain(pos, -Dir, y0, playerRoot, _back, (int)LocalHalfBase);

            float alpha = 1f - Mathf.Exp(-dt / SmoothTau);

            for (int i = 0; i < AheadDistances.Length; i++)
            {
                int d = (int)AheadDistances[i];
                float y = _fwd[d];
                if (float.IsNaN(y)) { _aheadMed[i].Reset(); _aheadSmooth[i] = float.NaN; }
                else Smooth(ref _aheadSmooth[i], _aheadMed[i].Push((y - y0) / d * 100f), alpha);
                AheadPct[i] = _aheadSmooth[i];
            }

            float yf = _fwd[(int)LocalHalfBase], yb = _back[(int)LocalHalfBase];
            bool haveF = !float.IsNaN(yf), haveB = !float.IsNaN(yb);
            if (haveF || haveB)
            {
                // Central difference when both sides are followable; one-sided (using the rider's own surface)
                // at an edge such as the top of a tall ramp, so the value does not drop out.
                float hi = haveF ? yf : y0, lo = haveB ? yb : y0;
                float span = (haveF ? LocalHalfBase : 0f) + (haveB ? LocalHalfBase : 0f);
                Smooth(ref _curSmooth, _curMed.Push((hi - lo) / span * 100f), alpha);
                CurrentGradientPct = _curSmooth;
            }
            else { _curMed.Reset(); _curSmooth = float.NaN; CurrentGradientPct = float.NaN; }
        }

        private static void Smooth(ref float state, float sample, float alpha)
        {
            state = float.IsNaN(state) ? sample : state + (sample - state) * alpha;
        }

        private void PickDirection(GameState s)
        {
            Vector3 hv = new Vector3(s.Velocity.x, 0f, s.Velocity.z);
            if (hv.magnitude >= MinDirSpeed)
            {
                Dir = hv.normalized; DirSource = "velocity";
                return;
            }
            // Nearly stationary (or creeping on a slope): velocity direction is meaningless, so use the way the
            // bike is pointing, which is also the direction pedalling would push it.
            Vector3 f = new Vector3(s.Forward.x, 0f, s.Forward.z);
            if (f.sqrMagnitude > 0.01f) { Dir = f.normalized; DirSource = "bike-forward"; }
            else DirSource = "held"; // bike pointing straight up/down: keep last direction
        }

        private int Cast(Vector3 xz, float refY)
        {
            var origin = new Vector3(xz.x, refY + RayStartAbove, xz.z);
            return Physics.RaycastNonAlloc(origin, Vector3.down, _buf, RayLength, GroundMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>Surface under the rider: the highest non-player collider at or just above the rider's transform.</summary>
        private bool PickStart(Vector3 pos, Transform root, out float y)
        {
            y = 0f;
            int n = Cast(pos, pos.y);
            float limit = pos.y + StartAllowance;
            int best = -1; float bestY = float.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                var h = _buf[i];
                if (h.collider == null || h.collider.transform.IsChildOf(root)) continue;
                if (h.point.y <= limit && h.point.y > bestY) { bestY = h.point.y; best = i; }
            }
            if (best >= 0)
            {
                y = bestY;
                Note(_buf[best].collider.GetType().Name + " '" + _buf[best].collider.name + "' layer=" + LayerMask.LayerToName(_buf[best].collider.gameObject.layer));
                return true;
            }
            return TerrainFallback(pos, out y);
        }

        /// <summary>Follow the surface outward in 1 m steps; fills outY[0..count], NaN beyond where it loses the surface.</summary>
        private void Chain(Vector3 pos, Vector3 dir, float y0, Transform root, float[] outY, int count)
        {
            outY[0] = y0;
            float prev = y0;
            for (int i = 1; i <= count; i++)
            {
                float y;
                if (!PickStep(pos + dir * (i * StepM), prev, root, out y))
                {
                    for (int k = i; k <= count; k++) outY[k] = float.NaN;
                    return;
                }
                outY[i] = y; prev = y;
            }
        }

        /// <summary>Highest surface at this column within a plausible rise/drop of the previous surface height.</summary>
        private bool PickStep(Vector3 p, float prevY, Transform root, out float y)
        {
            y = 0f;
            float minY = prevY - (DownPerM * StepM + DownAbs);
            float maxY = prevY + (UpPerM * StepM + UpAbs);
            int n = Cast(p, prevY);
            bool found = false; float best = float.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                var h = _buf[i];
                if (h.collider == null || h.collider.transform.IsChildOf(root)) continue;
                float hy = h.point.y;
                if (hy >= minY && hy <= maxY && hy > best) { best = hy; found = true; }
            }
            if (found) { y = best; return true; }
            if (n == 0)
            {
                float ty;
                if (TerrainFallback(p, out ty) && ty >= minY && ty <= maxY) { y = ty; return true; }
            }
            return false;
        }

        /// <summary>For terrains without a TerrainCollider: read the heightmap directly.</summary>
        private bool TerrainFallback(Vector3 xz, out float y)
        {
            y = 0f;
            var t = Terrain.activeTerrain;
            if (t == null) return false;
            y = t.SampleHeight(xz) + t.transform.position.y;
            Note("Terrain.SampleHeight(fallback)");
            return true;
        }

        private void Note(string kind)
        {
            int c;
            if (!_hitKinds.TryGetValue(kind, out c))
            {
                if (_hitKinds.Count < 40) Log.Info("Sampler: new ground surface type -> " + kind);
                else return;
            }
            _hitKinds[kind] = c + 1;
            LastGroundKind = kind;
        }

        public string HitSummary()
        {
            var parts = new List<string>();
            foreach (var kv in _hitKinds) parts.Add(kv.Key + " x" + kv.Value);
            return parts.Count == 0 ? "(none)" : string.Join("; ", parts.ToArray());
        }

        /// <summary>Median of the last 5 values; stops one-sample blips (a table top, a lip) reaching the output.</summary>
        private sealed class Median
        {
            private readonly float[] _b = new float[5];
            private readonly float[] _s = new float[5];
            private int _n, _i;

            public void Reset() { _n = 0; _i = 0; }

            public float Push(float v)
            {
                _b[_i] = v; _i = (_i + 1) % _b.Length;
                if (_n < _b.Length) _n++;
                for (int k = 0; k < _n; k++)
                {
                    float x = _b[k]; int j = k - 1;
                    while (j >= 0 && _s[j] > x) { _s[j + 1] = _s[j]; j--; }
                    _s[j + 1] = x;
                }
                return _s[_n / 2];
            }
        }
    }
}
