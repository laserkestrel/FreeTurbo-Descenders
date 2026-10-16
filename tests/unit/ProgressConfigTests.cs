using DescendersBridge.Ant;
using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// (docs/PLANS.md TP6): plan progress lives in the `progress` section of the per-user TrainerBridgeConfig.json, next to the
/// bridge's `rider` and `devices` sections. Round-trip, the other sections survive both writers, an unreadable file is never rewritten,
/// and the repo's example file loads in both. Uses a temp folder, never the real %APPDATA% file.
/// </summary>
public static class ProgressConfigTests
{
    public static void Run(Action<bool, string> check, Func<string, string?> findFixture)
    {
        Console.WriteLine("TP6: progress in TrainerBridgeConfig.json: round-trip, shared file, corruption");
        string dir = Path.Combine(Path.GetTempPath(), "descenders_progress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            check(ProgressFile.DefaultPath().EndsWith(Path.Combine("TrainerBridge", "TrainerBridgeConfig.json")), "the mod's default path is %APPDATA%\\TrainerBridge\\TrainerBridgeConfig.json");
            check(TrainerBridgeConfig.FileName == ProgressFile.FileName, "mod and bridge use the same file name");

            var store = new ProgressStore();
            store.Enroll("6wk_fixture", "Fixture Plan", "2026-09-22T18:04:11Z");
            store.RecordCompletion("6wk_fixture", "b0.a2", "steady_b.xml", "2026-09-22T18:52:36Z", 2740, 176.5f, 180f, 100f, false);

            // A missing file: created with just the progress section.
            string p1 = Path.Combine(dir, "new", TrainerBridgeConfig.FileName);
            ProgressFile.Save(p1, store);
            check(File.Exists(p1) && !File.Exists(p1 + ".tmp"), "missing file created, no leftover .tmp");
            var l1 = ProgressFile.Load(p1);
            check(l1.Problem == null && l1.Store.ActivePlan == "6wk_fixture", "activePlan round-trips");
            check(l1.Store.Plans.TryGetValue("6wk_fixture", out var e0) && e0.Completed.Count == 1 && e0.Completed[0].Ref == "b0.a2"
                && e0.Completed[0].AvgW == 176.5f && e0.StartedUtc == "2026-09-22T18:04:11Z", "record and plan fields round-trip exactly");
            check(File.ReadAllText(p1).Contains(Environment.NewLine + "  \"progress\": {"), "the file is written indented, readable by a person");

            // A file the bridge wrote: rider and devices survive a progress save, and progress survives a bridge save.
            string p2 = Path.Combine(dir, "shared", TrainerBridgeConfig.FileName);
            var log = new List<string>();
            UserConfig LoadBridge() => TrainerBridgeConfig.Load(p2, Path.Combine(dir, "none.json"), Path.Combine(dir, "none2.json"), () => null, log.Add);
            TrainerBridgeConfig.SaveRider(p2, new RiderConfig { RiderWeightKg = 81.5, BikeWeightKg = 8.2, FtpW = 230, WheelDiameterM = 0.68, GearRatio = 0 });
            var prefs = LoadBridge().Devices; prefs.Set(Role.Trainer, new DeviceKey(17, 44226, 5)); TrainerBridgeConfig.SaveDevices(p2, prefs);
            check(ProgressFile.Load(p2) is { Problem: null } l2 && l2.Store.Plans.Count == 0, "a bridge-only file (no progress section) loads empty, no problem");
            ProgressFile.Save(p2, store);
            var b = LoadBridge();
            check(b.Rider is { RiderWeightKg: 81.5, BikeWeightKg: 8.2, FtpW: 230, WheelDiameterM: 0.68, GearRatio: 0 }, "rider section unchanged after a progress save");
            check(b.Devices.Get(Role.Trainer) is { DeviceNumber: 44226, DeviceType: 17, TransmissionType: 5 }, "devices section unchanged after a progress save");
            check(TrainerBridgeConfig.IsReadable(p2, out _), "the bridge still finds the file readable");
            TrainerBridgeConfig.SaveRider(p2, b.Rider with { FtpW = 250 });
            prefs = LoadBridge().Devices; prefs.Set(Role.HeartRate, new DeviceKey(120, 7000, 1)); TrainerBridgeConfig.SaveDevices(p2, prefs);
            var l3 = ProgressFile.Load(p2);
            check(l3.Problem == null && l3.Store.Plans.TryGetValue("6wk_fixture", out var e3) && e3.Completed.Count == 1, "progress section survives the bridge saving rider and devices");
            store.RecordCompletion("6wk_fixture", "b0.a3", "steady_c.xml", "2026-09-23T18:00:00Z", 1800, 170f, 180f, 100f, true);
            ProgressFile.Save(p2, store);
            check(ProgressFile.Load(p2).Store.Plans["6wk_fixture"].Completed.Count == 2 && LoadBridge().Rider.FtpW == 250, "a second progress save keeps the newer FTP the bridge wrote");

            // A section named with other casing (the bridge matches sections case-insensitively) is replaced in place, not duplicated.
            string p4 = Path.Combine(dir, "case", TrainerBridgeConfig.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(p4)!);
            File.WriteAllText(p4, "{ \"rider\": { \"ftpW\": 200 }, \"Progress\": { \"version\": 1, \"activePlan\": \"x\", \"plans\": {} } }");
            check(ProgressFile.Load(p4).Store.ActivePlan == "x", "a \"Progress\" section is read case-insensitively");
            ProgressFile.Save(p4, store);
            string t4 = File.ReadAllText(p4);
            check(t4.Contains("\"Progress\"") && !t4.Contains("\"progress\"") && ProgressFile.Load(p4).Store.ActivePlan == "6wk_fixture", "and rewritten under the same key");

            // Unreadable file: loads empty with a problem, a save refuses and leaves every byte as it was (the bridge's settings are in it).
            string good = File.ReadAllText(p2);
            string broken = good.Substring(0, good.Length / 2);
            File.WriteAllText(p2, broken);
            var bad = ProgressFile.Load(p2);
            check(bad.Problem != null && bad.Store.Plans.Count == 0, "a truncated file loads empty and reports a problem");
            bool threw = false;
            try { ProgressFile.Save(p2, store); } catch (InvalidOperationException) { threw = true; }
            check(threw && File.ReadAllText(p2) == broken, "saving into an unreadable file refuses and leaves it untouched");
            check(Directory.GetFiles(Path.GetDirectoryName(p2)!).Length == 1, "nothing is moved aside or added beside it");

            // A progress section that is not an object: problem on load, save refuses.
            File.WriteAllText(p2, "{ \"rider\": { \"ftpW\": 200 }, \"progress\": \"oops\" }");
            check(ProgressFile.Load(p2).Problem != null, "a progress section that is not an object is reported");
            threw = false;
            try { ProgressFile.Save(p2, store); } catch (InvalidOperationException) { threw = true; }
            check(threw && File.ReadAllText(p2).Contains("oops"), "and is not overwritten");

            // A kill between the tmp write and the replace: the previous file survives and the stray .tmp is ignored.
            File.WriteAllText(p2, good);
            File.WriteAllText(p2 + ".tmp", "{not valid json, simulating a kill between the tmp write and the replace");
            var afterKill = ProgressFile.Load(p2);
            check(afterKill.Problem == null && afterKill.Store.Plans.ContainsKey("6wk_fixture"), "the previous good file survives intact and parses; the stray .tmp is ignored");
            File.Delete(p2 + ".tmp");

            // The example in the repo is valid for both readers.
            string? example = findFixture(Path.Combine("bridge", "TrainerBridgeConfig.example.json"));
            check(example != null, "bridge/TrainerBridgeConfig.example.json found");
            if (example != null)
            {
                var ex = ProgressFile.Load(example);
                check(ex.Problem == null && ex.Store.ActivePlan.Length > 0 && ex.Store.Plans.TryGetValue(ex.Store.ActivePlan, out var ep) && ep.Completed.Count > 0,
                    "the example's progress section loads, with an active plan and a completed session");
                check(TrainerBridgeConfig.IsReadable(example, out _), "the example is readable by the bridge");
                check(TrainerBridgeConfig.LoadLibraryDir(example) == @"C:\Users\you\Documents\WorkoutLibrary", "the example's library section loads");
                var exLog = new List<string>();
                var exCfg = TrainerBridgeConfig.Load(example, "", "", () => null, exLog.Add);
                check(!exLog.Any(s => s.Contains("unreadable") || s.Contains("clamped") || s.Contains("no rider section")) && exCfg.Devices.Get(Role.Trainer) != null,
                    "the example's rider and devices load in the bridge without clamping");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
