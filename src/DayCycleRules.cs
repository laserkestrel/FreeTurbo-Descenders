#nullable disable

using System;

// Pure logic (no Unity, no logging): also compiled into the bridge (bridge/DescendersBridge.csproj links this file) so `selftest` checks it
// (tests\unit\DayCycleTests.cs).
namespace FreeTurboDescenders
{
    /// <summary>
    /// The day-cycle probe (run by DayCycle.cs): the game's sky (the TOD_Sky asset) draws whatever hour it is given, so the mod
    /// runs that hour round the clock during a ride. Midnight to noon takes the given seconds, noon to midnight the same; the clock slows
    /// through dawn and dusk (so the night look fades over seconds, not a moment) and is quicker the rest of the day.
    /// </summary>
    internal static class DayCycleRules
    {
        /// <summary>Night (the game's night colour grading, fog, sun rays, head torch and lamps) starts to come in when the sun is this high (degrees) ...</summary>
        public const float NightStartsDeg = 4f;
        /// <summary>... and is all there when it is this far below the horizon.</summary>
        public const float NightFullDeg = -10f;
        public const float ShortestS = 2f, LongestS = 3600f;

        public enum Act { Start, Stop }
        public enum PartyAct { None, Start, Stop }

        /// <summary>
        /// Whether party mode's night and day are in effect: party mode on AND a course with a path, the same places its crowd stands. The hub
        /// (mt_palumbo) and the bike parks load no path: they keep their own sky (the rider's session of 2026-10-16: a bike park with party
        /// mode on went to night with no crowd at all).
        /// </summary>
        public static bool PartyInEffect(bool partyMode, bool courseLoaded) { return partyMode && courseLoaded; }

        /// <summary>
        /// The day cycle goes with party mode (F10): on with it, off with it, and on again on every new course while party mode is on.
        /// <paramref name="lastWanted"/> is party mode as last seen; <paramref name="owed"/> = a start is due (set by the caller on a new course
        /// while party mode is on, and again when a start found no sky); <paramref name="ready"/> = the course and the rider are there.
        /// </summary>
        public static PartyAct Party(bool wanted, ref bool lastWanted, ref bool owed, bool ready, bool running)
        {
            if (wanted != lastWanted)
            {
                lastWanted = wanted;
                owed = wanted;
                if (!wanted) return running ? PartyAct.Stop : PartyAct.None;
            }
            if (!owed) return PartyAct.None;
            if (running) { owed = false; return PartyAct.None; }   // already going (started by the developer command)
            if (!ready) return PartyAct.None;
            owed = false;
            return PartyAct.Start;
        }

        /// <summary>
        /// What the `daycycle` command does. No value: the key's toggle (stop when running, else start with <paramref name="defaultS"/>);
        /// 0 or less: stop; a number: (re)start with that many seconds from midnight to noon, kept within ShortestS .. LongestS.
        /// </summary>
        public static Act Command(bool running, double? value, float defaultS, out float seconds)
        {
            seconds = Clamp(defaultS);
            if (!value.HasValue || double.IsNaN(value.Value)) return running ? Act.Stop : Act.Start;
            if (value.Value <= 0) return Act.Stop;
            seconds = Clamp((float)value.Value);
            return Act.Start;
        }

        private static float Clamp(float s)
        {
            if (float.IsNaN(s) || s <= 0f) return 15f;
            return Math.Max(ShortestS, Math.Min(LongestS, s));
        }

        // ---- the clock: slow through dawn and dusk, quicker the rest of the day; midnight to noon and noon to midnight each still take nightToDayS ----
        /// <summary>How much longer an hour of sky lasts at the very middle of dawn or dusk than far from them (1 + this).</summary>
        public const float SlowBy = 4.3f;
        /// <summary>How far (hours of sky) either side of that middle the clock is slowed, easing in and out (a raised cosine).</summary>
        public const float SlowHalfWidthH = 1.5f;
        /// <summary>The middle of dawn / dusk, from the sky's sunrise / sunset hours: where the night look is half way (sun about -3 deg; the
        /// sky's sunrise and sunset hours are at about -6 deg, 2026-10-16 log). With these numbers the hour round that middle (the night look
        /// coming in or going) takes 13 % of the whole day: 4 s of a 30 s day.</summary>
        public const float DawnAfterSunriseH = 0.25f, DuskBeforeSunsetH = 0.25f;

        /// <summary>Seconds of riding one hour of sky lasts at <paramref name="hour"/> for a day of 2 x <paramref name="nightToDayS"/>; the sky's
        /// <paramref name="sunrise"/> and <paramref name="sunset"/> hours (6 and 18 when they make no sense).</summary>
        public static double SecondsPerHour(double hour, float nightToDayS, float sunrise, float sunset)
        {
            Sun(ref sunrise, ref sunset);
            double day = 2.0 * Clamp(nightToDayS);
            double slow = 1.0 + SlowBy * (Bump(hour, sunrise + DawnAfterSunriseH) + Bump(hour, sunset - DuskBeforeSunsetH));
            return slow * day / (24.0 + 2.0 * SlowBy * SlowHalfWidthH);   // each bump adds SlowBy x SlowHalfWidthH hours' worth: the day keeps its length
        }

        /// <summary>The sky's hour <paramref name="dt"/> seconds after <paramref name="hour"/> (0 .. 24, only ever forward, round the clock).</summary>
        public static float Advance(float hour, double dt, float nightToDayS, float sunrise, float sunset)
        {
            float f = (float)Advance((double)hour, dt, nightToDayS, sunrise, sunset);
            return f >= 24f ? 0f : f;   // just short of midnight rounds up to 24.0 as a float
        }

        /// <summary>As Advance, kept in double: a slow day adds a very small step every frame, which a float hour would round (DayCycle keeps this one).</summary>
        public static double Advance(double hour, double dt, float nightToDayS, float sunrise, float sunset)
        {
            double h = double.IsNaN(hour) || double.IsInfinity(hour) ? 0.0 : hour;
            if (double.IsNaN(dt) || double.IsInfinity(dt) || dt <= 0) return Wrap(h);
            dt = Math.Min(dt, 60.0);
            while (dt > 0)   // small steps: the speed changes along the way
            {
                double step = Math.Min(dt, 0.02);
                h += step / SecondsPerHour(h, nightToDayS, sunrise, sunset);
                dt -= step;
            }
            return Wrap(h);
        }

        // ---- party mode off: back to the level's own hour, not at once (the rider, 2026-10-16) -------------------------------------------

        /// <summary>
        /// The way back from <paramref name="from"/> to <paramref name="to"/> when party mode goes off: forward when that is the shorter way
        /// round (or as short), else backward (<paramref name="dir"/> +1 / -1), <paramref name="hours"/> of sky. Returns the half-day seconds that
        /// make the trip take <paramref name="returnS"/>, still slowed through any dawn or dusk on the way.
        /// </summary>
        public static float ReturnPlan(double from, double to, float returnS, float sunrise, float sunset, out int dir, out double hours)
        {
            from = Wrap(from); to = Wrap(to);
            double forward = Wrap(to - from);
            dir = forward <= 12.0 ? 1 : -1;
            hours = dir > 0 ? forward : 24.0 - forward;
            if (hours < 1e-9) { hours = 0; return Clamp(returnS); }
            const float reference = 100f;   // SecondsPerHour grows in step with the half-day seconds
            int n = Math.Max(50, (int)(hours * 100));
            double s = 0;
            for (int i = 0; i < n; i++) s += SecondsPerHour(from + dir * hours * (i + 0.5) / n, reference, sunrise, sunset) * hours / n;
            return (float)(reference * Math.Max(0.1, returnS) / s);
        }

        /// <summary>The sky's hour <paramref name="dt"/> seconds further along the way back (<paramref name="dir"/>), never past its end:
        /// <paramref name="hoursLeft"/> goes down to 0 on arrival.</summary>
        public static double Return(double hour, double dt, int dir, float nightToDayS, float sunrise, float sunset, ref double hoursLeft)
        {
            double h = double.IsNaN(hour) || double.IsInfinity(hour) ? 0.0 : hour;
            if (double.IsNaN(dt) || double.IsInfinity(dt) || dt <= 0 || hoursLeft <= 0) return Wrap(h);
            dt = Math.Min(dt, 60.0);
            while (dt > 0 && hoursLeft > 0)
            {
                double step = Math.Min(dt, 0.02);
                double dh = Math.Min(hoursLeft, step / SecondsPerHour(h, nightToDayS, sunrise, sunset));
                h += dir < 0 ? -dh : dh;
                hoursLeft -= dh;
                dt -= step;
            }
            if (hoursLeft < 1e-12) hoursLeft = 0;
            return Wrap(h);
        }

        /// <summary>Seconds of riding the night look takes to come in at dusk (the hour of sky round its middle) for this day.</summary>
        public static double FadeSeconds(float nightToDayS, float sunrise, float sunset)
        {
            Sun(ref sunrise, ref sunset);
            double centre = sunset - DuskBeforeSunsetH, s = 0;
            for (int i = 0; i < 100; i++) s += SecondsPerHour(centre - 0.5 + (i + 0.5) / 100.0, nightToDayS, sunrise, sunset) / 100.0;
            return s;
        }

        private static double Wrap(double h)
        {
            h %= 24.0;
            if (h < 0) h += 24.0;
            return h >= 24.0 ? 0.0 : h;
        }

        /// <summary>1 at <paramref name="centre"/>, easing to 0 at SlowHalfWidthH either side (round the clock: 23:30 is near 00:30).</summary>
        private static double Bump(double hour, double centre)
        {
            double d = Math.Abs(hour - centre) % 24.0;
            if (d > 12.0) d = 24.0 - d;
            return d >= SlowHalfWidthH ? 0.0 : (1.0 + Math.Cos(Math.PI * d / SlowHalfWidthH)) / 2.0;
        }

        private static void Sun(ref float sunrise, ref float sunset)
        {
            if (float.IsNaN(sunrise) || float.IsNaN(sunset) || sunrise <= 1f || sunset >= 23f || sunset - sunrise < 3f) { sunrise = 6f; sunset = 18f; }
        }

        /// <summary>How much night there is (0 = the level's own day look .. 1 = the game's whole night look) for the sun at
        /// <paramref name="sunAltitudeDeg"/> (above the horizon positive): a smooth S from NightStartsDeg down to NightFullDeg. Unknown sun: none.</summary>
        public static float NightAmount(float sunAltitudeDeg)
        {
            if (float.IsNaN(sunAltitudeDeg) || float.IsInfinity(sunAltitudeDeg)) return 0f;
            float x = (NightStartsDeg - sunAltitudeDeg) / (NightStartsDeg - NightFullDeg);
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            return x * x * (3f - 2f * x);
        }

        /// <summary>
        /// The hours `daycycle_shots` takes a picture at: midnight, round sunrise (an hour before to an hour after, every quarter hour), noon and
        /// round sunset the same. The sky's own sunrise and sunset hours; 6 and 18 when it gives none that make sense.
        /// </summary>
        public static float[] ShotHours(float sunrise, float sunset)
        {
            Sun(ref sunrise, ref sunset);
            var hours = new float[20];
            hours[0] = 0f; hours[10] = 12f;
            for (int i = 0; i < 9; i++) { hours[1 + i] = sunrise - 1f + 0.25f * i; hours[11 + i] = sunset - 1f + 0.25f * i; }
            return hours;
        }

        /// <summary>"06:30" for 6.5.</summary>
        public static string Clock(float hour)
        {
            if (float.IsNaN(hour) || float.IsInfinity(hour)) hour = 0f;
            int min = (int)Math.Round(hour * 60.0) % (24 * 60);
            if (min < 0) min += 24 * 60;
            return (min / 60).ToString("00") + ":" + (min % 60).ToString("00");
        }

        /// <summary>The picture's file name: its place in the row and the hour, so the files sort in the order they were taken.</summary>
        public static string ShotFile(int index, float hour)
        {
            return "day_" + (index + 1).ToString("00") + "_" + Clock(hour).Replace(":", "") + ".png";
        }
    }
}
