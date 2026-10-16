using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// A new course without a scene load (src\CourseRules.cs, used by src\PathTrack.cs): in a session the game builds the next level
/// inside the same scene, the mod kept the old path and the old crowd, and the crowd stood in the air over the new ground.
/// </summary>
public static class CourseRulesTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Course: the next level in the same scene is a new course");

        float[] a = { 187.5f, 200.6f, 184.3f,   500f, 100f, 520f,   640f, 60f, 700f,   881.6f, 5.5f, 902.7f };
        float[] sa = CourseRules.Samples(a);
        check(sa.Length == 9 && sa[0] == 187.5f && sa[3] == 640f && sa[6] == 881.6f && sa[8] == 902.7f, "a path is told apart by its first, middle and last point");
        check(CourseRules.SameCourse(4, sa, 4, CourseRules.Samples((float[])a.Clone())), "the path the game still holds is the same course");

        float[] nudged = (float[])a.Clone(); nudged[1] += 0.2f;
        check(CourseRules.SameCourse(4, sa, 4, CourseRules.Samples(nudged)), "a point 0.2 m from where it was is still the same course");

        float[] next = { 183.9f, 180f, 182.5f,   400f, 90f, 410f,   600f, 40f, 650f,   870f, 3f, 880f };
        check(!CourseRules.SameCourse(4, sa, 4, CourseRules.Samples(next)), "the next level of a session (other points, same scene) is a new course");
        float[] lower = (float[])a.Clone(); lower[lower.Length - 2] -= 20f;
        check(!CourseRules.SameCourse(4, sa, 4, CourseRules.Samples(lower)), "the same start with another end is a new course");
        check(!CourseRules.SameCourse(838, sa, 1087, sa), "another number of points is a new course");
        check(!CourseRules.SameCourse(4, sa, 4, null), "no path to compare with is not the same course");
        check(CourseRules.Samples(null).Length == 9 && CourseRules.Samples(new float[0]).Length == 9, "an empty path gives samples, no error");

        // the crowd comes back by itself on the new course when it was on
        check(!CourseRules.CrowdNow(false, true, true, 10f, 0f), "no crowd was on: none is spawned on the new course");
        check(!CourseRules.CrowdNow(true, false, true, 10f, 0f), "the new path is not loaded yet: wait");
        check(!CourseRules.CrowdNow(true, true, false, 10f, 0f), "the rider is not in the world yet: wait");
        check(!CourseRules.CrowdNow(true, true, true, 10f, 10f + CourseRules.CrowdWaitS), "not before its time (the wait between tries)");
        check(CourseRules.CrowdNow(true, true, true, 12f, 12f), "a crowd that was on comes back on the new course");
        check(CourseRules.CrowdFirstS < 1f && CourseRules.CrowdWaitS >= 1f, "the first try comes at once, so the crowd stands before the rider leaves the gate; later tries wait for the ground");
        check(CourseRules.CrowdAgain(0, 1) && !CourseRules.CrowdAgain(0, CourseRules.CrowdTries), "nobody placed: tried again, at most 10 times");
        check(!CourseRules.CrowdAgain(36, 1), "a placed crowd is not spawned again");
    }
}
