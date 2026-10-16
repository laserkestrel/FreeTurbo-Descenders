using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// The picture of a completed workout (src\WorkoutShotRules.cs, taken by src\WorkoutShot.cs): one file, always the same name,
/// beside the user's settings file; taken a moment after the `complete` event, while the confetti is still showing.
/// </summary>
public static class WorkoutShotTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Workout picture: last-workout.png beside the settings file, taken while the confetti shows");
        check(WorkoutShotRules.FileName == "last-workout.png", "the file is always last-workout.png: no date, no number");
        check(WorkoutShotRules.FilePath(@"C:\Users\rider\AppData\Roaming") == @"C:\Users\rider\AppData\Roaming\TrainerBridge\last-workout.png", "it is in %APPDATA%\\TrainerBridge");
        string beside = Path.Combine(TrainerBridgeConfig.Dir, WorkoutShotRules.FileName);
        check(WorkoutShotRules.FilePath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)) == beside, "that is beside the user's settings file (" + beside + ")");
        check(WorkoutShotRules.FilePath(@"C:\A") == WorkoutShotRules.FilePath(@"C:\A"), "the same path every time: a second workout replaces the first picture");
        check(WorkoutShotRules.FilePath("") == "" && WorkoutShotRules.FilePath(null) == "" && WorkoutShotRules.FilePath("  ") == "", "no %APPDATA% folder = no path (one log line in the mod, no picture)");

        check(WorkoutShotRules.DelayS(6f, 1.5f) == 1.5f && WorkoutShotRules.DelayS(30f, 1.5f) == 1.5f, "the standard 6 s of confetti: 1.5 s after the workout ends");
        check(WorkoutShotRules.DelayS(0f, 1.5f) == 1.5f && WorkoutShotRules.DelayS(-1f, 1.5f) == 1.5f, "no confetti: still a picture, 1.5 s after the workout ends");
        check(WorkoutShotRules.DelayS(2.5f, 1.5f) == 1.5f, "2.5 s of confetti: 1.5 s is just before it starts to fade");
        check(WorkoutShotRules.DelayS(2f, 1.5f) == 1f, "2 s of confetti: before its last, fading second");
        check(WorkoutShotRules.DelayS(1f, 1.5f) == 0.5f && WorkoutShotRules.DelayS(0.4f, 1.5f) == 0.2f, "confetti shorter than the delay: half way through it");
        bool inside = true;
        for (float c = 0.1f; c <= 30f; c += 0.1f)
        {
            float d = WorkoutShotRules.DelayS(c, 1.5f);
            if (!(d > 0f && d < c && d <= 1.5f)) inside = false;
        }
        check(inside, "any length of confetti: the picture is taken while it is showing, never later than 1.5 s");
        check(WorkoutShotRules.DelayS(6f, 0f) == 0f && WorkoutShotRules.DelayS(6f, -2f) == 0f && WorkoutShotRules.DelayS(6f, float.NaN) == 0f && WorkoutShotRules.DelayS(float.NaN, 1.5f) == 1.5f,
            "a delay of nothing or nonsense is at once, never before the workout ends");
    }
}
