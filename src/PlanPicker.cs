#nullable disable
using System;
using System.Collections.Generic;
using System.IO;

// Pure logic (no Unity, no logging), bridge-only: the bridge's Menu runs it (bridge/DescendersBridge.csproj links this file; the mod leaves it out).
namespace DescendersCyclingPOC
{
    internal sealed class PlanPickerEntry
    {
        public Plan Plan;
        public Progress.Counts Counts;
        public bool Active;
        public bool Complete { get { return Counts.Total > 0 && Counts.Done >= Counts.Total; } }
    }

    internal sealed class PlanSessionEntry
    {
        public string Ref = "";
        public string Path = "";         // the resolved workout file ("" when it does not resolve), for the effort graph
        public string Label = "";        // "W1 D2"
        public string Block = "", BlockDescription = "";   // the block (week) the session belongs to
        public string Description = "";  // the resolved .zwo's description
        public float TotalS;
        public string Title = "";
        public bool Optional;
        public bool Done;
        public bool IsNext;
        public string Error;            // set when the activity's workout_filename does not resolve or load: shown, not startable
        public bool Selectable { get { return Error == null; } }
    }

    /// <summary>
    /// The training plan lists of the bridge window's Workouts tab (docs/PLANS.md "UI"; they replaced the in-game lists):
    /// <see cref="Plans"/> for the plan drop-down (every usable plan in LibraryDir\TrainingPlans with its progress), <see cref="Sessions"/> for
    /// the chosen plan's sessions (done ones marked, the next one flagged). Nothing is kept: every call reads the disk again, so a file dropped
    /// in shows up. Enrol / reset are the caller's (the bridge's Menu), on the ProgressStore.
    /// </summary>
    internal static class PlanPicker
    {
        /// <summary>The cycling plans in <paramref name="libraryDir"/>\TrainingPlans, sorted by file name; catalogue / broken / non-cycling
        /// files are skipped silently (docs/PLANS.md "Plan discovery"). <paramref name="message"/> says why the list is empty. Never throws.</summary>
        public static List<PlanPickerEntry> Plans(string libraryDir, ProgressStore progress, out string message)
        {
            message = "";
            var list = new List<PlanPickerEntry>();
            if (string.IsNullOrEmpty(libraryDir)) { message = "No training plans: no workout library"; return list; }
            string dir = Path.Combine(libraryDir, "TrainingPlans");
            try
            {
                if (Directory.Exists(dir))
                {
                    string[] files = Directory.GetFiles(dir, "*.xml");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (string f in files)
                    {
                        Plan p = PlanParser.Load(f);
                        if (!p.Ok || !p.SupportsCycling) continue;
                        PlanProgressEntry pe;
                        progress.Plans.TryGetValue(p.Key, out pe);
                        list.Add(new PlanPickerEntry
                        {
                            Plan = p,
                            Counts = Progress.Count(p, pe),
                            Active = string.Equals(progress.ActivePlan, p.Key, StringComparison.OrdinalIgnoreCase),
                        });
                    }
                }
                if (list.Count == 0) message = "No training plans in " + dir;
            }
            catch (Exception ex) { message = "Cannot read " + dir + ": " + ex.Message; }
            return list;
        }

        /// <summary>The sessions of <paramref name="plan"/> in plan order (optional ones only with <paramref name="includeOptional"/>), each
        /// with its workout resolved under the library: one that is missing or does not load carries an Error. <paramref name="entry"/> = the
        /// plan's progress (null = none yet).</summary>
        public static List<PlanSessionEntry> Sessions(string libraryDir, Plan plan, PlanProgressEntry entry, bool includeOptional, float ftpW)
        {
            var list = new List<PlanSessionEntry>();
            HashSet<string> done = Progress.DoneRefs(entry);
            PlanActivity next = Progress.NextActivity(plan, entry);
            foreach (PlanBlock block in plan.Blocks)
            {
                foreach (PlanActivity act in block.Activities)
                {
                    if (!Progress.IsOffered(act, includeOptional)) continue;
                    string err;
                    string resolved = PlanPaths.Resolve(libraryDir, act.WorkoutFilename, out err);
                    Workout w = resolved != null ? WorkoutParser.Load(resolved, ftpW) : null;
                    if (w != null && !w.Ok) err = w.Error ?? "no steps";
                    list.Add(new PlanSessionEntry
                    {
                        Ref = act.Ref,
                        Path = resolved ?? "",
                        Label = act.WeekDayLabel,
                        Block = block.Name,
                        BlockDescription = block.Description,
                        Description = w != null ? w.Description : "",
                        TotalS = w != null && w.Ok ? w.TotalS : 0f,
                        Title = PlanParser.SessionName(act.WorkoutName.Length > 0 ? act.WorkoutName : PlanParser.Stem(act.LeafFile)),
                        Optional = act.IsOptional,
                        Done = done.Contains(act.Ref),
                        IsNext = next != null && next.Ref == act.Ref,
                        Error = resolved == null || !w.Ok ? err : null,
                    });
                }
            }
            return list;
        }
    }
}
