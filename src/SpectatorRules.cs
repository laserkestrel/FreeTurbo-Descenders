#nullable disable

using System;

// Pure logic (no Unity, no logging): also compiled into the bridge (bridge/DescendersBridge.csproj links this file) so `selftest` checks where
// a spectator may stand (tests\unit\SpectatorRulesTests.cs).
namespace FreeTurboDescenders
{
    /// <summary>
    /// Where a spectator may stand. The ground is found by walking out from the course path, whose height is known, and at every
    /// step taking only a surface the last one could plausibly lead to: a tree top, a rock, a roof or a bridge high above (or far below) the
    /// spot is never "the ground", which is what made the crowd float on other courses. A spot is refused when it is on or near any part of the
    /// path (a hairpin brings another part of the course to where "beside the path" was), when the ground is too steep, or when something is
    /// above it (inside a tree, under a roof).
    /// </summary>
    internal static class SpectatorRules
    {
        public const float StepM = 1f;                      // the walk out from the path
        public const float UpPerM = 1.5f, UpAbs = 0.3f;     // most the ground may rise per metre walked (steeper than this nobody stands on anyway)
        public const float DownPerM = 1.5f, DownAbs = 0.5f;
        public const float PathSnapM = 3f;                  // the path's own height may be this far from the surface under it
        public const float MaxGapM = 3f;                    // most metres of the walk that may have no ground (a ditch, a trunk) before the spot is refused
        public const float ClearM = 4f;                     // nobody stands closer than this to any part of the path
        public const float HeadroomM = 0.3f;                // anything higher than this above the ground of the spot is in the way
        public const float MinNormalY = 0.55f;              // flattest-enough ground to stand on (about 56 degrees)
        public const float RetryOffM = 2.5f;                // a refused spot is tried this much further out, then on the other side

        public enum Spot { Ok, NearTrack, NoGround, Steep, Blocked }

        /// <summary>Steps of the walk from the path to a spot off metres beside it.</summary>
        public static int Steps(float off) { return Math.Max(1, (int)Math.Ceiling(off / StepM - 1e-4f)); }

        /// <summary>The surface the path lies on: of the heights hit under a path point, the nearest to the path's own height. -1 = none near it.</summary>
        public static int PickAtPath(float[] ys, int n, float pathY)
        {
            int best = -1; float bd = PathSnapM;
            for (int i = 0; i < n; i++)
            {
                float d = Math.Abs(ys[i] - pathY);
                if (d <= bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// The ground at the next column of the walk: the highest of the heights hit there that the last ground (lastY, dist metres back) can lead
        /// to. -1 = none, or the last ground is too far back to say.
        /// </summary>
        public static int PickStep(float[] ys, int n, float lastY, float dist)
        {
            if (dist > MaxGapM + 1e-3f) return -1;
            float lo = lastY - (DownPerM * dist + DownAbs), hi = lastY + (UpPerM * dist + UpAbs);
            int best = -1;
            for (int i = 0; i < n; i++)
                if (ys[i] >= lo && ys[i] <= hi && (best < 0 || ys[i] > ys[best])) best = i;
            return best;
        }

        /// <summary>Is anything above the ground of the spot (a trunk or rock the spot is inside, a roof, a bridge)?</summary>
        public static bool Blocked(float[] ys, int n, float groundY)
        {
            for (int i = 0; i < n; i++) if (ys[i] > groundY + HeadroomM) return true;
            return false;
        }

        /// <summary>Horizontal distance from a point to the nearest part of the path (its segments, not only its points).</summary>
        public static float DistanceToPath(float[] xs, float[] zs, float x, float z)
        {
            if (xs == null || zs == null || xs.Length == 0) return float.MaxValue;
            double best = double.MaxValue;
            if (xs.Length == 1) best = Sq(x - xs[0], z - zs[0]);
            for (int i = 0; i + 1 < xs.Length; i++)
            {
                double ax = xs[i], az = zs[i], dx = xs[i + 1] - ax, dz = zs[i + 1] - az;
                double len2 = dx * dx + dz * dz;
                double f = len2 > 1e-12 ? ((x - ax) * dx + (z - az) * dz) / len2 : 0.0;
                if (f < 0.0) f = 0.0; else if (f > 1.0) f = 1.0;
                double d = Sq(x - (ax + dx * f), z - (az + dz * f));
                if (d < best) best = d;
            }
            return (float)Math.Sqrt(best);
        }

        private static double Sq(double dx, double dz) { return dx * dx + dz * dz; }

        public static bool NearTrack(float[] xs, float[] zs, float x, float z) { return DistanceToPath(xs, zs, x, z) < ClearM; }

        /// <summary>
        /// The places tried for one spectator, in order: where it was meant to stand, further out on the same side, then the same two on the
        /// other side. attempt 0..Attempts-1.
        /// </summary>
        public const int Attempts = 4;
        public static void Candidate(int attempt, float side, float off, out float trySide, out float tryOff)
        {
            trySide = attempt < 2 ? side : -side;
            tryOff = off + (attempt % 2 == 1 ? RetryOffM : 0f);
        }
        // ---- round the finish area ---------------------------------------------------------------------------------------------------------
        // Past the finish arch every level has a round area, usually inside barriers. Part of the crowd stands round it, behind the barrier,
        // facing in; the way in from the arch stays open. Those people are taken from the groups that gathered near the start.
        public const float FinishShare = 0.30f, StartShare = 0.08f;   // of the groups beside the path: near the finish end, near the start (was 0.20)
        public const float RingShare = 0.12f;       // of the whole crowd: round the finish area
        public const int RingMin = 6;               // a small crowd still shows round the finish
        public const float RingGapDeg = 40f;        // nobody within this angle either side of the way in from the arch
        public const float RingCentreM = 10f;       // the point the area is measured from: this far past the end of the path
        public const float RingDefaultM = 14f;      // where the crowd stands when no barrier is found in a direction
        public const float RingMinM = 6f, RingMaxM = 35f;
        public const float RingBehindM = 1.2f;      // behind the barrier
        public const float RingRowM = 1.3f;         // between rows
        public const float RingSpacingM = 1.1f;     // shoulder to shoulder
        public const int RingRowsMax = 3;
        public const float RingRetryM = 1.5f;       // a refused spot is tried this much further out

        /// <summary>How many of the crowd stand round the finish area.</summary>
        public static int RingCount(int want)
        {
            if (want < 8) return 0;
            int n = Math.Max(RingMin, (int)Math.Round(want * RingShare));
            return Math.Min(n, want / 3);
        }

        /// <summary>Rows needed for n people round an area of that radius, with the way in left open.</summary>
        public static int RingRows(int n, float radiusM)
        {
            double arc = 2.0 * Math.PI * Math.Max(radiusM, RingMinM) * (360.0 - 2.0 * RingGapDeg) / 360.0;
            int perRow = Math.Max(1, (int)(arc / RingSpacingM));
            int rows = (n + perRow - 1) / perRow;
            return rows < 1 ? 1 : rows > RingRowsMax ? RingRowsMax : rows;
        }

        /// <summary>
        /// Where person i of n stands: degrees from "straight on past the arch" (0), positive to the right; +/-180 would be the way in, which
        /// stays open. Evenly round, jitter -0.5..0.5 moves one within its own share of the arc.
        /// </summary>
        public static float RingAngleDeg(int i, int n, float jitter)
        {
            float half = 180f - RingGapDeg;
            if (n <= 0) return 0f;
            if (jitter < -0.5f) jitter = -0.5f; else if (jitter > 0.5f) jitter = 0.5f;
            return -half + (i + 0.5f + jitter * 0.8f) / n * 2f * half;
        }

        /// <summary>How far from the measuring point a person of that row stands, the barrier (or the default) being at barrierM.</summary>
        public static float RingOff(float barrierM, bool barrierFound, int row, int attempt)
        {
            float d = barrierFound ? barrierM : RingDefaultM;
            if (d < RingMinM) d = RingMinM; else if (d > RingMaxM) d = RingMaxM;
            return d + (barrierFound ? RingBehindM : 0f) + row * RingRowM + attempt * RingRetryM;
        }

        // ---- horns -------------------------------------------------------------------------------------------------------------------------
        // A horn or vuvuzela is not blown once: a few short paps, then the full hoot. A pap is the same sound cut short.
        public const float PapS = 0.14f;            // a pap sounds this long
        public const float PapFadeS = 0.04f;        // of which the last part fades, so the cut does not click
        public const float PapGapS = 0.26f;         // from one pap to the next sound
        public const float HornRestS = 1.5f;        // extra rest after a horn's hoot before the next burst

        // ---- getting out of the rider's way ------------------------------------------------------------------------------------------------
        // A rider who leaves the course and heads at the crowd used to ride straight through them. A spectator the rider would reach within
        // DodgeReactS and pass closer than DodgeMissM runs sideways out of the line (never onto the track), watches, and walks back.
        public const float DodgeMinSpeedMs = 3f;     // a slower rider is no danger
        public const float DodgeReactS = 0.8f;       // the rider would be here within this (a runner needs about 0.6 s to clear a metre or two)
        public const float DodgeMaxAlongM = 14f;     // and is no further than this (a fast rider's heading wobble is a long way off 40 m out)
        public const float DodgeMissM = 1.2f;        // ... and pass closer than this
        public const float DodgeBehindM = 1.0f;      // a rider already this far past is gone
        public const float DodgeSafeM = 2.5f;        // run until this far from the rider's line
        public const float DodgeRunMs = 5.2f, DodgeWalkMs = 1.9f;
        public const float DodgeHoldMinS = 3f, DodgeHoldMaxS = 6f;   // stay out of the way at least this long
        public const float DodgeCloseM = 8f;         // and until the rider is further than this

        /// <summary>
        /// Is the rider (velocity vx, vz) heading at a spectator standing at (relX, relZ) from the rider? True when the rider is faster than
        /// DodgeMinSpeedMs, is not already past, would get there within DodgeReactS and would pass closer than DodgeMissM. lateral = the
        /// spectator's distance from the rider's line, positive on the rider's right.
        /// </summary>
        public static bool Threatened(float relX, float relZ, float vx, float vz, out float lateral)
        {
            lateral = 0f;
            float sp = (float)Math.Sqrt(vx * vx + vz * vz);
            if (sp < DodgeMinSpeedMs) return false;
            float hx = vx / sp, hz = vz / sp;
            float along = relX * hx + relZ * hz;
            lateral = relX * hz - relZ * hx;   // right of travel is (hz, -hx)
            if (along < -DodgeBehindM) return false;
            if (along / sp > DodgeReactS || along > DodgeMaxAlongM) return false;
            return Math.Abs(lateral) < DodgeMissM;
        }

        /// <summary>The horizontal direction (unit) to run: off the rider's line, to the side the spectator already is. tie (any sign) decides when dead on the line.</summary>
        public static void EscapeDir(float vx, float vz, float lateral, float tie, out float ex, out float ez)
        {
            float sp = (float)Math.Sqrt(vx * vx + vz * vz);
            if (sp < 1e-4f) { ex = 1f; ez = 0f; return; }
            float side = Math.Abs(lateral) < 0.15f ? (tie >= 0f ? 1f : -1f) : (lateral > 0f ? 1f : -1f);
            ex = vz / sp * side; ez = -vx / sp * side;
        }

        /// <summary>How far to run to be DodgeSafeM from the rider's line (a little more, at least 1.5 m, at most 7 m).</summary>
        public static float RunDistance(float lateral)
        {
            float d = DodgeSafeM - Math.Abs(lateral) + 0.5f;
            return d < 1.5f ? 1.5f : d > 7f ? 7f : d;
        }

        /// <summary>May the spectator step onto this ground, dist metres on? (the same rise and drop as the walk that placed the crowd)</summary>
        public static bool StepOk(float dy, float dist)
        {
            return dy <= UpPerM * dist + UpAbs && -dy <= DownPerM * dist + DownAbs;
        }

        /// <summary>Time to go back to the place: held long enough, the rider not coming at that place and not close.</summary>
        public static bool MayReturn(float heldS, bool homeThreatened, float riderDistM)
        {
            return heldS >= DodgeHoldMinS && !homeThreatened && riderDistM > DodgeCloseM;
        }

        /// <summary>Gait at a ground speed: step cycles per second (both legs), foot swing and foot lift (m). 0 speed = standing.</summary>
        public static void Gait(float speedMs, out float cyclesPerS, out float swingM, out float liftM)
        {
            float k = speedMs <= 0f ? 0f : speedMs >= DodgeRunMs ? 1f : speedMs / DodgeRunMs;
            float stride = 1.3f + 1.4f * k;   // metres per cycle: short walking steps .. long strides
            cyclesPerS = speedMs <= 0f ? 0f : speedMs / stride;
            float amp = speedMs >= 1.5f ? 1f : speedMs <= 0f ? 0f : speedMs / 1.5f;   // eases in, so a stop does not snap the legs
            swingM = (0.14f + 0.22f * k) * amp;
            liftM = (0.04f + 0.14f * k) * amp;
        }

        // ---- placing the crowd -----------------------------------------------------------------------------------------------------------
        public const float BuildMsPerFrame = 4f;    // the crowd is placed a few per frame: 500 at once froze the game for over half a second

        /// <summary>Place another spectator in this frame? At least one per frame, then until the time is used up.</summary>
        public static bool KeepBuilding(double elapsedMs, int doneThisFrame) { return doneThisFrame < 1 || elapsedMs < BuildMsPerFrame; }

        // The head accessories that are a whole animal's head: the spectators wearing one play the motion-capture clips (a mimed animal), the
        // rest of the crowd cheers as before. Ears, antlers, hats and mohawks are not animal heads.
        private static readonly string[] AnimalHeads = { "Bear", "Dino", "Flamingo", "Giraffe", "Penguin", "Rhino", "Shark", "Unicorn" };

        /// <summary>Is this head accessory (the game's display name) a whole animal's head?</summary>
        public static bool IsAnimalHead(string hat)
        {
            if (hat == null) return false;
            string name = hat.Trim();
            foreach (string a in AnimalHeads) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Does this spectator play the motion-capture clip? Only one with an animal's head, and only while the clip players are fewer than
        /// share of the crowd placed so far (the spectator included): with every animal's head playing it, about a fifth of the crowd mimed a
        /// T-rex, too many (the rider, 2026-10-16). Placed nearest the start first, so the few are spread along the course.
        /// </summary>
        public static bool PlaysClip(bool animalHead, int placed, int clips, float share)
        {
            return animalHead && clips < share * (placed + 1);
        }

        /// <summary>
        /// The joints of a finger that bend, from the knuckle out, as the numbers in the game's bone names. The game numbers a finger's bones
        /// from the END inwards (Index1 is the last joint, Index3 the knuckle, Index4 lies inside the palm at the wrist; the thumb has 1 to 3),
        /// so of found bones (1..found) the chain is 3, 2, 1. Taken the other way round, every finger bent backwards.
        /// </summary>
        public static int[] BendingJoints(int found)
        {
            int n = found > 3 ? 3 : found;
            if (n < 2) return new int[0];
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = n - i;
            return order;
        }

        /// <summary>
        /// The crowd's random seed for a course: a hash of its path (positions to 0.1 m), plus salt for each random stream. The seeds were
        /// fixed (7, 11, 13) and every course makes the same number of draws, so every course had the same crowd: the same pattern stretched to
        /// its length, in the same outfits, the same people round the finish (the rider, 2026-10-16). Now the same course keeps its crowd and
        /// another course gets another one.
        /// </summary>
        public static int CourseSeed(float[] xs, float[] zs, int salt)
        {
            unchecked
            {
                int h = (int)2166136261 ^ salt;
                for (int i = 0; i < xs.Length && i < zs.Length; i++)
                {
                    h = (h ^ (int)Math.Round(xs[i] * 10f)) * 16777619;
                    h = (h ^ (int)Math.Round(zs[i] * 10f)) * 16777619;
                }
                return h & 0x7fffffff;
            }
        }

        // ---- skin tones -------------------------------------------------------------------------------------------------------
        // The game tints the body (character_base, head included) with a colour of its skin palette (PlayerCustomization.SetSkinColor: GameData's
        // ColorPalette at RiderCustomizations.SkinColour, set as the material's _Color). A copy of the rider kept the rider's tint: everyone white.
        public const float PaletteMatch = 0.02f;   // largest difference per channel for "the rider's tint is this palette's colour"

        /// <summary>
        /// Which palette is the skin palette: GameData holds two (skin, and hair / beard) and their names are obfuscated. Each candidate's colour
        /// at the rider's SkinColour index is compared with the rider's body tint; the one that matches (and only one) is the skin palette.
        /// -1 = none or more than one matched (the caller then takes the first, as GameData declares it).
        /// </summary>
        public static int SkinPalette(float[] r, float[] g, float[] b, float tintR, float tintG, float tintB)
        {
            int found = -1;
            for (int i = 0; i < r.Length; i++)
            {
                if (Math.Abs(r[i] - tintR) > PaletteMatch || Math.Abs(g[i] - tintG) > PaletteMatch || Math.Abs(b[i] - tintB) > PaletteMatch) continue;
                if (found >= 0) return -1;
                found = i;
            }
            return found;
        }

        /// <summary>
        /// The next spectator's skin tone, an index into a palette of deck.Length tones: every tone once, in a shuffled order, then a new shuffle.
        /// So a crowd is spread evenly over the whole palette (plain random picks bunch up), and neighbours seldom match. -1 = no palette.
        /// </summary>
        public static int NextTone(int[] deck, ref int next, Random rng)
        {
            if (deck == null || deck.Length == 0) return -1;
            int n = deck.Length;
            if (next % n == 0)
            {
                for (int i = 0; i < n; i++) deck[i] = i;
                for (int i = n - 1; i > 0; i--) { int k = rng.Next(i + 1); int t = deck[i]; deck[i] = deck[k]; deck[k] = t; }
            }
            return deck[next++ % n];
        }

        public static bool IsHorn(string eventPath) { return eventPath != null && eventPath.IndexOf("/horn/", StringComparison.OrdinalIgnoreCase) >= 0; }

        /// <summary>Paps before the hoot: 2 to 4. r = 0..1.</summary>
        public static int HornPaps(double r) { int n = 2 + (int)(r * 3.0); return n < 2 ? 2 : n > 4 ? 4 : n; }

        /// <summary>Is this sound of a horn's burst a pap (cut short)? soundsAfter = sounds of the burst still to come after it; the last is the hoot.</summary>
        public static bool IsPap(bool horn, int soundsAfter) { return horn && soundsAfter > 0; }

        /// <summary>The level of a pap at age seconds (1 = full): full, then faded out over the last PapFadeS; 0 = over, stop it.</summary>
        public static float PapLevel(float age)
        {
            if (age >= PapS) return 0f;
            float left = PapS - age;
            return left >= PapFadeS ? 1f : left / PapFadeS;
        }
    }
}
