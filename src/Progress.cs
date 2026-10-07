#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

// Pure logic (no Unity, no logging), bridge-only (bridge/DescendersBridge.csproj links this file; the mod leaves it out).
namespace DescendersCyclingPOC
{
    /// <summary>One completed activity (docs/PLANS.md progress.json "completed"); append-only (V3.5/V3.6).</summary>
    internal sealed class ProgressRecord
    {
        public string Ref = "", File = "", Utc = "";
        public int ElapsedS;
        public float AvgW, FtpW, Bias = 100f;
        public bool Skipped;
    }

    /// <summary>Progress for one plan (progress.json "plans"[key]).</summary>
    internal sealed class PlanProgressEntry
    {
        public string Title = "", StartedUtc = "";
        public readonly List<ProgressRecord> Completed = new List<ProgressRecord>();
    }

    /// <summary>The whole progress store, the `progress` section of TrainerBridgeConfig.json (docs/PLANS.md V3.4): one active plan, history retained for every plan ever started (V3.9).</summary>
    internal sealed class ProgressStore
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public string ActivePlan = "";
        public readonly Dictionary<string, PlanProgressEntry> Plans = new Dictionary<string, PlanProgressEntry>(StringComparer.OrdinalIgnoreCase);

        public PlanProgressEntry GetOrAdd(string planKey)
        {
            PlanProgressEntry e;
            if (!Plans.TryGetValue(planKey, out e)) { e = new PlanProgressEntry(); Plans[planKey] = e; }
            return e;
        }

        /// <summary>V3.9: makes this plan active; starts its history the first time (an existing startedUtc is kept).</summary>
        public void Enroll(string planKey, string title, string nowUtc)
        {
            ActivePlan = planKey;
            var e = GetOrAdd(planKey);
            if (!string.IsNullOrEmpty(title)) e.Title = title;
            if (e.StartedUtc.Length == 0) e.StartedUtc = nowUtc;
        }

        /// <summary>V3.6: called once a workout reaches its end ("finished" or "skipped to the end"); never for "cancelled".
        /// Riding the same ref twice appends a second record (V126: a ref is done if any record matches it).</summary>
        public void RecordCompletion(string planKey, string reference, string file, string nowUtc, int elapsedS, float avgW, float ftpW, float bias, bool skipped)
        {
            GetOrAdd(planKey).Completed.Add(new ProgressRecord { Ref = reference, File = file, Utc = nowUtc, ElapsedS = elapsedS, AvgW = avgW, FtpW = ftpW, Bias = bias, Skipped = skipped });
        }

        /// <summary>"Reset plan": drops this plan's history only; other plans are untouched (V3.9).</summary>
        public void Reset(string planKey)
        {
            Plans.Remove(planKey);
            if (string.Equals(ActivePlan, planKey, StringComparison.OrdinalIgnoreCase)) ActivePlan = "";
        }
    }

    /// <summary>UTC timestamp formatting shared by everything that writes a progress.json record or an Enroll() call.</summary>
    internal static class Iso
    {
        public static string NowUtc() { return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture); }
    }

    /// <summary>progress.json &lt;-&gt; <see cref="ProgressStore"/> using the existing <see cref="JsonWriter"/> / <see cref="JsonReader"/> (no new dependency).</summary>
    internal static class ProgressStoreIO
    {
        public static string ToJson(ProgressStore s)
        {
            var w = new JsonWriter();
            w.Obj();
            w.Prop("version", (long)s.Version);
            w.Prop("activePlan", s.ActivePlan);
            w.Obj("plans");
            foreach (var kv in s.Plans)
            {
                w.Obj(kv.Key);
                w.Prop("title", kv.Value.Title);
                w.Prop("startedUtc", kv.Value.StartedUtc);
                w.Arr("completed");
                foreach (var r in kv.Value.Completed)
                {
                    w.Obj();
                    w.Prop("ref", r.Ref);
                    w.Prop("file", r.File);
                    w.Prop("utc", r.Utc);
                    w.Prop("elapsedS", (long)r.ElapsedS);
                    w.Prop("avgW", r.AvgW);
                    w.Prop("ftpW", r.FtpW);
                    w.Prop("bias", r.Bias);
                    w.Prop("skipped", r.Skipped);
                    w.End();
                }
                w.EndArr();
                w.End();
            }
            w.End();
            w.End();
            return w.ToString();
        }

        public static ProgressStore FromJson(string json) { return FromObject(JsonReader.Parse(json)); }

        /// <summary>A parsed progress object (the `progress` section of TrainerBridgeConfig.json). Throws when it is not an object.</summary>
        public static ProgressStore FromObject(object parsed)
        {
            var d = parsed as Dictionary<string, object>;
            if (d == null) throw new FormatException("the progress section is not an object");
            var s = new ProgressStore();
            object v;
            if (d.TryGetValue("version", out v) && v is double) s.Version = (int)(double)v;
            if (d.TryGetValue("activePlan", out v) && v is string) s.ActivePlan = (string)v;
            object plansObj;
            if (d.TryGetValue("plans", out plansObj))
            {
                var plans = plansObj as Dictionary<string, object>;
                if (plans != null)
                    foreach (var kv in plans)
                    {
                        var pd = kv.Value as Dictionary<string, object>;
                        if (pd != null) s.Plans[kv.Key] = ReadEntry(pd);
                    }
            }
            return s;
        }

        private static PlanProgressEntry ReadEntry(Dictionary<string, object> pd)
        {
            var e = new PlanProgressEntry();
            object t;
            if (pd.TryGetValue("title", out t) && t is string) e.Title = (string)t;
            if (pd.TryGetValue("startedUtc", out t) && t is string) e.StartedUtc = (string)t;
            if (pd.TryGetValue("completed", out t))
            {
                var arr = t as List<object>;
                if (arr != null)
                    foreach (var item in arr)
                    {
                        var rd = item as Dictionary<string, object>;
                        if (rd != null) e.Completed.Add(ReadRecord(rd));
                    }
            }
            return e;
        }

        private static ProgressRecord ReadRecord(Dictionary<string, object> rd)
        {
            var r = new ProgressRecord();
            object x;
            if (rd.TryGetValue("ref", out x) && x is string) r.Ref = (string)x;
            if (rd.TryGetValue("file", out x) && x is string) r.File = (string)x;
            if (rd.TryGetValue("utc", out x) && x is string) r.Utc = (string)x;
            if (rd.TryGetValue("elapsedS", out x) && x is double) r.ElapsedS = (int)(double)x;
            if (rd.TryGetValue("avgW", out x) && x is double) r.AvgW = (float)(double)x;
            if (rd.TryGetValue("ftpW", out x) && x is double) r.FtpW = (float)(double)x;
            if (rd.TryGetValue("bias", out x) && x is double) r.Bias = (float)(double)x;
            if (rd.TryGetValue("skipped", out x) && x is bool) r.Skipped = (bool)x;
            return r;
        }
    }

    /// <summary>Load result: <see cref="Problem"/> is set when the file exists but its progress could not be read, for the caller to log once
    /// and to stop saving for the rest of the session (saving would replace the history it could not read).</summary>
    internal sealed class ProgressLoadResult
    {
        public ProgressStore Store = new ProgressStore();
        public string Problem;
    }

    /// <summary>
    /// Progress on disk (docs/PLANS.md V3.4/V3.5): the `progress` section of the per-user %APPDATA%\TrainerBridge\TrainerBridgeConfig.json,
    /// next to the bridge's `rider`, `devices` and `library` sections; only the bridge writes the file. Every save re-reads the file and replaces
    /// only the `progress` section, keeping everything else; atomic tmp+replace writes. A file that does not parse is never rewritten or moved
    /// (the other settings are in it too): the problem is reported and the save refuses.
    /// </summary>
    internal static class ProgressFile
    {
        public const string Section = "progress";
        public const string FileName = "TrainerBridgeConfig.json";

        /// <summary>%APPDATA%\TrainerBridge\TrainerBridgeConfig.json, the same file as the bridge's TrainerBridgeConfig.Path.</summary>
        public static string DefaultPath()
        {
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrainerBridge"), FileName);
        }

        public static ProgressLoadResult Load(string path)
        {
            var result = new ProgressLoadResult();
            if (!File.Exists(path)) return result;
            try
            {
                Dictionary<string, object> root = ReadRoot(path);
                string key = SectionKey(root);
                if (key != null && root[key] != null) result.Store = ProgressStoreIO.FromObject(root[key]);
            }
            catch (Exception ex) { result.Problem = ex.Message; result.Store = new ProgressStore(); }
            return result;
        }

        /// <summary>Atomic write (V3.5): tmp file, flush, then replace. Never called per frame - on enrol, reset and completion only.
        /// Throws (and leaves the file as it is) when the existing file or its progress section cannot be read.</summary>
        public static void Save(string path, ProgressStore store)
        {
            Dictionary<string, object> root;
            if (File.Exists(path))
            {
                try { root = ReadRoot(path); }
                catch (Exception ex) { throw new InvalidOperationException(path + " is unreadable (" + ex.Message + "); left as it is, progress not saved"); }
            }
            else root = new Dictionary<string, object>();

            string key = SectionKey(root);
            if (key != null && root[key] != null && !(root[key] is Dictionary<string, object>))
                throw new InvalidOperationException("the \"" + key + "\" section of " + path + " is not an object; left as it is, progress not saved");
            root[key ?? Section] = JsonReader.Parse(ProgressStoreIO.ToJson(store));

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs))
            {
                sw.Write(JsonPretty.Write(root));
                sw.Flush();
                fs.Flush();
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        private static Dictionary<string, object> ReadRoot(string path)
        {
            var root = JsonReader.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
            if (root == null) throw new FormatException("not a JSON object");
            return root;
        }

        /// <summary>The existing key for the progress section, matched case-insensitively like the bridge's sections; null when absent.</summary>
        private static string SectionKey(Dictionary<string, object> root)
        {
            foreach (string k in root.Keys)
                if (string.Equals(k, Section, StringComparison.OrdinalIgnoreCase)) return k;
            return null;
        }
    }

    /// <summary>Derived state (docs/PLANS.md "Derived state"): computed on load, never stored.</summary>
    internal static class Progress
    {
        public static HashSet<string> DoneRefs(PlanProgressEntry entry)
        {
            var set = new HashSet<string>();
            if (entry != null) foreach (var r in entry.Completed) set.Add(r.Ref);
            return set;
        }

        /// <summary>First required (non-optional) activity with no record, in block-then-priority order; optionals never
        /// block progression (V3.7) regardless of PlanIncludeOptional. Null when every required activity is done.</summary>
        public static PlanActivity NextActivity(Plan plan, PlanProgressEntry entry)
        {
            var done = DoneRefs(entry);
            foreach (var block in plan.Blocks)
                foreach (var act in block.Activities)
                    if (!act.IsOptional && !done.Contains(act.Ref)) return act;
            return null;
        }

        /// <summary>Whether an optional activity is offered in the picker/session list; required activities are always offered.</summary>
        public static bool IsOffered(PlanActivity act, bool includeOptional) { return !act.IsOptional || includeOptional; }

        /// <summary>True if any stored record's file no longer matches the name now at that ref (V137). Nothing is deleted; the plan detail shows a warning.</summary>
        public static bool Changed(Plan plan, PlanProgressEntry entry)
        {
            if (entry == null) return false;
            foreach (var r in entry.Completed)
            {
                PlanActivity act = plan.ByRef(r.Ref);
                if (act == null || !string.Equals(act.LeafFile, r.File, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public struct Counts { public int Done, Total, OptionalDone, OptionalTotal; }

        /// <summary>completedCount / totalCount (V136): totalCount counts required activities only; optionals are tracked separately ("+n optional").</summary>
        public static Counts Count(Plan plan, PlanProgressEntry entry)
        {
            var done = DoneRefs(entry);
            var c = new Counts();
            foreach (var block in plan.Blocks)
                foreach (var act in block.Activities)
                {
                    bool isDone = done.Contains(act.Ref);
                    if (act.IsOptional) { c.OptionalTotal++; if (isDone) c.OptionalDone++; }
                    else { c.Total++; if (isDone) c.Done++; }
                }
            return c;
        }
    }
}
