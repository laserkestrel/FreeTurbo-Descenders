using DescendersBridge.Ant;
using DescendersBridge.Gui;

namespace DescendersBridge;

/// <summary>
/// The window shows who made an ANT+ device (e.g. Tacx) when the device says so, and no "(FE-C)". The page decoders, the
/// manufacturer table, the words the window uses and the settings file (with the manufacturer, and a file from before it). Uses a temp
/// folder, never the real %APPDATA% file.
/// </summary>
public static class DeviceIdentityTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Device identity: the manufacturer of ANT+ devices, no \"(FE-C)\" in the window");

        // common page 80 (0x50): bytes 4-5 manufacturer, 6-7 model, little endian
        byte[] page80 = { 0x50, 0xFF, 0xFF, 0x01, 89, 0x00, 0x3B, 0x0B };
        check(DeviceIdentityPages.Decode(AntDeviceTypes.FitnessEquipment, page80) == new DeviceIdentity(89, 0x0B3B), "trainer, page 80: manufacturer 89, model 2875");
        byte[] page80b = { 0x50, 0xFF, 0xFF, 0x02, 0x07, 0x01, 0x10, 0x00 };
        check(DeviceIdentityPages.Decode(AntDeviceTypes.PowerMeter, page80b) == new DeviceIdentity(263, 16), "power meter, page 80: a manufacturer ID above 255 (263), model 16");
        bool none = true;
        foreach (byte page in new byte[] { 0x10, 0x19, 0x47, 0x51, 0x02, 0xD0 })
        {
            var p = (byte[])page80.Clone(); p[0] = page;
            none &= DeviceIdentityPages.Decode(AntDeviceTypes.FitnessEquipment, p) is null && DeviceIdentityPages.Decode(AntDeviceTypes.PowerMeter, p) is null;
        }
        check(none, "trainer / power meter: other pages (16, 25, 71, 81, 2, 80 with the top bit set) give nothing");
        check(DeviceIdentityPages.Decode(AntDeviceTypes.FitnessEquipment, new byte[] { 0x50, 0, 0, 0, 89 }) is null, "a payload shorter than 8 bytes gives nothing");

        // heart rate / cadence / speed: background page 2, byte 1 = manufacturer; the top bit of byte 0 is the page-change toggle
        byte[] bg = { 0x02, 123, 0x34, 0x12, 0x10, 0x20, 0x30, 72 };
        byte[] bgToggled = { 0x82, 123, 0x34, 0x12, 0x10, 0x20, 0x30, 72 };
        check(DeviceIdentityPages.Decode(AntDeviceTypes.HeartRate, bg) == new DeviceIdentity(123) && DeviceIdentityPages.Decode(AntDeviceTypes.HeartRate, bgToggled) == new DeviceIdentity(123),
            "heart rate, page 2: manufacturer 123 with the toggle bit clear and set, no model number");
        check(DeviceIdentityPages.Decode(AntDeviceTypes.BikeCadence, bg) == new DeviceIdentity(123) && DeviceIdentityPages.Decode(AntDeviceTypes.BikeCadence, bgToggled) == new DeviceIdentity(123)
            && DeviceIdentityPages.Decode(AntDeviceTypes.BikeSpeed, bgToggled) == new DeviceIdentity(123), "cadence and speed sensors, page 2: the same");
        none = true;
        foreach (byte page in new byte[] { 0, 1, 3, 4, 0x80, 0x81, 0x83, 0x84, 0x50 })
        {
            var p = (byte[])bg.Clone(); p[0] = page;
            none &= DeviceIdentityPages.Decode(AntDeviceTypes.HeartRate, p) is null && DeviceIdentityPages.Decode(AntDeviceTypes.BikeCadence, p) is null;
        }
        check(none, "heart rate / cadence: pages 0, 1, 3, 4 (either toggle) and 80 give nothing");
        check(DeviceIdentityPages.Decode(AntDeviceTypes.BikeSpeedCadence, bg) is null && DeviceIdentityPages.Decode(AntDeviceTypes.BikeSpeedCadence, page80) is null
            && DeviceIdentityPages.Decode(99, page80) is null, "speed+cadence (no background pages) and unknown device types give nothing");

        // the tracker keeps the latest one and says so once
        var said = new List<DeviceIdentity>();
        var tracker = new DeviceTracker(new DeviceKey(AntDeviceTypes.FitnessEquipment, 44226, 5)) { IdentityChanged = (_, id) => said.Add(id) };
        tracker.Update(new byte[] { 0x10, 25, 0, 0, 0, 0, 0xFF, 0 });
        check(tracker.Identity is null && said.Count == 0, "the tracker: no manufacturer before its page is heard");
        tracker.Update(page80); tracker.Update(new byte[] { 0x19, 0, 90, 0, 0, 200, 0, 0 }); tracker.Update(page80);
        check(tracker.Identity == new DeviceIdentity(89, 2875) && said.Count == 1 && tracker.Messages == 4, "the tracker keeps the manufacturer through other pages and reports it once");

        // the table
        check(Manufacturers.Name(89) == "Tacx" && Manufacturers.Name(1) == "Garmin" && Manufacturers.Name(32) == "Wahoo" && Manufacturers.Name(263) == "Favero",
            "the table: known IDs give the brand (89 Tacx, 1 Garmin, 32 Wahoo, 263 Favero)");
        check(Manufacturers.Name(0) is null && Manufacturers.Name(255) is null && Manufacturers.Name(65535) is null && Manufacturers.Name(null) is null, "the table: an unknown ID (or none) has no name");

        // the words
        var trainer = new DeviceKey(AntDeviceTypes.FitnessEquipment, 44226, 5);
        var watch = new DeviceKey(AntDeviceTypes.HeartRate, 7000, 1);
        var cad = new DeviceKey(AntDeviceTypes.BikeCadence, 28600, 1);
        // a drop-down line is the brand (when known) and the number only; the drop-down's label says what kind it is
        check(DeviceLabels.Describe(trainer, 89) == "Tacx 44226", "drop-down, known brand: \"Tacx 44226\"");
        check(DeviceLabels.Describe(trainer, null) == "device 44226" && DeviceLabels.Describe(trainer, 65000) == "device 44226",
            "drop-down, no brand or one we do not list: \"device 44226\" (no brand, no kind, no \"(FE-C)\")");
        check(DeviceLabels.Describe(watch, 1) == "Garmin 7000" && DeviceLabels.Describe(watch, null) == "device 7000"
            && DeviceLabels.Describe(cad, 32) == "Wahoo 28600" && DeviceLabels.Describe(cad, null) == "device 28600", "the same for a heart-rate strap and a cadence sensor");
        check(DeviceLabels.Describe(trainer, 89) == DeviceLabels.Short(trainer.DeviceNumber, 89), "a drop-down names a device as the Ride tab does");
        check(new byte[] { 11, 17, 120, 121, 122, 123, 99 }.All(t => !DeviceLabels.Kind(t).Contains("FE-C") && !DeviceLabels.Kind(t).Contains('(')),
            "no device kind the window shows carries a protocol name");
        check(AntDeviceTypes.Name(AntDeviceTypes.FitnessEquipment).Contains("FE-C"), "the log and the console keep \"Trainer (FE-C)\"");
        check(DeviceLabels.Short(44226, 89) == "Tacx 44226" && DeviceLabels.Short(44226, null) == "device 44226" && DeviceLabels.Short(44226, 65000) == "device 44226",
            "Ride tab, short form: \"Tacx 44226\", unknown \"device 44226\"");
        check(DeviceLabels.Make(89) == "Tacx" && DeviceLabels.Make(null) == "" && DeviceLabels.Make(65000) == "", "found list, Make cell: the brand, blank when unknown");
        check(StripRules.Trainer(RideController.State.Stopped, false, false, Health.NotChosen, 44226, 89).Text == "Trainer: Tacx 44226, not connected"
            && StripRules.Trainer(RideController.State.Stopped, false, false, Health.NotChosen, 44226).Text == "Trainer: device 44226, not connected", "the status strip names the saved trainer with its brand when known");
        check(new DeviceIdentity(89, 2875).LogText == "manufacturer 89 (Tacx), model 2875" && new DeviceIdentity(200).LogText == "manufacturer 200"
            && new DeviceIdentity(65000, 7).LogText == "manufacturer 65000, model 7", "the log prints the numbers always, the brand when known");
        check(new FoundDevice(trainer, 1, 4, 0, "", new DeviceIdentity(89, 1)).ManufacturerId == 89 && new FoundDevice(trainer, 1, 4, 0, "").ManufacturerId is null,
            "a found device carries the manufacturer when the search heard it");
        check(Manufacturers.Name(DemoLiveSource.TrainerMake) is not null && Manufacturers.Name(DemoLiveSource.HrMake) is not null, "the demo trainer and strap have a brand (the snapshots show it)");

        // the settings file
        string dir = Path.Combine(Path.GetTempPath(), "descenders_identity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new List<string>();
            string p = Path.Combine(dir, TrainerBridgeConfig.FileName);
            UserConfig Load(string path) => TrainerBridgeConfig.Load(path, "", "", () => null, log.Add);

            var prefs = Load(p).Devices;
            prefs.Set(Role.Trainer, trainer, 89); prefs.Set(Role.HeartRate, watch);
            TrainerBridgeConfig.SaveDevices(p, prefs);
            string text = File.ReadAllText(p);
            var back = Load(p).Devices;
            check(back.ManufacturerOf(trainer) == 89 && back.Roles["Trainer"].ManufacturerId == 89 && back.Known[0].ManufacturerId == 89 && text.Contains("\"manufacturerId\": 89"),
                "round trip: the trainer's manufacturer is saved in devices and knownDevices and read back");
            check(back.ManufacturerOf(watch) is null && text.Split("\"manufacturerId\"").Length == 3, "a device with no known manufacturer has no manufacturerId entry in the file");

            // already saved devices gain the brand without being chosen again; nothing else is added
            int n = TrainerBridgeConfig.LearnManufacturers(p, new[] { (watch, (ushort)1), (cad, (ushort)32), (trainer, (ushort)89) }, log.Add);
            back = Load(p).Devices;
            check(n == 1 && back.ManufacturerOf(watch) == 1 && back.Get(Role.HeartRate) == watch && back.Known.Count == 2 && back.ManufacturerOf(cad) is null,
                $"a saved device heard with its manufacturer gains it in the file; one not saved is not added, one already right is not rewritten (changed {n})");
            check(log.Exists(l => l.Contains("Garmin") && l.Contains("manufacturer 1")), "and the log says so");
            DateTime stamp = File.GetLastWriteTimeUtc(p);
            check(TrainerBridgeConfig.LearnManufacturers(p, new[] { (watch, (ushort)1) }, log.Add) == 0 && File.GetLastWriteTimeUtc(p) == stamp, "hearing the same again does not write the file");

            // choosing a known device again without hearing its manufacturer keeps it; setting the role to none keeps it too
            back.Clear(Role.Trainer); TrainerBridgeConfig.SaveDevices(p, back);
            var again = Load(p).Devices;
            check(again.Get(Role.Trainer) is null && again.ManufacturerOf(trainer) == 89, "a role set to none: the known device keeps its manufacturer");
            again.Set(Role.Trainer, trainer); TrainerBridgeConfig.SaveDevices(p, again);
            check(Load(p).Devices.Roles["Trainer"].ManufacturerId == 89, "chosen again without a search: the manufacturer comes from the known device");

            // a file from before this change loads unchanged, and saves without inventing a manufacturer
            string old = Path.Combine(dir, "old", TrainerBridgeConfig.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            File.WriteAllText(old, "{ \"rider\": { \"ftpW\": 200 }, \"devices\": { \"Trainer\": { \"deviceType\": 17, \"deviceNumber\": 44226, \"transmissionType\": 5, \"name\": \"Trainer (FE-C)\" } },"
                + " \"knownDevices\": [ { \"deviceType\": 17, \"deviceNumber\": 44226, \"transmissionType\": 5, \"name\": \"Trainer (FE-C)\" }, { \"deviceType\": 120, \"deviceNumber\": 7000, \"transmissionType\": 1 } ] }");
            var o = Load(old);
            check(o.Rider.FtpW == 200 && o.Devices.Get(Role.Trainer) == trainer && o.Devices.Known.Select(d => d.Key).SequenceEqual(new[] { trainer, watch })
                && o.Devices.ManufacturerOf(trainer) is null, "a settings file from before the manufacturer existed loads as before, no manufacturer");
            TrainerBridgeConfig.SaveDevices(old, o.Devices);
            check(!File.ReadAllText(old).Contains("manufacturerId") && Load(old).Devices.Known.Count == 2, "and saving it writes no manufacturerId");
            check(TrainerBridgeConfig.LearnManufacturers(old, new[] { (trainer, (ushort)89) }, log.Add) == 1 && Load(old) is { } o2 && o2.Devices.Roles["Trainer"].ManufacturerId == 89
                && o2.Devices.Known[0].ManufacturerId == 89 && o2.Rider.FtpW == 200, "its saved trainer gains the brand when heard (role and known entry), the rider section is kept");
            check(TrainerBridgeConfig.LearnManufacturers(Path.Combine(dir, "missing.json"), new[] { (trainer, (ushort)89) }, log.Add) == 0 && !File.Exists(Path.Combine(dir, "missing.json")),
                "no settings file: nothing is learned and none is made");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
