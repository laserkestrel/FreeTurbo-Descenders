using DescendersBridge.Gui;
using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>The plan session a workout was started for (docs/PLANS.md V3.6): its completion is recorded at this ref.</summary>
internal sealed record PlanRun(string Key, string Ref, string File, string Title, string SessionLabel);

/// <summary>
/// Training plan progress (docs/PLANS.md V3.4-V3.6), owned by the bridge alone: the `progress` section of
/// TrainerBridgeConfig.json, loaded once per ride and mutated in place, saved on enrol, reset and completion only. The corruption rules
/// carry over unchanged: a file that could not be read at load is logged once, and nothing is saved for the rest of the session (each
/// would-be save logs why); the file is never overwritten or moved. <see cref="InMemory"/> never touches a file (tests, the test RideLoop).
/// </summary>
internal sealed class PlanBook
{
    /// <summary>Offer optional plan activities in the session list (they never block progression, V3.7).</summary>
    public const bool IncludeOptional = true;

    private readonly string? _path;
    private readonly Action<string> _log;

    public ProgressStore Store { get; }
    /// <summary>Why the file could not be read at load (null = fine): nothing is saved this session.</summary>
    public string? Problem { get; }
    /// <summary>Successful saves, for `selftest`.</summary>
    public int Saves { get; private set; }

    private PlanBook(string? path, ProgressStore store, string? problem, Action<string> log) { _path = path; Store = store; Problem = problem; _log = log; }

    public static PlanBook InMemory(Action<string>? log = null) => new(null, new ProgressStore(), null, log ?? (_ => { }));

    public static PlanBook Load(string path, Action<string> log)
    {
        ProgressLoadResult r = ProgressFile.Load(path);
        if (r.Problem != null)
            log("Progress: could not read " + path + " (" + r.Problem + "); starting empty, and plan progress will NOT be saved this session. Fix the file, then restart the bridge");
        else
            log("Progress: " + path + ", " + r.Store.Plans.Count + " plan(s), active '" + r.Store.ActivePlan + "'");
        return new PlanBook(path, r.Store, r.Problem, log);
    }

    /// <summary>Re-reads the file and replaces only the `progress` section (ProgressFile.Save). False when not saved (logged).</summary>
    public bool Save()
    {
        if (_path is null) { Saves++; return true; }
        if (Problem != null) { _log("Progress: not saved, " + _path + " could not be read at startup (" + Problem + ")"); return false; }
        try { ProgressFile.Save(_path, Store); Saves++; return true; }
        catch (Exception ex) { _log("Progress: could not save " + _path + ": " + ex.Message); return false; }
    }
}

/// <summary>What a press on the Workouts tab did: <see cref="Text"/> is shown to the rider as it is.</summary>
internal readonly record struct MenuResult(bool Ok, string Text);

/// <summary>
/// What the bridge window's Workouts tab does (the workout / training plan menu moved here from the game; docs/PLANS.md "UI",
/// docs/REQUIREMENTS.md V2.19): start a workout, start a plan session, follow a plan, reset a plan. The lists it chooses from are the pure
/// <see cref="WorkoutPicker"/> / <see cref="PlanPicker"/> models. No window code here, so `selftest` drives it (tests\unit\MenuTests.cs).
/// Threads: everything that takes a <see cref="Session"/> runs on the ride loop's thread (the window hands it over with
/// <see cref="RideLoop.Post"/>); plan progress has one writer at a time: the ride's <see cref="PlanBook"/> while a ride runs, else one the
/// window loads from the settings file for the press.
/// </summary>
internal static class Menu
{
    public const string NoLibraryText = "Set the workout library folder in the trainer bridge window (Logs tab)";

    /// <summary>The library folder to use, or null with <paramref name="problem"/> set (not set, or not found).</summary>
    public static string? UsableLibrary(string dir, out string problem)
    {
        problem = "";
        if (string.IsNullOrWhiteSpace(dir)) { problem = NoLibraryText; return null; }
        if (!Directory.Exists(dir)) { problem = "Workout library folder not found: " + dir + ". Check it on the Logs tab"; return null; }
        return dir;
    }

    /// <summary>The library's Workouts folder (what <see cref="WorkoutPicker"/> lists).</summary>
    public static string WorkoutsDir(string libraryDir) => Path.Combine(libraryDir, "Workouts");

    /// <summary>Start the workout file at <paramref name="path"/> (not a plan session): ERG on, armed until the first pedal stroke.</summary>
    public static MenuResult StartWorkout(Session s, string path, DateTime now)
    {
        if (!s.StartWorkout(path, now)) return new(false, "That workout could not be loaded (see the Logs tab).");
        return new(true, "Started \"" + s.Run!.W.Name + "\": it begins at your first pedal stroke.");
    }

    /// <summary>Start the session <paramref name="reference"/> of plan <paramref name="planKey"/>: its workout runs tagged, so reaching its
    /// end is recorded at this ref (V3.6); the plan becomes the active one. A missing or broken workout file starts and records nothing.</summary>
    public static MenuResult StartPlanSession(Session s, string libraryDir, string planKey, string reference, DateTime now)
    {
        if (UsableLibrary(libraryDir, out string problem) is not { } lib) return new(false, problem + ".");
        Plan? plan = PlanParser.FindByKey(lib, planKey);
        PlanActivity? act = plan?.ByRef(reference);
        if (plan is null || act is null) { s.Log("Plan: " + planKey + " " + reference + " not found under " + lib); return new(false, "That plan session is no longer in the workout library."); }

        string path = PlanPaths.Resolve(lib, act.WorkoutFilename, out string err);
        if (path == null) { s.Log("Plan activity " + act.Ref + " (" + plan.Key + "): " + err); return new(false, "The workout file of that session was not found (" + err + ")."); }
        Workout w = WorkoutParser.Load(path, (float)s.FtpW);   // checked here too, so a broken file never enrols
        if (!w.Ok) { s.Log("Plan activity " + act.Ref + ": could not load " + path + ": " + (w.Error ?? "no steps")); return new(false, "The workout file of that session could not be loaded."); }

        var book = s.Plans;
        Progress.Counts counts = Progress.Count(plan, book.Store.GetOrAdd(plan.Key));
        var run = new PlanRun(plan.Key, act.Ref, act.LeafFile, plan.Name, act.WeekDayLabel + " (" + counts.Done + "/" + counts.Total + ")");
        if (!s.StartWorkout(path, now, plan.Key + "|" + act.Ref, run)) return new(false, "That workout could not be loaded (see the Logs tab).");
        book.Store.Enroll(plan.Key, plan.Name, Iso.NowUtc());
        book.Save();
        s.Log("Plan: starting " + plan.Key + " " + act.Ref + " '" + w.Name + "' (" + WorkoutRunner.Fmt(w.TotalS) + ")");
        return new(true, "Started " + act.LongLabel + ": it begins at your first pedal stroke.");
    }

    /// <summary>Make <paramref name="planKey"/> the active plan (its history, if any, is kept) and save.</summary>
    public static MenuResult FollowPlan(PlanBook book, string planKey, string planName, Action<string> log)
    {
        if (string.Equals(book.Store.ActivePlan, planKey, StringComparison.OrdinalIgnoreCase)) return new(true, "");
        book.Store.Enroll(planKey, planName, Iso.NowUtc());
        bool saved = book.Save();
        log("Plan: active -> " + planKey);
        return new(saved, saved ? "Now following " + planName + "." : "Now following " + planName + ", but it could not be saved (see the Logs tab).");
    }

    /// <summary>Forget every finished session of <paramref name="planKey"/>; it stays the active plan and starts again from its first session.</summary>
    public static MenuResult ResetPlan(PlanBook book, string planKey, string planName, Action<string> log)
    {
        book.Store.Reset(planKey);
        book.Store.Enroll(planKey, planName, Iso.NowUtc());
        bool saved = book.Save();
        log("Plan: reset " + planKey);
        return new(saved, saved ? planName + " starts again from its first session." : "The plan was reset, but it could not be saved (see the Logs tab).");
    }

    // ---------------------------------------------------------------- the pickers' words (a name, and a detail line under it)

    /// <summary>"Fixture plan" over "2 of 3 done" / "complete".</summary>
    public static PickerEntry PlanEntry(PlanPickerEntry e) =>
        new(e.Plan.Name, e.Complete ? "complete" : e.Counts.Done + " of " + e.Counts.Total + " done");

    /// <summary>The plan picker's first entry while no plan is followed yet.</summary>
    public static PickerEntry ChoosePlanEntry(int plans) => new("Choose a training plan", plans == 1 ? "1 plan in the library" : plans + " plans in the library");

    /// <summary>"W1 D2 Sweet Spot 3x10" over "45:00 · next" (done / next / optional / file missing).</summary>
    public static PickerEntry SessionEntry(PlanSessionEntry e) =>
        new(string.Join(" ", new[] { e.Label, e.Title }.Where(p => !string.IsNullOrEmpty(p))),
            Dots(e.TotalS > 0f ? WorkoutRunner.Fmt(e.TotalS) : "", !e.Selectable ? "file missing" : e.Done ? "done" : e.IsNext ? "next" : e.Optional ? "optional" : ""));

    /// <summary>"Intervals" over "12 workouts".</summary>
    public static PickerEntry FolderEntry(string sub, int workouts) => new(FolderText(sub), workouts == 1 ? "1 workout" : workouts + " workouts");

    /// <summary>"Alpha" over "0:10" / "cannot load".</summary>
    public static PickerEntry WorkoutEntry(WorkoutPickerEntry e) => new(e.Title, !e.Selectable ? "cannot load" : e.TotalS > 0f ? WorkoutRunner.Fmt(e.TotalS) : "");

    /// <summary>Shown in place of the effort graph of a session that cannot be started.</summary>
    public static string SessionProblem(PlanSessionEntry e) => "This session's workout file is missing or cannot be loaded" + (string.IsNullOrEmpty(e.Error) ? "." : ": " + e.Error);

    /// <summary>The line under the workout's graph: duration, sections, author, description, or why it cannot be started.</summary>
    public static string WorkoutDetail(WorkoutPickerEntry? e)
    {
        if (e is null) return "";
        if (!e.Selectable) return "This workout cannot be loaded: " + e.Error;
        return Join(e.TotalS > 0f ? WorkoutRunner.Fmt(e.TotalS) : "", e.Steps > 0 ? e.Steps + " sections" : "", e.Author.Length > 0 ? "by " + e.Author : "", OneLine(e.Description, 160));
    }

    private static string Dots(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>The folder picker's words: "" is the Workouts folder itself.</summary>
    public static string FolderText(string sub) => sub.Length == 0 ? "Workouts" : sub.Replace("\\", " / ");

    private static string OneLine(string s, int max)
    {
        s = string.Join(" ", (s ?? "").Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    private static string Join(params string[] parts) => string.Join("   ", parts.Where(p => !string.IsNullOrEmpty(p)));
}

/// <summary>What the bridge window shows about the active training plan: the plan with its progress, and its next session, in plain words.</summary>
internal static class PlanSummary
{
    public const string NoPlan = "No training plan chosen yet: pick one above.";
    public const string NoLibrary = "No training plan chosen yet: choose your workout library folder on the Logs tab first.";

    public static (string Active, string Next) Describe(string libraryDir, ProgressStore store)
    {
        if (string.IsNullOrEmpty(store.ActivePlan)) return (NoPlan, "");
        store.Plans.TryGetValue(store.ActivePlan, out PlanProgressEntry? entry);
        string name = entry is { Title.Length: > 0 } ? entry.Title : store.ActivePlan;
        Plan? plan = Menu.UsableLibrary(libraryDir, out _) is { } lib ? PlanParser.FindByKey(lib, store.ActivePlan) : null;
        if (plan is null) return ("Active training plan: " + name + " (its file is not in the workout library)", "");
        var c = Progress.Count(plan, entry);
        PlanActivity next = Progress.NextActivity(plan, entry);
        if (next is null) return ("Active training plan: " + plan.Name + ", complete (" + c.Done + " of " + c.Total + " sessions done)", "Next session: none, the plan is complete");
        return ("Active training plan: " + plan.Name + " (" + c.Done + " of " + c.Total + " sessions done)", "Next session: " + next.LongLabel);
    }

    /// <summary>0 to 100 for the progress line: the share of the active plan's required sessions that are done (0 = no plan / not found).</summary>
    public static int Percent(string libraryDir, ProgressStore store)
    {
        if (string.IsNullOrEmpty(store.ActivePlan) || Menu.UsableLibrary(libraryDir, out _) is not { } lib) return 0;
        if (PlanParser.FindByKey(lib, store.ActivePlan) is not { } plan) return 0;
        store.Plans.TryGetValue(store.ActivePlan, out PlanProgressEntry? entry);
        var c = Progress.Count(plan, entry);
        return c.Total > 0 ? Math.Clamp(100 * c.Done / c.Total, 0, 100) : 0;
    }
}
