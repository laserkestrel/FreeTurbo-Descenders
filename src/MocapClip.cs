#nullable disable

using System;
using System.IO;

// Pure logic (no Unity, no logging): also compiled into the bridge (bridge/DescendersBridge.csproj links this file) so `selftest` checks it
// (tests\unit\MocapTests.cs), and into the converter that writes the clips (a private developer tool).
namespace FreeTurboDescenders
{
    /// <summary>The bones a motion-capture clip moves, in the order they are stored. Parents come before children.</summary>
    internal static class MocapBone
    {
        public const int Pelvis = 0, LowerBack = 1, UpperBack = 2, Thorax = 3, LowerNeck = 4, UpperNeck = 5, Head = 6,
                         LUpperArm = 7, LForearm = 8, LHand = 9, RUpperArm = 10, RForearm = 11, RHand = 12,
                         LThigh = 13, LShin = 14, LFoot = 15, RThigh = 16, RShin = 17, RFoot = 18, Count = 19;
    }

    /// <summary>
    /// A recorded human motion for the spectators (docs/SPECTATORS.md "Motion capture"), made by tools\MocapBake from a motion-capture file.
    /// Frame of reference: x = the character's left, y = up, z = the character's forward (right-handed), turned so the actor faces +z on average.
    /// Every bone has, per frame, the rotation that takes it from the capture's rest pose (a T-pose: arms straight out, legs straight down and a
    /// little apart) to where it is in that frame, in that frame of reference and NOT relative to its parent. So a bone's direction in a frame is
    /// that rotation applied to <see cref="RestDir"/>, whatever skeleton plays it. The pelvis also has where it is (sideways and forward from the
    /// middle of the clip) and how far the lower foot is off the ground, both in leg lengths. The clip loops: its end was blended into its start.
    /// </summary>
    internal sealed class MocapClip
    {
        private const int Magic = 0x31434D44;   // "DMC1"
        private const float RotScale = 32767f, RootScale = 4096f;

        public string Name = "";
        public float Fps = 30f;
        public int Frames;
        public float[] RestDir = new float[MocapBone.Count * 3];   // where each bone points in the rest pose (unit, to its child)
        public short[] Rot = new short[0];                         // Frames x Count x (x, y, z, w)
        public short[] Root = new short[0];                        // Frames x (sideways, forward, foot off the ground), leg lengths x RootScale

        public float Seconds { get { return Fps > 0f ? Frames / Fps : 0f; } }

        public void SetRotation(int frame, int bone, float x, float y, float z, float w)
        {
            float n = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            if (n < 1e-9f) { x = y = z = 0f; w = 1f; n = 1f; }
            int i = (frame * MocapBone.Count + bone) * 4;
            Rot[i] = Pack(x / n, RotScale); Rot[i + 1] = Pack(y / n, RotScale); Rot[i + 2] = Pack(z / n, RotScale); Rot[i + 3] = Pack(w / n, RotScale);
        }

        public void SetRoot(int frame, float side, float forward, float lift)
        {
            int i = frame * 3;
            Root[i] = Pack(side, RootScale); Root[i + 1] = Pack(forward, RootScale); Root[i + 2] = Pack(lift, RootScale);
        }

        private static short Pack(float v, float scale)
        {
            float s = v * scale;
            return (short)(s > 32767f ? 32767 : s < -32767f ? -32767 : (int)Math.Round(s));
        }

        /// <summary>A bone's rotation t seconds into the clip (any t: the clip loops), blended between the two frames around it. Unit length.</summary>
        public void Rotation(float t, int bone, out float x, out float y, out float z, out float w)
        {
            int a, b; float k;
            Around(t, out a, out b, out k);
            if (Frames <= 0) { x = y = z = 0f; w = 1f; return; }
            int i = (a * MocapBone.Count + bone) * 4, j = (b * MocapBone.Count + bone) * 4;
            float ax = Rot[i], ay = Rot[i + 1], az = Rot[i + 2], aw = Rot[i + 3], bx = Rot[j], by = Rot[j + 1], bz = Rot[j + 2], bw = Rot[j + 3];
            if (ax * bx + ay * by + az * bz + aw * bw < 0f) { bx = -bx; by = -by; bz = -bz; bw = -bw; }   // the short way round
            x = ax + (bx - ax) * k; y = ay + (by - ay) * k; z = az + (bz - az) * k; w = aw + (bw - aw) * k;
            float n = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            if (n < 1e-6f) { x = y = z = 0f; w = 1f; return; }
            x /= n; y /= n; z /= n; w /= n;
        }

        /// <summary>Where the pelvis is t seconds into the clip (leg lengths): sideways (to the left), forward, and the lower foot's height off the ground.</summary>
        public void RootAt(float t, out float side, out float forward, out float lift)
        {
            int a, b; float k;
            Around(t, out a, out b, out k);
            if (Frames <= 0) { side = forward = lift = 0f; return; }
            int i = a * 3, j = b * 3;
            side = (Root[i] + (Root[j] - Root[i]) * k) / RootScale;
            forward = (Root[i + 1] + (Root[j + 1] - Root[i + 1]) * k) / RootScale;
            lift = (Root[i + 2] + (Root[j + 2] - Root[i + 2]) * k) / RootScale;
        }

        private void Around(float t, out int a, out int b, out float k)
        {
            a = b = 0; k = 0f;
            if (Frames <= 0) return;
            float f = t * Fps;
            if (float.IsNaN(f) || float.IsInfinity(f)) f = 0f;
            f -= (float)Math.Floor(f / Frames) * Frames;
            a = (int)f;
            if (a >= Frames) a = Frames - 1;
            k = f - a;
            b = a + 1 < Frames ? a + 1 : 0;
        }

        public void Write(Stream s)
        {
            var w = new BinaryWriter(s);
            w.Write(Magic); w.Write(Name ?? ""); w.Write(Fps); w.Write(Frames); w.Write(MocapBone.Count);
            for (int i = 0; i < RestDir.Length; i++) w.Write(RestDir[i]);
            for (int i = 0; i < Rot.Length; i++) w.Write(Rot[i]);
            for (int i = 0; i < Root.Length; i++) w.Write(Root[i]);
            w.Flush();
        }

        /// <summary>Read a clip; null when the bytes are not one (wrong start, another bone count, cut short).</summary>
        public static MocapClip Read(Stream s)
        {
            try
            {
                var r = new BinaryReader(s);
                if (r.ReadInt32() != Magic) return null;
                var c = new MocapClip { Name = r.ReadString(), Fps = r.ReadSingle(), Frames = r.ReadInt32() };
                if (r.ReadInt32() != MocapBone.Count || c.Frames <= 0 || c.Frames > 200000 || !(c.Fps > 0f)) return null;
                for (int i = 0; i < c.RestDir.Length; i++) c.RestDir[i] = r.ReadSingle();
                c.Rot = new short[c.Frames * MocapBone.Count * 4];
                for (int i = 0; i < c.Rot.Length; i++) c.Rot[i] = r.ReadInt16();
                c.Root = new short[c.Frames * 3];
                for (int i = 0; i < c.Root.Length; i++) c.Root[i] = r.ReadInt16();
                return c;
            }
            catch (IOException) { return null; }
        }

        public static MocapClip Empty(string name, float fps, int frames)
        {
            var c = new MocapClip { Name = name, Fps = fps, Frames = frames, Rot = new short[frames * MocapBone.Count * 4], Root = new short[frames * 3] };
            for (int f = 0; f < frames; f++) for (int b = 0; b < MocapBone.Count; b++) c.Rot[(f * MocapBone.Count + b) * 4 + 3] = 32767;
            return c;
        }
    }
}
