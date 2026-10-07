namespace DescendersBridge.Ant;

/// <summary>
/// What <see cref="RideLoop"/> reads the rider from and writes the trainer through: the chosen devices of a real ride
/// (<see cref="DescendersBridge.RideSource"/>: ANT+, Bluetooth or both) or the fake trainer of `run --sim`
/// (<see cref="SimTrainer"/>). Values are null when there is no fresh reading (absent is never zero). The loop does not know which
/// transport it has.
/// </summary>
public interface ITrainerSource
{
    /// <summary>The link to the devices is gone and being looked for (always false for the fake trainer). For ANT+ that is the USB stick:
    /// an <see cref="AntSession"/> is dead once this is true; the ride's <see cref="RecoveringSource"/> then opens a new one and says true
    /// only until the stick is back. A Bluetooth trainer that disconnects reads as "not paired" instead, like an ANT+ trainer out of range.</summary>
    bool LinkLost { get; }
    /// <summary>What to tell the rider while <see cref="LinkLost"/> ("ANT+ stick lost: reconnecting"); "" otherwise.</summary>
    string LinkLostText { get; }
    /// <summary>The trainer's control writes, null when no trainer is chosen.</summary>
    ITrainerControl? Control { get; }
    bool TrainerPaired { get; }
    int? PowerW { get; }
    double? CadenceRpm { get; }
    int? HeartRateBpm { get; }
    string HeartRateDiagnosis { get; }
    /// <summary>Send no `trainer` message at all (the fake trainer's --stop-sending-after: tests the mod's failsafe).</summary>
    bool Muted { get; }
}
