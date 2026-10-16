using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// The game's generated course centreline: a Vector3[] (about 1 m spacing, on the terrain surface) held by the
    /// track object that TerrainInfo references. Found by reflection (no obfuscated names hard-coded): the largest
    /// Vector3[] reachable from the TerrainInfo instance. Verified on the "forest" generated course: 838 points,
    /// 812 m, median 0.9 m from the line a rider actually rode.
    /// </summary>
    internal sealed class PathTrack
    {
        public Vector3[] Points;
        public float[] Cum;          // cumulative HORIZONTAL distance along the path (m)
        public float Length { get { return Cum == null ? 0f : Cum[Cum.Length - 1]; } }
        public bool Loaded { get { return Points != null; } }
        public float FinishS = -1f;   // path distance of the game's finish trigger (it takes control of the bike from here); -1 = none found
        public float StartS = -1f;    // path distance of the game's start trigger (top; entering it after the finish trigger completes an Ascender); -1 = none found
        public string Source = "-";

        private float _nextTry;
        private bool _finishSeen, _startSeen;   // trigger objects found (on the path or not); they can spawn after the path exists

        public void Reset() { Points = null; Cum = null; Line = null; FinishS = -1f; StartS = -1f; _finishSeen = _startSeen = false; _nextTry = 0f; Source = "-"; }

        /// <summary>Retry every 2 s until a track is found (it may not exist in menus or non-generated maps), then until both
        /// Ascender triggers are found: on some courses (seed 100044016319, highlands) they appear only after the path does.</summary>
        public void EnsureLoaded()
        {
            if (Loaded && _finishSeen && _startSeen) return;
            float now = Time.unscaledTime;
            if (now < _nextTry) return;
            _nextTry = now + 2f;
            try { if (Loaded) FindTriggers(); else TryLoad(); }
            catch (Exception ex) { Log.ErrorLimited("pathload", "PathTrack.TryLoad", ex, 30f); }
        }

        /// <summary>
        /// Checked every 2 s: does the game hold another course path than the one loaded? In a session the game builds the next
        /// level inside the same scene, so no scene load says "new course"; the caller then resets as it does for a scene change.
        /// </summary>
        public bool CourseChanged()
        {
            if (!Loaded) return false;
            float now = Time.unscaledTime;
            if (now < _nextCheck) return false;
            _nextCheck = now + 2f;
            try
            {
                var ti = UnityEngine.Object.FindObjectOfType<TerrainInfo>();
                if (ti == null) return false;   // between two levels: the new path says so when it is there
                Vector3[] best = null; string bestName = null;
                Search(ti, "TerrainInfo", 0, ref best, ref bestName);
                if (best == null || best.Length < 10) return false;
                if (CourseRules.SameCourse(Points.Length, Samples(Points), best.Length, Samples(best))) return false;
                Log.Info("Path: the game has another course in this scene (" + best.Length + " points, first=" + best[0] + "; was " + Points.Length
                         + " points, first=" + Points[0] + "): new course");
                return true;
            }
            catch (Exception ex) { Log.ErrorLimited("pathcheck", "PathTrack.CourseChanged", ex, 30f); return false; }
        }

        private float _nextCheck;

        private static float[] Samples(Vector3[] pts)
        {
            var xyz = new float[pts.Length * 3];
            for (int i = 0; i < pts.Length; i++) { xyz[i * 3] = pts[i].x; xyz[i * 3 + 1] = pts[i].y; xyz[i * 3 + 2] = pts[i].z; }
            return CourseRules.Samples(xyz);
        }

        private void TryLoad()
        {
            var ti = UnityEngine.Object.FindObjectOfType<TerrainInfo>();
            if (ti == null) return;
            Vector3[] best = null; string bestName = null;
            Search(ti, "TerrainInfo", 0, ref best, ref bestName);
            if (best == null || best.Length < 10) return;

            Points = (Vector3[])best.Clone();
            Cum = new float[Points.Length];
            for (int i = 1; i < Points.Length; i++)
            {
                float dx = Points[i].x - Points[i - 1].x, dz = Points[i].z - Points[i - 1].z;
                Cum[i] = Cum[i - 1] + (float)Math.Sqrt(dx * dx + dz * dz);
            }
            Source = bestName;
            int nn = Points.Length, b = Math.Min(5, nn - 1);
            StartTangent = Flat(Points[b] - Points[0]);
            EndTangent = Flat(Points[nn - 1] - Points[nn - 1 - b]);
            BuildLine(Config.AsLineOffset);
            FindTriggers();
            Log.Info("Path: loaded " + Points.Length + " points, " + Length.ToString("F0") + " m horizontal, from " + bestName
                     + ", first=" + Points[0] + " last=" + Points[Points.Length - 1]
                     + (_finishSeen ? "" : "; finish trigger not spawned yet, retrying") + (_startSeen ? "" : "; start trigger not spawned yet, retrying"));
        }

        /// <summary>Path distance of each Ascender trigger not found yet (FindObjectOfType sees only active objects).</summary>
        private void FindTriggers()
        {
            var fin = _finishSeen ? null : UnityEngine.Object.FindObjectOfType<AscendersTriggerFinishLine>();
            if (fin != null)
            {
                _finishSeen = true;
                float fd; int fi = Nearest(fin.transform.position, -1, out fd);
                if (fd < 6f) FinishS = Cum[fi];
                Log.Info("Path: finish trigger at " + (FinishS >= 0f ? FinishS.ToString("F0") + " m along the path (" + (Length - FinishS).ToString("F0") + " m before the end)" : "not on the path (" + fd.ToString("F0") + " m away)"));
            }
            var st = _startSeen ? null : UnityEngine.Object.FindObjectOfType<AscendersTriggerStartLine>();
            if (st != null)
            {
                _startSeen = true;
                float sd; int si = Nearest(st.transform.position, -1, out sd);
                if (sd < 6f) StartS = Cum[si];
                Log.Info("Path: start trigger at " + (StartS >= 0f ? StartS.ToString("F0") + " m along the path" : "not on the path (" + sd.ToString("F0") + " m away)")
                         + ", world pos " + st.transform.position);
            }
        }

        private static void Search(object owner, string label, int depth, ref Vector3[] best, ref string bestName)
        {
            for (Type t = owner.GetType(); (object)t != null && !object.ReferenceEquals(t, typeof(object)) && !object.ReferenceEquals(t, typeof(MonoBehaviour))
                 && !object.ReferenceEquals(t, typeof(Behaviour)) && !object.ReferenceEquals(t, typeof(Component)); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    object v;
                    try { v = f.GetValue(owner); } catch (Exception) { continue; }
                    if (v == null) continue;
                    var pts = v as Vector3[];
                    if (pts != null)
                    {
                        if (best == null || pts.Length > best.Length) { best = pts; bestName = label + "." + f.Name; }
                    }
                    else if (depth < 2 && f.FieldType.IsClass && !(v is UnityEngine.Object) && !(v is string) && !(v is Array) && !(v is System.Collections.IEnumerable)
                             && (f.FieldType.Namespace == null || (!f.FieldType.Namespace.StartsWith("System") && !f.FieldType.Namespace.StartsWith("UnityEngine"))))
                    {
                        Search(v, label + "/" + f.Name, depth + 1, ref best, ref bestName);
                    }
                }
            }
        }

        /// <summary>Index of the nearest point (horizontal distance), searching a window around <paramref name="hint"/> first.</summary>
        public int Nearest(Vector3 pos, int hint, out float dist)
        {
            int n = Points.Length;
            int lo = 0, hi = n - 1;
            if (hint >= 0 && hint < n) { lo = Math.Max(0, hint - 60); hi = Math.Min(n - 1, hint + 60); }
            int best = FindIn(pos, lo, hi, out dist);
            if ((lo != 0 || hi != n - 1) && dist > 12f) best = FindIn(pos, 0, n - 1, out dist);   // lost: search everywhere
            return best;
        }

        private int FindIn(Vector3 pos, int lo, int hi, out float dist)
        {
            float bd = float.MaxValue; int bi = lo;
            for (int i = lo; i <= hi; i++)
            {
                float dx = pos.x - Points[i].x, dz = pos.z - Points[i].z;
                float d = dx * dx + dz * dz;
                if (d < bd) { bd = d; bi = i; }
            }
            dist = (float)Math.Sqrt(bd);
            return bi;
        }

        // ---- Racing line: minimum-curvature line inside a corridor around the centreline ----
        public Vector3[] Line;
        public float LineOffsetM;
        // Horizontal unit direction (increasing index) at the two ends; the start and finish arches face along these.
        public Vector3 StartTangent = Vector3.forward, EndTangent = Vector3.forward;
        private const float EndPinM = 4f, EndTaperM = 20f;   // corridor is zero within 4 m of either end, full beyond 20 m (offline: 780 m corner keeps R=33 m)

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

        /// <summary>
        /// Minimum-curvature line within +/-maxOffset of the centreline (horizontal only; heights stay on the centre).
        /// Projected gradient descent on the sum of squared second differences, solved coarse-to-fine (stride 16, 8, 4, 2, 1)
        /// so it converges in a few tens of ms. Enters corners wide, cuts the apex and exits wide like a rider, which raises
        /// the effective radius and cuts the lateral grip needed. On the forest course (+/-2.5 m): tightest radius 22.8 -> 34.9 m;
        /// the two marginal corners drop from 31 and 27 m/s^2 to 19 and 18 m/s^2 at 100/90 km/h.
        /// </summary>
        public void BuildLine(float maxOffset)
        {
            LineOffsetM = maxOffset;
            int n = Points.Length;
            var x = new float[n]; var z = new float[n];
            for (int i = 0; i < n; i++) { x[i] = Points[i].x; z[i] = Points[i].z; }
            if (maxOffset > 0.01f && n > 40)
            {
                int[] strides = { 16, 8, 4, 2, 1 };
                for (int si = 0; si < strides.Length; si++)
                {
                    int s = strides[si];
                    int m = (n - 1) / s + 1;
                    var idx = new int[m + 1];
                    for (int j = 0; j < m; j++) idx[j] = j * s;
                    if (idx[m - 1] != n - 1) { idx[m] = n - 1; m++; }
                    var px = new float[m]; var pz = new float[m]; var qx = new float[m]; var qz = new float[m]; var wq = new float[m];
                    for (int j = 0; j < m; j++) { px[j] = x[idx[j]]; pz[j] = z[idx[j]]; qx[j] = Points[idx[j]].x; qz[j] = Points[idx[j]].z; wq[j] = maxOffset * EndFactor(idx[j]); }
                    var lx = new float[m]; var lz = new float[m];
                    for (int it = 0; it < 3000; it++)
                    {
                        for (int i = 1; i < m - 1; i++) { lx[i] = px[i - 1] - 2f * px[i] + px[i + 1]; lz[i] = pz[i - 1] - 2f * pz[i] + pz[i + 1]; }
                        for (int i = 2; i < m - 2; i++)
                        {
                            px[i] -= 0.05f * (lx[i - 1] - 2f * lx[i] + lx[i + 1]);
                            pz[i] -= 0.05f * (lz[i - 1] - 2f * lz[i] + lz[i + 1]);
                            float dx = px[i] - qx[i], dz = pz[i] - qz[i];
                            float d = (float)Math.Sqrt(dx * dx + dz * dz);
                            if (d > wq[i]) { float k = wq[i] / d; px[i] = qx[i] + dx * k; pz[i] = qz[i] + dz * k; }
                        }
                    }
                    for (int j = 0; j < m - 1; j++)
                    {
                        int a = idx[j], b = idx[j + 1];
                        for (int i = a; i <= b; i++)
                        {
                            float f = b == a ? 0f : (float)(i - a) / (b - a);
                            x[i] = px[j] + (px[j + 1] - px[j]) * f; z[i] = pz[j] + (pz[j + 1] - pz[j]) * f;
                        }
                    }
                }
            }
            Line = new Vector3[n];
            float maxUsed = 0f;
            for (int i = 0; i < n; i++)
            {
                Line[i] = new Vector3(x[i], Points[i].y, z[i]);
                float dx = x[i] - Points[i].x, dz = z[i] - Points[i].z;
                maxUsed = Math.Max(maxUsed, (float)Math.Sqrt(dx * dx + dz * dz));
            }
            Log.Info("Path: racing line built, corridor +/-" + maxOffset.ToString("F1") + " m, max offset used " + maxUsed.ToString("F2") + " m");
        }

        /// <summary>0 at the ends (line pinned to the centre so it passes exactly through the start/finish arch), 1 further in.</summary>
        private float EndFactor(int i)
        {
            float d = Math.Min(Cum[i], Length - Cum[i]);
            float u = Mathf.Clamp01((d - EndPinM) / (EndTaperM - EndPinM));
            return u * u * (3f - 2f * u);
        }

        /// <summary>Racing-line point at centreline arc length s (same parameterisation as the centreline).</summary>
        public Vector3 LineAt(float s)
        {
            if (Line == null) return At(s);
            if (s <= 0f) return Line[0] + StartTangent * s;                 // beyond the start: straight on, through the start arch
            if (s >= Length) return Line[Line.Length - 1] + EndTangent * (s - Length);   // beyond the end: straight on, through the finish arch
            int lo = 0, hi = Cum.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (Cum[mid] <= s) lo = mid; else hi = mid; }
            float seg = Cum[hi] - Cum[lo];
            float f = seg > 1e-4f ? (s - Cum[lo]) / seg : 0f;
            return Vector3.Lerp(Line[lo], Line[hi], f);
        }

        /// <summary>Point at horizontal arc length s (clamped to the path).</summary>
        public Vector3 At(float s)
        {
            if (s <= 0f) return Points[0];
            if (s >= Length) return Points[Points.Length - 1];
            int lo = 0, hi = Cum.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (Cum[mid] <= s) lo = mid; else hi = mid; }
            float seg = Cum[hi] - Cum[lo];
            float f = seg > 1e-4f ? (s - Cum[lo]) / seg : 0f;
            return Vector3.Lerp(Points[lo], Points[hi], f);
        }

        /// <summary>Horizontal unit tangent in the direction of increasing index, around index i.</summary>
        public Vector3 Tangent(int i)
        {
            int a = Math.Max(0, i - 3), b = Math.Min(Points.Length - 1, i + 3);
            var t = new Vector3(Points[b].x - Points[a].x, 0f, Points[b].z - Points[a].z);
            return t.sqrMagnitude > 1e-6f ? t.normalized : Vector3.forward;
        }
    }
}
