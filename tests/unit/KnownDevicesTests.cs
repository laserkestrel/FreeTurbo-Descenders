using DescendersBridge.Ant;
using DescendersBridge.Gui;

namespace DescendersBridge;

/// <summary>
/// Setting a job on the "My devices" tab to none must not forget the device. Every device ever chosen stays in the
/// `knownDevices` list of TrainerBridgeConfig.json and is offered again in the drop-downs without a search. Uses a temp folder, never
/// the real %APPDATA% file.
/// </summary>
public static class KnownDevicesTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Known devices: a device set to none is remembered");
        string dir = Path.Combine(Path.GetTempPath(), "descenders_known_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new List<string>();
            string p = Path.Combine(dir, TrainerBridgeConfig.FileName);
            UserConfig Load() => TrainerBridgeConfig.Load(p, Path.Combine(dir, "none.json"), Path.Combine(dir, "none2.json"), () => null, log.Add);
            DeviceKey trainer = new(17, 44226, 5), watch = new(120, 7000, 1), strap = new(120, 8111, 1), meter = new(11, 300, 5);

            // the issue: heart rate chosen, then set to none, then the bridge is started again
            var prefs = Load().Devices;
            check(prefs.Known.Count == 0, "a new settings file knows no devices");
            prefs.Set(Role.Trainer, trainer); prefs.Set(Role.HeartRate, watch);
            TrainerBridgeConfig.SaveDevices(p, prefs);
            prefs = Load().Devices; prefs.Clear(Role.HeartRate);
            TrainerBridgeConfig.SaveDevices(p, prefs);
            var after = Load().Devices;
            check(after.Get(Role.HeartRate) is null && after.Get(Role.Trainer) == trainer, "heart rate set to none: the role is empty, the trainer is kept");
            check(after.Known.Select(d => d.Key).SequenceEqual(new[] { trainer, watch }), "the watch is still known after its role was set to none and the file was read again");
            check(after.KnownFor(Role.HeartRate).SequenceEqual(new[] { watch }) && after.KnownFor(Role.Power).SequenceEqual(new[] { trainer }),
                "known devices are offered for the jobs they can do (the watch for heart rate, the trainer also for power)");
            string text = File.ReadAllText(p);
            check(text.Contains("\"knownDevices\"") && text.Contains("7000") && !text.Contains("\"HeartRate\""), "the file has a knownDevices list with the watch and no HeartRate role");

            // replacing a device keeps the old one; choosing a known one again adds nothing
            after.Set(Role.HeartRate, strap); TrainerBridgeConfig.SaveDevices(p, after);
            var swapped = Load().Devices; swapped.Set(Role.HeartRate, watch); TrainerBridgeConfig.SaveDevices(p, swapped);
            var back = Load().Devices;
            check(back.Get(Role.HeartRate) == watch && back.Known.Select(d => d.Key).SequenceEqual(new[] { trainer, watch, strap }), "replacing a device keeps the old one known; no device is listed twice");

            // the other sections survive, and the other writers keep the list
            TrainerBridgeConfig.SaveRider(p, new RiderConfig { FtpW = 250 });
            TrainerBridgeConfig.SaveTraining(p, new TrainingConfig());
            TrainerBridgeConfig.SaveLibraryDir(p, @"C:\lib");
            var kept = Load();
            check(kept.Rider.FtpW == 250 && kept.Devices.Known.Count == 3 && TrainerBridgeConfig.LoadLibraryDir(p) == @"C:\lib", "saving the rider, training and library keeps the known devices (and the reverse)");
            check(log.Exists(l => l.StartsWith("Bridge config " + p + ":") && l.Contains("\"knownDevices\"")), "every load prints the known devices with the rest");

            // a file written before #72 (no knownDevices): what is in the roles becomes known at the first load, so clearing no longer loses it
            string old = Path.Combine(dir, "old", TrainerBridgeConfig.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            File.WriteAllText(old, "{ \"rider\": { \"ftpW\": 200 }, \"devices\": { \"Trainer\": { \"deviceType\": 17, \"deviceNumber\": 44226, \"transmissionType\": 5 }, \"heartRate\": { \"deviceType\": 120, \"deviceNumber\": 7000, \"transmissionType\": 1 } } }");
            UserConfig LoadOld() => TrainerBridgeConfig.Load(old, "", "", () => null, log.Add);
            var o = LoadOld().Devices;
            check(o.Known.Count == 2, "a file from before the list existed: the chosen devices count as known");
            o.Clear(Role.HeartRate); TrainerBridgeConfig.SaveDevices(old, o);
            check(LoadOld().Devices is { } o2 && o2.Get(Role.HeartRate) is null && o2.KnownFor(Role.HeartRate).SequenceEqual(new[] { watch }) && LoadOld().Rider.FtpW == 200,
                "and setting heart rate to none there keeps the watch (and the rider section)");

            // a hand-edited list: other casing and a doubled entry are fine
            File.WriteAllText(old, "{ \"devices\": {}, \"KnownDevices\": [ { \"deviceType\": 120, \"deviceNumber\": 7000, \"transmissionType\": 1 }, { \"deviceType\": 120, \"deviceNumber\": 7000, \"transmissionType\": 1 } ] }");
            var h = LoadOld().Devices;
            check(h.Known.Count == 1 && h.Known[0].Key == watch, "a hand-edited list is read case-insensitively, a doubled entry counts once");
            h.Set(Role.Power, meter); TrainerBridgeConfig.SaveDevices(old, h);
            string t2 = File.ReadAllText(old);
            check(!t2.Contains("\"KnownDevices\"") && t2.Split("\"knownDevices\"").Length == 2 && LoadOld().Devices.Known.Count == 2, "and saved back as one knownDevices section, not two");

            // the drop-downs (the window's pure rule)
            var none = Array.Empty<DeviceKey>();
            var offer = DeviceChoiceRules.Offered(Role.HeartRate, null, none, new[] { trainer, watch, strap });
            check(offer.SequenceEqual(new[] { (watch, ChoiceOrigin.UsedBefore), (strap, ChoiceOrigin.UsedBefore) }),
                "heart rate set to none, no search: both straps used before are offered, the trainer is not");
            offer = DeviceChoiceRules.Offered(Role.HeartRate, watch, none, new[] { trainer, watch, strap });
            check(offer.SequenceEqual(new[] { (watch, ChoiceOrigin.Saved), (strap, ChoiceOrigin.UsedBefore) }), "the saved device comes first and is not repeated as used before");
            offer = DeviceChoiceRules.Offered(Role.HeartRate, watch, new[] { trainer, strap }, new[] { trainer, watch, strap });
            check(offer.SequenceEqual(new[] { (watch, ChoiceOrigin.Saved), (strap, ChoiceOrigin.Heard) }), "after a search: a heard device is listed as heard only, a heard trainer is not offered for heart rate");
            offer = DeviceChoiceRules.Offered(Role.Power, null, new[] { trainer }, new[] { trainer, meter, watch });
            check(offer.SequenceEqual(new[] { (trainer, ChoiceOrigin.Heard), (meter, ChoiceOrigin.UsedBefore) }), "power: the heard trainer, then the power meter used before");
            check(DeviceChoiceRules.Offered(Role.Trainer, null, none, none).Count == 0, "nothing saved, heard or known: only the None line");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
