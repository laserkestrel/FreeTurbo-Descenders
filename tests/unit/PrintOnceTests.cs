using DescendersBridge.Ant;

namespace DescendersBridge;

/// <summary>
/// The settings block in the log (the file's values, "Workout library: ...", "no training section ..."): the window loads the settings
/// file at every connect, save and undo, and each load printed the block again. It is printed once, and again only when it changed.
/// </summary>
public static class PrintOnceTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Settings block in the log: printed once, and again only when it changed");
        var once = new PrintOnce();
        string[] block = { "Bridge config: no training section in x.json, using the defaults", "Workout library: workouts" };
        check(once.IsNew(block), "the first load is printed");
        check(!once.IsNew(block) && !once.IsNew(new List<string>(block)), "the same lines again are not printed");
        string[] moved = { block[0], "Workout library: other" };
        check(once.IsNew(moved) && !once.IsNew(moved), "a changed line prints the block again, once");
        check(once.IsNew(block), "going back to the earlier values is a change too");
        check(once.IsNew(new[] { block[0] }) && once.IsNew(block), "a line more or less is a change");

        // the real loader: the same file twice says its lines once
        string dir = Path.Combine(Path.GetTempPath(), "descenders_printonce_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? before = TrainerBridgeConfig.PathOverride;
        var real = Console.Out;
        try
        {
            TrainerBridgeConfig.PathOverride = Path.Combine(dir, TrainerBridgeConfig.FileName);
            File.WriteAllText(TrainerBridgeConfig.PathOverride, "{ \"rider\": { \"ftpW\": 250 }, \"devices\": {} }");
            string Said(Action act)
            {
                var w = new StringWriter();
                Console.SetOut(w);
                try { act(); } finally { Console.SetOut(real); }
                return w.ToString();
            }
            string first = Said(() => TrainerBridgeConfig.Load()), second = Said(() => TrainerBridgeConfig.Load());
            check(first.Contains("no training section") && first.Contains("Workout library: ") && second.Length == 0,
                "loading the same settings file twice prints its lines the first time only");
            TrainerBridgeConfig.SaveRider(TrainerBridgeConfig.PathOverride, new RiderConfig { FtpW = 260 });
            string third = Said(() => TrainerBridgeConfig.Load());
            check(third.Contains("260") && third.Contains("Workout library: "), "after a saved change the block is printed again");
        }
        finally
        {
            Console.SetOut(real);
            TrainerBridgeConfig.PathOverride = before;
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
