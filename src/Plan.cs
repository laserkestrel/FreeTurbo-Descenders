#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

// Pure logic (no Unity, no logging), bridge-only (bridge/DescendersBridge.csproj links this file; the mod leaves it out).
namespace DescendersCyclingPOC
{
    /// <summary>One &lt;activity&gt; inside a <see cref="PlanBlock"/> (docs/PLANS.md "Plan format").</summary>
    internal sealed class PlanActivity
    {
        public string WorkoutFilename = "", Auid = "", WorkoutName = "";
        public int? Priority;
        public bool IsOptional;
        public int DocOrder;                 // position among this block's <activity> elements before sorting (priority tie-break)
        public int BlockIndex, IndexInBlock;  // assigned after the priority sort (V3.3)

        /// <summary>b&lt;block&gt;.a&lt;index&gt;, both zero-based, taken after the priority sort (V3.3).</summary>
        public string Ref { get { return "b" + BlockIndex + ".a" + IndexInBlock; } }

        /// <summary>Leaf file name only, for the V3.3 changed-plan check against progress.json's stored "file".</summary>
        public string LeafFile { get { return WorkoutFilename.Length == 0 ? "" : Path.GetFileName(WorkoutFilename.Replace('/', '\\')); } }

        /// <summary>"W&lt;block+1&gt; D&lt;index+1&gt;" (docs/PLANS.md "Rider HUD" example "W2 D3"). Per the open question there this assumes
        /// one block = one training week, which is not guaranteed but "probably enough".</summary>
        public string WeekDayLabel { get { return "W" + (BlockIndex + 1) + " D" + (IndexInBlock + 1); } }

        /// <summary>The same in full words for the bridge window: "Week 1 Day 4 Session 2 70rpm Strength Ramps".
        /// The name is workout_name, else the file's stem, with '_' and '-' shown as spaces.</summary>
        public string LongLabel
        {
            get
            {
                string name = PlanParser.SessionName(WorkoutName.Length > 0 ? WorkoutName : PlanParser.Stem(LeafFile));
                return "Week " + (BlockIndex + 1) + " Day " + (IndexInBlock + 1) + (name.Length > 0 ? " Session " + name : "");
            }
        }
    }

    /// <summary>One &lt;block&gt; (a training week, usually) with its activities already sorted by priority.</summary>
    internal sealed class PlanBlock
    {
        public string Name = "", Description = "";
        public readonly List<PlanActivity> Activities = new List<PlanActivity>();
    }

    /// <summary>A parsed training plan (docs/PLANS.md). Scheduling attributes (V3.1: starting_offset, window_duration,
    /// blackout_duration, plan_start_dayofweek, days_per_block, relative_to_plan_start, is_preplan) are read by nobody: sequence, not calendar.</summary>
    internal sealed class Plan
    {
        public string FileName = "", Name = "", Author = "", Description = "", Difficulty = "", Error;
        public float? AvgHoursPerWeek;
        public int? MinWeeks;
        public bool SupportsCycling = true;
        public readonly List<PlanBlock> Blocks = new List<PlanBlock>();
        public bool Ok { get { return Error == null && Blocks.Count > 0; } }

        /// <summary>Plan key (V3.3 / progress.json "plans" map): the file's stem. Compare case-insensitively.</summary>
        public string Key { get { return PlanParser.Stem(FileName); } }

        public PlanActivity ByRef(string reference)
        {
            foreach (var b in Blocks) foreach (var a in b.Activities) if (a.Ref == reference) return a;
            return null;
        }
    }

    /// <summary>&lt;plan&gt; XML -&gt; <see cref="Plan"/> (docs/PLANS.md "Plan format"). Never throws; unknown elements/attributes are skipped, not errors.</summary>
    internal static class PlanParser
    {
        public static string Stem(string fileName) { return string.IsNullOrEmpty(fileName) ? "" : Path.GetFileNameWithoutExtension(fileName); }

        /// <summary>"6wk_FTP_Builder" -&gt; "6wk FTP Builder" (the only prettification applied: doc's name-fallback rule).</summary>
        public static string Prettify(string stem) { return (stem ?? "").Replace('_', ' '); }

        /// <summary>"2_70rpm_Strength_Ramps" / "sweet-spot--3x10" -&gt; "2 70rpm Strength Ramps" / "sweet spot 3x10":
        /// the rider does not need the literal file name, so '_' and '-' become single spaces.</summary>
        public static string SessionName(string raw)
        {
            return string.Join(" ", (raw ?? "").Split(new[] { '_', '-', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static Plan Load(string path)
        {
            try { return Parse(File.ReadAllText(path), Path.GetFileName(path)); }
            catch (Exception ex)
            {
                string fn = Path.GetFileName(path);
                return new Plan { FileName = fn, Error = ex.Message, Name = Prettify(Stem(fn)) };
            }
        }

        /// <summary>Scans libraryDir\TrainingPlans for the plan whose file stem matches key (docs/PLANS.md V3.3), case-insensitively.
        /// Null if LibraryDir/key is empty, the folder is missing, no file matches, or the match does not parse as a usable plan.</summary>
        public static Plan FindByKey(string libraryDir, string key)
        {
            if (string.IsNullOrEmpty(libraryDir) || string.IsNullOrEmpty(key)) return null;
            string dir = Path.Combine(libraryDir, "TrainingPlans");
            if (!Directory.Exists(dir)) return null;
            foreach (string f in Directory.GetFiles(dir, "*.xml"))
            {
                if (!string.Equals(Stem(f), key, StringComparison.OrdinalIgnoreCase)) continue;
                Plan p = Load(f);
                return p.Ok ? p : null;
            }
            return null;
        }

        public static Plan Parse(string xml, string fileName)
        {
            var p = new Plan { FileName = fileName ?? "" };
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                doc.LoadXml(xml);
                XmlNode root = doc.DocumentElement;
                if (root == null || root.Name != "plan") throw new FormatException("root element is not <plan>");

                p.Name = ResolveName(Attr(root, "name"), p.FileName);
                p.Author = Attr(root, "author");
                p.Description = Attr(root, "description");
                p.AvgHoursPerWeek = FloatAttr(root, "avg_hours_per_week");
                p.MinWeeks = IntAttr(root, "min_weeks");
                p.Difficulty = Attr(root, "difficulty");
                p.SupportsCycling = SupportsCycling(Attr(root, "supports_sports"));

                foreach (XmlNode blockNode in root.ChildNodes)
                {
                    if (blockNode.NodeType != XmlNodeType.Element || blockNode.Name != "block") continue;
                    var block = new PlanBlock { Name = Attr(blockNode, "name"), Description = Attr(blockNode, "description") };
                    var acts = new List<PlanActivity>();
                    int docOrder = 0;
                    foreach (XmlNode actNode in blockNode.ChildNodes)
                    {
                        if (actNode.NodeType != XmlNodeType.Element || actNode.Name != "activity") continue;
                        acts.Add(new PlanActivity
                        {
                            WorkoutFilename = Attr(actNode, "workout_filename"),
                            Auid = Attr(actNode, "auid"),
                            WorkoutName = Attr(actNode, "workout_name"),
                            Priority = IntAttr(actNode, "priority"),
                            IsOptional = BoolAttr(actNode, "is_optional"),
                            DocOrder = docOrder++,
                        });
                    }
                    acts.Sort(new ActivityOrder());
                    int blockIndex = p.Blocks.Count;
                    for (int i = 0; i < acts.Count; i++) { acts[i].BlockIndex = blockIndex; acts[i].IndexInBlock = i; }
                    block.Activities.AddRange(acts);
                    p.Blocks.Add(block);
                }
                if (p.Blocks.Count == 0) throw new FormatException("no blocks");
            }
            catch (Exception ex) { p.Error = ex.Message; if (p.Name == null || p.Name.Length == 0) p.Name = ResolveName(null, p.FileName); }
            return p;
        }

        /// <summary>Ascending by priority; missing priority falls back to document order, ties broken by document order (stable).</summary>
        private sealed class ActivityOrder : IComparer<PlanActivity>
        {
            public int Compare(PlanActivity x, PlanActivity y)
            {
                int kx = x.Priority ?? x.DocOrder, ky = y.Priority ?? y.DocOrder;
                int c = kx.CompareTo(ky);
                return c != 0 ? c : x.DocOrder.CompareTo(y.DocOrder);
            }
        }

        private static string ResolveName(string raw, string fileName)
        {
            string s = (raw ?? "").Trim();
            if (s.Length == 0 || s.StartsWith("LOC_", StringComparison.OrdinalIgnoreCase)) return Prettify(Stem(fileName));
            return s;
        }

        /// <summary>Comma-separated sport list; a plan that omits the attribute entirely is assumed to support cycling
        /// (real rips always set it on every plan - running and tri plans declare their own sports - so this default is only hit by malformed input).</summary>
        private static bool SupportsCycling(string supportsSports)
        {
            if (string.IsNullOrEmpty(supportsSports)) return true;
            foreach (string s in supportsSports.Split(','))
                if (string.Equals(s.Trim(), "CYCLING", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Attr(XmlNode n, string name)
        {
            XmlAttribute a = n.Attributes != null ? n.Attributes[name] : null;
            return a != null ? a.Value.Trim() : "";
        }

        private static int? IntAttr(XmlNode n, string name)
        {
            string s = Attr(n, name);
            int v;
            return s.Length > 0 && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : (int?)null;
        }

        private static float? FloatAttr(XmlNode n, string name)
        {
            string s = Attr(n, name);
            float v;
            return s.Length > 0 && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : (float?)null;
        }

        private static bool BoolAttr(XmlNode n, string name)
        {
            string s = Attr(n, name);
            return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Resolves a plan activity's workout_filename under LibraryDir (docs/PLANS.md "Path resolution"). Read-only (File.Exists checks only).</summary>
    internal static class PlanPaths
    {
        private const string DataPrefix = "data\\";

        /// <summary>Resolved absolute path, or null with <paramref name="error"/> set (missing, or rejected as escaping LibraryDir).</summary>
        public static string Resolve(string libraryDir, string workoutFilename, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(libraryDir)) { error = "no workout library"; return null; }
            if (string.IsNullOrEmpty(workoutFilename)) { error = "no workout_filename"; return null; }

            string rel = workoutFilename.Replace('/', '\\');
            if (Path.IsPathRooted(rel)) { error = "absolute path rejected"; return null; }
            if (rel.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(DataPrefix.Length);

            string rootFull;
            try { rootFull = Path.GetFullPath(libraryDir); }
            catch (Exception ex) { error = ex.Message; return null; }

            string found = ResolveUnder(rootFull, rel, out error);
            if (found != null) return found;
            if (error != null) return null;   // escaped the root: the .zwo variant would escape it too

            found = ResolveUnder(rootFull, Path.ChangeExtension(rel, ".zwo"), out error);
            if (found != null) return found;
            if (error == null) error = "not found under " + rootFull;
            return null;
        }

        /// <summary>Combines, rejects anything outside <paramref name="rootFull"/> (V67), then checks existence.
        /// error stays null when simply not found (inside the root) so the caller can still try the .xml/.zwo fallback.</summary>
        private static string ResolveUnder(string rootFull, string rel, out string error)
        {
            error = null;
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(rootFull, rel)); }
            catch (Exception ex) { error = ex.Message; return null; }
            string rootWithSep = rootFull.EndsWith("\\") ? rootFull : rootFull + "\\";
            if (!candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)) { error = "escapes the workout library"; return null; }
            return File.Exists(candidate) ? candidate : null;
        }
    }
}
