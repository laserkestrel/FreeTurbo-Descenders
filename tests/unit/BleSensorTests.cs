using DescendersBridge.Ant;
using DescendersBridge.Bluetooth;

namespace DescendersBridge;

/// <summary>
/// The Bluetooth sensor bytes, pure. Heart Rate Measurement (8-bit, 16-bit, contact), CSC Measurement (with and without wheel
/// data), cadence from crank data (across a revolution-count wrap and an event-time wrap, stop detection, then absent), Cycling Power
/// Measurement (with and without crank data, the fields before it stepped over), and the small rules around them (which service a role
/// listens to, what a scan offers for which role, the heart-rate words, the settings file). No radio. Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class BleSensorTests
{
    private static bool Near(double? a, double b, double within = 0.01) => a is { } x && Math.Abs(x - b) < within;

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Bluetooth sensors: Heart Rate Measurement decode");
        {
            var m = BleSensors.DecodeHeartRate(new byte[] { 0x00, 0x48 });
            check(m is { Bpm: 72, Contact: null } && BleSensors.Valid(m.Value), "8-bit value (flags bit 0 clear): 72 bpm; the sensor does not report contact");
            m = BleSensors.DecodeHeartRate(new byte[] { 0x01, 0x2C, 0x01 });
            check(m is { Bpm: 300 }, "16-bit value (flags bit 0 set): 0x012C = 300");
            m = BleSensors.DecodeHeartRate(new byte[] { 0x16, 0x50, 0xEE, 0xEE });
            check(m is { Bpm: 80, Contact: true } && BleSensors.Valid(m.Value), "contact supported and detected, RR intervals after the value: 80 bpm");
            m = BleSensors.DecodeHeartRate(new byte[] { 0x04, 0x47 });
            check(m is { Contact: false } && !BleSensors.Valid(m.Value), "contact supported and NOT detected: no heart rate is taken from it");
            m = BleSensors.DecodeHeartRate(new byte[] { 0x02, 0x47 });
            check(m is { Bpm: 71, Contact: null } && BleSensors.Valid(m.Value), "the contact bit without the \"supported\" bit says nothing: the value counts");
            check(BleSensors.DecodeHeartRate(new byte[] { 0x00, 0x00 }) is { } zero && !BleSensors.Valid(zero), "0 bpm is not a heart rate (absent, never zero)");
            check(BleSensors.DecodeHeartRate(new byte[] { 0x01, 0x48 }) is null && BleSensors.DecodeHeartRate(new byte[] { 0x00 }) is null && BleSensors.DecodeHeartRate(Array.Empty<byte>()) is null,
                "a packet shorter than its flags say is not used");
        }

        Console.WriteLine("Bluetooth sensors: CSC Measurement decode");
        {
            var m = BleSensors.DecodeCsc(new byte[] { 0x02, 0x10, 0x00, 0x00, 0x04 });
            check(m is { Crank: { Revolutions: 16, EventTime1024: 1024 } }, "crank data alone: 16 revolutions, event time 1024");
            m = BleSensors.DecodeCsc(new byte[] { 0x03, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0x11, 0x00, 0x00, 0x08 });
            check(m is { Crank: { Revolutions: 17, EventTime1024: 2048 } }, "wheel data first (uint32 + uint16 = 6 bytes, stepped over), then the crank data");
            m = BleSensors.DecodeCsc(new byte[] { 0x01, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE });
            check(m is { Crank: null }, "wheel data alone (a speed sensor): a valid packet with no crank data");
            check(BleSensors.DecodeCsc(new byte[] { 0x03, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0x11, 0x00 }) is null && BleSensors.DecodeCsc(new byte[] { 0x02, 0x10 }) is null
                && BleSensors.DecodeCsc(Array.Empty<byte>()) is null, "a packet shorter than its flags say is not used");
        }

        Console.WriteLine("Bluetooth sensors: cadence from crank revolutions and event time");
        {
            var c = new CrankCadence();
            c.Add(new CrankData(100, 1000), 0.0);
            check(c.Rpm(0.0) is null, "one packet is not a rate yet: absent");
            c.Add(new CrankData(101, 2024), 1.0);
            check(Near(c.Rpm(1.0), 60), "1 revolution in 1024/1024 s = 60 rpm");
            c.Add(new CrankData(104, 4072), 3.0);
            check(Near(c.Rpm(3.0), 90), "3 revolutions in 2048/1024 s = 90 rpm");

            var w = new CrankCadence();
            w.Add(new CrankData(65535, 1000), 0.0);
            w.Add(new CrankData(1, 3048), 2.0);
            check(Near(w.Rpm(2.0), 60), "across a revolution-count wrap (65535 -> 1 = 2 revolutions in 2 s): 60 rpm");

            var t = new CrankCadence();
            t.Add(new CrankData(10, 65000), 0.0);
            t.Add(new CrankData(11, 488), 1.0);
            check(Near(t.Rpm(1.0), 60), "across an event-time wrap (65000 -> 488 = 1024): 60 rpm");
            t.Add(new CrankData(13, 1171), 1.7);
            check(Near(t.Rpm(1.7), 2 * 60.0 * 1024 / 683), "and on after it (2 revolutions in 683/1024 s = 180 rpm)");

            // stop detection: the sensor keeps sending the same counters
            var s = new CrankCadence();
            s.Add(new CrankData(50, 0), 0.0);
            s.Add(new CrankData(51, 1024), 1.0);
            s.Add(new CrankData(51, 1024), 2.0);
            s.Add(new CrankData(51, 1024), 3.0);
            check(Near(s.Rpm(3.9), 60), "no new crank event for under 3 s: the last cadence stands (slow pedalling is not a stop)");
            s.Add(new CrankData(51, 1024), 4.5);
            check(s.Rpm(4.5) == 0.0, "no new crank event for over 3 s: 0 rpm");
            check(s.Rpm(9.4) == 0.0 && s.Rpm(9.6) is null, "then the sensor goes quiet: absent 5 s after its last packet (never zero for ever)");
            s.Add(new CrankData(52, 1024 + 5632), 6.5);
            check(Near(s.Rpm(6.5), 60.0 * 1024 / 5632), "pedalling again: the first event after the stop counts (1 revolution in 5.5 s)");
            s.Add(new CrankData(53, 1024 + 5632 + 768), 7.25);
            check(Near(s.Rpm(7.25), 80), "and the next one is the real cadence again (80 rpm)");

            s.Add(new CrankData(600, 1024 + 5632 + 768 + 100), 7.5);
            check(Near(s.Rpm(7.5), 80), "a jump that cannot be pedalling (547 revolutions in 0.1 s) is not taken as a cadence");
            s.Add(new CrankData(601, (ushort)(1024 + 5632 + 768 + 100 + 1024)), 8.5);
            check(Near(s.Rpm(8.5), 60), "the counters after the jump are the new base");

            var p = new CrankCadence();
            p.Add(new CrankData(5, 100), 0.0);
            p.Add(new CrankData(5, 100), 1.0);
            p.Add(new CrankData(6, 200), 100.0);   // the event time wrapped more than once meanwhile: 100/1024 s would be 614 rpm
            check(p.Rpm(100.0) == 0.0, "the first event after a long pause (the 64 s event clock has wrapped) only sets the counters again");
            p.Add(new CrankData(7, 200 + 768), 100.75);
            check(Near(p.Rpm(100.75), 80), "and the one after it gives the cadence");
        }

        Console.WriteLine("Bluetooth sensors: Cycling Power Measurement decode");
        {
            var m = BleSensors.DecodePower(new byte[] { 0x00, 0x00, 0xFA, 0x00 });
            check(m is { PowerW: 250, Crank: null }, "no optional fields: 250 W, no crank data");
            m = BleSensors.DecodePower(new byte[] { 0x00, 0x00, 0xF6, 0xFF });
            check(m is { PowerW: -10 }, "power is signed (-10 W)");
            m = BleSensors.DecodePower(new byte[] { 0x20, 0x00, 0x2C, 0x01, 0x10, 0x00, 0x00, 0x04 });
            check(m is { PowerW: 300, Crank: { Revolutions: 16, EventTime1024: 1024 } }, "crank revolution data (bit 5): 300 W, 16 revolutions, event time 1024");
            // pedal balance (1 byte), accumulated torque (2), wheel data (6) before the crank data, each filled with 0xEE so a wrong length
            // would land in the crank data; bits 1 and 3 (balance reference, torque source) carry no field
            m = BleSensors.DecodePower(new byte[] { 0x3F, 0x00, 0x2C, 0x01, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0x11, 0x00, 0x00, 0x08 });
            check(m is { PowerW: 300, Crank: { Revolutions: 17, EventTime1024: 2048 } }, "pedal balance, accumulated torque and wheel data are stepped over by their lengths (1 + 2 + 6 bytes)");
            m = BleSensors.DecodePower(new byte[] { 0x10, 0x00, 0x64, 0x00, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE });
            check(m is { PowerW: 100, Crank: null }, "wheel data and no crank data (a hub power meter): power only");
            m = BleSensors.DecodePower(new byte[] { 0x20, 0x0F, 0x64, 0x00, 0x10, 0x00, 0x00, 0x04, 0xEE, 0xEE, 0xEE });
            check(m is { PowerW: 100, Crank: { Revolutions: 16 } }, "fields after the crank data are not read");
            check(BleSensors.DecodePower(new byte[] { 0x20, 0x00, 0x2C, 0x01, 0x10, 0x00 }) is null && BleSensors.DecodePower(new byte[] { 0x00, 0x00, 0xFA }) is null,
                "a packet shorter than its flags say is not used");
        }

        Console.WriteLine("Bluetooth sensors: which device does which job");
        {
            check(BleSensors.Feed(Role.HeartRate, AntDeviceTypes.HeartRate) == BleFeed.HeartRate && BleSensors.Feed(Role.Power, AntDeviceTypes.PowerMeter) == BleFeed.Power
                && BleSensors.Feed(Role.Cadence, AntDeviceTypes.BikeCadence) == BleFeed.Csc && BleSensors.Feed(Role.Cadence, AntDeviceTypes.PowerMeter) == BleFeed.Power
                && BleSensors.Feed(Role.Trainer, AntDeviceTypes.FitnessEquipment) == BleFeed.None,
                "heart rate = Heart Rate Measurement, power = Cycling Power Measurement, cadence = CSC Measurement, or a power meter's crank data");

            var strap = new BleAdvert(0x0000A1A1A1A1, "Polar H10", -50, 9, new[] { BleSensors.HeartRateService });
            var csc = new BleAdvert(0x0000B2B2B2B2, "Wahoo CADENCE", -70, 5, new[] { BleSensors.CscService });
            var meter = new BleAdvert(0x0000C3C3C3C3, "Assioma", -65, 7, new[] { BleSensors.PowerService });
            var both = new BleAdvert(0x0000D4D4D4D4, "Stages", -66, 7, new[] { BleSensors.PowerService, BleSensors.CscService });
            var kickr = new BleAdvert(0xC123456789AB, "KICKR CORE 1234", -60, 12, new[] { Ftms.Service, BleSensors.PowerService });
            var phones = new BleAdvert(0x0000E5E5E5E5, "Headphones", -40, 30, Array.Empty<Guid>());
            var all = new[] { phones, csc, kickr, meter, strap, both };

            check(BleRules.KeyFor(Role.HeartRate, strap) == DeviceKey.Bluetooth(AntDeviceTypes.HeartRate, strap.Address) && BleRules.KeyFor(Role.Power, strap) is null
                && BleRules.KeyFor(Role.Cadence, strap) is null && BleRules.KeyFor(Role.Trainer, strap) is null, "a device advertising 0x180D is offered for heart rate only");
            check(BleRules.KeyFor(Role.Cadence, csc) == DeviceKey.Bluetooth(AntDeviceTypes.BikeCadence, csc.Address) && BleRules.KeyFor(Role.Power, csc) is null, "0x1816 is a cadence sensor");
            check(BleRules.KeyFor(Role.Power, meter) == DeviceKey.Bluetooth(AntDeviceTypes.PowerMeter, meter.Address) && BleRules.KeyFor(Role.Cadence, meter) == BleRules.KeyFor(Role.Power, meter),
                "0x1818 is a power meter, also offered for cadence (its crank data)");
            check(BleRules.KeyFor(Role.Cadence, both) == DeviceKey.Bluetooth(AntDeviceTypes.BikeCadence, both.Address) && BleRules.KeyFor(Role.Power, both) == DeviceKey.Bluetooth(AntDeviceTypes.PowerMeter, both.Address),
                "a power meter that also advertises 0x1816 gives cadence through that service");
            check(BleRules.KeyFor(Role.Trainer, kickr) == kickr.TrainerKey && BleRules.KeyFor(Role.Power, kickr) is null && BleRules.KeyFor(Role.Cadence, kickr) is null,
                "a trainer that also advertises 0x1818 is the trainer only: its power and cadence come with that job");
            check(BleRules.Sensors(all).Select(a => a.Name).SequenceEqual(new[] { "Polar H10", "Assioma", "Stages", "Wahoo CADENCE" })
                && BleRules.Devices(all).Select(a => a.Name).SequenceEqual(new[] { "KICKR CORE 1234", "Polar H10", "Assioma", "Stages", "Wahoo CADENCE" }),
                "a scan's list: the trainers, then the sensors nearest first; anything else (headphones) is left out");
            check(BleRules.Describe(strap).StartsWith("Heart rate (Bluetooth)") && BleRules.Describe(strap).Contains("Polar H10") && BleRules.Describe(strap).Contains("00:00:A1:A1:A1:A1")
                && BleRules.Describe(both).StartsWith("Power meter + Cadence sensor (Bluetooth)") && BleRules.Describe(kickr).StartsWith("Trainer (Bluetooth)"),
                "the console names each by kind, name and address");

            // the console setup
            DeviceKey antTrainer = new(17, 44226, 5), antWatch = new(120, 7000, 1);
            var prefs = new DevicePreferences(); prefs.Set(Role.Trainer, antTrainer); prefs.Set(Role.HeartRate, antWatch);
            var ant = new[] { new DeviceTracker(antTrainer), new DeviceTracker(antWatch) };
            var hr = Setup.Candidates(Role.HeartRate, prefs, ant, all);
            check(hr.Count == 2 && hr[0].Key == antWatch && hr[1].Key == DeviceKey.Bluetooth(AntDeviceTypes.HeartRate, strap.Address) && hr[1].Name == "Polar H10",
                "setup, heart rate: the ANT+ watch heard, then the Bluetooth strap heard (with its name)");
            check(Setup.Candidates(Role.Power, prefs, ant, all).Select(c => c.Key).SequenceEqual(new[] { antTrainer, DeviceKey.Bluetooth(11, meter.Address), DeviceKey.Bluetooth(11, both.Address) })
                && Setup.Candidates(Role.Cadence, prefs, ant, all).Select(c => c.Key).SequenceEqual(new[] { antTrainer, DeviceKey.Bluetooth(122, csc.Address), DeviceKey.Bluetooth(11, meter.Address), DeviceKey.Bluetooth(122, both.Address) }),
                "setup, power and cadence: the Bluetooth power meters and cadence sensors are offered beside the ANT+ trainer");
            check(Setup.Candidates(Role.Trainer, prefs, ant, all).Select(c => c.Key).SequenceEqual(new[] { antTrainer, kickr.TrainerKey }), "setup, trainer: as before, the sensors are not offered there");

            check(Setup.BluetoothKeyFor(Role.HeartRate, strap.Address, prefs).DeviceType == AntDeviceTypes.HeartRate && Setup.BluetoothKeyFor(Role.Power, meter.Address, prefs).DeviceType == AntDeviceTypes.PowerMeter
                && Setup.BluetoothKeyFor(Role.Cadence, csc.Address, prefs).DeviceType == AntDeviceTypes.BikeCadence && Setup.BluetoothKeyFor(Role.Trainer, kickr.Address, prefs) == kickr.TrainerKey,
                "`set <role> bluetooth <address>`: saved as that role's kind of device");
            prefs.Set(Role.Power, DeviceKey.Bluetooth(AntDeviceTypes.PowerMeter, meter.Address), name: "Assioma");
            check(Setup.BluetoothKeyFor(Role.Cadence, meter.Address, prefs).DeviceType == AntDeviceTypes.PowerMeter, "`set Cadence bluetooth` with the address of the saved power meter: its crank data is the cadence");
        }

        Console.WriteLine("Bluetooth sensors: the settings file");
        string dir = Path.Combine(Path.GetTempPath(), "descenders_btsensor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, TrainerBridgeConfig.FileName);
            UserConfig Load() => TrainerBridgeConfig.Load(path, "", "", () => null, _ => { });
            DeviceKey antTrainer = new(17, 44226, 5);
            DeviceKey strap = DeviceKey.Bluetooth(AntDeviceTypes.HeartRate, 0x0000A1A1A1A1), meter = DeviceKey.Bluetooth(AntDeviceTypes.PowerMeter, 0x0000C3C3C3C3);
            var prefs = Load().Devices;
            prefs.Set(Role.Trainer, antTrainer);
            prefs.Set(Role.HeartRate, strap, name: "Polar H10");
            prefs.Set(Role.Power, meter, name: "Assioma");
            prefs.Set(Role.Cadence, meter);
            TrainerBridgeConfig.SaveDevices(path, prefs);
            string text = File.ReadAllText(path);
            check(text.Contains("\"transport\": \"bluetooth\"") && text.Contains("\"address\": \"00:00:A1:A1:A1:A1\"") && text.Contains("\"name\": \"Polar H10\"") && text.Contains("\"deviceType\": 120"),
                "a Bluetooth heart-rate strap is saved with transport, address, name and its kind (deviceType 120)");
            var back = Load().Devices;
            check(back.Get(Role.HeartRate) == strap && back.Get(Role.Power) == meter && back.Get(Role.Cadence) == meter && back.Get(Role.Trainer) == antTrainer && back.NameOf(meter) == "Assioma",
                "and read back: an ANT+ trainer with Bluetooth heart rate, power and cadence (a mix)");
            check(back.NeedsAnt && back.NeedsBluetooth && back.AntOnly() is { } antOnly && antOnly.Get(Role.Trainer) == antTrainer && antOnly.Get(Role.HeartRate) is null && antOnly.Get(Role.Power) is null,
                "the ANT+ side is given the trainer only");
            back.Clear(Role.HeartRate); back.Clear(Role.Cadence);
            check(back.KnownFor(Role.HeartRate).SequenceEqual(new[] { strap }) && back.KnownFor(Role.Cadence).SequenceEqual(new[] { antTrainer, meter }) && back.KnownFor(Role.Trainer).SequenceEqual(new[] { antTrainer }),
                "a Bluetooth sensor set to none stays known and on offer for its jobs, never as a trainer");
            check(Setup.Label(strap, back) == "Polar H10 (Bluetooth 00:00:A1:A1:A1:A1)" && strap.ToString() == "Heart rate (Bluetooth) 00:00:A1:A1:A1:A1" && DeviceLabels.Bluetooth(meter, back.NameOf(meter)) == "Assioma (Bluetooth)",
                "and it is named by its name and address, in the console and in the window's drop-down");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine("Bluetooth sensors: why there is no heart rate");
        check(BleSensors.HeartRateWords(connected: false, everConnected: false, notFound: false, hasReading: false, contact: null) == "Bluetooth sensor not found yet"
            && BleSensors.HeartRateWords(false, false, true, false, null) == "not found: riding without it, still looking"
            && BleSensors.HeartRateWords(false, true, true, false, null) == "Bluetooth sensor lost: reconnecting",
            "not connected: not found yet / not found, riding without it / lost, reconnecting");
        check(BleSensors.HeartRateWords(true, true, false, true, true) == "" && BleSensors.HeartRateWords(true, true, false, false, false) == "connected, NO CONTACT: check the strap"
            && BleSensors.HeartRateWords(true, true, false, false, null) == "connected, no reading yet",
            "connected: nothing to say with a reading; \"no contact\" when the strap reports it; else no reading yet");
    }
}
