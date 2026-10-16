using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// Where a spectator may stand (src\SpectatorRules.cs, used by src\Spectators.cs): on other courses the crowd floated (the highest
/// thing under a spot was taken for the ground: a tree top, a rock, a bridge) or stood on the track (a hairpin brings another part of the
/// path to where "beside the path" was).
/// </summary>
public static class SpectatorRulesTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Spectators: on the ground beside the course, never in the air or on the track");

        // the surface under the path: the nearest to the path's own height, not the highest
        float[] atPath = { 131.5f, 100.2f, 62f };   // a bridge above, the trail, a cave floor below
        check(SpectatorRules.PickAtPath(atPath, 3, 100f) == 1, "under a path point the surface nearest the path's height is the ground, not a bridge above it");
        check(SpectatorRules.PickAtPath(atPath, 3, 110f) == -1, "nothing within 3 m of the path's height: no surface (the path's own height is used)");
        check(SpectatorRules.PickAtPath(atPath, 0, 100f) == -1, "no hits at all: no surface");

        // one step of the walk: the highest surface the last one can lead to
        float[] tree = { 112f, 100.4f };            // a tree top 12 m up, the ground
        check(SpectatorRules.PickStep(tree, 2, 100f, 1f) == 1, "a tree top 12 m above is not the ground: the ground below it is");
        float[] treeOnly = { 112f };                // a terrain's tree hides the ground under it from the ray
        check(SpectatorRules.PickStep(treeOnly, 1, 100f, 1f) == -1, "only a tree top under the spot: no ground there");
        float[] bank = { 101.6f, 99f };             // a bank rising 1.6 m in one metre, and something under it
        check(SpectatorRules.PickStep(bank, 2, 100f, 1f) == 0, "a bank within the plausible rise is followed, and the highest such surface wins");
        float[] rock = { 102.5f, 100.1f };
        check(SpectatorRules.PickStep(rock, 2, 100f, 1f) == 1, "a rock 2.5 m high one metre on is not climbed");
        float[] drop = { 97.5f };
        check(SpectatorRules.PickStep(drop, 1, 100f, 1f) == -1, "a 2.5 m drop one metre on is not the ground (a spot far below a bridge)");
        check(SpectatorRules.PickStep(drop, 1, 100f, 2f) == 0, "after 2 m with no ground the window has grown with the distance");
        check(SpectatorRules.PickStep(drop, 1, 100f, 3.5f) == -1, "more than 3 m of the walk without ground: refused, the height can no longer be trusted");

        // something above the ground of the spot
        float[] roof = { 104f, 100f };
        check(SpectatorRules.Blocked(roof, 2, 100f), "a roof (or the top of the trunk the spot is inside) above the ground: in the way");
        float[] open = { 100f, 100.2f, 80f };
        check(!SpectatorRules.Blocked(open, 3, 100f), "open ground, with only things at or below it: free");

        // the track: a hairpin, the path goes out along z = 0 and comes back along z = 8
        float[] xs = { 0f, 20f, 40f, 44f, 40f, 20f, 0f };
        float[] zs = { 0f, 0f, 0f, 4f, 8f, 8f, 8f };
        check(Math.Abs(SpectatorRules.DistanceToPath(xs, zs, 10f, 5f) - 3f) < 1e-3f, "distance is to the nearest part of the whole path (here the leg coming back), between its points too");
        check(SpectatorRules.NearTrack(xs, zs, 10f, 7.5f), "5 m+ beside one leg of a hairpin but on the other leg: on the track, refused");
        check(SpectatorRules.NearTrack(xs, zs, 10f, 4f) == false && SpectatorRules.NearTrack(xs, zs, 10f, -6f) == false, "4 m or more from every part of the path: allowed");
        check(SpectatorRules.NearTrack(xs, zs, 10f, 3.9f), "closer than 4 m: refused");
        check(SpectatorRules.DistanceToPath(new float[0], new float[0], 1f, 1f) == float.MaxValue, "no path: nothing is near it");

        // the places tried, and the walk's steps
        float side, off;
        SpectatorRules.Candidate(0, 1f, 5f, out side, out off);
        bool first = side == 1f && off == 5f;
        SpectatorRules.Candidate(1, 1f, 5f, out side, out off);
        bool second = side == 1f && off == 5f + SpectatorRules.RetryOffM;
        SpectatorRules.Candidate(2, 1f, 5f, out side, out off);
        bool third = side == -1f && off == 5f;
        SpectatorRules.Candidate(3, 1f, 5f, out side, out off);
        bool fourth = side == -1f && off == 5f + SpectatorRules.RetryOffM;
        check(first && second && third && fourth && SpectatorRules.Attempts == 4, "a refused spot is tried further out, then on the other side, then further out there");
        check(SpectatorRules.Steps(5f) == 5 && SpectatorRules.Steps(5.2f) == 6 && SpectatorRules.Steps(0.2f) == 1, "the walk out from the path is in steps of at most 1 m");

        // round the finish area: part of the crowd, taken from the start end, the way in from the arch left open
        check(SpectatorRules.StartShare < 0.20f && SpectatorRules.FinishShare == 0.30f, "fewer groups gather near the start than before (20 %), as many near the finish");
        check(SpectatorRules.RingCount(36) == 6 && SpectatorRules.RingCount(508) == 61 && SpectatorRules.RingCount(12) == 4 && SpectatorRules.RingCount(4) == 0, "about 12 % of the crowd stands round the finish area, at least 6, never more than a third");
        bool wayIn = true, spread = true; float last = -999f;
        for (int i = 0; i < 61; i++)
        {
            float a = SpectatorRules.RingAngleDeg(i, 61, i % 2 == 0 ? 0.5f : -0.5f);
            if (Math.Abs(a) > 180f - SpectatorRules.RingGapDeg) wayIn = false;
            if (a <= last) spread = false;
            last = a;
        }
        check(wayIn, "nobody stands in the way in from the arch (40 degrees either side of it)");
        check(spread, "the people go round the area in order, each within their own share of it");
        check(Math.Abs(SpectatorRules.RingAngleDeg(0, 1, 0f)) < 0.01f, "one person alone stands straight on past the arch");
        check(SpectatorRules.RingRows(6, 14f) == 1 && SpectatorRules.RingRows(100, 14f) == 2 && SpectatorRules.RingRows(500, 14f) == 3, "a big crowd stands in rows, at most 3");
        check(SpectatorRules.RingOff(11f, true, 0, 0) == 11f + SpectatorRules.RingBehindM, "the front row stands just behind the barrier");
        check(SpectatorRules.RingOff(11f, true, 1, 0) > SpectatorRules.RingOff(11f, true, 0, 0) && SpectatorRules.RingOff(11f, true, 0, 1) > SpectatorRules.RingOff(11f, true, 0, 0), "the next row and a retry stand further out");
        check(SpectatorRules.RingOff(0f, false, 0, 0) == SpectatorRules.RingDefaultM, "no barrier in a direction: 14 m out");
        check(SpectatorRules.RingOff(2f, true, 0, 0) == SpectatorRules.RingMinM + SpectatorRules.RingBehindM, "something right beside the middle is not the edge of the area");

        // placed a few per frame, so the game does not freeze as the rider leaves the gate
        check(SpectatorRules.KeepBuilding(0.0, 0) && SpectatorRules.KeepBuilding(99.0, 0), "at least one spectator is placed in a frame, however slow the frame");
        check(SpectatorRules.KeepBuilding(3.9, 3) && !SpectatorRules.KeepBuilding(4.0, 3), "placing stops for this frame after 4 ms");

        // animal heads: those spectators play the motion-capture clips, everyone else cheers as before
        check(SpectatorRules.IsAnimalHead("Dino") && SpectatorRules.IsAnimalHead("Penguin") && SpectatorRules.IsAnimalHead("shark") && SpectatorRules.IsAnimalHead(" Unicorn "), "a dinosaur, penguin, shark or unicorn head is an animal head");
        check(!SpectatorRules.IsAnimalHead("Bear Ears") && !SpectatorRules.IsAnimalHead("Antlers") && !SpectatorRules.IsAnimalHead("Top Hat") && !SpectatorRules.IsAnimalHead("Blue Mohawk")
            && !SpectatorRules.IsAnimalHead("") && !SpectatorRules.IsAnimalHead(null), "ears, antlers, hats, mohawks and a bare head are not");

        // fingers: the game numbers a finger's bones from the end inwards; taken the other way round every finger bent backwards
        check(SpectatorRules.BendingJoints(4).SequenceEqual(new[] { 3, 2, 1 }), "a finger bends at its bones 3 (the knuckle), 2 and 1 (the last joint), in that order; bone 4, inside the palm, is left alone");
        check(SpectatorRules.BendingJoints(3).SequenceEqual(new[] { 3, 2, 1 }) && SpectatorRules.BendingJoints(2).SequenceEqual(new[] { 2, 1 }), "the thumb, with three bones, bends at all of them from the inside out");
        check(SpectatorRules.BendingJoints(1).Length == 0 && SpectatorRules.BendingJoints(0).Length == 0, "a finger with one bone or none found is left alone");

        // horns: a few short paps, then the full hoot; the bells are not horns
        check(SpectatorRules.IsHorn("event:/sfx/noisemakers/horn/classic_1") && SpectatorRules.IsHorn("event:/sfx/noisemakers/horn/vuvu_good"), "the air horn and the vuvuzela are horns");
        check(!SpectatorRules.IsHorn("event:/sfx/noisemakers/bell/cow_high") && !SpectatorRules.IsHorn(null), "a cow bell is not a horn: it rings as before");
        check(SpectatorRules.HornPaps(0.0) == 2 && SpectatorRules.HornPaps(0.5) == 3 && SpectatorRules.HornPaps(0.999) == 4 && SpectatorRules.HornPaps(1.0) == 4, "a horn gives 2 to 4 paps");
        check(SpectatorRules.IsPap(true, 2) && SpectatorRules.IsPap(true, 1) && !SpectatorRules.IsPap(true, 0), "the last sound of a horn burst is the full hoot, the ones before are paps");
        check(!SpectatorRules.IsPap(false, 2), "a bell is never cut short");
        check(SpectatorRules.PapLevel(0f) == 1f && SpectatorRules.PapLevel(SpectatorRules.PapS - SpectatorRules.PapFadeS / 2f) > 0.4f && SpectatorRules.PapLevel(SpectatorRules.PapS - SpectatorRules.PapFadeS / 2f) < 0.6f && SpectatorRules.PapLevel(SpectatorRules.PapS) == 0f, "a pap is full, fades at its end and is over after 0.14 s");
        check(SpectatorRules.PapGapS > SpectatorRules.PapS, "one pap is over before the next sound starts");

        // getting out of the rider's way (riding off course used to go straight through the crowd)
        float lat;
        check(!SpectatorRules.Threatened(0f, 5f, 15f, 0f, out lat), "a rider on the course, 5 m to the side of a spectator, is no threat");
        check(SpectatorRules.Threatened(7f, -0.5f, 10f, 0f, out lat) && lat > 0f, "a rider heading at a spectator 7 m on (0.7 s away) is a threat; the spectator is on the rider's right (travel +x, right is -z)");
        check(SpectatorRules.Threatened(7f, 0.5f, 10f, 0f, out lat) && lat < 0f, "...and on the left for the other side");
        check(!SpectatorRules.Threatened(7f, 1.5f, 10f, 0f, out lat), "passing 1.5 m off is not a threat (it was 2 m; the crowd scattered when the rider was just riding the course)");
        check(!SpectatorRules.Threatened(12f, 0f, 10f, 0f, out lat), "1.2 s away: not yet (was 1.7 s)");
        check(!SpectatorRules.Threatened(20f, 0f, 30f, 0f, out lat), "fast rider, 0.67 s but 20 m away: not yet (heading wobble at that range is not aim)");
        check(SpectatorRules.Threatened(12f, 0f, 25f, 0f, out lat), "fast rider 12 m away and dead on: a threat");
        check(!SpectatorRules.Threatened(-3f, 0.5f, 10f, 0f, out lat), "already past: no threat");
        check(!SpectatorRules.Threatened(8f, 0f, 2f, 0f, out lat), "a rider at walking pace is no threat");
        check(SpectatorRules.Threatened(0.5f, 0.5f, 10f, 0f, out lat), "a rider just about on top of a spectator is a threat (not behind)");
        float ex, ez;
        SpectatorRules.EscapeDir(10f, 0f, 1f, 1f, out ex, out ez);
        check(Math.Abs(ex) < 1e-4f && Math.Abs(ez + 1f) < 1e-4f, "a spectator on the rider's right (travel +x: right is -z) runs to -z, out of the line at right angles");
        SpectatorRules.EscapeDir(0f, 8f, -1f, 1f, out ex, out ez);
        check(Math.Abs(ez) < 1e-4f && Math.Abs(ex + 1f) < 1e-4f, "heading along +z, a spectator on the left runs to -x: away from the line, on its own side");
        SpectatorRules.EscapeDir(10f, 0f, 0f, 1f, out ex, out ez);
        float ex2, ez2;
        SpectatorRules.EscapeDir(10f, 0f, 0f, -1f, out ex2, out ez2);
        check(ez * ez2 < 0f && Math.Abs(ex) < 1e-4f, "dead on the line: either side, chosen by the tie-break");
        bool never = true;   // the escape is always on the spectator's own side of the rider's line, whatever way the rider heads (it was run across the line when the far side was blocked)
        for (int deg = 0; deg < 360; deg += 15)
            foreach (float l in new[] { -1.1f, -0.4f, 0.4f, 1.1f })
            {
                float vx = (float)Math.Cos(deg * Math.PI / 180.0) * 12f, vz = (float)Math.Sin(deg * Math.PI / 180.0) * 12f;
                SpectatorRules.EscapeDir(vx, vz, l, 1f, out ex, out ez);
                float onRight = ex * vz - ez * vx;   // the right of travel is (vz, -vx)
                if (onRight * l <= 0f || Math.Abs(ex * vx + ez * vz) > 1e-3f) never = false;
            }
        check(never, "a spectator always runs sideways off the line, to their own side of it");        check(SpectatorRules.RunDistance(0f) > SpectatorRules.RunDistance(2f) && SpectatorRules.RunDistance(3.9f) >= 1.5f && SpectatorRules.RunDistance(0f) <= 7f, "the nearer the line, the further to run; never less than 1.5 m or more than 7 m");
        check(SpectatorRules.StepOk(0.2f, 0.1f) && !SpectatorRules.StepOk(1.5f, 0.1f) && !SpectatorRules.StepOk(-2f, 0.1f) && SpectatorRules.StepOk(-0.3f, 0.1f), "a runner does not climb a wall or step off a cliff");
        check(!SpectatorRules.MayReturn(1f, false, 30f) && !SpectatorRules.MayReturn(5f, true, 30f) && !SpectatorRules.MayReturn(5f, false, 4f) && SpectatorRules.MayReturn(5f, false, 30f), "walks back only after a while, when the rider is not coming back at the place and not close");
        float hz0, sw0, lf0, hzW, swW, lfW, hzR, swR, lfR;
        SpectatorRules.Gait(0f, out hz0, out sw0, out lf0);
        SpectatorRules.Gait(SpectatorRules.DodgeWalkMs, out hzW, out swW, out lfW);
        SpectatorRules.Gait(SpectatorRules.DodgeRunMs, out hzR, out swR, out lfR);
        check(hz0 == 0f && sw0 == 0f && lf0 == 0f, "standing: no stepping");
        check(hzR > hzW && swR > swW && lfR > lfW && hzW > 0f, "running steps faster, further and higher than walking");
        check(hzR > 1.5f && hzR < 3.5f && swR < 0.5f && lfR < 0.25f, "a run is 1.5 to 3.5 cycles a second with feet swinging under half a metre");
        float hzS, swS, lfS;
        SpectatorRules.Gait(0.5f, out hzS, out swS, out lfS);
        check(swS < swW && lfS < lfW, "slowing to a stop brings the legs in gradually");

        // skin tones from the game's palette, spread evenly over the crowd
        Console.WriteLine("Spectators: skin tones from the game's own palette, spread evenly over the crowd");
        float[] pr = { 0.95f, 0.30f }, pg = { 0.80f, 0.20f }, pb = { 0.70f, 0.10f };   // the two palettes' colours at the rider's index: skin, hair
        check(SpectatorRules.SkinPalette(pr, pg, pb, 0.951f, 0.795f, 0.705f) == 0, "the palette whose colour at the rider's skin index is the rider's tint is the skin palette");
        check(SpectatorRules.SkinPalette(pr, pg, pb, 0.30f, 0.20f, 0.10f) == 1, "the order of the palettes does not matter, the match does");
        check(SpectatorRules.SkinPalette(pr, pg, pb, 0.5f, 0.5f, 0.5f) == -1, "no palette matches the tint: unknown (the caller takes the first)");
        float[] same = { 0.9f, 0.9f };
        check(SpectatorRules.SkinPalette(same, same, same, 0.9f, 0.9f, 0.9f) == -1, "both palettes match: unknown, never a guess");

        var deck = new int[6]; int next = 0; var trng = new Random(3);
        var seen = new int[6]; bool inRange = true;
        for (int i = 0; i < 6 * 5; i++) { int t = SpectatorRules.NextTone(deck, ref next, trng); if (t < 0 || t >= 6) inRange = false; else seen[t]++; }
        bool even = true; foreach (int c in seen) if (c != 5) even = false;
        check(inRange && even, "30 spectators over 6 tones: every tone exactly 5 times (an even spread, not plain random picks)");
        int next2 = 0, next3 = 0; var d2 = new int[6]; var d3 = new int[6]; var r2 = new Random(3); var r3 = new Random(4);
        bool differs = false;
        for (int i = 0; i < 12; i++) if (SpectatorRules.NextTone(d2, ref next2, r2) != SpectatorRules.NextTone(d3, ref next3, r3)) differs = true;
        check(differs, "the order is shuffled, not the palette from light to dark along the course");
        int next4 = 0;
        check(SpectatorRules.NextTone(new int[0], ref next4, trng) == -1 && SpectatorRules.NextTone(null, ref next4, trng) == -1, "no palette: no tone (the copy keeps the rider's)");

        // each course its own crowd (it was the same on every course: fixed seeds, a fixed number of draws)
        Console.WriteLine("Spectators: each course has its own crowd, and the same course keeps it");
        float[] ax = { 0f, 10f, 20.3f, 31f }, az = { 0f, 5f, 9.9f, 15f }, bx = { 0f, 12f, 22f, 35f }, bz = { 0f, 4f, 11f, 13f };
        check(SpectatorRules.CourseSeed(ax, az, 7) == SpectatorRules.CourseSeed((float[])ax.Clone(), (float[])az.Clone(), 7), "the same course gives the same seed: the same crowd every ride");
        check(SpectatorRules.CourseSeed(ax, az, 7) != SpectatorRules.CourseSeed(bx, bz, 7), "another course, another seed: another crowd");
        check(SpectatorRules.CourseSeed(ax, az, 7) != SpectatorRules.CourseSeed(ax, az, 11), "each random stream (outfits, body types, skin) its own seed");
        float[] axNoise = { 0f, 10.01f, 20.3f, 31f };
        check(SpectatorRules.CourseSeed(ax, az, 7) == SpectatorRules.CourseSeed(axNoise, az, 7), "a centimetre of difference in the path is the same course");
        check(SpectatorRules.CourseSeed(ax, az, 7) >= 0 && SpectatorRules.CourseSeed(new float[0], new float[0], 7) >= 0, "a seed is never negative, even with no path");

        // the T-rex clip: about 2 % of the crowd, not every animal's head (about a fifth of it)
        Console.WriteLine("Spectators: the motion-capture clip is played by about 2 % of the crowd");
        var crng = new Random(5); int clips = 0, animalsSeen = 0;
        for (int placed = 0; placed < 500; placed++)
        {
            bool animal = crng.NextDouble() < 0.22;
            if (animal) animalsSeen++;
            if (SpectatorRules.PlaysClip(animal, placed, clips, 0.02f)) clips++;
        }
        check(clips >= 8 && clips <= 10 && animalsSeen > 80, "500 spectators, about a fifth with an animal's head: 8 to 10 play the clip, not " + animalsSeen + " (got " + clips + ")");
        check(!SpectatorRules.PlaysClip(false, 100, 0, 0.02f), "only a spectator with an animal's head plays the clip");
        check(SpectatorRules.PlaysClip(true, 0, 0, 0.02f), "the first animal's head near the start plays it: a small crowd still shows one");
        check(!SpectatorRules.PlaysClip(true, 10, 1, 0.02f), "one clip player in the first 11: no second until about 50 are placed");
        check(!SpectatorRules.PlaysClip(true, 0, 0, 0f), "share 0: nobody plays it");
    }
}
