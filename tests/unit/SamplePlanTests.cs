using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// The repo's own sample training plan (trainingplans\FreeTurbo_Starter.xml) and its workouts (workouts\FreeTurbo_Starter\), laid out the
/// way README "Workouts (optional)" tells the rider to: workouts\ copied to the library's Workouts\, trainingplans\ to its TrainingPlans\.
/// The plan names each workout by its .xml name under data/Workouts/, as the format does, and must find every one through the .zwo fallback
/// (docs/PLANS.md V3.8). Every sample workout in workouts\ parses cleanly. Temp folders only. Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class SamplePlanTests
{
    public static void Run(Action<bool, string> check, Func<string, string?> findFixture)
    {
        Console.WriteLine("Sample training plan: trainingplans\\FreeTurbo_Starter.xml and its workouts, copied into a library as the README says");
        string? planFile = findFixture(Path.Combine("trainingplans", "FreeTurbo_Starter.xml"));
        string? anyWorkout = findFixture(Path.Combine("workouts", "workout30.zwo"));
        check(planFile != null && anyWorkout != null, "trainingplans\\FreeTurbo_Starter.xml and workouts\\ found in the repo");
        if (planFile == null || anyWorkout == null) return;
        string repoWorkouts = Path.GetDirectoryName(anyWorkout)!, repoPlans = Path.GetDirectoryName(planFile)!;

        // every sample workout in the repo, the plan's and the loose ones, parses with no step skipped and has a length
        // ("ignored attributes: pace" is only a note: the format writes pace="0" on every step and the samples keep it)
        var samples = Directory.GetFiles(repoWorkouts, "*.zwo", SearchOption.AllDirectories);
        var bad = samples.Select(f => WorkoutParser.Load(f, 200f))
            .Select(w => (w, warnings: w.Warnings.Where(x => !x.StartsWith("ignored attributes: pace")).ToList()))
            .Where(x => !x.w.Ok || x.warnings.Count > 0 || !(x.w.TotalS > 0f))
            .Select(x => x.w.FileName + ": " + (x.w.Error ?? string.Join("; ", x.warnings))).ToList();
        check(samples.Length >= 10 && bad.Count == 0, $"all {samples.Length} sample workouts parse cleanly (bad: {string.Join(" | ", bad)})");

        string dir = Path.Combine(Path.GetTempPath(), "descenders_sampleplan_" + Guid.NewGuid().ToString("N"));
        string lib = Path.Combine(dir, "Library");
        try
        {
            Copy(repoWorkouts, Path.Combine(lib, "Workouts"));
            Copy(repoPlans, Path.Combine(lib, "TrainingPlans"));

            var plans = PlanPicker.Plans(lib, new ProgressStore(), out string msg);
            check(plans.Count == 1 && msg == "" && plans[0].Plan.Key == "FreeTurbo_Starter" && plans[0].Plan.Name == "FreeTurbo Starter",
                $"the plan drop-down offers the sample plan by its own name (got {plans.Count}: {string.Join(", ", plans.Select(p => p.Plan.Name))} {msg})");
            if (plans.Count != 1) return;
            Plan plan = plans[0].Plan;
            check(plan.SupportsCycling && plan.Blocks.Count == 2 && plan.MinWeeks == 2 && plan.Author == "FreeTurbo-Descenders" && plan.Description.Length > 0,
                "two weekly blocks, cycling, with author, description and min_weeks read");
            check(plans[0].Counts.Total == 6 && plans[0].Counts.OptionalTotal == 2 && plans[0].Counts.Done == 0,
                $"6 required sessions and 2 optional recovery spins (got {plans[0].Counts.Total} + {plans[0].Counts.OptionalTotal})");
            check(plan.Blocks.All(b => b.Activities.Count > 0 && b.Activities.All(a => a.WorkoutFilename.StartsWith("data/Workouts/") && a.WorkoutFilename.EndsWith(".xml"))),
                "every activity names its workout the way the format does: data/Workouts/<folder>/<name>.xml");

            var sessions = PlanPicker.Sessions(lib, plan, null, true, 200f);
            var missing = sessions.Where(s => !s.Selectable).Select(s => s.Label + ": " + s.Error).ToList();
            check(sessions.Count == 8 && missing.Count == 0, $"all 8 sessions resolve to the .zwo beside the .xml name and can be started (missing: {string.Join(" | ", missing)})");
            check(sessions.All(s => s.Path.EndsWith(".zwo", StringComparison.OrdinalIgnoreCase) && s.TotalS >= 20 * 60 && s.TotalS <= 60 * 60),
                $"each session is a .zwo of 20 to 60 minutes (got {string.Join(", ", sessions.Select(s => s.Label + " " + (int)(s.TotalS / 60) + " min"))})");
            check(sessions[0].IsNext && sessions[0].Label == "W1 D1" && Progress.NextActivity(plan, null)?.Ref == "b0.a0",
                "a new rider starts at Week 1 Day 1");
            check(sessions.Where(s => s.Optional).Select(s => s.Label).SequenceEqual(new[] { "W1 D4", "W2 D4" }), "the recovery spin is the optional last day of each week");
            check(sessions.Select(s => s.Ref).Distinct().Count() == 8 && plan.Blocks.SelectMany(b => b.Activities).Select(a => a.Auid).Distinct().Count() == 8,
                "every session has its own ref and auid, though the two recovery spins share a file");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void Copy(string from, string to)
    {
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
    }
}
