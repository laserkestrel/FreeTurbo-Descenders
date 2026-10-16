using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// Night and day during one ride (src\DayCycleRules.cs, run by src\DayCycle.cs): the sky's hour goes round the clock, midnight
/// to noon in the given seconds, slower through dawn and dusk; the game's night look comes in as the sun goes down, a smooth amount from 0 to 1, never a jump.
/// </summary>
public static class DayCycleTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Day cycle: the sky's hour round the clock, how much night for the sun, the pictures' hours");
        // the test course's sky (2026-10-16 log): sunrise 05:35, sunset 18:27
        const float rise = 5.583f, set = 18.45f;
        check(Near(DayCycleRules.Advance(3f, 0, 15f, rise, set), 3f) && Near(DayCycleRules.Advance(9f, double.NaN, 15f, rise, set), 9f) && Near(DayCycleRules.Advance(float.NaN, 0, 15f, rise, set), 0f),
            "no time or nonsense: the clock stands");
        double toNoon = SecondsTo(0f, 12f, 15f, rise, set), toMidnight = SecondsTo(12f, 24f, 15f, rise, set);
        check(Math.Abs(toNoon - 15) < 0.1 && Math.Abs(toMidnight - 15) < 0.1, "midnight to noon still 15 s, noon to midnight 15 s: the day keeps its length (got " + toNoon.ToString("F2") + " s, " + toMidnight.ToString("F2") + " s)");
        double fade = DayCycleRules.FadeSeconds(15f, rise, set), fadeIn = SecondsTo(set - 0.75f, set + 0.25f, 15f, rise, set), fadeOut = SecondsTo(rise - 0.25f, rise + 0.75f, 15f, rise, set);
        check(fade > 3.5 && fade < 4.5 && Math.Abs(fadeIn - fade) < 0.1, "the hour round dusk (the night look coming in) takes about 4 s, it was 1.25 s (got " + fade.ToString("F2") + " s)");
        check(Math.Abs(fadeOut - fade) < 0.1, "dawn the same (got " + fadeOut.ToString("F2") + " s)");
        double noonHour = DayCycleRules.SecondsPerHour(12, 15f, rise, set), midnightHour = DayCycleRules.SecondsPerHour(0, 15f, rise, set);
        check(noonHour < 1.25 && Math.Abs(noonHour - midnightHour) < 1e-9, "the rest of the day goes quicker than the even 1.25 s an hour (got " + noonHour.ToString("F2") + " s)");
        check(Math.Abs(DayCycleRules.FadeSeconds(60f, rise, set) - 4 * fade) < 0.05, "a 2-minute half day: everything 4 times as long");
        bool smooth = true;
        for (double h = 0; h < 24; h += 0.01)
        {
            double a = DayCycleRules.SecondsPerHour(h, 15f, rise, set), b = DayCycleRules.SecondsPerHour(h + 0.01, 15f, rise, set);
            if (Math.Abs(b - a) > 0.05 * Math.Min(a, b)) smooth = false;   // the speed eases, never jumps
        }
        check(smooth, "the clock's speed eases in and out: under 5 % change per 0.01 h of sky");
        bool forward = true, inside = true;
        float hr = 0f, last = 0f;
        for (int i = 0; i < 90 * 60; i++)   // 90 s at 60 frames a second
        {
            hr = DayCycleRules.Advance(hr, 1.0 / 60, 15f, rise, set);
            if (hr < 0f || hr >= 24f) inside = false;
            if (!(hr > last || last - hr > 23f)) forward = false;   // only ever forward, or round from 24 to 0
            last = hr;
        }
        check(inside && forward && Math.Abs(hr) < 0.05f, "three days at 60 frames a second: always 0 .. 24, always forward, back at midnight (got " + DayCycleRules.Clock(hr) + ")");
        check(Math.Abs(SecondsTo(0f, 12f, 15f, float.NaN, 30f) - 15) < 0.1 && Math.Abs(DayCycleRules.FadeSeconds(15f, float.NaN, 0f) - fade) < 0.3, "no sensible sunrise / sunset from the sky: 06:00 and 18:00, the same shape");
        // the rider's day (2026-10-16): 12 minutes, so 6 minutes midnight to noon
        double day12 = SecondsTo(0f, 12f, 360f, rise, set) + SecondsTo(12f, 24f, 360f, rise, set);
        check(Math.Abs(day12 - 720) < 1, "a 12-minute day: 720 s round the clock (got " + day12.ToString("F1") + " s)");
        double fade12 = DayCycleRules.FadeSeconds(360f, rise, set);
        check(fade12 > 85 && fade12 < 105, "... with dusk and dawn about a minute and a half each (got " + fade12.ToString("F0") + " s)");
        check(Near(DayCycleRules.Advance(8f, 0, 360f, rise, set), 8f), "it starts from the hour it is given (the level's own: no sudden dark in a daytime ride)");

        // party mode off: back to the course's own hour over 10 s, the shorter way round, never a jump
        int retDir; double retHours;
        DayCycleRules.ReturnPlan(22.0, 8.0, 10f, rise, set, out retDir, out retHours);
        check(retDir == 1 && Math.Abs(retHours - 10) < 1e-9, "22:00 back to 08:00: forward through dawn, 10 h (not 14 h back)");
        DayCycleRules.ReturnPlan(14.0, 8.0, 10f, rise, set, out retDir, out retHours);
        check(retDir == -1 && Math.Abs(retHours - 6) < 1e-9, "14:00 back to 08:00: backwards, 6 h (not 18 h on)");
        DayCycleRules.ReturnPlan(2.0, 20.0, 10f, rise, set, out retDir, out retHours);
        check(retDir == -1 && Math.Abs(retHours - 6) < 1e-9, "round midnight the short way: 02:00 back to 20:00 is 6 h back");
        foreach (double from in new[] { 22.0, 14.0, 9.5, 3.0, 19.0 })
        {
            float secs = DayCycleRules.ReturnPlan(from, 8.0, 10f, rise, set, out retDir, out retHours);
            double left = retHours, h = from, tt = 0, dawnS = 0;
            bool retSmooth = true;
            while (left > 0 && tt < 60)
            {
                double next = DayCycleRules.Return(h, 1.0 / 60, retDir, secs, rise, set, ref left);
                double moved = Math.Abs(next - h); if (moved > 12) moved = 24 - moved;
                if (moved > 0.2) retSmooth = false;   // no hour of sky in a frame: a fade, not a jump
                if (next > rise - 0.25 && next < rise + 0.75) dawnS += 1.0 / 60;
                h = next; tt += 1.0 / 60;
            }
            check(left == 0 && Math.Abs(h - 8.0) < 1e-6 && Math.Abs(tt - 10) < 0.2 && retSmooth,
                DayCycleRules.Clock((float)from) + " back to 08:00 in about 10 s, smoothly, arriving exactly (got " + tt.ToString("F2") + " s at " + DayCycleRules.Clock((float)h) + ")");
            if (from == 22.0) check(dawnS > 2.0, "... and the dawn on the way still takes a few seconds of it (got " + dawnS.ToString("F1") + " s)");
        }
        DayCycleRules.ReturnPlan(8.0, 8.0, 10f, rise, set, out retDir, out retHours);
        check(retHours == 0, "already at the course's own hour: nothing to do");
        double none = 0;
        check(DayCycleRules.Return(8.0, 1.0, 1, 10f, rise, set, ref none) == 8.0, "nothing left: the clock stands");
        // only where party mode shows its crowd: a course with a path (not the hub, not a bike park)
        check(DayCycleRules.PartyInEffect(true, true), "party mode on a generated course: night and day");
        check(!DayCycleRules.PartyInEffect(true, false), "party mode on in a bike park or the hub (no path, no crowd): the place keeps its own sky");
        check(!DayCycleRules.PartyInEffect(false, true) && !DayCycleRules.PartyInEffect(false, false), "party mode off: never");
        // the rider's session (2026-10-16): course -> bike park -> course with party mode on all along
        bool bpLast = false, bpOwed = false;
        check(DayCycleRules.Party(DayCycleRules.PartyInEffect(true, true), ref bpLast, ref bpOwed, true, false) == DayCycleRules.PartyAct.Start, "on a course: starts");
        bpOwed = bpLast;   // scene change: DayCycle.NewCourse stops the cycle and owes a start while in effect
        check(DayCycleRules.Party(DayCycleRules.PartyInEffect(true, false), ref bpLast, ref bpOwed, true, false) == DayCycleRules.PartyAct.None && !bpOwed,
            "into a bike park: nothing starts, nothing is owed");
        check(DayCycleRules.Party(DayCycleRules.PartyInEffect(true, false), ref bpLast, ref bpOwed, true, false) == DayCycleRules.PartyAct.None, "riding round the bike park: still nothing");
        check(DayCycleRules.Party(DayCycleRules.PartyInEffect(true, true), ref bpLast, ref bpOwed, true, false) == DayCycleRules.PartyAct.Start, "back on a course: starts again, from that course's own hour");
        bpLast = true; bpOwed = false;
        check(DayCycleRules.Party(DayCycleRules.PartyInEffect(true, false), ref bpLast, ref bpOwed, true, true) == DayCycleRules.PartyAct.Stop, "the path gone while it runs: back to the place's own sky");

        // party mode (F10) brings the cycle with it
        bool partyLast = false, owed = false;
        check(DayCycleRules.Party(false, ref partyLast, ref owed, true, false) == DayCycleRules.PartyAct.None, "party mode off: nothing");
        check(DayCycleRules.Party(true, ref partyLast, ref owed, false, false) == DayCycleRules.PartyAct.None && owed, "F10 on before the course is there: waits");
        check(DayCycleRules.Party(true, ref partyLast, ref owed, true, false) == DayCycleRules.PartyAct.Start && !owed, "... and starts once it is");
        check(DayCycleRules.Party(true, ref partyLast, ref owed, true, true) == DayCycleRules.PartyAct.None, "running: nothing more");
        check(DayCycleRules.Party(false, ref partyLast, ref owed, true, true) == DayCycleRules.PartyAct.Stop, "F10 off: stop (the level's own look again)");
        check(DayCycleRules.Party(false, ref partyLast, ref owed, true, false) == DayCycleRules.PartyAct.None, "off stays off");
        partyLast = false; owed = false;
        DayCycleRules.Party(true, ref partyLast, ref owed, true, false);
        owed = partyLast;   // DayCycle.NewCourse: a new course while party mode is on owes a start
        check(DayCycleRules.Party(true, ref partyLast, ref owed, false, false) == DayCycleRules.PartyAct.None && DayCycleRules.Party(true, ref partyLast, ref owed, true, false) == DayCycleRules.PartyAct.Start,
            "a new course with party mode on: the cycle again, once the course is there");
        partyLast = false; owed = false;
        check(DayCycleRules.Party(true, ref partyLast, ref owed, true, true) == DayCycleRules.PartyAct.None && !owed, "already running (the developer's command): not started twice");
        partyLast = true; owed = false;
        check(DayCycleRules.Party(false, ref partyLast, ref owed, true, false) == DayCycleRules.PartyAct.None && !owed, "F10 off with nothing running: nothing to stop");
        float s;
        check(DayCycleRules.Command(false, null, 15f, out s) == DayCycleRules.Act.Start && s == 15f, "the key / no value when stopped: start with Config's 15 s");
        check(DayCycleRules.Command(true, null, 15f, out s) == DayCycleRules.Act.Stop, "the key / no value when running: stop");
        check(DayCycleRules.Command(true, 0, 15f, out s) == DayCycleRules.Act.Stop && DayCycleRules.Command(false, -3, 15f, out s) == DayCycleRules.Act.Stop, "0 or less: stop");
        check(DayCycleRules.Command(true, 60, 15f, out s) == DayCycleRules.Act.Start && s == 60f, "a number: (re)start with that many seconds, also while running");
        check(DayCycleRules.Command(false, 0.1, 15f, out s) == DayCycleRules.Act.Start && s == DayCycleRules.ShortestS
              && DayCycleRules.Command(false, 1e9, 15f, out s) == DayCycleRules.Act.Start && s == DayCycleRules.LongestS, "kept within 2 s .. an hour");

        check(DayCycleRules.NightAmount(30f) == 0f && DayCycleRules.NightAmount(DayCycleRules.NightStartsDeg) == 0f, "sun well up: no night at all, the level's own look");
        check(DayCycleRules.NightAmount(-30f) == 1f && DayCycleRules.NightAmount(DayCycleRules.NightFullDeg) == 1f, "sun well down: the whole night look");
        float mid = DayCycleRules.NightAmount((DayCycleRules.NightStartsDeg + DayCycleRules.NightFullDeg) / 2f);
        check(Math.Abs(mid - 0.5f) < 1e-4f, "half way down the band: half night (got " + mid + ")");
        bool rising = true, gentle = true;
        float prev = 0f;
        for (float alt = DayCycleRules.NightStartsDeg; alt >= DayCycleRules.NightFullDeg - 0.01f; alt -= 0.1f)
        {
            float n = DayCycleRules.NightAmount(alt);
            if (n < prev) rising = false;
            if (n - prev > 0.02f) gentle = false;   // no step bigger than 2 % per 0.1 degree of sun: no jump
            prev = n;
        }
        check(rising && gentle, "as the sun goes down the night only grows, never more than 2 % per 0.1 degree: a fade, no jump");
        check(DayCycleRules.NightAmount(DayCycleRules.NightStartsDeg - 0.2f) < 0.002f && DayCycleRules.NightAmount(DayCycleRules.NightFullDeg + 0.2f) > 0.998f, "it eases in and out at both ends of the band");
        check(DayCycleRules.NightAmount(float.NaN) == 0f, "unknown sun: no night");

        float[] hours = DayCycleRules.ShotHours(5.5f, 20f);
        check(hours.Length == 20 && hours[0] == 0f && hours[1] == 4.5f && hours[5] == 5.5f && hours[9] == 6.5f && hours[10] == 12f && hours[11] == 19f && hours[15] == 20f && hours[19] == 21f,
            "pictures at midnight, round sunrise every quarter hour, noon, round sunset");
        float[] fallback = DayCycleRules.ShotHours(float.NaN, 0f);
        check(fallback[5] == 6f && fallback[15] == 18f, "no sensible sunrise / sunset from the sky: 06:00 and 18:00");        check(DayCycleRules.Clock(6.5f) == "06:30" && DayCycleRules.Clock(0f) == "00:00" && DayCycleRules.Clock(23.999f) == "00:00" && DayCycleRules.Clock(-1f) == "23:00", "the clock reads 06:30, wraps at midnight");
        check(DayCycleRules.ShotFile(0, 0f) == "day_01_0000.png" && DayCycleRules.ShotFile(19, 21.5f) == "day_20_2130.png", "picture names sort in the order taken");
    }

    private static bool Near(float a, float b) { return Math.Abs(a - b) < 1e-3f; }

    /// <summary>Seconds of riding from one hour of sky to a later one, at 60 frames a second.</summary>
    private static double SecondsTo(float from, float to, float nightToDayS, float sunrise, float sunset)
    {
        double t = 0, gone = 0, h = from, step = nightToDayS / 9000.0;   // 9000 steps a half day, like DayCycle's double clock
        while (gone < to - from && t < 3600)
        {
            double next = DayCycleRules.Advance(h, step, nightToDayS, sunrise, sunset);
            gone += next >= h ? next - h : next + 24 - h;
            h = next; t += step;
        }
        return t;
    }
}
