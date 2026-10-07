using DescendersBridge.Ant;
using DescendersBridge.Bluetooth;
using DescendersBridge.Gui;

namespace DescendersBridge;

/// <summary>
/// A chosen device is ANT+ or Bluetooth. The settings file (an old file without `transport` is ANT+, a Bluetooth device
/// round-trips, `knownDevices` keeps both kinds), and the rule the ride opens its radios by: the stick and the ANT+ key only when a chosen
/// device is ANT+, Bluetooth only when the trainer is a Bluetooth one, and a PC with no Bluetooth says so in one plain sentence.
/// Uses a temp folder and fakes, never the real %APPDATA% file, a stick or a radio. Run by `turbotrainer-bridge selftest`.
/// </summary>
public static class BluetoothSettingsTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Bluetooth settings: a chosen device is ANT+ or Bluetooth");
        string dir = Path.Combine(Path.GetTempPath(), "descenders_bt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new List<string>();
            string p = Path.Combine(dir, TrainerBridgeConfig.FileName);
            UserConfig Load(string path) => TrainerBridgeConfig.Load(path, "", "", () => null, log.Add);
            DeviceKey antTrainer = new(17, 44226, 5), watch = new(120, 7000, 1);
            DeviceKey kickr = DeviceKey.Bluetooth(AntDeviceTypes.FitnessEquipment, 0xC123456789AB);

            // a file from before this issue: no transport anywhere
            string old = Path.Combine(dir, "old.json");
            string oldText = "{ \"rider\": { \"ftpW\": 200 }, \"devices\": { \"Trainer\": { \"deviceType\": 17, \"deviceNumber\": 44226, \"transmissionType\": 5, \"name\": \"Trainer (FE-C)\", \"manufacturerId\": 89 } },"
                + " \"knownDevices\": [ { \"deviceType\": 17, \"deviceNumber\": 44226, \"transmissionType\": 5, \"name\": \"Trainer (FE-C)\", \"manufacturerId\": 89 } ] }";
            File.WriteAllText(old, oldText);
            var o = Load(old).Devices;
            check(o.Get(Role.Trainer) == antTrainer && !o.Get(Role.Trainer)!.Value.IsBluetooth && o.NeedsAnt && !o.NeedsBluetooth && o.ManufacturerOf(antTrainer) == 89,
                "an old file without `transport` loads as ANT+, unchanged (trainer 17 / 44226 / 5, its manufacturer kept)");
            TrainerBridgeConfig.SaveDevices(old, o);
            string resaved = File.ReadAllText(old);
            check(!resaved.Contains("transport") && !resaved.Contains("address") && resaved.Contains("44226"), "and saving it again writes no `transport` or `address` for an ANT+ device");
            File.WriteAllText(old, oldText.Replace("\"deviceType\": 17, \"deviceNumber\": 44226, \"transmissionType\": 5, \"name\": \"Trainer (FE-C)\", \"manufacturerId\": 89 } },",
                "\"transport\": \"ant\", \"deviceType\": 17, \"deviceNumber\": 44226, \"transmissionType\": 5 } },"));
            check(Load(old).Devices is { NeedsAnt: true } a2 && a2.Get(Role.Trainer) == antTrainer, "`transport` \"ant\" written out is ANT+ too");

            // a Bluetooth trainer round-trips
            var prefs = Load(p).Devices;
            prefs.Set(Role.Trainer, kickr, name: "KICKR CORE 1234");
            check(prefs.Get(Role.Trainer) == kickr && !prefs.NeedsAnt && prefs.NeedsBluetooth, "a Bluetooth trainer chosen alone: nothing ANT+ is needed");
            TrainerBridgeConfig.SaveDevices(p, prefs);
            string text = File.ReadAllText(p);
            check(text.Contains("\"transport\": \"bluetooth\"") && text.Contains("\"address\": \"C1:23:45:67:89:AB\"") && text.Contains("\"name\": \"KICKR CORE 1234\""),
                "the file holds its transport, address and advertised name");
            var back = Load(p).Devices;
            check(back.Get(Role.Trainer) == kickr && back.Get(Role.Trainer)!.Value.IsBluetooth && back.NameOf(kickr) == "KICKR CORE 1234" && !back.NeedsAnt,
                "and it is read back as the same Bluetooth device, by address");
            check(log.Exists(l => l.StartsWith("Bridge config " + p + ":") && l.Contains("\"transport\": \"bluetooth\"")), "every load prints it with the rest");

            // a mix, and knownDevices keeps both kinds
            back.Set(Role.HeartRate, watch);
            check(back.NeedsAnt && back.NeedsBluetooth && back.AntOnly() is { } antOnly && antOnly.Get(Role.Trainer) is null && antOnly.Get(Role.HeartRate) == watch,
                "Bluetooth trainer + ANT+ heart rate: both are needed, and the ANT+ side is given the strap only");
            TrainerBridgeConfig.SaveDevices(p, back);
            var mixed = Load(p).Devices;
            mixed.Set(Role.Trainer, antTrainer);
            TrainerBridgeConfig.SaveDevices(p, mixed);
            var both = Load(p).Devices;
            check(both.Get(Role.Trainer) == antTrainer && both.Known.Select(d => d.Key).SequenceEqual(new[] { kickr, watch, antTrainer }),
                "replacing the Bluetooth trainer with an ANT+ one keeps both in knownDevices");
            check(both.KnownFor(Role.Trainer).SequenceEqual(new[] { kickr, antTrainer }) && both.KnownFor(Role.Power).SequenceEqual(new[] { antTrainer }) && both.NameOf(kickr) == "KICKR CORE 1234",
                "the Bluetooth trainer is offered again as the trainer, with its name; only the ANT+ trainer is offered as a separate power source");
            both.Clear(Role.Trainer); both.Clear(Role.HeartRate);
            both.Set(Role.Trainer, kickr);   // chosen again from the known list: no name given
            check(both.Roles["Trainer"] is { IsBluetooth: true, Name: "KICKR CORE 1234", Address: "C1:23:45:67:89:AB" }, "choosing a known Bluetooth device again keeps its address and name");
            TrainerBridgeConfig.SaveRider(p, new RiderConfig { FtpW = 250 });
            check(Load(p).Devices.Known.Count == 3 && Load(p).Rider.FtpW == 250, "saving another section keeps both kinds of device");

            // hand-edited
            string hand = Path.Combine(dir, "hand.json");
            File.WriteAllText(hand, "{ \"devices\": { \"trainer\": { \"transport\": \"Bluetooth\", \"address\": \"c1-23-45-67-89-ab\", \"name\": \"My trainer\" }, \"HeartRate\": { \"transport\": \"bluetooth\", \"address\": \"nonsense\" } } }");
            var h = Load(hand).Devices;
            check(h.Get(Role.Trainer) == kickr && h.Get(Role.HeartRate) is null && h.Known.Count == 1,
                "a hand-written Bluetooth trainer needs only transport and address (any case); an entry whose address cannot be read is left out");

            Console.WriteLine("Bluetooth settings: the ride opens only the radios its devices need");
            {
                var radio = new FakeFtmsRadio();
                int antOpens = 0;
                var btOnly = new DevicePreferences(); btOnly.Set(Role.Trainer, kickr, name: "KICKR CORE 1234");
                using var src = Ride.OpenSourceAsync(btOnly, _ => { antOpens++; throw new AntStickException("ANT+ stick not available: test", null); }, () => radio, _ => { }).GetAwaiter().GetResult();
                check(antOpens == 0 && src.AntSide is null && src.BluetoothSide is not null && !src.LinkLost && src.Control is not null,
                    "only Bluetooth chosen: the stick is not opened and the ANT+ key is not asked for; no AntStickException");

                var mix = new DevicePreferences(); mix.Set(Role.Trainer, kickr); mix.Set(Role.HeartRate, watch);
                DevicePreferences? given = null;
                using var src2 = Ride.OpenSourceAsync(mix, a => { antOpens++; given = a; return new SimTrainer(new SimOptions()); }, () => radio, _ => { }).GetAwaiter().GetResult();
                check(antOpens == 1 && given is not null && given.Get(Role.Trainer) is null && given.Get(Role.HeartRate) == watch && src2.AntSide is not null && src2.BluetoothSide is not null,
                    "a mix: the ANT+ side is opened once, for the heart-rate strap alone; the trainer is the Bluetooth one");

                var antPrefs = new DevicePreferences(); antPrefs.Set(Role.Trainer, antTrainer);
                int radios = 0;
                bool threw = false;
                try { Ride.OpenSourceAsync(antPrefs, _ => throw new AntStickException("ANT+ stick not available: test", null), () => { radios++; return radio; }, _ => { }).GetAwaiter().GetResult(); }
                catch (AntStickException) { threw = true; }
                check(threw && radios == 0, "only ANT+ chosen: as before (no stick = AntStickException), and Bluetooth is never touched");

                var none = new FakeFtmsRadio { Adapter = new BleAdapterInfo(false) };
                string? said = null; antOpens = 0;
                try { Ride.OpenSourceAsync(mix, _ => { antOpens++; return new SimTrainer(new SimOptions()); }, () => none, _ => { }).GetAwaiter().GetResult(); }
                catch (BluetoothUnavailableException ex) { said = ex.Message; }
                check(said == "This PC has no Bluetooth" && antOpens == 0, "a Bluetooth trainer chosen on a PC with no Bluetooth: one plain message, and the stick is not opened first");
                check(FriendlyText.For(new BluetoothUnavailableException(said ?? "")).Title == "This PC has no Bluetooth." && FriendlyText.For(new Exception("outer", new BluetoothUnavailableException("x"))).Title == "x.",
                    "the window shows that sentence as its banner");
                check(ConnectRules.KeyOk(needsAnt: false, keyPresent: false) && !ConnectRules.KeyOk(needsAnt: true, keyPresent: false) && ConnectRules.KeyOk(needsAnt: true, keyPresent: true),
                    "the window asks for the ANT+ key only when a saved device is ANT+");
            }

            Console.WriteLine("Bluetooth settings: the console setup offers Bluetooth trainers");
            {
                var p2 = new DevicePreferences(); p2.Set(Role.Trainer, antTrainer); p2.Set(Role.HeartRate, watch);
                var ant = new[] { new DeviceTracker(antTrainer), new DeviceTracker(watch) };
                var bt = new[] { new BleAdvert(0xC123456789AB, "KICKR CORE 1234", -60, 12, new[] { Ftms.Service }) };
                var forTrainer = Setup.Candidates(Role.Trainer, p2, ant, bt);
                check(forTrainer.Count == 2 && forTrainer[0].Key == antTrainer && forTrainer[1].Key == kickr && forTrainer[1].Name == "KICKR CORE 1234" && forTrainer[1].Text.Contains("C1:23:45:67:89:AB"),
                    "the trainer's list: the ANT+ trainer heard, then the Bluetooth trainer heard (name and address)");
                check(Setup.Candidates(Role.HeartRate, p2, ant, bt).Select(c => c.Key).SequenceEqual(new[] { watch }) && Setup.Candidates(Role.Power, p2, ant, bt).Select(c => c.Key).SequenceEqual(new[] { antTrainer }),
                    "a Bluetooth trainer is not offered for heart rate, nor as a separate power source");
                p2.Set(Role.Trainer, kickr, name: "KICKR CORE 1234");
                var later = Setup.Candidates(Role.Trainer, p2, Array.Empty<DeviceTracker>(), Array.Empty<BleAdvert>());
                check(later.Count == 1 && later[0].Key == antTrainer && later[0].Text.Contains("used before"), "nothing heard: the ANT+ trainer used before is still offered (the current Bluetooth one is the heading)");
                check(Setup.Label(kickr, p2) == "KICKR CORE 1234 (Bluetooth C1:23:45:67:89:AB)" && kickr.ToString() == "Trainer (Bluetooth) C1:23:45:67:89:AB", "and a Bluetooth device is named by its name and address");
                var noRadio = Setup.BluetoothTrainersAsync(new FakeFtmsRadio { Adapter = new BleAdapterInfo(false) }, 1, log.Add).GetAwaiter().GetResult();
                check(noRadio.Count == 0 && log.Exists(l => l.StartsWith("Bluetooth not searched: This PC has no Bluetooth")), "a PC with no Bluetooth: the search says so in one line and carries on with ANT+");
                var found = Setup.BluetoothTrainersAsync(new FakeFtmsRadio(), 0, log.Add).GetAwaiter().GetResult();
                check(found.Count == 1 && found[0].Name == FakeFtmsRadio.TrainerName, "a radio with a trainer in range: found by its Fitness Machine Service advertisement");
            }

            Console.WriteLine("Bluetooth settings: the window's words for a Bluetooth trainer");
            check(DeviceLabels.Bluetooth(kickr, "KICKR CORE 1234") == "KICKR CORE 1234 (Bluetooth)" && DeviceLabels.Bluetooth(kickr, null) == "Bluetooth C1:23:45:67:89:AB",
                "a drop-down line: its name, or its address when it has none (no kind: the drop-down's label says it)");
            check(StripRules.Trainer(RideController.State.Stopped, false, false, Health.NotChosen, 0, null, "KICKR CORE 1234").Text == "Trainer: KICKR CORE 1234, not connected",
                "the status strip names it (it has no device number)");
            check(HealthRules.Classify(new LinkInfo(kickr, 30, 0.9), 1.0) == Health.Good && HealthRules.Classify(new LinkInfo(kickr, 30, 2.5), 1.0) == Health.Weak
                && HealthRules.Classify(new LinkInfo(kickr, 30, 4.0), 0.2) == Health.Lost && HealthRules.Classify(new LinkInfo(kickr, 0, double.PositiveInfinity), null) == Health.Searching
                && HealthRules.Classify(new LinkInfo(antTrainer, 30, 0.9), 1.0) == Health.Weak,
                "a Bluetooth trainer sending once a second is healthy (an ANT+ device at that rate is weak); a gap is weak, then lost; never heard = searching");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
