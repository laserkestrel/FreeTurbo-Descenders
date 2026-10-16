using DescendersBridge.Ant;
using DescendersBridge.Gui;
using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// The bridge owns the workout library setting (`library` section of TrainerBridgeConfig.json) and is the only writer of plan
/// progress. Workouts and training plans are chosen and started in the bridge window's Workouts tab (Menu.cs over the
/// WorkoutPicker / PlanPicker lists), and the game has no menu any more (no `menu` message, no menu_* intents, no picker code or keys in the
/// mod). Temp folders only, never the real %APPDATA% file. Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class MenuTests
{
    private static readonly DateTime T0 = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private const string SubZwo = "<workout_file><name>Session test</name><author>Tester</author><description>Two\nsteps.</description><workout><SteadyState Duration='10' Power='0.5'/><SteadyState Duration='10' Power='1'/></workout></workout_file>";
    private static string Short(string name) => "<workout_file><name>" + name + "</name><workout><SteadyState Duration='10' Power='0.5'/></workout></workout_file>";

    private const string PlanXml = "<plan name='Fixture plan' supports_sports='CYCLING' description='A test plan.'>"
        + "<block name='Week 1' description='First week'>"
        + "<activity workout_filename='data/Workouts/Plan/p1.zwo' workout_name='P1' priority='1'/>"
        + "<activity workout_filename='data/Workouts/Plan/p2.zwo' workout_name='P2' priority='2'/>"
        + "<activity workout_filename='data/Workouts/Plan/p3.zwo' workout_name='P3' priority='3' is_optional='1'/>"
        + "</block><block name='Week 2'><activity workout_filename='data/Workouts/Plan/p4.zwo' workout_name='P4' priority='1'/>"
        + "<activity workout_filename='data/Workouts/Plan/gone.zwo' workout_name='Gone' priority='2' is_optional='1'/></block></plan>";

    private sealed class Rig
    {
        public readonly List<string> Log = new();
        public readonly Session S;
        public DateTime Now = T0;
        public readonly SessionEventFilter Filter = new();
        public readonly List<SessionEvent> Got = new();

        /// <summary><paramref name="cfgPath"/> null = plan progress in memory only.</summary>
        public Rig(string? cfgPath)
        {
            S = new Session(new SessionConfig(), () => 200, Log.Add, cfgPath is null ? null : PlanBook.Load(cfgPath, Log.Add));
            Deliver();   // the mod primes its event filter on the first message
        }

        public void Do(string name) => S.Apply(new Intent(name), Now);
        public SessionView Deliver() { var v = SessionView.Parse((Dictionary<string, object>)JsonReader.Parse(S.Message(Now))); Filter.Take(v, Got); return v; }

        public void Ride(int ticks, bool pedalling = true)
        {
            for (int i = 0; i < ticks; i++) { Now = Now.AddMilliseconds(250); S.Tick(0.25, pedalling, true, 150, Now); Deliver(); }
        }
    }

    public static void Run(Action<bool, string> check, Func<string, string?> findFixture)
    {
        string dir = Path.Combine(Path.GetTempPath(), "descenders_menu_" + Guid.NewGuid().ToString("N"));
        string lib = Path.Combine(dir, "Library");
        Directory.CreateDirectory(Path.Combine(lib, "Workouts", "Sub"));
        Directory.CreateDirectory(Path.Combine(lib, "Workouts", "Plan"));
        Directory.CreateDirectory(Path.Combine(lib, "TrainingPlans"));
        File.WriteAllText(Path.Combine(lib, "Workouts", "Sub", "t.zwo"), SubZwo);
        File.WriteAllText(Path.Combine(lib, "Workouts", "a.zwo"), Short("Alpha"));
        File.WriteAllText(Path.Combine(lib, "Workouts", "broken.zwo"), "<not xml");
        for (int i = 1; i <= 4; i++) File.WriteAllText(Path.Combine(lib, "Workouts", "Plan", "p" + i + ".zwo"), Short("P" + i));
        File.WriteAllText(Path.Combine(lib, "TrainingPlans", "fx.xml"), PlanXml);
        File.WriteAllText(Path.Combine(lib, "TrainingPlans", "run.xml"), PlanXml.Replace("CYCLING", "RUNNING"));
        try
        {
            NoLibrary(check, dir);
            Workouts(check, lib);
            Plans(check, dir, lib);
            Library(check, dir, lib);
            ModSide(check, findFixture);
            Posted(check, lib);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void NoLibrary(Action<bool, string> check, string dir)
    {
        Console.WriteLine("Workouts tab: no workout library set");
        check(Menu.UsableLibrary("", out string p1) == null && p1 == Menu.NoLibraryText && Menu.UsableLibrary("   ", out _) == null, "no library: not usable, the message says where to set it");
        check(Menu.UsableLibrary(Path.Combine(dir, "does-not-exist"), out string p2) == null && p2.Contains("not found"), "a library folder that does not exist: 'not found'");
        var r = new Rig(null);
        var res = Menu.StartPlanSession(r.S, "", "fx", "b0.a0", r.Now);
        check(!res.Ok && res.Text.Contains(Menu.NoLibraryText) && r.S.Run == null && !r.S.Erg, "a plan session with no library starts nothing and says why");
        check(PlanPicker.Plans("", new ProgressStore(), out string none).Count == 0 && none.StartsWith("No training plans"), "and no plans are listed");
        check(PlanSummary.Describe("", new ProgressStore()) == (PlanSummary.NoPlan, "") && PlanSummary.Percent("", new ProgressStore()) == 0, "the summary: no plan chosen yet, progress 0");
    }

    private static void Workouts(Action<bool, string> check, string lib)
    {
        Console.WriteLine("Workouts tab: the folder and workout lists, starting a single workout");
        string wdir = Menu.WorkoutsDir(lib);
        var folders = WorkoutPicker.Folders(wdir);
        check(folders.SequenceEqual(new[] { "", "Plan", "Sub" }) && Menu.FolderText("") == "Workouts" && Menu.FolderText(Path.Combine("Sub", "Deep")) == "Sub / Deep",
            $"the folder drop-down: Workouts, Plan, Sub (got {string.Join(" | ", folders)}); nested folders read \"Sub / Deep\"");
        var top = WorkoutPicker.Workouts(wdir, "", 200f, out _);
        check(top.Count == 2 && Menu.WorkoutEntry(top[0]) == new PickerEntry("Alpha", "0:10") && Menu.WorkoutEntry(top[1]).Detail == "cannot load",
            $"the workout picker: name, and its duration under it; a broken file says cannot load (got {string.Join(" | ", top.Select(Menu.WorkoutEntry))})");
        check(Menu.FolderEntry("", WorkoutPicker.Count(wdir, "")) == new PickerEntry("Workouts", "2 workouts") && Menu.FolderEntry("Sub", WorkoutPicker.Count(wdir, "Sub")) == new PickerEntry("Sub", "1 workout")
            && WorkoutPicker.Count(wdir, "no-such-folder") == 0, "the folder picker: the folder, and how many workouts it holds under it");
        check(Menu.WorkoutDetail(top[1]).StartsWith("This workout cannot be loaded") && Menu.WorkoutDetail(null) == "", "the detail line of a broken file says why; nothing chosen, nothing shown");
        var sub = WorkoutPicker.Workouts(wdir, "Sub", 200f, out _);
        check(sub.Count == 1 && Menu.WorkoutDetail(sub[0]) == "0:20   2 sections   by Tester   Two steps.", $"the detail line: duration, sections, author, description on one line (got '{Menu.WorkoutDetail(sub[0])}')");

        var r = new Rig(null);
        var res = Menu.StartWorkout(r.S, sub[0].Path, r.Now);
        check(res.Ok && res.Text.Contains("Session test") && r.S.Run != null && r.S.Erg && r.S.Run.W.Name == "Session test" && r.S.Tag == "" && r.S.Plan == null,
            "Start workout: armed, ERG on, not a plan session, and the window is told so");
        check(r.S.Describe().Contains("\"Session test\"") && r.S.Describe().Contains("0:00 of 0:20") && r.S.Describe().EndsWith("(starts when you pedal)"), $"the window's line while armed (got '{r.S.Describe()}')");
        r.Ride(8);
        check(r.S.Describe().Contains("0:02 of 0:20, section 1 of 2") && !r.S.Describe().Contains("starts when"), $"and while it runs (got '{r.S.Describe()}')");
        r.Ride(8, pedalling: false); r.Ride(8, pedalling: false);
        check(r.S.Describe().EndsWith("(paused)"), "and paused when the rider stops pedalling");
        res = Menu.StartWorkout(r.S, top[0].Path, r.Now);
        check(res.Ok && r.S.Run!.W.Name == "Alpha", "starting another workout replaces the running one");
        res = Menu.StartWorkout(r.S, top[1].Path, r.Now);
        check(!res.Ok && r.S.Run == null && r.S.Erg, "a file that does not load: not started, said so, manual ERG left on");
        r.Do("erg_toggle");
        check(r.S.Describe() == Session.NoWorkoutText, "ERG off in the game: the window's line is back to free ride");
        r.Do("erg_toggle");
        check(r.S.Describe().StartsWith("ERG mode at 120 W"), "manual ERG from the game (RB + D-pad Up) shows as ERG with no workout");
        r.Do("menu_toggle");
        check(r.Log.Exists(l => l.Contains("unknown intent 'menu_toggle'")), "the old menu_toggle intent is unknown now (an old mod's F3 does nothing)");
    }

    private static void Plans(Action<bool, string> check, string dir, string lib)
    {
        Console.WriteLine("Workouts tab: training plans, follow, start a session, completion recorded once by the bridge, reset (TP8 headless)");
        string cfg = Path.Combine(dir, "plans", TrainerBridgeConfig.FileName);
        TrainerBridgeConfig.SaveRider(cfg, new RiderConfig { FtpW = 230 });
        TrainerBridgeConfig.SaveLibraryDir(cfg, lib);
        var r = new Rig(cfg);
        check(r.S.Plans.Problem == null && r.S.Plans.Store.Plans.Count == 0, "no progress yet: loads empty, no problem");

        var plans = PlanPicker.Plans(lib, r.S.Plans.Store, out string msg);
        check(plans.Count == 1 && msg == "" && plans[0].Plan.Key == "fx" && !plans[0].Active && !plans[0].Complete && Menu.PlanEntry(plans[0]) == new PickerEntry("Fixture plan", "0 of 3 done"),
            $"the plan picker: the cycling plan with its 0 of 3 required sessions, the running plan left out (got {string.Join(" | ", plans.Select(Menu.PlanEntry))})");
        check(Menu.ChoosePlanEntry(1) == new PickerEntry("Choose a training plan", "1 plan in the library") && Menu.ChoosePlanEntry(3).Detail == "3 plans in the library",
            "with no plan followed yet the plan picker opens on \"Choose a training plan\"");
        var sessions = PlanPicker.Sessions(lib, plans[0].Plan, null, PlanBook.IncludeOptional, 200f);
        check(sessions.Count == 5 && sessions[0].IsNext && sessions.Count(s => s.IsNext) == 1 && Menu.SessionEntry(sessions[0]) == new PickerEntry("W1 D1 P1", "0:10 · next"),
            $"the session picker: every session in plan order, the next one marked (got {string.Join(" | ", sessions.Select(Menu.SessionEntry))})");
        check(Menu.SessionEntry(sessions[2]) == new PickerEntry("W1 D3 P3", "0:10 · optional") && sessions[2].Selectable && sessions[3].Block == "Week 2", "an optional session says so; each knows its week");
        check(!sessions[4].Selectable && Menu.SessionEntry(sessions[4]) == new PickerEntry("W2 D2 Gone", "file missing") && sessions[4].Path == "" && Menu.SessionProblem(sessions[4]).StartsWith("This session's workout file is missing"),
            "a session whose workout file is missing is listed but cannot be started, and says so where its graph would be");
        check(sessions[0].Path == Path.Combine(lib, "Workouts", "Plan", "p1.zwo"), $"a session knows its workout file, for the effort graph (got '{sessions[0].Path}')");

        // following a plan with no ride running: the window loads the progress, changes it, saves it
        var book = PlanBook.Load(cfg, _ => { });
        var res = Menu.FollowPlan(book, "fx", "Fixture plan", _ => { });
        check(res.Ok && res.Text == "Now following Fixture plan." && book.Saves == 1 && ProgressFile.Load(cfg).Store.ActivePlan == "fx", "choosing a plan makes it the active one, saved in the progress section");
        check(Menu.FollowPlan(book, "FX", "Fixture plan", _ => { }) is { Ok: true, Text: "" } && book.Saves == 1, "choosing the plan already followed changes and saves nothing");
        check(TrainerBridgeConfig.Load(cfg, "", "", () => null, _ => { }).Rider.FtpW == 230 && TrainerBridgeConfig.LoadLibraryDir(cfg) == lib, "the rider and library sections survive the progress save");
        check(PlanSummary.Describe(lib, book.Store) is var (a0, n0) && a0 == "Active training plan: Fixture plan (0 of 3 sessions done)" && n0 == "Next session: Week 1 Day 1 Session P1",
            "the window's summary: the active plan, 0 of 3, next Week 1 Day 1");

        // a ride: its own copy of the progress, loaded at its start
        r = new Rig(cfg);
        res = Menu.StartPlanSession(r.S, lib, "fx", "b0.a0", r.Now);
        check(res.Ok && res.Text.StartsWith("Started Week 1 Day 1 Session P1") && r.S.Run != null && r.S.Plan is { Key: "fx", Ref: "b0.a0" } && r.S.Tag == "fx|b0.a0", "Start session starts W1 D1 as a plan session");
        var v = r.Deliver();
        check(v.Workout != null && v.Workout.PlanTitle == "Fixture plan" && v.Workout.PlanSessionLabel == "W1 D1 (0/3)", "the session message carries the plan line for the game's workout panel");
        check(r.S.Describe().StartsWith("Fixture plan W1 D1 (0/3), \"P1\""), $"the window's line names the plan session (got '{r.S.Describe()}')");
        r.Ride(4 * 12);
        check(r.S.Run == null && r.Got.Count(e => e.Kind == "complete" && e.Tag == "fx|b0.a0" && !e.Skipped) == 1, "ridden to the end: one complete event with the plan tag");
        var st = ProgressFile.Load(cfg).Store;
        check(st.Plans["fx"].Completed.Count == 1 && st.Plans["fx"].Completed[0].Ref == "b0.a0" && st.Plans["fx"].Completed[0].File == "p1.zwo" && !st.Plans["fx"].Completed[0].Skipped
            && Math.Abs(st.Plans["fx"].Completed[0].AvgW - 150f) < 0.5f && st.Plans["fx"].Completed[0].FtpW == 200f, "the bridge recorded it in the file: ref, file, avg watts, FTP");
        r.Ride(8); r.Deliver(); r.Deliver();
        check(ProgressFile.Load(cfg).Store.Plans["fx"].Completed.Count == 1 && r.Log.Count(l => l.StartsWith("Plan: recorded")) == 1, "recorded once, however many messages repeat the event");
        check(PlanSummary.Describe(lib, st) is var (a1, n1) && a1.Contains("Fixture plan (1 of 3 sessions done)") && n1 == "Next session: Week 1 Day 2 Session P2" && PlanSummary.Percent(lib, st) == 33,
            $"the window's summary: 1 of 3, next Week 1 Day 2, progress line at 33 % (got {n1}, {PlanSummary.Percent(lib, st)})");
        sessions = PlanPicker.Sessions(lib, plans[0].Plan, st.Plans["fx"], PlanBook.IncludeOptional, 200f);
        check(sessions[0].Done && Menu.SessionEntry(sessions[0]).Detail == "0:10 · done" && sessions[1].IsNext, "the session picker: W1 D1 done, W1 D2 next");

        // the window shows the session in full words, with the file name's '_' and '-' as spaces
        var named = PlanParser.Parse("<plan name='N'><block>"
            + "<activity auid='1' workout_filename='data/Workouts/Dirt_Destroyer/a.xml' priority='1'/><activity auid='5' workout_filename='b.xml' workout_name='Sweet-Spot_3x10' priority='2'/>"
            + "<activity priority='3'/><activity auid='2' workout_filename='data/Workouts/Dirt_Destroyer/2_70rpm_Strength_Ramps.xml' priority='4'/></block></plan>", "n.xml");
        check(named.Blocks[0].Activities[3].LongLabel == "Week 1 Day 4 Session 2 70rpm Strength Ramps" && named.Blocks[0].Activities[3].WeekDayLabel == "W1 D4",
            $"a session named by its file: Week 1 Day 4 Session 2 70rpm Strength Ramps (got {named.Blocks[0].Activities[3].LongLabel})");
        check(named.Blocks[0].Activities[1].LongLabel == "Week 1 Day 2 Session Sweet Spot 3x10" && named.Blocks[0].Activities[2].LongLabel == "Week 1 Day 3", "workout_name gets the same spaces; no name, no 'Session'");
        check(PlanParser.SessionName("__a--b_ c-") == "a b c" && PlanParser.SessionName(null) == "", "runs of '_' / '-' collapse to one space, ends trimmed");
        var namedSessions = PlanPicker.Sessions(lib, named, null, true, 200f);
        check(namedSessions[3].Title == "2 70rpm Strength Ramps" && namedSessions[1].Title == "Sweet Spot 3x10" && namedSessions[0].Label == "W1 D1", "the session drop-down uses the same names");

        // a new ride (bridge restarted): the next session has moved on
        var r2 = new Rig(cfg);
        check(Menu.StartPlanSession(r2.S, lib, "fx", "b0.a1", r2.Now).Ok && r2.S.Plan is { Ref: "b0.a1", SessionLabel: "W1 D2 (1/3)" }, "after a restart, W1 D2 starts with 1/3 on its label");
        r2.Ride(8);
        r2.Do("erg_toggle");
        r2.Ride(4);
        check(r2.S.Run != null && r2.S.Held && r2.S.Plan is { Ref: "b0.a1" } && ProgressFile.Load(cfg).Store.Plans["fx"].Completed.Count == 1,
            "a plan session left mid-way with the mode button is on hold (V2.22): still loaded, nothing recorded");
        check(r2.S.Describe().Contains("on hold, free ride") && r2.S.Describe().Contains("RB + D-pad Up"), $"the window's line says it is on hold and how to come back (got '{r2.S.Describe()}')");
        Menu.StartPlanSession(r2.S, lib, "fx", "b0.a1", r2.Now);
        r2.Do("skip");
        check(r2.S.Run == null && ProgressFile.Load(cfg).Store.Plans["fx"].Completed is { Count: 2 } c2 && c2[1].Ref == "b0.a1" && c2[1].Skipped, "skipped to the end in the game: recorded with skipped");
        plans = PlanPicker.Plans(lib, r2.S.Plans.Store, out _);
        check(plans[0].Active && Menu.PlanEntry(plans[0]) == new PickerEntry("Fixture plan", "2 of 3 done"), "the plan picker: active, 2 of 3 done");

        int saves = r2.S.Plans.Saves;
        res = Menu.StartPlanSession(r2.S, lib, "fx", "b1.a1", r2.Now);
        check(!res.Ok && res.Text.Contains("not found") && r2.S.Run == null && r2.S.Plans.Saves == saves, "a session whose workout file is missing: not started, nothing saved, said so");
        check(!Menu.StartPlanSession(r2.S, lib, "fx", "b9.a9", r2.Now).Ok && !Menu.StartPlanSession(r2.S, lib, "nope", "b0.a0", r2.Now).Ok && r2.S.Run == null, "an unknown session or plan starts nothing");

        // reset, during the ride: the ride's own copy is the one changed and saved
        res = Menu.ResetPlan(r2.S.Plans, "fx", "Fixture plan", _ => { });
        st = ProgressFile.Load(cfg).Store;
        check(res.Ok && st.ActivePlan == "fx" && st.Plans["fx"].Completed.Count == 0 && r2.S.Plans.Saves == saves + 1, "Reset plan: history gone, still the active plan, saved");
        check(PlanSummary.Describe(lib, st).Active.Contains("(0 of 3 sessions done)") && PlanSummary.Percent(lib, st) == 0, "the window's summary: back to 0 of 3");

        // all required sessions done
        foreach (string rf in new[] { "b0.a0", "b0.a1", "b1.a0" }) st.RecordCompletion("fx", rf, "x.zwo", "2026-09-30T13:00:00Z", 10, 150, 200, 100, false);
        plans = PlanPicker.Plans(lib, st, out _);
        check(plans[0].Complete && Menu.PlanEntry(plans[0]) == new PickerEntry("Fixture plan", "complete") && PlanSummary.Percent(lib, st) == 100 && PlanSummary.Describe(lib, st).Next.Contains("complete"),
            "a finished plan: 'complete' in the picker, the progress line full, no next session");
        var moved = new ProgressStore(); moved.Enroll("gone", "Old plan", "2026-09-30T12:00:00Z");
        check(PlanSummary.Describe(lib, moved).Active.Contains("Old plan (its file is not in the workout library)"), "an active plan whose file was removed: said so");

        // an unreadable file: loads empty, nothing saved for the session, the file is left as it is
        string broken = Path.Combine(dir, "broken", TrainerBridgeConfig.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        File.WriteAllText(broken, "{ \"rider\": { \"ftpW\": 2");
        var rb = new Rig(broken);
        check(rb.S.Plans.Problem != null && rb.Log.Exists(l => l.Contains("will NOT be saved")), "an unreadable settings file: logged once at load");
        res = Menu.FollowPlan(rb.S.Plans, "fx", "Fixture plan", _ => { });
        check(!res.Ok && res.Text.Contains("could not be saved") && rb.S.Plans.Store.ActivePlan == "fx" && rb.S.Plans.Saves == 0 && File.ReadAllText(broken) == "{ \"rider\": { \"ftpW\": 2"
            && rb.Log.Exists(l => l.StartsWith("Progress: not saved")), "following a plan still works for the ride, but nothing is saved, the file is untouched and the window says so");
    }

    private static void Library(Action<bool, string> check, string dir, string lib)
    {
        Console.WriteLine("Library setting (the `library` section) and the window's words");
        string p = Path.Combine(dir, "lib", TrainerBridgeConfig.FileName);
        check(TrainerBridgeConfig.LoadLibraryDir(p) == "", "no file: no library");
        var log = new List<string>();
        UserConfig Load() => TrainerBridgeConfig.Load(p, "", "", () => null, log.Add);
        Load();
        check(TrainerBridgeConfig.LoadLibraryDir(p) == "" && log.Exists(l => l.StartsWith("Workout library: not set") && l.Contains(Menu.NoLibraryText)), "a new file has none, and loading says so");
        var prefs = Load().Devices; prefs.Set(Role.Trainer, new DeviceKey(17, 4242, 5)); TrainerBridgeConfig.SaveDevices(p, prefs);
        TrainerBridgeConfig.SaveRider(p, new RiderConfig { FtpW = 250 });
        var store = new ProgressStore(); store.Enroll("fx", "Fixture plan", "2026-09-30T12:00:00Z");
        ProgressFile.Save(p, store);
        TrainerBridgeConfig.SaveLibraryDir(p, "  " + lib + "  ");
        check(TrainerBridgeConfig.LoadLibraryDir(p) == lib, "saved and read back (trimmed)");
        var b = Load();
        check(b.Rider.FtpW == 250 && b.Devices.Get(Role.Trainer) == new DeviceKey(17, 4242, 5) && ProgressFile.Load(p).Store.ActivePlan == "fx", "saving the library keeps rider, devices and progress");
        TrainerBridgeConfig.SaveRider(p, b.Rider with { FtpW = 260 });
        prefs = Load().Devices; prefs.Set(Role.HeartRate, new DeviceKey(120, 7, 1)); TrainerBridgeConfig.SaveDevices(p, prefs);
        store.RecordCompletion("fx", "b0.a0", "p1.zwo", "2026-09-30T13:00:00Z", 10, 150, 200, 100, false);
        ProgressFile.Save(p, store);
        check(TrainerBridgeConfig.LoadLibraryDir(p) == lib && Load().Rider.FtpW == 260 && ProgressFile.Load(p).Store.Plans["fx"].Completed.Count == 1, "rider, devices and progress saves keep the library");
        check(log.Exists(l => l == "Workout library: " + lib), "loading prints the library folder");
        File.WriteAllText(p, "{ \"LIBRARY\": { \"Dir\": \"sub\" } }");
        check(TrainerBridgeConfig.LoadLibraryDir(p) == Path.Combine(Path.GetDirectoryName(p)!, "sub"), "section read case-insensitively; a relative folder is taken from the settings file's folder");
        File.WriteAllText(p, "{ broken");
        check(TrainerBridgeConfig.LoadLibraryDir(p) == "", "an unreadable file: no library (never throws)");

        check(LibraryText.Status("") == LibraryText.NotSet && LibraryText.Status(Path.Combine(dir, "nope")).Contains("does not exist") && LibraryText.Status(lib).Contains("training plans"),
            "the window's status words: not set / does not exist / what is listed");
        check(!LibraryText.NotSet.Contains("game") && !LibraryText.Status(lib).Contains("game") && !LibraryText.Status(dir).Contains("game"), "none of them sends the rider to a menu in the game");
        check(LibraryText.Refuse("") == null && LibraryText.Refuse(lib) == null && LibraryText.Refuse("relative\\folder") != null && LibraryText.Refuse(Path.Combine(dir, "nope")) != null,
            "the window saves an empty box or an existing full path, and refuses a relative or missing folder");
    }

    /// <summary>The mod no longer touches plans or progress, and has no workout / plan menu: no picker code, keys or `menu` message.</summary>
    private static void ModSide(Action<bool, string> check, Func<string, string?> findFixture)
    {
        Console.WriteLine("The bridge is the only writer of TrainerBridgeConfig.json, and the game has no workout menu");
        string? csproj = findFixture("DescendersCyclingPOC.csproj");
        string? erg = findFixture(Path.Combine("src", "ErgControl.cs"));
        check(csproj != null && erg != null, "the mod's project and sources found");
        if (csproj == null || erg == null) return;
        string proj = File.ReadAllText(csproj);
        string[] bridgeOnly = { "Progress.cs", "Plan.cs", "WorkoutPicker.cs", "PlanPicker.cs" };
        check(bridgeOnly.All(f => proj.Contains("src/" + f)) && proj.Contains("Compile Remove"), "Plan / Progress / the workout and plan lists are left out of the mod");
        var modSources = Directory.GetFiles(Path.GetDirectoryName(erg)!, "*.cs").Where(f => !bridgeOnly.Contains(Path.GetFileName(f))).ToList();
        List<string?> Using(params string[] words) => modSources.Where(f => { string t = File.ReadAllText(f); return words.Any(t.Contains); }).Select(Path.GetFileName).ToList();
        var offenders = Using("ProgressFile", "\"WorkoutLibrary\"");
        check(offenders.Count == 0, "no mod source uses ProgressFile or the old WorkoutLibrary setting" + (offenders.Count > 0 ? ": " + string.Join(", ", offenders) : ""));
        offenders = Using("\"menu_", "MenuView.Parse", "PickerOverlay(", "WorkoutPickerKey", "PickerScale", "\"menu\"");
        check(offenders.Count == 0, "no mod source sends a menu_* intent, reads a `menu` message, draws the picker or has its keys / size setting" + (offenders.Count > 0 ? ": " + string.Join(", ", offenders) : ""));
        string ergText = File.ReadAllText(erg);
        check(ergText.Contains("\"erg_toggle\"") && ergText.Contains("\"adjust\"") && ergText.Contains("\"skip\""), "the workout controls stay in the game: ERG on / off, easier / harder, skip a section");
        check(!Hello.Capabilities.Contains("menu") && Hello.Capabilities.Contains("hud"), "the game's hello no longer offers a menu; it still shows the session (the workout panel)");
    }

    /// <summary>The window's presses reach the session on the ride loop's own thread (RideLoop.Post), in order, and tell the window what happened.</summary>
    private static void Posted(Action<bool, string> check, string lib)
    {
        Console.WriteLine("Workouts tab: a press is handed to the ride loop and answered from it");
        var loop = new RideLoop(new ResistanceConfig());
        string path = Path.Combine(Menu.WorkoutsDir(lib), "a.zwo");
        var first = loop.Post((s, now) => Menu.StartWorkout(s, path, now));
        var second = loop.Post((s, now) => new MenuResult(s.Run != null, "second"));
        var bad = loop.Post((_, _) => throw new InvalidOperationException("boom"));
        check(!first.IsCompleted && loop.Session.Run == null, "nothing runs on the caller's thread");
        var realOut = Console.Out;
        Console.SetOut(TextWriter.Null);   // the thrown action is logged
        try { loop.TakePosted(T0); }
        finally { Console.SetOut(realOut); }
        check(first.IsCompleted && first.Result.Ok && loop.Session.Run?.W.Name == "Alpha" && loop.Session.Erg, "taken on the loop's tick: the workout is started, the task carries the result");
        check(second.IsCompleted && second.Result is { Ok: true, Text: "second" }, "posted actions run in order (the second saw the first's workout)");
        check(bad.IsCompleted && !bad.Result.Ok && loop.Session.Run != null, "an action that throws ends with a failed result and leaves the ride alone");
        check(RideLoop.Current == null, "no loop is 'current' until one runs (the window then says: not riding)");
    }
}
