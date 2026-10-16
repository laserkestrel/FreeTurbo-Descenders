using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// The clip a spectator plays (src\MocapClip.cs, in the mod): a recorded human movement, per bone and frame the rotation from the rest pose,
/// looping. How a clip is made from a recording is the converter's business and is tested there.
/// </summary>
public static class MocapTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Motion capture: the clip a spectator plays");

        // 90 frames at 30 a second: the left upper arm, straight out to the left at rest, turns 1 degree further up (about z) every frame
        MocapClip clip = MocapClip.Empty("test", 30f, 90);
        clip.RestDir[MocapBone.LUpperArm * 3] = 1f;
        for (int f = 0; f < clip.Frames; f++)
        {
            double a = f * Math.PI / 180 / 2;
            clip.SetRotation(f, MocapBone.LUpperArm, 0f, 0f, (float)Math.Sin(a) * 3f, (float)Math.Cos(a) * 3f);   // not unit length: stored as one
            clip.SetRoot(f, 0.1f, -0.2f, f == 30 ? 0.25f : 0f);
        }
        check(Math.Abs(clip.Seconds - 3f) < 1e-5f, "90 frames at 30 a second are 3 seconds");
        check(Near(Dir(clip, 1f, MocapBone.LUpperArm), Cos(30), Sin(30), 0), "a bone points where its rotation turns its rest direction: 30 degrees up after one second");
        check(Near(Dir(clip, 1f + 0.5f / 30f, MocapBone.LUpperArm), Cos(30.5), Sin(30.5), 0), "between two frames it is between them");
        check(Near(Dir(clip, 1f, MocapBone.Head), 0, 0, 0) && Unit(clip, 0.777f, MocapBone.LUpperArm) && Unit(clip, 0.777f, MocapBone.Head),
            "a rotation read from the clip has unit length; a bone that was never set is not turned");

        // looping
        check(Near(Dir(clip, 1f, MocapBone.LUpperArm), Dir(clip, 1f + clip.Seconds * 3, MocapBone.LUpperArm)) && Near(Dir(clip, 1f, MocapBone.LUpperArm), Dir(clip, 1f - clip.Seconds, MocapBone.LUpperArm)),
            "any time is a time in the clip: later and earlier than it wrap round");
        check(Near(Dir(clip, clip.Seconds - 0.5f / 30f, MocapBone.LUpperArm), Cos(44.5), Sin(44.5), 0), "after the last frame comes the first: half-way between them the bone is half-way (89 and 0 degrees)");
        check(Near(Dir(clip, float.NaN, MocapBone.LUpperArm), 1, 0, 0), "a time that is no number is the start, not a crash");

        // the short way round: the same turn written with the other sign must not send the blend the long way
        MocapClip two = MocapClip.Empty("two", 30f, 2);
        two.RestDir[0] = 1f;
        two.SetRotation(0, 0, 0f, 0f, (float)Math.Sin(Math.PI / 36), (float)Math.Cos(Math.PI / 36));     // 10 degrees
        two.SetRotation(1, 0, 0f, 0f, -(float)Math.Sin(Math.PI / 12), -(float)Math.Cos(Math.PI / 12));   // 30 degrees, written the other way
        check(Near(Dir(two, 0.5f / 30f, 0), Cos(20), Sin(20), 0), "two frames 20 degrees apart blend through 20, whichever sign the rotation was written with");

        // where the pelvis is
        float side, forward, lift;
        clip.RootAt(1f, out side, out forward, out lift);
        check(Math.Abs(side - 0.1f) < 0.001f && Math.Abs(forward + 0.2f) < 0.001f && Math.Abs(lift - 0.25f) < 0.001f, "the pelvis is where the frame says: sideways, forward, and the lower foot's height off the ground");
        clip.RootAt(1f + 0.5f / 30f, out side, out forward, out lift);
        check(Math.Abs(lift - 0.125f) < 0.001f, "and between two frames it is between them");
        clip.SetRoot(0, 100f, -100f, 0f);
        clip.RootAt(0f, out side, out forward, out lift);
        check(side > 7.9f && side < 8.1f && forward < -7.9f && forward > -8.1f, "a place too far to store is held at the furthest that can be (8 leg lengths), not wrapped to the other side");

        // the file
        var ms = new MemoryStream();
        clip.Write(ms);
        byte[] bytes = ms.ToArray();
        MocapClip back = MocapClip.Read(new MemoryStream(bytes));
        check(back != null && back.Name == "test" && back.Frames == clip.Frames && back.Fps == clip.Fps && back.Rot.SequenceEqual(clip.Rot) && back.Root.SequenceEqual(clip.Root)
            && back.RestDir.SequenceEqual(clip.RestDir), "a clip written to a file reads back the same");
        check(MocapClip.Read(new MemoryStream(bytes, 0, bytes.Length / 2)) == null && MocapClip.Read(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })) == null
            && MocapClip.Read(new MemoryStream()) == null, "a file cut short, or one that is no clip, is refused and does not crash");
        MocapClip none = MocapClip.Empty("none", 30f, 0);
        none.Rotation(1f, 0, out float x, out float y, out float z, out float w);
        check(x == 0f && y == 0f && z == 0f && w == 1f && none.Seconds == 0f, "a clip with no frames turns nothing");
    }

    private static double Cos(double deg) => Math.Cos(deg * Math.PI / 180);
    private static double Sin(double deg) => Math.Sin(deg * Math.PI / 180);

    /// <summary>Where the bone points at t: its rest direction turned by the clip's rotation.</summary>
    private static double[] Dir(MocapClip c, float t, int bone)
    {
        c.Rotation(t, bone, out float x, out float y, out float z, out float w);
        double vx = c.RestDir[bone * 3], vy = c.RestDir[bone * 3 + 1], vz = c.RestDir[bone * 3 + 2];
        double tx = 2 * (y * vz - z * vy), ty = 2 * (z * vx - x * vz), tz = 2 * (x * vy - y * vx);
        return new[] { vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx) };
    }

    private static bool Unit(MocapClip c, float t, int bone)
    {
        c.Rotation(t, bone, out float x, out float y, out float z, out float w);
        return Math.Abs(Math.Sqrt(x * x + y * y + z * z + w * w) - 1) < 1e-4;
    }

    private static bool Near(double[] v, double x, double y, double z) => Math.Abs(v[0] - x) < 0.01 && Math.Abs(v[1] - y) < 0.01 && Math.Abs(v[2] - z) < 0.01;
    private static bool Near(double[] a, double[] b) => Near(a, b[0], b[1], b[2]);
}
