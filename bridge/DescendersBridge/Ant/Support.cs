namespace DescendersBridge.Ant;

/// <summary>The outcome of a shutdown release attempt (kcd2-turbo docs/failsafe.md P0 #1).</summary>
public enum ReleaseOutcome
{
    /// <summary>A zero-grade write was acknowledged: the crank is free.</summary>
    Released,
    /// <summary>Tried repeatedly, no acknowledgement: the trainer may still be loaded.</summary>
    Failed,
    /// <summary>The link is already down; a send cannot succeed and none was made.</summary>
    LinkAlreadyDead,
}

public readonly record struct ReleaseResult(ReleaseOutcome Outcome, double HeldGradePercent);

/// <summary>
/// The rider profile (the `rider` section of the per-user %APPDATA%\TrainerBridge\TrainerBridgeConfig.json, see <see cref="DescendersBridge.TrainerBridgeConfig"/>). The weights, wheel and gear
/// go to the trainer once after every (re)pair (FE-C page 0x37) so it can turn a gradient into a believable force; weights, FTP and the bike
/// type with its CdA / Crr go to the mod (`rider` message, docs/IPC.md).
/// </summary>
public sealed record RiderConfig
{
    public double RiderWeightKg { get; init; } = 75.0;   // the mod has no fallback: it waits for the `rider` message
    public double BikeWeightKg { get; init; } = 9.0;
    public double FtpW { get; init; } = 180.0;
    public double WheelDiameterM { get; init; } = 0.7;
    public double GearRatio { get; init; } = 1.0;
    /// <summary>Bike-type preset (src\SpeedModel.cs BikePresets): "road" (the physics before #47), "gravel" or "mtb"; each a fixed CdA + Crr.</summary>
    public string BikeType { get; init; } = FreeTurboDescenders.BikePresets.Default;
}
