using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// One-shot diagnostic dumps written to the MelonLoader log and to UserData/FreeTurbo-Descenders/.
    /// Descenders' Vehicle members are obfuscated, so the useful trick (as in Sandbox's SpeedWatcher) is to
    /// dump values while doing something (pedal / brake / steer / jump) and diff two dumps.
    /// </summary>
    internal static class Diagnostics
    {
        // Own delegate: System.Func lives in System.Core on this game's old Mono, not mscorlib.
        private delegate object ValueGetter();

        // Type ==/!= compile to operators that don't exist on Unity 2017's old Mono; compare references.
        private static bool Is(Type a, Type b) { return object.ReferenceEquals(a, b); }

        public static void DumpVehicle(PlayerProbe probe)
        {
            if (probe.Vehicle == null || probe.Player == null)
            {
                Log.Warn("Dump: no Vehicle to dump (Player_Human not found)");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== Vehicle dump  scene=" + PlayerProbe.SceneName() + "  t=" + Time.realtimeSinceStartup.ToString("F1"));
            var v = probe.Vehicle;
            var t = v.GetType();

            sb.AppendLine("-- Components on Player_Human and children (type @ path)");
            try
            {
                foreach (var c in probe.Player.GetComponentsInChildren<Component>(true))
                    if (c != null) sb.AppendLine("   " + c.GetType().FullName + " @ " + Path(c.transform));
            }
            catch (Exception ex) { sb.AppendLine("   (component listing failed: " + ex.Message + ")"); }

            if (probe.Rb != null)
            {
                var rb = probe.Rb;
                sb.AppendLine("-- Rigidbody: mass=" + rb.mass + " drag=" + rb.drag + " angDrag=" + rb.angularDrag + " useGravity=" + rb.useGravity
                    + " kinematic=" + rb.isKinematic + " interp=" + rb.interpolation + " collision=" + rb.collisionDetectionMode
                    + " com=" + rb.centerOfMass + " vel=" + rb.velocity + " angVel=" + rb.angularVelocity);
            }

            sb.AppendLine("-- Vehicle public instance FIELDS (float/bool/int/Vector3)");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                AppendValue(sb, f.Name, f.FieldType, () => f.GetValue(v));

            sb.AppendLine("-- Vehicle public instance PROPERTIES (float/bool/int/Vector3)");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.CanRead && p.GetIndexParameters().Length == 0)
                    AppendValue(sb, p.Name, p.PropertyType, () => p.GetValue(v, null));

            Emit(sb.ToString(), "vehicle_dump");
        }

        public static void TerrainCensus(PlayerProbe probe, TerrainSampler sampler)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Terrain census  scene=" + PlayerProbe.SceneName());
            try
            {
                var terrains = Terrain.activeTerrains;
                sb.AppendLine("Terrain.activeTerrains: " + terrains.Length);
                for (int i = 0; i < terrains.Length && i < 16; i++)
                {
                    var tr = terrains[i];
                    var td = tr.terrainData;
                    sb.AppendLine("  [" + i + "] '" + tr.name + "' pos=" + tr.transform.position + " size=" + td.size
                        + " heightmapRes=" + td.heightmapResolution + " layer=" + LayerMask.LayerToName(tr.gameObject.layer)
                        + " collider=" + (tr.GetComponent<TerrainCollider>() != null));
                }
            }
            catch (Exception ex) { sb.AppendLine("  terrain enumeration failed: " + ex); }

            try
            {
                sb.AppendLine("TerrainCollider objects: " + UnityEngine.Object.FindObjectsOfType<TerrainCollider>().Length);
                var meshCols = UnityEngine.Object.FindObjectsOfType<MeshCollider>();
                sb.AppendLine("MeshCollider objects: " + meshCols.Length);
                var byLayer = new Dictionary<string, int>();
                foreach (var m in meshCols)
                {
                    string l = LayerMask.LayerToName(m.gameObject.layer);
                    int n; byLayer.TryGetValue(l, out n); byLayer[l] = n + 1;
                }
                foreach (var kv in byLayer) sb.AppendLine("   MeshCollider layer '" + kv.Key + "': " + kv.Value);
                int shown = 0;
                foreach (var m in meshCols)
                {
                    if (m == null || m.sharedMesh == null) continue;
                    if (m.sharedMesh.vertexCount < 2000) continue; // only big meshes are ground candidates
                    sb.AppendLine("   big MeshCollider '" + m.name + "' verts=" + m.sharedMesh.vertexCount + " layer=" + LayerMask.LayerToName(m.gameObject.layer) + " bounds=" + m.bounds.size);
                    if (++shown >= 12) break;
                }
            }
            catch (Exception ex) { sb.AppendLine("  collider census failed: " + ex); }

            // Course/route objects the game may have built (candidate path sources for auto-steer).
            try
            {
                var routes = UnityEngine.Object.FindObjectsOfType<Route>();
                var rcps = UnityEngine.Object.FindObjectsOfType<RouteCheckpoint>();
                var cps = UnityEngine.Object.FindObjectsOfType<Checkpoint>();
                var guides = UnityEngine.Object.FindObjectsOfType<Guideline>();
                sb.AppendLine("Route objects: " + routes.Length + "   RouteCheckpoint: " + rcps.Length + "   Checkpoint: " + cps.Length + "   Guideline: " + guides.Length);
                ListPositions(sb, "RouteCheckpoint", rcps, probe);
                ListPositions(sb, "Checkpoint", cps, probe);
                ListPositions(sb, "Guideline", guides, probe);
            }
            catch (Exception ex) { sb.AppendLine("  route census failed: " + ex); }

            if (probe.Player != null)
                sb.AppendLine("Player layer: " + LayerMask.LayerToName(probe.Player.layer) + " (" + probe.Player.layer + ")");
            sb.AppendLine("Ground colliders seen by the sampler so far: " + sampler.HitSummary());
            Emit(sb.ToString(), "terrain_census");
            DumpCourse();
            DumpTrackPoints();
        }

        /// <summary>
        /// Writes every course object the game generated (start/finish lines, checkpoints, guideline tapes) with full
        /// position and orientation to UserData/FreeTurbo-Descenders/course_*.csv, for designing the path extractor offline.
        /// </summary>
        public static void DumpCourse()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("kind,name,parent,x,y,z,fx,fy,fz,rx,ry,rz,ux,uy,uz");
                int n = 0;
                n += AppendCourse(sb, "StartLine", UnityEngine.Object.FindObjectsOfType<StartLine>());
                n += AppendCourse(sb, "FinishLine", UnityEngine.Object.FindObjectsOfType<FinishLine>());
                n += AppendCourse(sb, "AscStart", UnityEngine.Object.FindObjectsOfType<AscendersTriggerStartLine>());
                n += AppendCourse(sb, "AscFinish", UnityEngine.Object.FindObjectsOfType<AscendersTriggerFinishLine>());
                n += AppendCourse(sb, "Checkpoint", UnityEngine.Object.FindObjectsOfType<Checkpoint>());
                n += AppendCourse(sb, "RouteCheckpoint", UnityEngine.Object.FindObjectsOfType<RouteCheckpoint>());
                n += AppendCourse(sb, "Guideline", UnityEngine.Object.FindObjectsOfType<Guideline>());
                string file = Write("course", "csv", sb.ToString());
                Log.Info("Course dump: " + n + " objects -> " + file);
            }
            catch (Exception ex) { Log.Error("Course dump failed", ex); }
        }

        private static int AppendCourse(StringBuilder sb, string kind, Component[] items)
        {
            int n = 0;
            foreach (var c in items)
            {
                if (c == null) continue;
                var t = c.transform; var p = t.position; var f = t.forward; var r = t.right; var u = t.up;
                sb.AppendLine(kind + "," + Csv(c.name) + "," + Csv(t.parent != null ? t.parent.name : "-") + ","
                    + F(p.x) + "," + F(p.y) + "," + F(p.z) + "," + F(f.x) + "," + F(f.y) + "," + F(f.z) + ","
                    + F(r.x) + "," + F(r.y) + "," + F(r.z) + "," + F(u.x) + "," + F(u.y) + "," + F(u.z));
                n++;
            }
            return n;
        }

        /// <summary>
        /// Looks for the game's generated track: TerrainPathCarver (the in-game course generator) holds private
        /// 'track' and 'spline' objects whose base class stores a Vector3[] of path points, and TerrainInfo holds a
        /// public reference to the same kind of object. Everything is found by reflection (no obfuscated names
        /// hard-coded) and written to UserData/FreeTurbo-Descenders/trackpoints_*.csv.
        /// </summary>
        public static void DumpTrackPoints()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("source,field,index,x,y,z");
                var seen = new List<object>();
                int arrays = 0;
                foreach (var carver in UnityEngine.Object.FindObjectsOfType<TerrainPathCarver>())
                {
                    if (carver == null) continue;
                    Log.Info("Track: TerrainPathCarver '" + carver.name + "' enabled=" + carver.enabled + " active=" + carver.gameObject.activeInHierarchy + " smoothness=" + carver.smoothness);
                    LogPreset(carver);
                    arrays += ExtractPoints(sb, "carver:" + carver.name, carver, 0, seen);
                }
                foreach (var ti in UnityEngine.Object.FindObjectsOfType<TerrainInfo>())
                {
                    if (ti == null) continue;
                    arrays += ExtractPoints(sb, "terraininfo:" + ti.name, ti, 0, seen);
                }
                if (arrays == 0) { Log.Warn("Track: no Vector3[] path arrays found on TerrainPathCarver/TerrainInfo objects"); return; }
                string file = Write("trackpoints", "csv", sb.ToString());
                Log.Info("Track: " + arrays + " point arrays -> " + file);
            }
            catch (Exception ex) { Log.Error("Track dump failed", ex); }
        }

        private static int ExtractPoints(StringBuilder sb, string source, object owner, int depth, List<object> seen)
        {
            int found = 0;
            for (Type t = owner.GetType(); (object)t != null && !Is(t, typeof(object)) && !Is(t, typeof(MonoBehaviour)) && !Is(t, typeof(Behaviour)) && !Is(t, typeof(Component)); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    object v;
                    try { v = f.GetValue(owner); } catch (Exception) { continue; }
                    if (v == null) continue;
                    var pts = v as Vector3[];
                    if (pts != null)
                    {
                        bool dup = false;
                        foreach (var s in seen) if (object.ReferenceEquals(s, pts)) { dup = true; break; }
                        if (dup || pts.Length < 2) continue;
                        seen.Add(pts);
                        found++;
                        float len = 0f, minY = pts[0].y, maxY = pts[0].y;
                        for (int i = 0; i < pts.Length; i++)
                        {
                            sb.AppendLine(Csv(source) + "," + Csv(f.Name) + "," + i + "," + F(pts[i].x) + "," + F(pts[i].y) + "," + F(pts[i].z));
                            if (i > 0) len += (pts[i] - pts[i - 1]).magnitude;
                            if (pts[i].y < minY) minY = pts[i].y;
                            if (pts[i].y > maxY) maxY = pts[i].y;
                        }
                        Log.Info("Track: " + source + " -> " + owner.GetType().Name + "." + f.Name + " Vector3[" + pts.Length + "] first=" + pts[0] + " last=" + pts[pts.Length - 1]
                            + " length=" + len.ToString("F0") + " m, y " + minY.ToString("F1") + ".." + maxY.ToString("F1"));
                    }
                    else if (depth < 2 && f.FieldType.IsClass && !(v is UnityEngine.Object) && !(v is string) && !(v is Array) && !(v is System.Collections.IEnumerable)
                             && (f.FieldType.Namespace == null || !f.FieldType.Namespace.StartsWith("System")) && (f.FieldType.Namespace == null || !f.FieldType.Namespace.StartsWith("UnityEngine")))
                    {
                        found += ExtractPoints(sb, source + "/" + f.Name, v, depth + 1, seen);
                    }
                }
            }
            return found;
        }

        private static void LogPreset(object carver)
        {
            foreach (var f in carver.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType.Name != "SportModePreset") continue;
                object preset; try { preset = f.GetValue(carver); } catch (Exception) { continue; }
                if (preset == null) { Log.Info("Track: preset field is null"); continue; }
                var parts = new List<string>();
                foreach (var pf in preset.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!(Is(pf.FieldType, typeof(float)) || Is(pf.FieldType, typeof(int)) || Is(pf.FieldType, typeof(bool)))) continue;
                    try { parts.Add(pf.Name + "=" + Convert.ToString(pf.GetValue(preset), System.Globalization.CultureInfo.InvariantCulture)); } catch (Exception) { }
                }
                Log.Info("Track: SportModePreset " + string.Join(", ", parts.ToArray()));
            }
        }

        private static string F(float v) { return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
        private static string Csv(string s) { return "\"" + s.Replace("\"", "\"\"") + "\""; }

        private static void ListPositions(StringBuilder sb, string label, Component[] items, PlayerProbe probe)
        {
            if (items.Length == 0) return;
            var origin = probe.Player != null ? probe.Player.transform.position : Vector3.zero;
            int shown = 0;
            foreach (var c in items)
            {
                if (c == null) continue;
                var p = c.transform.position;
                sb.AppendLine("   " + label + " '" + c.name + "' parent='" + (c.transform.parent != null ? c.transform.parent.name : "-") + "' pos=" + p + " dist=" + (p - origin).magnitude.ToString("F0") + " m");
                if (++shown >= 15) { sb.AppendLine("   ... (" + (items.Length - shown) + " more " + label + ")"); break; }
            }
        }

        private static void AppendValue(StringBuilder sb, string name, Type type, ValueGetter get)
        {
            if (!(Is(type, typeof(float)) || Is(type, typeof(bool)) || Is(type, typeof(int)) || Is(type, typeof(Vector3)))) return;
            try
            {
                object val = get();
                string s = val is float ? ((float)val).ToString("0.####") : Convert.ToString(val);
                sb.AppendLine("   " + type.Name + " " + name + " = " + s);
            }
            catch (Exception ex) { sb.AppendLine("   " + type.Name + " " + name + " = <read failed: " + ex.GetType().Name + ">"); }
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts.ToArray());
        }

        /// <summary>
        /// Logs every collider a downward ray passes through at the rider and at a few points ahead (nearest first,
        /// marking which one the sampler would use), plus what the camera crosshair is looking at. Use it to work out
        /// why props (tables, tents, gates) get treated as ground.
        /// </summary>
        public static void RayProbe(PlayerProbe probe, TerrainSampler sampler)
        {
            if (probe.Player == null) { Log.Warn("RayProbe: no player"); return; }
            var sb = new StringBuilder();
            var pos = probe.Player.transform.position;
            sb.AppendLine("=== Ray probe  scene=" + PlayerProbe.SceneName() + "  pos=" + pos + "  dir=" + sampler.Dir + " (" + sampler.DirSource + ")");

            float[] ahead = { 0f, 3f, 5f, 8f, 12f };
            foreach (float d in ahead)
            {
                Vector3 p = pos + sampler.Dir * d;
                var origin = new Vector3(p.x, pos.y + 60f, p.z);
                sb.AppendLine("-- column at +" + d.ToString("F0") + " m  (x=" + p.x.ToString("F1") + " z=" + p.z.ToString("F1") + ")");
                AppendHits(sb, Physics.RaycastAll(origin, Vector3.down, 400f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide), origin, probe.Player.transform);
            }

            var cam = Camera.main;
            if (cam != null)
            {
                var ray = new Ray(cam.transform.position, cam.transform.forward);
                sb.AppendLine("-- camera crosshair ray (all colliders it passes, nearest first)");
                AppendHits(sb, Physics.RaycastAll(ray, 200f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide), ray.origin, probe.Player.transform);
            }
            else sb.AppendLine("-- no Camera.main");

            Emit(sb.ToString(), "ray_probe");
        }

        private static void AppendHits(StringBuilder sb, RaycastHit[] hits, Vector3 origin, Transform playerRoot)
        {
            // nearest first
            for (int i = 1; i < hits.Length; i++)
            {
                var h = hits[i]; int j = i - 1;
                while (j >= 0 && hits[j].distance > h.distance) { hits[j + 1] = hits[j]; j--; }
                hits[j + 1] = h;
            }
            bool sampled = false;
            int shown = 0;
            foreach (var h in hits)
            {
                if (h.collider == null) continue;
                var c = h.collider;
                string layer = LayerMask.LayerToName(c.gameObject.layer);
                bool mine = c.transform.IsChildOf(playerRoot);
                bool masked = layer == "Bike" || layer == "Cyclist";
                bool trig = c.isTrigger;
                string tag = "";
                if (mine) tag += " [player]"; if (masked) tag += " [masked layer]"; if (trig) tag += " [trigger]";
                if (!sampled && !mine && !masked && !trig) { sampled = true; tag += "  <== sampler uses this"; }
                sb.AppendLine("   y=" + h.point.y.ToString("F2") + "  d=" + h.distance.ToString("F1") + "  " + c.GetType().Name + " '" + c.name + "'"
                    + "  root='" + c.transform.root.name + "'  layer=" + layer
                    + "  bounds=" + c.bounds.size.x.ToString("F1") + "x" + c.bounds.size.y.ToString("F1") + "x" + c.bounds.size.z.ToString("F1")
                    + (c is MeshCollider && ((MeshCollider)c).sharedMesh != null ? " verts=" + ((MeshCollider)c).sharedMesh.vertexCount : "")
                    + " tag=" + c.tag + (c.gameObject.isStatic ? " static" : "") + tag);
                if (++shown >= 12) { sb.AppendLine("   ... (truncated)"); break; }
            }
            if (shown == 0) sb.AppendLine("   (no hits)");
        }

        public static void EmitPublic(string text, string prefix) { Emit(text, prefix); }

        private static void Emit(string text, string prefix)
        {
            foreach (var line in text.Split('\n')) if (line.Length > 0) Log.Info(line.TrimEnd('\r'));
            try { Log.Info("Dump written to " + Write(prefix, "txt", text)); }
            catch (Exception ex) { Log.Error("Dump: writing file failed", ex); }
        }

        /// <summary>Writes a dump to UserData/FreeTurbo-Descenders/kind_scene.ext, overwriting the previous one of that kind and scene (DumpFiles).</summary>
        private static string Write(string kind, string ext, string text)
        {
            string dir = System.IO.Path.Combine("UserData", Names.Mod);
            Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, DumpFiles.FileName(kind, PlayerProbe.SceneName(), ext));
            File.WriteAllText(file, text);
            return file;
        }
    }
}
