using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// I5: maps the free space around both ends of the course path so a U-turn can be planned. For every metre of path (rows) and every
    /// metre sideways (columns, right of the increasing-index direction is +) it finds the terrain height, then checks a rider-sized
    /// capsule 0.2-1.9 m above it for colliders (poles, trees, fences, tape, arches). Written as text with a legend of what blocks.
    /// </summary>
    internal static class ZoneMap
    {
        private const int Half = 10;                       // columns cover -Half..+Half metres
        private static readonly Collider[] Buf = new Collider[16];

        public static void Run(AutoSteer auto, PlayerProbe probe)
        {
            var tr = auto.Track;
            if (!tr.Loaded) { Log.Warn("ZoneMap: no path loaded (generated course only)"); return; }
            var sb = new StringBuilder();
            sb.AppendLine("=== Zone map  scene=" + PlayerProbe.SceneName() + "  path " + tr.Length.ToString("F0") + " m, finish trigger at " + tr.FinishS.ToString("F1") + " m, start trigger " + (tr.StartS >= 0f ? tr.StartS.ToString("F1") + " m" : "not on path"));
            sb.AppendLine("Rows = path metre (increasing = toward the finish/bottom), columns = sideways metre (-" + Half + " left .. +" + Half + " right of that direction). '.' free, '?' no terrain found, letters = blocked (legend below).");
            int mask = ~0;
            foreach (string n in new[] { "Bike", "Cyclist" }) { int l = LayerMask.NameToLayer(n); if (l >= 0) mask &= ~(1 << l); }
            var legend = new Dictionary<string, char>();
            var legendCount = new Dictionary<string, int>();
            var legendPos = new Dictionary<string, string>();
            Map(sb, tr, "BOTTOM (finish end)", tr.Length - 60f, tr.Length + 15f, mask, legend, legendCount, legendPos, tr.FinishS, "finish trigger (game takes over)", probe);
            Map(sb, tr, "TOP (start end)", -15f, 40f, mask, legend, legendCount, legendPos, -1f, "", probe);
            sb.AppendLine("--- legend: blocking colliders (cells hit, first world position) ---");
            foreach (var kv in legend) sb.AppendLine("  " + kv.Value + " = " + kv.Key + "  x" + legendCount[kv.Key] + "  at " + legendPos[kv.Key]);
            Diagnostics.EmitPublic(sb.ToString(), "zonemap");
        }

        private static void Map(StringBuilder sb, PathTrack tr, string title, float from, float to, int mask, Dictionary<string, char> legend,
                                Dictionary<string, int> legendCount, Dictionary<string, string> legendPos, float markS, string markText, PlayerProbe probe)
        {
            sb.AppendLine("--- " + title + ": path " + from.ToString("F0") + " .. " + to.ToString("F0") + " m ---");
            var head = new StringBuilder("   path m  ");
            for (int c = -Half; c <= Half; c++) head.Append(c == 0 ? "0" : (c % 5 == 0 ? (Math.Abs(c) / 5).ToString() : " "));
            sb.AppendLine(head.ToString() + "   ground y   (header digits mark every 5 m: 1 = 5 m, 2 = 10 m)");
            for (float s = from; s <= to + 0.01f; s += 1f)
            {
                Vector3 c0 = Pos(tr, s);
                Vector3 tan = Pos(tr, s + 1f) - Pos(tr, s - 1f); tan.y = 0f;
                if (tan.sqrMagnitude < 1e-4f) tan = Vector3.forward;
                tan.Normalize();
                Vector3 right = new Vector3(tan.z, 0f, -tan.x);
                var row = new StringBuilder();
                float yc = float.NaN;
                for (int c = -Half; c <= Half; c++)
                {
                    Vector3 p = c0 + right * c;
                    float yt;
                    if (!GroundY(p, mask, out yt)) { row.Append('?'); continue; }
                    if (c == 0) yc = yt;
                    Vector3 a = new Vector3(p.x, yt + 0.5f, p.z), b = new Vector3(p.x, yt + 1.6f, p.z);
                    int n = Physics.OverlapCapsuleNonAlloc(a, b, 0.3f, Buf, mask, QueryTriggerInteraction.Ignore);
                    char g = '.';
                    for (int i = 0; i < n; i++)
                    {
                        var col = Buf[i];
                        if (col is TerrainCollider) continue;
                        string key = col.GetType().Name + " '" + col.name + "' layer=" + LayerMask.LayerToName(col.gameObject.layer);
                        char ch;
                        if (!legend.TryGetValue(key, out ch))
                        {
                            ch = (char)('A' + Math.Min(legend.Count, 25));
                            legend[key] = ch; legendCount[key] = 0;
                            legendPos[key] = p.ToString("F1") + " (path " + s.ToString("F0") + " m, side " + c + ")";
                        }
                        legendCount[key]++;
                        g = ch;
                    }
                    row.Append(g);
                }
                string tag = "";
                if (markS >= 0f && Mathf.Abs(s - markS) < 0.5f) tag = "   <-- " + markText;
                if (probe != null && probe.Player != null)
                {
                    Vector3 pp = probe.Player.transform.position; float dx = pp.x - c0.x, dz = pp.z - c0.z;
                    if (dx * dx + dz * dz < 1f) tag += "   <-- rider";
                }
                sb.AppendLine("  " + s.ToString("F0").PadLeft(6) + "   " + row + "   " + (float.IsNaN(yc) ? "  n/a" : yc.ToString("F1")) + tag);
            }
        }

        private static Vector3 Pos(PathTrack tr, float s)
        {
            if (s <= 0f) return tr.Points[0] + tr.StartTangent * s;
            if (s >= tr.Length) return tr.Points[tr.Points.Length - 1] + tr.EndTangent * (s - tr.Length);
            return tr.At(s);
        }

        private static bool GroundY(Vector3 p, int mask, out float y)
        {
            y = 0f;
            var origin = new Vector3(p.x, p.y + 15f, p.z);
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 60f, mask, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0) return false;
            float best = float.NegativeInfinity, lowest = float.PositiveInfinity; bool haveTerrain = false;
            foreach (var h in hits)
            {
                if (h.collider is TerrainCollider) { haveTerrain = true; if (h.point.y > best) best = h.point.y; }
                if (h.point.y < lowest) lowest = h.point.y;
            }
            y = haveTerrain ? best : lowest;
            return true;
        }
    }
}
