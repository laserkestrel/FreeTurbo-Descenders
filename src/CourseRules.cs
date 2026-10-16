#nullable disable

using System;

// Pure logic (no Unity, no logging): also compiled into the bridge (bridge/DescendersBridge.csproj links this file) so `selftest` checks it
// (tests\unit\CourseRulesTests.cs).
namespace FreeTurboDescenders
{
    /// <summary>
    /// Is the course path the game holds now still the one the mod loaded? In a session the game builds the next level inside
    /// the same scene: no scene load, so nothing told the mod, and it kept the old path and the old crowd, which then stood where the old
    /// ground had been: in the air. A path is told apart by its number of points and by its first, middle and last point.
    /// </summary>
    internal static class CourseRules
    {
        public const float SameM = 0.5f;   // a point this close to where it was has not moved

        /// <summary>The nine numbers a path is told apart by: x, y, z of its first, middle and last point. xyz = x0, y0, z0, x1, ...</summary>
        public static float[] Samples(float[] xyz)
        {
            var s = new float[9];
            int n = xyz == null ? 0 : xyz.Length / 3;
            if (n == 0) return s;
            int[] at = { 0, n / 2, n - 1 };
            for (int k = 0; k < 3; k++)
                for (int c = 0; c < 3; c++) s[k * 3 + c] = xyz[at[k] * 3 + c];
            return s;
        }

        /// <summary>The same course: as many points, and the first, middle and last where they were.</summary>
        public static bool SameCourse(int countA, float[] samplesA, int countB, float[] samplesB)
        {
            if (countA != countB) return false;
            if (samplesA == null || samplesB == null || samplesA.Length != samplesB.Length) return false;
            for (int i = 0; i < samplesA.Length; i++)
                if (Math.Abs(samplesA[i] - samplesB[i]) > SameM) return false;
            return true;
        }

        // The crowd comes back by itself on a new course when it was on.
        public const float CrowdFirstS = 0.25f;  // after the new course is seen: the crowd is placed while the rider is still in the start gate
        public const float CrowdWaitS = 2f;    // between tries: the ground may not be there yet
        public const int CrowdTries = 10;

        /// <summary>Spawn the crowd now? Only when it is owed, the new path is loaded, the rider is in the world and the wait is over.</summary>
        public static bool CrowdNow(bool owed, bool pathLoaded, bool riderReady, float now, float notBefore)
        {
            return owed && pathLoaded && riderReady && now >= notBefore;
        }

        /// <summary>A spawn that placed nobody (no ground beside the path yet) is tried again, tries = spawns made so far.</summary>
        public static bool CrowdAgain(int placed, int tries) { return placed <= 0 && tries < CrowdTries; }
    }
}
