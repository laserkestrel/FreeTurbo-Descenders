using DescendersBridge.Bluetooth;

namespace DescendersBridge;

/// <summary>
/// The Bluetooth Fitness Machine Service (FTMS) bytes, pure. Indoor Bike Data decode for several flag combinations (fields to
/// skip, power / cadence absent), every control-point command byte for byte, the response decode, the power-range clamp, and the small
/// rules around the radio (address text, "This PC has no Bluetooth"). No radio. Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class FtmsTests
{
    private static bool Near(double? a, double b) => a is { } x && Math.Abs(x - b) < 1e-6;

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("FTMS: Indoor Bike Data decode");
        {
            // flags 0x0044 = cadence + power; bit 0 clear = the speed is there too
            var d = Ftms.DecodeIndoorBikeData(new byte[] { 0x44, 0x00, 0xB8, 0x0B, 0xB4, 0x00, 0xFA, 0x00 });
            check(d is { PowerW: 250 } && Near(d.Value.CadenceRpm, 90) && Near(d.Value.SpeedMps, 30.0 / 3.6), "speed + cadence + power: 30.00 km/h, 90 rpm (180 x 0.5), 250 W");

            d = Ftms.DecodeIndoorBikeData(new byte[] { 0x44, 0x00, 0x00, 0x00, 0xAB, 0x00, 0xF6, 0xFF });
            check(d is { PowerW: -10 } && Near(d.Value.CadenceRpm, 85.5), "cadence is in half revolutions (171 = 85.5 rpm), power is signed (-10 W)");

            // every optional field present, each skipped field filled with 0xEE so a wrong length would land in power or cadence
            var all = new List<byte> { 0xFE, 0x1F };
            all.AddRange(new byte[] { 0x10, 0x0E });                    // instantaneous speed 36.00 km/h
            all.AddRange(new byte[] { 0xEE, 0xEE });                    // average speed
            all.AddRange(new byte[] { 0xA0, 0x00 });                    // instantaneous cadence 160 = 80 rpm
            all.AddRange(new byte[] { 0xEE, 0xEE });                    // average cadence
            all.AddRange(new byte[] { 0xEE, 0xEE, 0xEE });              // total distance (3 bytes)
            all.AddRange(new byte[] { 0xEE, 0xEE });                    // resistance level
            all.AddRange(new byte[] { 0x2C, 0x01 });                    // instantaneous power 300 W
            all.AddRange(new byte[] { 0xEE, 0xEE });                    // average power
            all.AddRange(new byte[] { 0xEE, 0xEE, 0xEE, 0xEE, 0xEE });  // expended energy (total, per hour, per minute)
            all.Add(0xEE);                                              // heart rate
            all.Add(0xEE);                                              // metabolic equivalent
            all.AddRange(new byte[] { 0xEE, 0xEE });                    // elapsed time
            all.AddRange(new byte[] { 0xEE, 0xEE });                    // remaining time
            d = Ftms.DecodeIndoorBikeData(all.ToArray());
            check(d is { PowerW: 300 } && Near(d.Value.CadenceRpm, 80) && Near(d.Value.SpeedMps, 10.0), "every optional field present: each is stepped over by its own length (300 W, 80 rpm, 36 km/h)");
            check(Ftms.DecodeIndoorBikeData(all.Take(all.Count - 1).ToArray()) is null, "a packet one byte shorter than its flags say is not used at all");

            d = Ftms.DecodeIndoorBikeData(new byte[] { 0x70, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x0A, 0x00, 0x90, 0x01 });
            check(d is { PowerW: 400, CadenceRpm: null }, "distance (3 bytes) and resistance level skipped before the power: 400 W, no cadence in the packet");

            d = Ftms.DecodeIndoorBikeData(new byte[] { 0x41, 0x00, 0xC8, 0x00 });
            check(d is { PowerW: 200, CadenceRpm: null, SpeedMps: null }, "\"more data\" (bit 0 set): no speed field, the power follows the flags at once");

            d = Ftms.DecodeIndoorBikeData(new byte[] { 0x00, 0x00, 0xE8, 0x03 });
            check(d is { PowerW: null, CadenceRpm: null } && Near(d.Value.SpeedMps, 10.0 / 3.6), "power and cadence absent are null, never zero (speed only: 10 km/h)");
            d = Ftms.DecodeIndoorBikeData(new byte[] { 0x01, 0x00 });
            check(d is { PowerW: null, CadenceRpm: null, SpeedMps: null }, "flags alone (more data, nothing else): a valid packet with no reading");
            d = Ftms.DecodeIndoorBikeData(new byte[] { 0x04, 0x00, 0x00, 0x00, 0x00, 0x00 });
            check(d is { PowerW: null } && Near(d.Value.CadenceRpm, 0), "a cadence of 0 that IS in the packet is a real 0 rpm (not pedalling)");

            check(Ftms.DecodeIndoorBikeData(new byte[] { 0x44, 0x00, 0xB8, 0x0B, 0xB4 }) is null && Ftms.DecodeIndoorBikeData(new byte[] { 0x44 }) is null
                && Ftms.DecodeIndoorBikeData(Array.Empty<byte>()) is null, "truncated or empty packets give nothing");
        }

        Console.WriteLine("FTMS: control point commands, byte for byte");
        check(Ftms.RequestControl().SequenceEqual(new byte[] { 0x00 }), "Request Control = 00");
        check(Ftms.Reset().SequenceEqual(new byte[] { 0x01 }), "Reset = 01");
        check(Ftms.SetTargetPower(250).SequenceEqual(new byte[] { 0x05, 0xFA, 0x00 }), "Set Target Power 250 W = 05 FA 00");
        check(Ftms.SetTargetPower(1000).SequenceEqual(new byte[] { 0x05, 0xE8, 0x03 }), "Set Target Power 1000 W = 05 E8 03");
        check(Ftms.SetIndoorBikeSimulation(5.0).SequenceEqual(new byte[] { 0x11, 0x00, 0x00, 0xF4, 0x01, 0x28, 0x33 }),
            "simulation 5 %: wind 0, grade 500 x 0.01 %, Crr 40 x 0.0001 (0.004), wind coefficient 51 x 0.01 (0.51 kg/m, the FE-C default) = 11 00 00 F4 01 28 33");
        check(Ftms.SetIndoorBikeSimulation(0.0).SequenceEqual(new byte[] { 0x11, 0x00, 0x00, 0x00, 0x00, 0x28, 0x33 }), "simulation 0 % (the release) = 11 00 00 00 00 28 33");
        check(Ftms.SetIndoorBikeSimulation(-2.5).SequenceEqual(new byte[] { 0x11, 0x00, 0x00, 0x06, 0xFF, 0x28, 0x33 }), "a negative grade is a signed number: -2.5 % = 06 FF");
        check(Ftms.SetIndoorBikeSimulation(12.0, 0.005, 0.40).SequenceEqual(new byte[] { 0x11, 0x00, 0x00, 0xB0, 0x04, 0x32, 0x28 }), "a given Crr 0.005 and wind coefficient 0.40 are sent as 50 and 40");
        check(Ftms.SetIndoorBikeSimulation(double.NaN, double.NaN)[3] == 0 && Ftms.SetIndoorBikeSimulation(1000)[3] == 0xFF && Ftms.SetIndoorBikeSimulation(1000)[4] == 0x7F,
            "an unknown grade is flat; an absurd one is clamped to the field (327.67 %)");
        check(Ftms.SetIndoorBikeSimulation(3.0, 0.004, 0.51, windSpeedMps: 2.5).Skip(1).Take(2).SequenceEqual(new byte[] { 0xC4, 0x09 }), "wind speed is 0.001 m/s (2.5 m/s = 2500)");

        Console.WriteLine("FTMS: control point response");
        check(Ftms.DecodeResponse(new byte[] { 0x80, 0x11, 0x01 }) == new FtmsResponse(0x11, FtmsResult.Success), "80 11 01 = simulation parameters: success");
        check(Ftms.DecodeResponse(new byte[] { 0x80, 0x05, 0x02 })?.Result == FtmsResult.NotSupported, "result 02 = op code not supported");
        check(Ftms.DecodeResponse(new byte[] { 0x80, 0x05, 0x03 })?.Result == FtmsResult.InvalidParameter, "result 03 = invalid parameter");
        check(Ftms.DecodeResponse(new byte[] { 0x80, 0x11, 0x04 })?.Result == FtmsResult.Failed, "result 04 = operation failed");
        check(Ftms.DecodeResponse(new byte[] { 0x80, 0x11, 0x05 })?.Result == FtmsResult.ControlNotPermitted, "result 05 = control not permitted");
        check(Ftms.DecodeResponse(new byte[] { 0x80, 0x00, 0x01, 0xAA })?.RequestOpCode == 0x00, "the request's op code is the second byte; extra bytes are ignored");
        check(Ftms.DecodeResponse(new byte[] { 0x80, 0x11 }) is null && Ftms.DecodeResponse(new byte[] { 0x05, 0x11, 0x01 }) is null
            && Ftms.DecodeResponse(new byte[] { 0x80, 0x11, 0x00 }) is null && Ftms.DecodeResponse(new byte[] { 0x80, 0x11, 0x09 }) is null,
            "too short, not a response, or a result the specification does not have: not a response");

        Console.WriteLine("FTMS: supported power range");
        {
            var r = Ftms.DecodePowerRange(new byte[] { 0x00, 0x00, 0xD0, 0x07, 0x01, 0x00 });
            check(r == new FtmsPowerRange(0, 2000, 1), "00 00 D0 07 01 00 = 0..2000 W in steps of 1 W");
            check(Ftms.TargetWatts(2500, r) == 2000 && Ftms.TargetWatts(-5, r) == 0 && Ftms.TargetWatts(250.4, r) == 250, "a target is clamped to the range and rounded to whole watts");
            var low = new FtmsPowerRange(25, 800, 5);
            check(Ftms.TargetWatts(10, low) == 25 && Ftms.TargetWatts(123, low) == 125 && Ftms.TargetWatts(799, low) == 800 && Ftms.TargetWatts(5000, low) == 800,
                "a trainer with a 25 W floor and 5 W steps: 10 -> 25, 123 -> 125, never above 800");
            check(Ftms.TargetWatts(250.4, null) == 250 && Ftms.TargetWatts(-5, null) == 0 && Ftms.TargetWatts(double.NaN, null) == 0, "no range published: whole watts, never negative");
            check(Ftms.DecodePowerRange(new byte[] { 0x00, 0x00, 0xD0 }) is null && Ftms.DecodePowerRange(new byte[] { 0xD0, 0x07, 0x00, 0x00, 0x01, 0x00 }) is null,
                "a short value or a maximum below the minimum is no range");
        }

        Console.WriteLine("FTMS: service numbers, addresses, the radio rules");
        check(Ftms.Service == new Guid("00001826-0000-1000-8000-00805f9b34fb") && Ftms.IndoorBikeDataChar == new Guid("00002ad2-0000-1000-8000-00805f9b34fb")
            && Ftms.ControlPointChar == new Guid("00002ad9-0000-1000-8000-00805f9b34fb") && Ftms.SupportedPowerRangeChar == new Guid("00002ad8-0000-1000-8000-00805f9b34fb"),
            "service 0x1826, Indoor Bike Data 0x2AD2, control point 0x2AD9, power range 0x2AD8 as full Bluetooth UUIDs");
        check(BleAddress.Format(0xC123456789AB) == "C1:23:45:67:89:AB" && BleAddress.Format(0x1B) == "00:00:00:00:00:1B", "an address is written as six hex pairs");
        check(BleAddress.Parse("C1:23:45:67:89:AB") == 0xC123456789AB && BleAddress.Parse("c1-23-45-67-89-ab") == 0xC123456789AB && BleAddress.Parse(" C123456789AB ") == 0xC123456789AB,
            "and read back with colons, dashes or neither, in either case");
        check(BleAddress.Parse("") is null && BleAddress.Parse(null) is null && BleAddress.Parse("C1:23:45") is null && BleAddress.Parse("ZZ:23:45:67:89:AB") is null
            && BleAddress.Parse("00:00:00:00:00:00") is null, "empty, short, not hex or all zero is not an address");

        check(BleRules.Problem(new BleAdapterInfo(false)) == "This PC has no Bluetooth", "no adapter: one plain message, \"This PC has no Bluetooth\"");
        check(BleRules.Problem(new BleAdapterInfo(true, LowEnergy: false, CentralRole: true, RadioOn: true))!.Contains("too old")
            && BleRules.Problem(new BleAdapterInfo(true, LowEnergy: true, CentralRole: false, RadioOn: true))!.Contains("too old"), "an adapter without Low Energy or the central role is too old");
        check(BleRules.Problem(new BleAdapterInfo(true, true, true, RadioOn: false))!.Contains("switched off"), "the radio off: switch it on");
        check(BleRules.Problem(new BleAdapterInfo(true, true, true, RadioOn: true)) is null && BleRules.Problem(new BleAdapterInfo(true, true, true, RadioOn: null)) is null,
            "usable; a radio state Windows will not tell (the 32-bit exe) is not \"off\"");
        {
            var heard = new[]
            {
                new BleAdvert(0x10, "Headphones", -40, 20, Array.Empty<Guid>()),
                new BleAdvert(0x20, "KICKR CORE 1234", -70, 9, new[] { Ftms.Service, Ftms.Uuid(0x1818) }),
                new BleAdvert(0x30, "", -55, 4, new[] { Ftms.Service }),
            };
            var t = BleRules.Trainers(heard);
            check(t.Count == 2 && t[0].Address == 0x30 && t[1].Address == 0x20, "a scan's trainers are the devices advertising service 0x1826, nearest first");
            check(BleRules.Describe(t[1]).Contains("KICKR CORE 1234") && BleRules.Describe(t[1]).Contains("00:00:00:00:00:20") && BleRules.Describe(t[0]).Contains("(no name)"),
                "the console lists a trainer by name and address");
            check(t[1].TrainerKey.IsBluetooth && t[1].TrainerKey.BluetoothAddress == 0x20 && t[1].TrainerKey.DeviceType == Ant.AntDeviceTypes.FitnessEquipment, "and it is chosen as a Bluetooth trainer with that address");
        }
    }
}
