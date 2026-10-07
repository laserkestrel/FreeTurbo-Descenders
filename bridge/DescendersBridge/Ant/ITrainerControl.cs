namespace DescendersBridge.Ant;

/// <summary>The two FE-C control writes <see cref="RideLoop"/> makes, so its ramp / refresh / release logic can be tested without a radio (`selftest`).</summary>
public interface ITrainerControl
{
    Task<bool> SetTrackResistanceAsync(double gradePercent, double crr, CancellationToken ct = default);
    Task<bool> SetTargetPowerAsync(double watts, CancellationToken ct = default);
}
