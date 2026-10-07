namespace DescendersBridge.Ant;

/// <summary>
/// One decoded FE-C broadcast. Fields are null when the trainer reported the
/// "invalid" sentinel (0xFF / 0x0FFF / 0xFFFF) — treat null as "no reading",
/// never as zero. A cadence of 255 rpm fed to the horse sends it into orbit.
/// </summary>
public sealed record TrainerTelemetry
{
    public int Page { get; init; }

    // Page 25 (0x19) — Specific Trainer Data
    public int? InstantaneousPowerWatts { get; init; }
    public int? CadenceRpm { get; init; }
    public int? AccumulatedPowerWatts { get; init; }
    public byte? UpdateEventCount { get; init; }

    // Page 16 (0x10) — General FE Data
    public double? SpeedMetersPerSecond { get; init; }
    public int? HeartRateBpm { get; init; }
    public byte? EquipmentType { get; init; }
    public double? ElapsedTimeSeconds { get; init; }
    public int? DistanceMeters { get; init; }
}

/// <summary>
/// Decoders for the FE-C data pages Stage 1 cares about. Pure functions over the
/// 8-byte ANT payload (<c>payload[0]</c> = page number) so they can be unit
/// tested with no radio attached — see <c>tests/</c>.
///
/// Byte offsets and scale factors are from <c>docs/fec-protocol.md</c>.
/// </summary>
public static class FecParser
{
    public const int PageGeneralFeData = 0x10;       // 16
    public const int PageSpecificTrainerData = 0x19; // 25

    private const int CadenceInvalid = 0xFF;
    private const int PowerInvalid = 0x0FFF;         // 12-bit field, all ones
    private const int SpeedInvalid = 0xFFFF;
    private const int HeartRateInvalid = 0xFF;

    /// <summary>
    /// Returns a <see cref="TrainerTelemetry"/> for pages 16 and 25, or null for
    /// any other page (there are many; we ignore them in Stage 1).
    /// </summary>
    public static TrainerTelemetry? Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
            return null;

        return payload[0] switch
        {
            PageSpecificTrainerData => ParsePage25(payload),
            PageGeneralFeData => ParsePage16(payload),
            _ => null,
        };
    }

    private static TrainerTelemetry ParsePage25(ReadOnlySpan<byte> p)
    {
        // p[1] update event count
        // p[2] instantaneous cadence           (0xFF invalid)
        // p[3..4] accumulated power, LE, wraps  (watts)
        // p[5]  instantaneous power low byte
        // p[6]  low nibble = instantaneous power bits 8..11; high nibble = status
        // p[7]  FE state (high nibble) + flags
        int cadence = p[2];
        int power = p[5] | ((p[6] & 0x0F) << 8);
        int accumulated = p[3] | (p[4] << 8);

        return new TrainerTelemetry
        {
            Page = PageSpecificTrainerData,
            UpdateEventCount = p[1],
            CadenceRpm = cadence == CadenceInvalid ? null : cadence,
            InstantaneousPowerWatts = power == PowerInvalid ? null : power,
            AccumulatedPowerWatts = accumulated,
        };
    }

    private static TrainerTelemetry ParsePage16(ReadOnlySpan<byte> p)
    {
        // p[1] equipment type (25 = trainer)
        // p[2] elapsed time,  x 0.25 s, wraps
        // p[3] distance,      metres,   wraps
        // p[4..5] speed, LE,  x 0.001 m/s   (0xFFFF invalid)
        // p[6] heart rate,    bpm             (0xFF invalid)
        int speedRaw = p[4] | (p[5] << 8);
        int hr = p[6];

        return new TrainerTelemetry
        {
            Page = PageGeneralFeData,
            EquipmentType = p[1],
            ElapsedTimeSeconds = p[2] * 0.25,
            DistanceMeters = p[3],
            SpeedMetersPerSecond = speedRaw == SpeedInvalid ? null : speedRaw * 0.001,
            HeartRateBpm = hr == HeartRateInvalid ? null : hr,
        };
    }
}
