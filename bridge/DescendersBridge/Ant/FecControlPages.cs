namespace DescendersBridge.Ant;

/// <summary>
/// Encoders for the FE-C control pages the bridge writes to the trainer. Each
/// returns the 8-byte ANT+ payload (page id + 7 data bytes); the ANT library
/// adds the message framing.
///
/// <b>Byte layout is from the ANT+ FE-C device profile and pycycling's
/// <c>tacx_trainer_control.py</c>, not the byte numbers in
/// <c>docs/fec-protocol.md</c></b> — that document counts a 12-byte BLE frame
/// (0xA4 / length / id / channel / page / payload / checksum), so its "byte 8"
/// is payload byte 3. Getting this wrong makes every hill feel wrong in a way
/// that is easy to misread as a grade-maths bug.
/// </summary>
public static class FecControlPages
{
    public const byte WindResistancePage = 0x32;     // 50
    public const byte TargetPowerPage = 0x31;       // 49
    public const byte TrackResistancePage = 0x33;   // 51
    public const byte UserConfigurationPage = 0x37;  // 55

    // ── Field quantisation. Every value below leaves as round(value / Step)
    //    clamped to [0, MaxRaw], so the trainer receives Sent(value), never the
    //    config value. Printing the config value instead is CLAUDE.md decision 7,
    //    and it has hidden a real config error for a whole ride (docs/plan.md —
    //    crr 0.127 confirmed on every banner while the trainer got 0.0127).
    public static readonly FecField Crr = new("crr", 5e-5, 254);
    public static readonly FecField GearRatio = new("gear", 0.03, 254);
    public static readonly FecField WheelDiameter = new("wheel", 0.01, 254);
    public static readonly FecField BikeWeight = new("bike", 0.05, 4094);
    public static readonly FecField RiderWeight = new("rider", 0.01, 65534);

    /// <summary>Wind resistance coefficient, docs/fec-protocol.md page 0x32 — range 0..1.86 kg/m.</summary>
    public static readonly FecField WindCoefficient = new("windCoeff", 0.01, 186);

    /// <summary>
    /// Page 0x32 — Wind Resistance. Never sent before P5.3, so the trainer used
    /// its own default drag coefficient (0.51 kg/m per the FE-C spec) while
    /// <see cref="Riding.SpeedModel"/> assumes <c>cdaM2 × airDensityKgM3</c>
    /// (≈0.392 today) — a silent disagreement between the two halves of the
    /// system. Send this once at startup, alongside <see cref="UserConfiguration"/>,
    /// with the model's own coefficient so both agree.
    /// </summary>
    /// <param name="windCoefficientKgPerM">
    /// <c>NaN</c> sends 0xFF = "use the trainer's default" — same convention as
    /// <see cref="TrackResistance"/>'s <c>crr</c>.
    /// </param>
    /// <param name="windSpeedKmh">Head/tailwind. 0 = none (we don't model wind).</param>
    /// <param name="draftingFactor">0..1. 0 = no drafting (a solo virtual ride).</param>
    public static byte[] WindResistance(
        double windCoefficientKgPerM, double windSpeedKmh = 0.0, double draftingFactor = 0.0)
    {
        byte coeffByte = double.IsNaN(windCoefficientKgPerM)
            ? (byte)0xFF
            : (byte)WindCoefficient.RawOf(windCoefficientKgPerM);
        // units: km/h + 127 offset, range -127..+127 km/h.
        byte speedByte = (byte)Math.Clamp(Math.Round(windSpeedKmh) + 127.0, 0, 254);
        byte draftByte = (byte)Math.Clamp(Math.Round(draftingFactor / 0.01), 0, 100);

        return
        [
            WindResistancePage,
            0xFF, 0xFF, 0xFF, 0xFF,          // reserved
            coeffByte,
            speedByte,
            draftByte,
        ];
    }

    /// <summary>Target power, page 0x31: the FE-C wire unit is 0.25 W in a 16-bit field (0..16383.75 W).</summary>
    public static readonly FecField TargetPowerField = new("targetPower", 0.25, 65534);

    /// <summary>
    /// Page 0x31 — Target Power (ERG). Payload: page, 5 reserved bytes, target power LSB/MSB in 0.25 W. The trainer holds this wattage
    /// whatever the cadence until it receives another control page (0x33 track resistance takes it back to simulation mode).
    /// </summary>
    public static byte[] TargetPower(double watts)
    {
        ushort raw = (ushort)TargetPowerField.RawOf(watts);
        return
        [
            TargetPowerPage,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF,    // reserved
            (byte)(raw & 0xFF),
            (byte)(raw >> 8),
        ];
    }

    /// <summary>
    /// Page 0x33 — Track Resistance (simulation mode). The trainer computes the
    /// resisting force itself from this gradient and the rider+bike mass supplied
    /// once via <see cref="UserConfiguration"/>.
    /// </summary>
    /// <param name="gradePercent">Slope, clamped to the FE-C range −200…+200 %.</param>
    /// <param name="crr">
    /// Coefficient of rolling resistance (~0.004 for tarmac). <c>NaN</c> sends
    /// 0xFF = "use the trainer's default".
    /// </param>
    public static byte[] TrackResistance(double gradePercent, double crr)
    {
        double g = Math.Clamp(gradePercent, -200.0, 200.0);
        // units 0.01 %, offset −200 % → 0 % encodes as 20000, not 0.
        ushort grade = (ushort)Math.Round((g + 200.0) / 0.01);

        byte crrByte = double.IsNaN(crr)
            ? (byte)0xFF
            : (byte)Crr.RawOf(crr);

        return
        [
            TrackResistancePage,
            0xFF, 0xFF, 0xFF, 0xFF,          // reserved
            (byte)(grade & 0xFF),
            (byte)(grade >> 8),
            crrByte,
        ];
    }

    /// <summary>
    /// Page 0x37 — User Configuration. Send once at startup: without the real
    /// rider+bike mass the trainer cannot turn a gradient into a believable force.
    /// </summary>
    public static byte[] UserConfiguration(
        double userWeightKg, double bikeWeightKg,
        double wheelDiameterM, double gearRatio)
    {
        ushort userWeight = (ushort)RiderWeight.RawOf(userWeightKg);     // 0.01 kg
        int bikeWeight = (int)BikeWeight.RawOf(bikeWeightKg);            // 0.05 kg, 12-bit
        byte wheelDia = (byte)WheelDiameter.RawOf(wheelDiameterM);      // 0.01 m
        byte gear = (byte)GearRatio.RawOf(gearRatio);                   // 0.03 (0 = invalid)

        const byte wheelDiameterOffsetInvalid = 0x0F;   // low nibble of byte 4
        byte byte4 = (byte)((wheelDiameterOffsetInvalid & 0x0F) | ((bikeWeight & 0x0F) << 4));
        byte byte5 = (byte)((bikeWeight >> 4) & 0xFF);

        return
        [
            UserConfigurationPage,
            (byte)(userWeight & 0xFF),
            (byte)(userWeight >> 8),
            0xFF,                           // reserved
            byte4,
            byte5,
            wheelDia,
            gear,
        ];
    }
}

/// <summary>
/// One FE-C payload field's quantisation: <see cref="Step"/> is the unit the
/// wire carries, <see cref="MaxRaw"/> the largest raw integer that fits its
/// bit-width. A config value <c>v</c> is transmitted as <c>round(v / Step)</c>
/// clamped to <c>[0, MaxRaw]</c>, so the trainer only ever acts on
/// <see cref="Sent"/> — report that, not <c>v</c> (CLAUDE.md decision 7).
/// </summary>
public readonly record struct FecField(string Name, double Step, double MaxRaw)
{
    /// <summary>The raw integer actually transmitted for <paramref name="value"/>.</summary>
    public double RawOf(double value) => Math.Clamp(Math.Round(value / Step), 0, MaxRaw);

    /// <summary>The value the trainer reconstructs — what it actually resists with.</summary>
    public double Sent(double value) => RawOf(value) * Step;

    /// <summary>The largest value that does not hit the clamp.</summary>
    public double Max => MaxRaw * Step;

    /// <summary>True when <paramref name="value"/> was pulled to a clamp (over the max, or negative).</summary>
    public bool Clamps(double value) => Math.Round(value / Step) != RawOf(value);

    /// <summary>Differs from the request for any reason — rounding or a clamp.</summary>
    public bool Differs(double value) => Math.Abs(Sent(value) - value) > 1e-9;
}
