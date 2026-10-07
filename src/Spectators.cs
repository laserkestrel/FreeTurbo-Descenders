using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// EXPERIMENT (docs/SPECTATORS.md): a gang of copies of the rider standing beside the course, off the bike, arms up, cheering.
    /// The rider object carries the vehicle physics, a ragdoll (PuppetMaster + joints), Photon networking and sounds, so it is NOT cloned live:
    /// it is instantiated once under a deactivated parent (no Awake / OnEnable runs on anything), stripped down to its meshes, and that stripped
    /// template is then copied along the path. Nothing on a spectator can collide, make sound or talk to the network; the only motion is the
    /// procedural cheer that <see cref="Tick"/> applies to the skeleton. The pose comes from the mesh's own bind pose (the rest pose it was authored in).
    /// </summary>
    internal sealed class Spectators
    {
        // Each spectator has a small state machine: a style chosen from a calm set, or from an excited set when the rider is close (switched a beat late,
        // each at their own pace), held for 6-14 s. Every frame the skeleton goes back to the rest pose and is built up again, parents first:
        //   body   - pelvis height and foot height per style (knees never locked, a dip before a jump, a flight, a landing), a slow weight shift from foot
        //            to foot, legs solved by two-bone IK so the feet stay planted;
        //   torso  - forward lean when excited or crouching, breathing, and a look (head and chest turn together, the head most) that follows the rider
        //            when close and wanders otherwise;
        //   arms   - a style gives the upper-arm and forearm directions, in the chest's frame so the cheer turns with the body, either directly or from a
        //            hand target by IK (clap, hands on hips, cupped hands). The directions are springs, the forearm softer than the upper arm, which gives
        //            lag, follow-through and smooth style changes. The hand is twisted about the forearm so the thumb points where the style wants.
        // Timing uses shaped cycles (fast up and slow down, a clap that holds on contact) on a per-spectator clock whose tempo drifts with Perlin noise.
        private enum Style { Idle, Hips, Wave, Clap, FistPump, Shout, Pump, Jump }
        private static readonly Style[] CalmSet = { Style.Idle, Style.Hips, Style.Wave, Style.Clap, Style.Idle, Style.Clap, Style.Hips, Style.FistPump };
        private static readonly Style[] ExcitedSet = { Style.Pump, Style.Jump, Style.FistPump, Style.Wave, Style.Shout, Style.Clap, Style.Pump, Style.Jump };

        private struct Spring { public Vector3 X, V; }         // a unit direction with a velocity

        private sealed class Cheerer
        {
            public Transform Root, Head;
            public int Id;                                     // 1-based, same as the "Spectator_N" object name
            // bones; null when the rig lacks one (that part of the motion is skipped)
            public Transform Pelvis, Spine2, Spine3, Spine4, Spine5, Chest, Neck1, Neck2;
            public Transform ClavL, ClavR, ShoulderL, ShoulderR, ElbowL, ElbowR, WristL, WristR, IndexL, IndexR, PinkL, PinkR;
            public Transform HipL, HipR, KneeL, KneeR, AnkleL, AnkleR;
            public Transform[] Anim; public Quaternion[] RestLocal;   // every bone Animate moves, and its rest local rotation (restored each frame)
            public Quaternion ChestRest, AnkleRestL, AnkleRestR;      // world rotations in the rest pose
            public Vector3 AnkleL0, AnkleR0;                          // world ankle positions in the rest pose: the feet are planted there
            public float UpperLen, ForeLen;
            public bool Legs;                                         // leg IK possible (hip, knee and ankle found on both sides)
            public Vector3 Fwd, Left, BasePos;                 // horizontal facing, character's left, resting position
            public Vector3 F, L, U;                            // this frame's chest frame (forward, left, up)
            public float Phase, Speed, Amp, Handed, Variant;   // Handed: +1 = left arm is the active one, -1 = right; Variant: per-style random 0..1
            public bool Shown = true, Excited, OtherOnHip;
            public Style Cur;
            public float NextChange, ReactAt = -1f, Clock, P, Crouch;   // P: phase 0..1 in the current style's cycle; Crouch 0..1 in a jump's dip
            public Spring UL, FL, UR, FR, TL, TR;              // upper arm, forearm and thumb directions
            public float Yaw, YawV, Pitch, PitchV, Lean, LeanV, PY, PYV, FY, FYV, YawHold;   // look (degrees), lean (rad), pelvis and feet height (m)
            public Transform[][] FingersL, FingersR;           // per finger the joints from the knuckle out (the last one is the tip)
            public Vector3 PalmL, PalmR;                       // palm normal in the wrist's local frame
            public float CurlL, CurlLV, CurlR, CurlRV;         // 0 open hand .. 1 fist
            public int Body = -1;                              // rider body type index, -1 = the rider's own
            public int Noise = -1;                             // index into Config.SpectatorNoiseEvents, -1 = empty-handed
            public float NoiseAt = -1f;                        // when the noisemaker sounds next (-1 = not excited)
            public int RingsLeft;                              // further rings still to come in this shake of a bell
            public float RingAt;
            public System.Random Rng;
        }

        private GameObject _root;
        private readonly List<Cheerer> _cheerers = new List<Cheerer>();
        public bool Active { get { return _root != null; } }
        public int Count;

        public void Toggle(PlayerProbe probe, PathTrack track)
        {
            if (Active) { Clear("key"); return; }
            try { Spawn(probe, track); }
            catch (Exception ex) { Log.Error("Spectators: spawn failed", ex); Clear("error"); }
        }

        public void Clear(string why)
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            if (_root != null || Count > 0) Log.Info("Spectators: removed (" + why + ")");
            _root = null; Count = 0; _cheerers.Clear();
        }

        /// <summary>Per-frame: cull the far ones, let the near ones react to the rider, and animate whatever is drawn.</summary>
        public void Tick(float time, Vector3 riderPos, bool haveRider)
        {
            float dt = Mathf.Clamp(time - _now, 0f, 0.05f);
            _now = time;
            if (_cheerers.Count == 0) return;
            _watch.Reset(); _watch.Start();
            int shown = 0;
            float cull2 = Config.SpectatorCullM * Config.SpectatorCullM;
            float near = Config.SpectatorExciteM, near2 = near * near, far2 = (near * 1.4f) * (near * 1.4f);   // hysteresis: excited inside near, calm again beyond 1.4 x near
            float look = Mathf.Max(near * 2f, 35f), look2 = look * look;                                          // heads follow the rider inside this
            foreach (Cheerer c in _cheerers)
            {
                if (c.Root == null) continue;
                float d2 = haveRider ? (c.BasePos - riderPos).sqrMagnitude : float.MaxValue;
                if (haveRider)   // only the ones near the rider are drawn and animated: hundreds of skinned meshes would cost frames for nothing
                {
                    bool show = d2 <= cull2;
                    if (show != c.Shown) { c.Root.gameObject.SetActive(show); c.Shown = show; }
                }
                if (!c.Shown) continue;
                shown++;

                bool want = c.Excited ? d2 < far2 : d2 < near2;
                if (want == c.Excited) c.ReactAt = -1f;
                else if (c.ReactAt < 0f) c.ReactAt = time + 0.15f + (float)c.Rng.NextDouble() * 0.5f;   // people react a beat late, each at their own pace
                else if (time >= c.ReactAt) { ChangeStyle(c, want, time); c.ReactAt = -1f; }
                if (time >= c.NextChange) ChangeStyle(c, c.Excited, time);
                if (c.Noise >= 0) Noisemaker(c, time);
                Animate(c, dt, time, riderPos, haveRider && d2 < look2);
            }
            _watch.Stop();
            if (shown > 0) { _perfTicks += _watch.ElapsedTicks; _perfFrames++; _perfShown += shown; }
            if (time >= _perfLogAt)
            {
                if (_perfFrames > 0)
                    Log.Info("Spectators: animation " + (_perfTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / _perfFrames).ToString("F0") + " us/frame for "
                        + (_perfShown / (float)_perfFrames).ToString("F1") + " shown (average over " + _perfFrames + " frames)");
                _perfTicks = 0; _perfFrames = 0; _perfShown = 0; _perfLogAt = time + 30f;
            }
        }

        private float _noiseFree;   // no two noisemakers start within NoiseSpacingS of each other: a crowd, not a wall of sound
        private const float NoiseSpacingS = 0.3f;
        private const float BellRingS = 0.22f;   // between the rings of one bell shake (the samples are 0.4-0.8 s, so they overlap like a real shaken bell)

        /// <summary>An excited spectator with a noisemaker sounds it a beat after getting excited (a bell is shaken: SpectatorBellRings rings), then every SpectatorNoiseGapS or so, from their head.</summary>
        private void Noisemaker(Cheerer c, float time)
        {
            string[] ev = Config.SpectatorNoiseEvents;
            if (c.Noise >= ev.Length) return;
            Vector3 at = c.Head != null ? c.Head.position : c.Root.position + Vector3.up * 1.6f;
            if (c.RingsLeft > 0 && time >= c.RingAt)   // the rest of a bell shake: not held back by the spacing, it is one gesture
            {
                Sfx.PlayAt(ev[c.Noise], at, Config.SpectatorNoiseVolume * (0.8f + 0.2f * (float)c.Rng.NextDouble()));
                c.RingsLeft--; c.RingAt = time + BellRingS * (0.8f + 0.4f * (float)c.Rng.NextDouble());
            }
            if (!c.Excited) { c.NoiseAt = -1f; return; }
            if (c.NoiseAt < 0f) { c.NoiseAt = time + 0.2f + (float)c.Rng.NextDouble() * 1.3f; return; }
            if (time < c.NoiseAt || time < _noiseFree) return;
            Sfx.PlayAt(ev[c.Noise], at, Config.SpectatorNoiseVolume * (0.8f + 0.2f * (float)c.Rng.NextDouble()));
            if (ev[c.Noise].IndexOf("bell", StringComparison.OrdinalIgnoreCase) >= 0) { c.RingsLeft = Config.SpectatorBellRings - 1; c.RingAt = time + BellRingS * (0.8f + 0.4f * (float)c.Rng.NextDouble()); }
            c.NoiseAt = time + Config.SpectatorNoiseGapS * (1f + (float)c.Rng.NextDouble());
            _noiseFree = time + NoiseSpacingS;
        }

        private readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();
        private long _perfTicks;
        private int _perfFrames, _perfShown;
        private float _perfLogAt;

        // ---- debug labels ----------------------------------------------------------------------------------------------------------------
        // A label over each drawn spectator so one can be named ("#14 is clapping too fast"): number, style, speed, amplitude, active hand, seconds to the
        // next style change. Drawn with IMGUI at the head's screen position; only spectators in front of the camera and within LabelRangeM are labelled.

        private const float LabelRangeM = 45f;
        private float _now;
        private GUIStyle _labelStyle, _shadowStyle;
        private readonly StringBuilder _labelText = new StringBuilder();

        public void OnGUI()
        {
            if (!Config.SpectatorLabels || _cheerers.Count == 0 || Event.current.type != EventType.Repaint) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.LowerCenter, wordWrap = false, richText = false };
                _labelStyle.normal.textColor = Color.white;
                _shadowStyle = new GUIStyle(_labelStyle);
                _shadowStyle.normal.textColor = Color.black;
            }
            Vector3 camPos = cam.transform.position;
            float range2 = LabelRangeM * LabelRangeM;
            foreach (Cheerer c in _cheerers)
            {
                if (c.Root == null || !c.Shown) continue;
                Vector3 anchor = (c.Head != null ? c.Head.position : c.Root.position + Vector3.up * 1.6f) + Vector3.up * 0.45f;
                if ((anchor - camPos).sqrMagnitude > range2) continue;
                Vector3 sp = cam.WorldToScreenPoint(anchor);
                if (sp.z <= 0f) continue;
                _labelText.Length = 0;
                _labelText.Append('#').Append(c.Id).Append(' ').Append(c.Cur)
                    .Append(c.Excited ? " (excited)" : "").Append(c.Body >= 0 ? "  body " + c.Body : "").Append(c.Noise >= 0 ? "  noise " + c.Noise : "")
                    .Append("\nspd ").Append(c.Speed.ToString("F2")).Append("  amp ").Append(c.Amp.ToString("F2"))
                    .Append("  ").Append(c.Handed > 0f ? "L" : "R").Append("  next ").Append(Mathf.Max(0f, c.NextChange - _now).ToString("F0")).Append('s');
                string text = _labelText.ToString();
                var r = new Rect(sp.x - 90f, Screen.height - sp.y - 32f, 180f, 32f);
                GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, _shadowStyle);
                GUI.Label(r, text, _labelStyle);
            }
        }

        private static void ChangeStyle(Cheerer c, bool excited, float time)
        {
            Style[] set = excited ? ExcitedSet : CalmSet;
            Style next = set[c.Rng.Next(set.Length)];
            if (next == c.Cur && excited == c.Excited) next = set[c.Rng.Next(set.Length)];   // one re-roll against repeats
            c.Cur = next;
            c.Excited = excited;
            c.Clock = 0f;   // every cycle starts from standing, so no style begins in mid-air; the springs carry the arms across
            c.NextChange = time + 6f + (float)c.Rng.NextDouble() * 8f;
            c.Speed = (excited ? 1.25f : 0.8f) * (0.85f + (float)c.Rng.NextDouble() * 0.3f);   // the excited crowd is faster
            c.Amp = 0.85f + (float)c.Rng.NextDouble() * 0.3f;
            c.Variant = (float)c.Rng.NextDouble();
            c.OtherOnHip = c.Rng.NextDouble() < 0.35;
        }

        /// <summary>Cycles per second of each style at Speed 1.</summary>
        private static float Freq(Style s)
        {
            switch (s)
            {
                case Style.Wave: return 1.5f;
                case Style.Clap: return 2.0f;
                case Style.FistPump: return 1.1f;
                case Style.Shout: return 0.8f;
                case Style.Pump: return 1.2f;
                case Style.Jump: return 0.95f;   // with the flight at 35 % of the cycle this gives a physical 10 cm hop at the excited speed
                default: return 0.5f;
            }
        }

        private const float SoftKnee = -0.025f;   // standing pelvis height against the rest pose: knees are never locked

        /// <summary>One frame of one spectator: rest pose, then body, torso and look, legs, arms (see the comment at the top of the class).</summary>
        private void Animate(Cheerer c, float dt, float time, Vector3 riderPos, bool track)
        {
            for (int i = 0; i < c.Anim.Length; i++) c.Anim[i].localRotation = c.RestLocal[i];

            c.Clock += dt * c.Speed * (0.85f + 0.3f * Mathf.PerlinNoise(time * 0.23f, c.Id * 3.7f));   // the tempo drifts a little
            c.P = c.Clock * Freq(c.Cur); c.P -= Mathf.Floor(c.P);
            float amp = c.Amp * (0.85f + 0.3f * Mathf.PerlinNoise(c.Id * 5.1f, time * 0.31f));          // and so does the size of the movement
            int steps = Mathf.Clamp(Mathf.CeilToInt(dt / 0.01f), 1, 5);
            float h = dt / steps;

            // targets for the body and the look
            float py, fy;
            Body(c, amp, out py, out fy);
            float shift = c.Legs ? (Mathf.PerlinNoise(time * 0.15f, c.Id * 1.3f) - 0.5f) * (c.Excited ? 0.05f : 0.10f) : 0f;   // weight from foot to foot
            float yawT, pitchT;
            if (track)
            {
                Vector3 eye = c.Head != null ? c.Head.position : c.Root.position + Vector3.up * 1.6f;
                Vector3 d = riderPos + Vector3.up * 1.0f - eye, hz = new Vector3(d.x, 0f, d.z);
                float a = Vector3.SignedAngle(c.Fwd, hz, Vector3.up);
                if (Mathf.Abs(a) <= 120f) c.YawHold = Mathf.Clamp(a, -105f, 105f);   // rider behind them: keep the last angle rather than whip round
                yawT = c.YawHold;
                pitchT = Mathf.Clamp(Mathf.Atan2(d.y, hz.magnitude) * Mathf.Rad2Deg, -35f, 25f);
            }
            else
            {
                yawT = (Mathf.PerlinNoise(time * 0.1f, c.Id * 2.9f) - 0.5f) * 70f;   // looking around
                pitchT = (Mathf.PerlinNoise(c.Id * 4.3f, time * 0.08f) - 0.5f) * 24f;
                c.YawHold = yawT;
            }
            if (c.Cur == Style.Shout) pitchT += 10f;
            float leanT = c.Excited ? 0.10f : 0.02f;
            for (int s = 0; s < steps; s++)
            {
                Step(ref c.PY, ref c.PYV, py, 30f, 1f, h);
                Step(ref c.FY, ref c.FYV, fy, 30f, 1f, h);
                Step(ref c.Yaw, ref c.YawV, yawT, 7f, 0.9f, h);
                Step(ref c.Pitch, ref c.PitchV, pitchT, 7f, 0.9f, h);
                Step(ref c.Lean, ref c.LeanV, leanT, 5f, 1f, h);
            }
            float dip = Mathf.Max(0f, SoftKnee - c.PY);
            c.Crouch = c.Cur == Style.Jump ? Mathf.Clamp01(dip / 0.105f) : 0f;

            // body: without leg IK the whole figure can only move up (the old bob)
            c.Root.position = c.BasePos + Vector3.up * (c.Legs ? c.PY : c.FY) + c.Left * shift;

            // torso: hips roll with the weight shift and the shoulders counter it; lean (more in a crouch, to stay balanced); breathing; the look
            float lean = c.Lean + dip * 1.8f;
            Bend(c.Pelvis, Vector3.up, c.Left, -shift * 1.5f);
            Bend(c.Spine2, Vector3.up, c.Fwd, lean * 0.5f);
            Bend(c.Spine3, Vector3.up, c.Left, shift * 2.2f);
            Bend(c.Spine4, Vector3.up, c.Fwd, lean * 0.5f);
            Bend(c.Spine5, Vector3.up, c.Fwd, -0.015f * Mathf.Sin(time * 1.7f + c.Phase));
            Turn(c.Spine3, c.Yaw * 0.06f);
            Turn(c.Spine4, c.Yaw * 0.08f);
            Turn(c.Spine5, c.Yaw * 0.08f);
            Turn(c.Chest, c.Yaw * 0.10f);
            Turn(c.Neck1, c.Yaw * 0.28f);
            Vector3 face = Quaternion.AngleAxis(c.Yaw, Vector3.up) * c.Fwd;
            float pitch = (c.Pitch + lean * Mathf.Rad2Deg) * Mathf.Deg2Rad;   // the lean tips the head down: look past it
            Bend(c.Neck2, face, Vector3.up, pitch * 0.4f);
            Turn(c.Head, c.Yaw * 0.40f);
            Bend(c.Head, face, Vector3.up, pitch * 0.6f);

            // legs: feet planted (or in the air in a jump), knees forward and a little out, feet flat
            if (c.Legs)
            {
                Vector3 lift = Vector3.up * c.FY;
                TwoBone(c.HipL, c.KneeL, c.AnkleL, c.AnkleL0 + lift, c.Fwd + c.Left * 0.15f);
                TwoBone(c.HipR, c.KneeR, c.AnkleR, c.AnkleR0 + lift, c.Fwd - c.Left * 0.15f);
                c.AnkleL.rotation = c.AnkleRestL;
                c.AnkleR.rotation = c.AnkleRestR;
            }

            // arms, in the chest's frame
            Quaternion q = c.Chest.rotation * Quaternion.Inverse(c.ChestRest);
            c.F = q * c.Fwd; c.L = q * c.Left; c.U = q * Vector3.up;
            Shrug(c.ClavL, c.ShoulderL, c.UL.X, c.U);   // a raised arm lifts the collarbone (last frame's arm is close enough)
            Shrug(c.ClavR, c.ShoulderR, c.UR.X, c.U);
            Vector3 uL, fL, tL, uR, fR, tR;
            float kL, kR;
            ArmTarget(c, 1f, amp, time, out uL, out fL, out tL, out kL);
            ArmTarget(c, -1f, amp, time, out uR, out fR, out tR, out kR);
            float w = c.Excited ? 22f : 16f;
            for (int s = 0; s < steps; s++)
            {
                StepDir(ref c.UL, uL, w, 0.8f, h); StepDir(ref c.FL, fL, w * 0.8f, 0.6f, h); StepDir(ref c.TL, tL, 10f, 1f, h);
                StepDir(ref c.UR, uR, w, 0.8f, h); StepDir(ref c.FR, fR, w * 0.8f, 0.6f, h); StepDir(ref c.TR, tR, 10f, 1f, h);
                Step(ref c.CurlL, ref c.CurlLV, kL, 12f, 1f, h); Step(ref c.CurlR, ref c.CurlRV, kR, 12f, 1f, h);
            }
            PointArm(c.ShoulderL, c.ElbowL, c.WristL, c.UL.X, c.FL.X);
            PointArm(c.ShoulderR, c.ElbowR, c.WristR, c.UR.X, c.FR.X);
            TwistHand(c.ElbowL, c.WristL, c.IndexL, c.PinkL, c.FL.X, c.TL.X);
            TwistHand(c.ElbowR, c.WristR, c.IndexR, c.PinkR, c.FR.X, c.TR.X);
            CurlFingers(c.FingersL, c.WristL, c.PalmL, c.CurlL);
            CurlFingers(c.FingersR, c.WristR, c.PalmR, c.CurlR);
        }

        private static readonly float[] CurlDeg = { 60f, 90f, 65f };   // knuckle, middle, end joint at a full fist

        /// <summary>
        /// Close the hand: every joint of a finger turns about one axis, found at the knuckle (the finger's direction turning towards the palm), so the
        /// finger rolls into the palm. The thumb (a 3-joint chain) closes half as far.
        /// </summary>
        private static void CurlFingers(Transform[][] fingers, Transform wrist, Vector3 palmLocal, float k)
        {
            if (fingers == null || k < 0.02f) return;
            Vector3 palm = wrist.rotation * palmLocal;
            foreach (Transform[] f in fingers)
            {
                if (f.Length < 2) continue;
                Vector3 d = f[1].position - f[0].position;
                Vector3 toward = palm - d.normalized * Vector3.Dot(palm, d.normalized);
                if (d.sqrMagnitude < 1e-8f || toward.sqrMagnitude < 1e-6f) continue;
                Vector3 axis = Vector3.Cross(d, toward);
                // the sense of the axis: rotating d about it by a positive angle must move it towards the palm
                if (Vector3.Dot(Quaternion.AngleAxis(10f, axis) * d - d, toward) < 0f) axis = -axis;
                float scale = f.Length < 4 ? 0.5f : 1f;   // the thumb
                for (int j = 0; j + 1 < f.Length && j < CurlDeg.Length; j++)
                    f[j].rotation = Quaternion.AngleAxis(CurlDeg[j] * k * scale, axis) * f[j].rotation;
            }
        }

        /// <summary>Pelvis and feet height against the rest pose for the current style and phase (m).</summary>
        private static void Body(Cheerer c, float amp, out float pelvisY, out float feetY)
        {
            float p = c.P;
            pelvisY = SoftKnee; feetY = 0f;
            switch (c.Cur)
            {
                case Style.Jump:
                {
                    float hop = 0.10f * amp;
                    if (p < 0.2f) pelvisY = Mathf.Lerp(SoftKnee, -0.13f, Smooth(p / 0.2f));                        // dip (anticipation)
                    else if (p < 0.3f) { float s = (p - 0.2f) / 0.1f; pelvisY = Mathf.Lerp(-0.13f, 0f, s * s); }   // drive up, accelerating
                    else if (p < 0.65f)                                                                               // flight, knees tucked a little
                    {
                        float s = (p - 0.3f) / 0.35f, y = 4f * hop * s * (1f - s);
                        pelvisY = y; feetY = y + 0.04f * amp * Mathf.Sin(s * Mathf.PI);
                    }
                    else if (p < 0.8f) pelvisY = Mathf.Lerp(0f, -0.10f, EaseOut((p - 0.65f) / 0.15f));              // land and absorb
                    else pelvisY = Mathf.Lerp(-0.10f, SoftKnee, Smooth((p - 0.8f) / 0.2f));
                    break;
                }
                case Style.Pump: pelvisY = SoftKnee - 0.035f * amp * (0.5f - 0.5f * Mathf.Cos(p * 4f * Mathf.PI)); break;   // bounce twice per arm cycle
                case Style.FistPump: pelvisY = SoftKnee - 0.03f * amp * Pulse(p, 0.22f); break;
                case Style.Clap: if (c.Excited) pelvisY = SoftKnee - 0.02f * (0.5f - 0.5f * Mathf.Cos(p * 2f * Mathf.PI)); break;
            }
        }

        /// <summary>Where one arm should point (upper arm u, forearm f, world directions) and where its thumb should point. side +1 = the character's left.</summary>
        private static void ArmTarget(Cheerer c, float side, float amp, float time, out Vector3 u, out Vector3 f, out Vector3 thumb, out float curl)
        {
            Vector3 up = c.U, fw = c.F, o = c.L * side;
            float p = c.P, tau = 2f * Mathf.PI;
            Style st = c.Cur;
            if ((st == Style.Wave || st == Style.FistPump) && side != c.Handed) st = c.OtherOnHip ? Style.Hips : Style.Idle;   // the free arm
            // no style may end in a straight arm raised forward with a flat hand: from some angles that reads as a salute. Raised arms go up and
            // out to the side with the elbow bent, and a pumping hand is a fist.
            switch (st)
            {
                case Style.Hips:
                    if (IkDirs(c, side, HipPoint(c, side, o, up, fw), o - fw * 0.5f - up * 0.2f, out u, out f)) { thumb = -fw; curl = 0.45f; return; }
                    break;
                case Style.Wave:   // the elbow out at about shoulder height, the forearm upright and swinging side to side, palm to the course
                    u = N(o * 0.85f + up * 0.45f + fw * 0.2f);
                    f = N(up + o * (Mathf.Sin(p * tau) * 0.45f * amp) + fw * 0.05f);
                    thumb = -o; curl = 0f;
                    return;
                case Style.Clap:   // hands meet in front of the chest (higher when excited), hold on contact, palms facing
                {
                    float gap = ClapGap(p);
                    float height = c.Excited ? -0.05f + c.Variant * 0.3f : -0.12f + c.Variant * 0.1f;
                    Vector3 centre = c.Chest.position + fw * 0.33f + up * height;
                    Vector3 wrist = centre + o * (0.02f + gap * 0.16f * amp) - fw * 0.07f;
                    if (IkDirs(c, side, wrist, o * 0.8f - up * 0.8f, out u, out f)) { thumb = up; curl = 0.1f; return; }
                    break;
                }
                case Style.FistPump:   // a fist, the elbow bent throughout: from beside the head (fist at ear height) punched straight up, fast, then back slowly
                {
                    float k = Pulse(p, 0.22f) * amp;
                    u = N(o * 0.6f + up * (0.15f + 0.7f * k) + fw * (0.25f - 0.2f * k));
                    f = N(up * (0.7f + 0.4f * k) + fw * (0.35f * (1f - k)) - o * (0.15f - 0.25f * k));
                    thumb = -o; curl = 1f;
                    return;
                }
                case Style.Shout:   // hands cupped round the mouth
                    if (c.Head != null && IkDirs(c, side, c.Head.position + fw * 0.10f + o * 0.07f - up * 0.02f, o - up * 0.8f, out u, out f)) { thumb = up; curl = 0.35f; return; }
                    break;
                case Style.Pump:   // both arms in a wide V, forearms waving out of step
                    u = N(o + up * 0.8f + fw * 0.1f);
                    f = N(up + o * (0.25f + 0.3f * Mathf.Sin(p * tau + side * 1.2f) * amp));
                    thumb = -o; curl = 0.6f;
                    return;
                case Style.Jump:   // arms swing down and back in the dip and up in the flight
                {
                    float k = c.Crouch;
                    u = N(o * (0.5f + 0.3f * k) + up * (1f - 1.5f * k) - fw * 0.3f * k);
                    f = N(up * (1f - 1.2f * k) + o * 0.1f + fw * 0.3f * k);
                    thumb = -o; curl = 0.3f;
                    return;
                }
            }
            // idle: arms hanging, elbows soft, drifting a little, fingers relaxed (slightly curled)
            float sway = (Mathf.PerlinNoise(time * 0.4f, c.Id * 1.7f + side) - 0.5f) * 0.2f;
            u = N(-up + o * 0.12f + fw * (0.04f + sway * 0.5f));
            f = N(-up + fw * (0.3f + sway) - o * 0.04f);
            thumb = fw; curl = 0.3f;
        }

        /// <summary>Where a hand rests on the hip: just above the hip joint and out to the side.</summary>
        private static Vector3 HipPoint(Cheerer c, float side, Vector3 o, Vector3 up, Vector3 fw)
        {
            Transform hip = side > 0f ? c.HipL : c.HipR;
            Vector3 joint = hip != null ? hip.position : c.Chest.position - up * 0.45f + o * 0.08f;
            return joint + o * 0.10f + up * 0.11f - fw * 0.03f;
        }

        /// <summary>Arm directions that put the wrist on a target, the elbow bending towards pole (a direction).</summary>
        private static bool IkDirs(Cheerer c, float side, Vector3 target, Vector3 pole, out Vector3 u, out Vector3 f)
        {
            Transform sh = side > 0f ? c.ShoulderL : c.ShoulderR;
            return Solve(sh.position, c.UpperLen, c.ForeLen, target, pole, out u, out f);
        }

        /// <summary>Two-bone IK: from a root at s with segment lengths l1, l2, the directions of both segments that reach target, bending towards pole.</summary>
        private static bool Solve(Vector3 s, float l1, float l2, Vector3 target, Vector3 pole, out Vector3 u, out Vector3 f)
        {
            u = f = Vector3.zero;
            Vector3 d = target - s;
            float dist = d.magnitude;
            if (dist < 1e-4f || l1 < 1e-4f || l2 < 1e-4f) return false;
            Vector3 dn = d / dist;
            dist = Mathf.Clamp(dist, Mathf.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-4f);
            float cosA = Mathf.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f), sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 bend = pole - dn * Vector3.Dot(pole, dn);
            if (bend.sqrMagnitude < 1e-8f) return false;
            bend.Normalize();
            Vector3 mid = s + dn * (l1 * cosA) + bend * (l1 * sinA);
            u = (mid - s) / l1;
            f = N(s + dn * dist - mid);
            return true;
        }

        private static void TwoBone(Transform a, Transform b, Transform c, Vector3 target, Vector3 pole)
        {
            Vector3 u, f;
            if (!Solve(a.position, (b.position - a.position).magnitude, (c.position - b.position).magnitude, target, pole, out u, out f)) return;
            PointArm(a, b, c, u, f);
        }

        /// <summary>Point the first segment along u and, after it moved, the second along f (minimal rotations from the current pose).</summary>
        private static void PointArm(Transform a, Transform b, Transform c, Vector3 u, Vector3 f)
        {
            a.rotation = Quaternion.FromToRotation(b.position - a.position, u) * a.rotation;
            b.rotation = Quaternion.FromToRotation(c.position - b.position, f) * b.rotation;
        }

        /// <summary>Roll the hand about the forearm so the thumb side (index knuckle minus little-finger knuckle) points towards thumbTo; half the roll in the forearm, the rest at the wrist.</summary>
        private static void TwistHand(Transform elbow, Transform wrist, Transform index, Transform pink, Vector3 f, Vector3 thumbTo)
        {
            if (index == null || pink == null) return;
            Vector3 t0 = thumbTo - f * Vector3.Dot(thumbTo, f);
            if (t0.sqrMagnitude < 1e-6f) return;
            for (int pass = 0; pass < 2; pass++)
            {
                Vector3 a = index.position - pink.position;
                Vector3 a0 = a - f * Vector3.Dot(a, f);
                if (a0.sqrMagnitude < 1e-8f) return;
                Transform b = pass == 0 ? elbow : wrist;
                b.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(a0, t0, f) * (pass == 0 ? 0.5f : 1f), f) * b.rotation;
            }
        }

        private static void Shrug(Transform clav, Transform shoulder, Vector3 arm, Vector3 up)
        {
            if (clav == null) return;
            float k = Mathf.Clamp01(Vector3.Dot(arm, up));
            if (k <= 0f) return;
            Bend(clav, N(shoulder.position - clav.position), up, k * 0.25f);
        }

        /// <summary>Rotate a bone about its pivot so that direction from turns towards toward by rad.</summary>
        private static void Bend(Transform bone, Vector3 from, Vector3 toward, float rad)
        {
            if (bone == null || Mathf.Abs(rad) < 1e-5f) return;
            bone.rotation = Quaternion.FromToRotation(from, from * Mathf.Cos(rad) + toward * Mathf.Sin(rad)) * bone.rotation;
        }

        private static void Turn(Transform bone, float deg)
        {
            if (bone != null) bone.rotation = Quaternion.AngleAxis(deg, Vector3.up) * bone.rotation;
        }

        /// <summary>A damped spring on a number (semi-implicit Euler); w = stiffness in rad/s, z = damping ratio.</summary>
        private static void Step(ref float x, ref float v, float target, float w, float z, float h)
        {
            v += (w * w * (target - x) - 2f * z * w * v) * h;
            x += v * h;
        }

        /// <summary>The same on a unit direction: only the part of the pull along the sphere moves it, so it swings round rather than through zero.</summary>
        private static void StepDir(ref Spring s, Vector3 target, float w, float z, float h)
        {
            s.V += (w * w * (target - s.X) - 2f * z * w * s.V) * h;
            s.V -= s.X * Vector3.Dot(s.V, s.X);
            s.X = N(s.X + s.V * h);
        }

        private static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        private static float EaseOut(float x) { x = Mathf.Clamp01(x); return 1f - (1f - x) * (1f - x); }

        /// <summary>0 -> 1 fast (ease out) over the first rise of the cycle, then back to 0 slowly (ease in and out).</summary>
        private static float Pulse(float p, float rise) { return p < rise ? EaseOut(p / rise) : 1f - Smooth((p - rise) / (1f - rise)); }

        /// <summary>Clap: hands open (1) accelerate together, hold on contact (0), then open again.</summary>
        private static float ClapGap(float p)
        {
            if (p < 0.3f) { float s = p / 0.3f; return 1f - s * s; }
            if (p < 0.38f) return 0f;
            return Smooth((p - 0.38f) / 0.62f);
        }

        private static Vector3 N(Vector3 v) { return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.up; }

        private void Spawn(PlayerProbe probe, PathTrack track)
        {
            if (!track.Loaded) { Log.Warn("Spectators: no generated course path in this scene"); return; }
            if (!_itemsListed) { _itemsListed = true; ListHeadItems(); }
            if (probe.Player == null) { Log.Warn("Spectators: no rider to copy (are you on the course?)"); return; }

            // 1. one stripped template, made under an inactive parent so none of the rider's scripts ever start
            var holder = new GameObject("CyclingPOC_CloneHolder");
            holder.SetActive(false);
            GameObject template = (GameObject)UnityEngine.Object.Instantiate(probe.Player, holder.transform);
            template.name = "Spectator_template";
            var report = new StringBuilder();
            Strip(template, report);
            Log.Info("Spectators: template stripped. " + report);

            // 2. place copies beside the path
            _root = new GameObject("CyclingPOC_Spectators");
            string hairParentRel = HairParentPath(probe.Player.transform);
            Transform realHairParent = hairParentRel != null ? probe.Player.transform.Find(hairParentRel) : null;
            // preferred: the hair exactly as the game itself places it on the real rider (see BuildHairTemplates); fallback: the bare prefabs
            GameObject[] hairs = Config.SpectatorHair ? BuildHairTemplates(probe.Player.transform, realHairParent, holder.transform) : new GameObject[0];
            string hairSource = "copied from the real rider";
            if (hairs.Length == 0) { hairs = HairPrefabs(); hairSource = "bare prefabs (fallback)"; }
            Log.Info("Spectators: " + hairs.Length + " hair styles, " + hairSource + "; the game parents hair under " + (hairParentRel ?? "(not found, falling back to the head bone)"));
            // female heads: spectators with another body type get one of the game's hair styles named female (bare prefab, the game's own placement)
            var femaleHairs = new List<GameObject>();
            var styleNames = new StringBuilder();
            foreach (GameObject hp in HairPrefabs())
            {
                styleNames.Append(hp.name).Append(", ");
                if (Config.SpectatorFemaleHair && hp.name.ToLowerInvariant().Contains("female")) femaleHairs.Add(hp);
            }
            Log.Info("Spectators: hair styles in the game: " + styleNames + "; " + femaleHairs.Count + " used for female heads");
            GameObject[] hats = Config.SpectatorHats ? BuildHatTemplates(holder.transform) : new GameObject[0];
            int want = Config.SpectatorCount, placed = 0;
            float margin = Mathf.Min(40f, track.Length * 0.1f);
            var rng = new System.Random(7);
            if (Config.SpectatorClothes) { LoadClothingCatalogue(); FindAlphaMaps(probe.Player); }
            FindBodyTypes();
            var bodyRng = new System.Random(11);   // its own stream, so the body types do not reshuffle the outfits
            bool loggedBody = false;
            List<Spot> spots = BuildSpots(track.Length, margin, want, rng);
            for (int i = 0; i < spots.Count; i++)
            {
                float s = spots[i].S;                Vector3 p = track.At(s);
                Vector3 t = track.At(Mathf.Min(track.Length, s + 2f)) - track.At(Mathf.Max(0f, s - 2f));
                t.y = 0f;
                if (t.sqrMagnitude < 1e-4f) continue;
                t.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, t);
                float side = spots[i].Side;
                float off = spots[i].Off;
                Vector3 pos = p + right * side * off;
                float gy;
                if (!GroundAt(pos, probe.Player.transform, out gy)) { Log.Info("Spectators: no ground at " + s.ToString("F0") + " m, skipped"); continue; }
                Vector3 face = -right * side;   // look at the course
                GameObject g = (GameObject)UnityEngine.Object.Instantiate(template, new Vector3(pos.x, gy + 2f, pos.z), Quaternion.LookRotation(face, Vector3.up), _root.transform);
                g.name = "Spectator_" + (placed + 1);
                if (Config.SpectatorClothes) SwapClothes(g, rng, holder.transform, placed == 0);
                int body = -1;
                if (_bodyTypes != null && bodyRng.NextDouble() < Config.SpectatorFemaleShare) { body = _otherBodies[bodyRng.Next(_otherBodies.Count)]; ApplyBodyType(g, _bodyTypes[body], ref loggedBody); }   // before the pose: the bind pose comes from the mesh
                if (body >= 0) g.transform.localScale = Vector3.one * FemaleScale;   // before the pose, so the feet are placed on the ground at the new size
                string msg = PoseAndPlace(g, new Vector3(pos.x, gy, pos.z), face, (float)rng.NextDouble() * 6.28f);
                if (_cheerers.Count > 0 && object.ReferenceEquals(_cheerers[_cheerers.Count - 1].Root, g.transform)) { _cheerers[_cheerers.Count - 1].Body = body; if (body >= 0) NarrowHead(_cheerers[_cheerers.Count - 1]); }
                if (placed == 0) Log.Info("Spectators: pose: " + msg);
                Transform hairParent = hairParentRel != null ? g.transform.Find(hairParentRel) : null;
                if (hairParent == null) hairParent = _lastHead;
                // equipped items hang directly under the link's spawn point (attribute_head); hair goes one level up, under the head bone (PlayerCustomization equip routine, IL)
                Transform hatParent = hairParent != null ? hairParent.Find("attribute_head") : null;
                if (hatParent == null) hatParent = hairParent;
                if (hats.Length > 0 && hatParent != null) AttachTemplate(hatParent, hats[rng.Next(hats.Length)], holder.transform, "hat", Config.SpectatorHatDropM);
                if (body >= 0 && femaleHairs.Count > 0 && hairParent != null) AttachHair(hairParent, femaleHairs[bodyRng.Next(femaleHairs.Count)], HairColours[bodyRng.Next(4)], holder.transform);   // natural colours only
                else if (Config.SpectatorHair && hairs.Length > 0 && hairParent != null) AttachHair(hairParent, hairs[rng.Next(hairs.Length)], HairColours[rng.Next(HairColours.Length)], holder.transform);
                placed++;
            }
            Count = placed;
            int noisy = 0;
            foreach (Cheerer c in _cheerers) if (c.Noise >= 0) noisy++;
            Log.Info("Spectators: " + noisy + " carry a noisemaker (" + string.Join(", ", Config.SpectatorNoiseEvents) + ")");
            UnityEngine.Object.Destroy(holder);   // takes the template with it, and the garment templates
            _clothCache.Clear();                  // so the next spawn must not get them back from the cache (they were destroyed: spectators came out undressed)
            Log.Info("Spectators: placed " + placed + " of " + want + " in " + Mathf.CeilToInt(want / (float)GroupSize) + " groups along " + track.Length.ToString("F0") + " m of path (offset " + Config.SpectatorOffsetM.ToString("F1") + " m+)");
        }

        /// <summary>Remove everything that is not a plain mesh: scripts (vehicle, ragdoll, network, sound), physics, particle systems, lights, animation drivers.</summary>
        private static void Strip(GameObject go, StringBuilder report)
        {
            int destroyed = 0;
            // the bike and the invisible helper objects (IK goals, body trigger) are not wanted on a spectator
            foreach (string n in new[] { "BikeModel", "BodyTrigger", "RightKnee_BendGoal", "LeftKnee_BendGoal", "wheel_front", "wheel_back" })
            {
                Transform t = go.transform.Find(n);
                if (t != null) { UnityEngine.Object.DestroyImmediate(t.gameObject); destroyed++; }
            }
            // helmet and goggles are separate, swappable meshes; SpectatorHelmets = false takes them off
            if (!Config.SpectatorHelmets)
            foreach (SkinnedMeshRenderer m in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string mn = m.name.ToLowerInvariant();
                if (mn.Contains("helmet") || mn.Contains("goggle")) { UnityEngine.Object.DestroyImmediate(m.gameObject); destroyed++; }
            }
            // particle systems first (they require their renderer), matched by type name so the module need not be referenced
            foreach (Component c in go.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().Name == "ParticleSystem") { if (Kill(c)) destroyed++; }
            foreach (Joint j in go.GetComponentsInChildren<Joint>(true)) if (Kill(j)) destroyed++;   // joints depend on rigidbodies
            for (int pass = 0; pass < 6; pass++)   // scripts may require each other: repeat until nothing more goes
            {
                int before = destroyed;
                foreach (Behaviour b in go.GetComponentsInChildren<Behaviour>(true)) if (b != null && Kill(b)) destroyed++;
                if (destroyed == before) break;
            }
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) if (Kill(c)) destroyed++;
            foreach (Rigidbody r in go.GetComponentsInChildren<Rigidbody>(true)) if (Kill(r)) destroyed++;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                string n = r.GetType().Name;
                if (n != "MeshRenderer" && n != "SkinnedMeshRenderer") { if (Kill(r)) destroyed++; }
            }
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) { try { t.gameObject.tag = "Untagged"; } catch (Exception) { } }   // the game finds the rider by tag
            var left = new Dictionary<string, int>();
            foreach (Component c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue;
                string k = c.GetType().Name;
                int n; left.TryGetValue(k, out n); left[k] = n + 1;
            }
            report.Append("removed " + destroyed + " components; left:");
            foreach (KeyValuePair<string, int> kv in left) report.Append(" " + kv.Key + "x" + kv.Value);
        }

        private static bool Kill(Component c)
        {
            try { UnityEngine.Object.DestroyImmediate(c); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>Highest walkable surface below the point that is not the rider (terrain, ground meshes), ignoring triggers and steep faces (trunks, walls).</summary>
        private static bool GroundAt(Vector3 pos, Transform rider, out float y)
        {
            y = 0f;
            RaycastHit[] hits = Physics.RaycastAll(pos + Vector3.up * 40f, Vector3.down, 120f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            foreach (RaycastHit h in hits)
            {
                if (h.collider.transform.root == rider.root) continue;
                if (h.normal.y < 0.55f) continue;
                if (h.point.y > best) best = h.point.y;
            }
            if (float.IsNegativeInfinity(best)) return false;
            y = best;
            return true;
        }

        /// <summary>
        /// Stand the copy up and place it: bones back to the mesh's bind (rest) pose, turned to face the course, both arms raised, feet on the ground.
        /// Returns a one-line description of what was found, for the log.
        /// </summary>
        private string PoseAndPlace(GameObject g, Vector3 groundPos, Vector3 face, float phase)
        {
            _lastHead = null;
            SkinnedMeshRenderer body = null;
            foreach (SkinnedMeshRenderer s in g.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (s.name == "character_base") { body = s; break; }
            if (body == null || body.sharedMesh == null) return "no character_base mesh, left as is";
            Transform[] bones = body.bones;
            Matrix4x4[] binds = body.sharedMesh.bindposes;
            if (bones.Length != binds.Length) return "bone / bindpose count mismatch (" + bones.Length + " / " + binds.Length + ")";

            // 1. rest pose: bone world matrix = mesh transform * inverse bindpose. Parents first, so a parent never drags a child that was already placed.
            Matrix4x4 l2w = body.transform.localToWorldMatrix;
            var order = new List<int>();
            for (int i = 0; i < bones.Length; i++) if (bones[i] != null) order.Add(i);
            order.Sort(delegate (int x, int y) { return Depth(bones[x]).CompareTo(Depth(bones[y])); });
            foreach (int i in order)
            {
                Matrix4x4 m = l2w * binds[i].inverse;
                Vector3 up = m.GetColumn(1), fw = m.GetColumn(2);
                if (up.sqrMagnitude < 1e-8f || fw.sqrMagnitude < 1e-8f) continue;
                bones[i].SetPositionAndRotation(m.GetColumn(3), Quaternion.LookRotation(fw, up));
            }

            Transform sl = Bone(bones, "_L_Shoulder"), sr = Bone(bones, "_R_Shoulder"), el = Bone(bones, "_L_Elbow"), er = Bone(bones, "_R_Elbow");
            Transform wl = Bone(bones, "_L_Wrist"), wr = Bone(bones, "_R_Wrist");
            Transform hl = Bone(bones, "_L_Hip"), hr = Bone(bones, "_R_Hip");
            _lastHead = Bone(bones, "_C_Head1");
            if (sl == null || sr == null || el == null || er == null) return "arm bones not found (" + bones.Length + " bones), standing in the rest pose";

            // 2. turn to face the course: the body's forward is perpendicular to the line between the shoulders
            Vector3 across = sr.position - sl.position; across.y = 0f;
            Vector3 fwd = Vector3.Cross(across, Vector3.up);
            Vector3 want = new Vector3(face.x, 0f, face.z);
            if (fwd.sqrMagnitude > 1e-6f && want.sqrMagnitude > 1e-6f)
                g.transform.rotation = Quaternion.FromToRotation(fwd.normalized, want.normalized) * g.transform.rotation;

            Vector3 left = sl.position - sr.position; left.y = 0f;

            // 4. stand on the ground spot: centred on the hips, lowest bone (ankle / toe) a shoe-sole above the ground
            Vector3 centre = (hl != null && hr != null) ? (hl.position + hr.position) * 0.5f : (sl.position + sr.position) * 0.5f;
            g.transform.position += new Vector3(groundPos.x - centre.x, 0f, groundPos.z - centre.z);
            float minY = float.MaxValue;
            foreach (Transform b in bones) if (b != null) minY = Mathf.Min(minY, b.position.y);
            g.transform.position += Vector3.up * (groundPos.y - (minY - SoleBelowBoneM));

            // 5. record the rest pose: Animate restores it every frame and builds the motion on top
            if (wl == null || wr == null) return "wrist bones not found, standing still in the rest pose";
            Transform chest = Bone(bones, "_C_Spine_6");
            if (chest == null) return "upper spine bone not found, standing still in the rest pose";
            var rng = new System.Random(unchecked((int)(phase * 100000f)) + 17);
            var ch = new Cheerer
            {
                Root = g.transform, Head = _lastHead, Id = _cheerers.Count + 1,
                Pelvis = Bone(bones, "_C_Spine_1"), Spine2 = Bone(bones, "_C_Spine_2"), Spine3 = Bone(bones, "_C_Spine_3"), Spine4 = Bone(bones, "_C_Spine_4"),
                Spine5 = Bone(bones, "_C_Spine_5"), Chest = chest, Neck1 = Bone(bones, "_C_Neck1"), Neck2 = Bone(bones, "_C_Neck2"),
                ClavL = Bone(bones, "_L_Clavicle"), ClavR = Bone(bones, "_R_Clavicle"), ShoulderL = sl, ShoulderR = sr, ElbowL = el, ElbowR = er, WristL = wl, WristR = wr,
                IndexL = Bone(bones, "_L_Index1"), IndexR = Bone(bones, "_R_Index1"), PinkL = Bone(bones, "_L_Pink1"), PinkR = Bone(bones, "_R_Pink1"),
                HipL = hl, HipR = hr, KneeL = Bone(bones, "_L_Calf"), KneeR = Bone(bones, "_R_Calf"), AnkleL = Bone(bones, "_L_Ankle"), AnkleR = Bone(bones, "_R_Ankle"),
                UpperLen = (el.position - sl.position).magnitude, ForeLen = (wl.position - el.position).magnitude,
                Fwd = want.sqrMagnitude > 1e-6f ? want.normalized : Vector3.forward,
                Left = left.sqrMagnitude > 1e-6f ? left.normalized : Vector3.right,
                BasePos = g.transform.position, Phase = phase, Handed = rng.NextDouble() < 0.5 ? 1f : -1f, Rng = rng
            };
            ch.Legs = ch.HipL != null && ch.HipR != null && ch.KneeL != null && ch.KneeR != null && ch.AnkleL != null && ch.AnkleR != null;
            if (Config.SpectatorNoiseEvents.Length > 0 && rng.NextDouble() < Config.SpectatorNoiseShare) ch.Noise = rng.Next(Config.SpectatorNoiseEvents.Length);
            ch.FingersL = Fingers(bones, "_L_"); ch.FingersR = Fingers(bones, "_R_");
            ch.PalmL = PalmLocal(bones, "_L_", wl, 1f); ch.PalmR = PalmLocal(bones, "_R_", wr, -1f);
            var anim = new List<Transform>();
            foreach (Transform b in new[] { ch.Pelvis, ch.Spine2, ch.Spine3, ch.Spine4, ch.Spine5, ch.Chest, ch.Neck1, ch.Neck2, ch.Head, ch.ClavL, ch.ClavR,
                                            sl, sr, el, er, wl, wr, ch.HipL, ch.HipR, ch.KneeL, ch.KneeR, ch.AnkleL, ch.AnkleR })
                if (b != null && !anim.Contains(b)) anim.Add(b);
            foreach (Transform[][] hand in new[] { ch.FingersL, ch.FingersR })
                foreach (Transform[] finger in hand)
                    foreach (Transform b in finger) if (!anim.Contains(b)) anim.Add(b);
            ch.Anim = anim.ToArray();
            ch.RestLocal = new Quaternion[ch.Anim.Length];
            for (int i = 0; i < ch.Anim.Length; i++) ch.RestLocal[i] = ch.Anim[i].localRotation;
            ch.ChestRest = chest.rotation;
            if (ch.Legs) { ch.AnkleL0 = ch.AnkleL.position; ch.AnkleR0 = ch.AnkleR.position; ch.AnkleRestL = ch.AnkleL.rotation; ch.AnkleRestR = ch.AnkleR.rotation; }
            ch.NextChange = 0f;   // the first Tick picks a style
            ch.Cur = Style.Idle; ch.Speed = 1f; ch.Amp = 1f; ch.PY = SoftKnee;
            ch.UL.X = N(-Vector3.up + ch.Left * 0.12f); ch.FL.X = N(-Vector3.up + ch.Fwd * 0.3f);
            ch.UR.X = N(-Vector3.up - ch.Left * 0.12f); ch.FR.X = N(-Vector3.up + ch.Fwd * 0.3f);
            ch.TL.X = ch.TR.X = ch.Fwd;
            _cheerers.Add(ch);
            string missing = "";
            Transform[] opt = { ch.Pelvis, ch.Spine2, ch.Spine3, ch.Spine4, ch.Spine5, ch.Neck1, ch.Neck2, ch.Head, ch.ClavL, ch.ClavR, ch.IndexL, ch.IndexR, ch.PinkL, ch.PinkR };
            string[] optNames = { "Spine_1", "Spine_2", "Spine_3", "Spine_4", "Spine_5", "Neck1", "Neck2", "Head1", "L_Clavicle", "R_Clavicle", "L_Index1", "R_Index1", "L_Pink1", "R_Pink1" };
            for (int i = 0; i < opt.Length; i++) if (opt[i] == null) missing += optNames[i] + " ";
            return bones.Length + " bones, " + ch.Anim.Length + " animated, leg IK " + (ch.Legs ? "on" : "off (leg bones not found)")
                + (missing.Length > 0 ? ", missing: " + missing : "") + ", arm " + ch.UpperLen.ToString("F2") + " + " + ch.ForeLen.ToString("F2")
                + " m, forward was (" + fwd.x.ToString("F2") + "," + fwd.z.ToString("F2") + "), lowest bone " + (minY - g.transform.position.y).ToString("F2") + " m from the root";
        }

        /// <summary>
        /// Where the game itself hangs hair on the rider: the parent of the Accessories_Head link's spawn point (PlayerCustomization.SetHairType ->
        /// Instantiate(prefab, link.spawnParent.parent)). Returned as a path relative to the rider root so it can be found on a copy. Also logs what
        /// the real rider currently has there, for diagnosis.
        /// </summary>
        private static string HairParentPath(Transform riderRoot)
        {
            try
            {
                Type pc = FindType("PlayerCustomization");
                if (object.ReferenceEquals(pc, null)) return null;
                Component comp = riderRoot.GetComponent(pc);
                if (comp == null) return null;
                string found = null;
                foreach (FieldInfo f in pc.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    object v = f.GetValue(comp);
                    Type ft = f.FieldType;
                    if (ft.IsArray && ft.GetElementType().Name == "CustomizationLink")
                    {
                        foreach (object link in (Array)v)
                        {
                            if (link == null) continue;
                            object slot = Member(link, "customizationSlot");
                            Transform sp = Member(link, "spawnParent") as Transform;
                            int slotNo = slot != null ? Convert.ToInt32(slot) : -1;
                            Log.Info("Spectators: link slot " + slotNo + " (" + slot + ") spawnParent " + (sp != null ? Rel(sp, riderRoot) + "  -> parent " + (sp.parent != null ? Rel(sp.parent, riderRoot) : "none") : "null"));
                            if (slotNo == 11 && sp != null && sp.parent != null) found = Rel(sp.parent, riderRoot);   // Accessories_Head = where hair goes
                        }
                    }
                    else if (object.ReferenceEquals(ft, typeof(GameObject)))
                    {
                        GameObject go = v as GameObject;
                        if (go != null)
                            Log.Info("Spectators: rider GameObject field '" + go.name + "' parent " + (go.transform.parent != null ? Rel(go.transform.parent, riderRoot) : "none")
                                + " localPos " + go.transform.localPosition + " localRot " + go.transform.localEulerAngles + " localScale " + go.transform.localScale + " active " + go.activeSelf);
                    }
                }
                return found;
            }
            catch (Exception ex) { Log.Warn("Spectators: reading the hair link failed: " + ex.Message); return null; }
        }

        private static string Rel(Transform t, Transform root)
        {
            if (object.ReferenceEquals(t, root)) return "";
            string s = t.name;
            for (Transform p = t.parent; p != null && !object.ReferenceEquals(p, root); p = p.parent) s = p.name + "/" + s;
            return s;
        }

        private static object Member(object o, string name)
        {
            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo f = o.GetType().GetField(name, bf);
            if (!object.ReferenceEquals(f, null)) return f.GetValue(o);
            PropertyInfo p = o.GetType().GetProperty(name, bf);
            return !object.ReferenceEquals(p, null) ? p.GetValue(o, null) : null;
        }

        private Transform _lastHead;   // head bone of the copy PoseAndPlace just handled

        // natural hair colours plus two for fun
        private static readonly Color[] HairColours =
        {
            new Color32(35, 24, 18, 255), new Color32(12, 12, 12, 255), new Color32(120, 82, 48, 255), new Color32(216, 178, 96, 255),
            new Color32(168, 70, 30, 255), new Color32(170, 170, 170, 255), new Color32(200, 60, 160, 255), new Color32(50, 140, 220, 255),
        };

        /// <summary>The game's hair models (RiderHairTypes._hairTypes), read at runtime because the member names around it are obfuscated.</summary>
        /// <summary>
        /// Ground truth for how hair looks on this rig: for each hair style, apply it to the REAL rider with the game's own SetHairType (which
        /// instantiates it exactly as the game does, at the game's own node with the game's own placement), take a stripped copy of that instance,
        /// then put the rider's original hair back. Everything happens synchronously inside one call, so nothing is drawn in between.
        /// </summary>
        private static GameObject[] BuildHairTemplates(Transform riderRoot, Transform hairParent, Transform holder)
        {
            var result = new List<GameObject>();
            if (hairParent == null) return result.ToArray();
            Type pcT = FindType("PlayerCustomization");
            Component pc = object.ReferenceEquals(pcT, null) ? null : riderRoot.GetComponent(pcT);
            if (pc == null) return result.ToArray();
            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo setHair = pcT.GetMethod("SetHairType", bf), setCol = pcT.GetMethod("SetHairColor", bf);
            MethodInfo rcP = pcT.GetMethod("get_RiderCustomizations", bf);   // the obfuscator strips property metadata but leaves the getter
            if (object.ReferenceEquals(setHair, null) || object.ReferenceEquals(setCol, null) || object.ReferenceEquals(rcP, null)) { Log.Warn("Spectators: hair API not found on PlayerCustomization"); return result.ToArray(); }
            object rc = rcP.Invoke(pc, null);
            FieldInfo htF = rc.GetType().GetField("HairType", bf), hcF = rc.GetType().GetField("HairColour", bf);
            if (object.ReferenceEquals(htF, null) || object.ReferenceEquals(hcF, null)) return result.ToArray();
            int origType = (int)htF.GetValue(rc), origCol = (int)hcF.GetValue(rc);
            FieldInfo voiceF = rc.GetType().GetField("VoiceType", bf), bodyF = rc.GetType().GetField("BodyType", bf);
            int voice = object.ReferenceEquals(voiceF, null) ? -1 : (int)voiceF.GetValue(rc), bodyType = object.ReferenceEquals(bodyF, null) ? -1 : (int)bodyF.GetValue(rc);
            bool male = voice == 0;   // VoiceType 0 = the "default" (male) voice set, see event:/vo/default; anything else is left alone
            Log.Info("Spectators: rider voice type " + voice + ", body type " + bodyType + (male ? " (male: styles named female are skipped)" : ""));

            GameObject[] raw = null;
            Type hairTypesT = FindType("RiderHairTypes");
            FieldInfo listF = object.ReferenceEquals(hairTypesT, null) ? null : hairTypesT.GetField("_hairTypes", bf);
            if (!object.ReferenceEquals(listF, null))
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(hairTypesT)) { raw = listF.GetValue(o) as GameObject[]; if (raw != null) break; }
            if (raw == null) return result.ToArray();

            var goFields = new List<FieldInfo>();
            foreach (FieldInfo f in pcT.GetFields(bf)) if (object.ReferenceEquals(f.FieldType, typeof(GameObject))) goFields.Add(f);

            Log.Info("Spectators: rider's own hair is type " + origType + " colour " + origCol + "; trying " + raw.Length + " styles on the real rider for a moment");
            try
            {
                for (int i = 0; i < raw.Length; i++)
                {
                    if (raw[i] == null) continue;
                    setHair.Invoke(pc, new object[] { i, false });
                    GameObject inst = null;
                    foreach (FieldInfo f in goFields)
                    {
                        GameObject go = f.GetValue(pc) as GameObject;
                        if (go != null && go.transform.parent == hairParent) { inst = go; break; }
                    }
                    if (inst == null) { Log.Info("Spectators: style " + i + " produced no hair object on the rider"); continue; }
                    bool female = inst.name.ToLowerInvariant().Contains("female");
                    Log.Info("Spectators: style " + i + " = '" + inst.name + "'" + (female && male ? " (skipped: female style on a male rider)" : ""));
                    if (female && male) continue;
                    if (result.Count < 2)
                        Log.Info("Spectators: real placement of style " + i + " ('" + inst.name + "'): local pos " + inst.transform.localPosition + " rot " + inst.transform.localEulerAngles + " scale " + inst.transform.localScale
                            + "; prefab said pos " + raw[i].transform.localPosition + " rot " + raw[i].transform.localEulerAngles + " scale " + raw[i].transform.localScale);
                    GameObject copy = (GameObject)UnityEngine.Object.Instantiate(inst, holder);   // inactive parent: nothing on it starts
                    Strip(copy, new StringBuilder());
                    copy.name = "hair_template_" + i;
                    result.Add(copy);
                }
            }
            catch (Exception ex) { Log.Warn("Spectators: applying hair styles to the rider failed: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); }
            finally
            {
                try { setHair.Invoke(pc, new object[] { origType, false }); setCol.Invoke(pc, new object[] { origCol }); Log.Info("Spectators: rider's hair restored (type " + origType + ", colour " + origCol + ")"); }
                catch (Exception ex) { Log.Warn("Spectators: RESTORING the rider's hair failed: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); }
            }
            return result.ToArray();
        }

        private static GameObject[] HairPrefabs()
        {
            var list = new List<GameObject>();
            try
            {
                Type t = FindType("RiderHairTypes");
                if (object.ReferenceEquals(t, null)) return list.ToArray();
                FieldInfo f = t.GetField("_hairTypes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (object.ReferenceEquals(f, null)) return list.ToArray();
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(t))
                {
                    GameObject[] arr = f.GetValue(o) as GameObject[];
                    if (arr == null) continue;
                    foreach (GameObject g in arr) if (g != null && !list.Contains(g)) list.Add(g);
                }
            }
            catch (Exception ex) { Log.Warn("Spectators: reading the hair list failed: " + ex.Message); }
            return list.ToArray();
        }

        /// <summary>Copy a hair prefab onto the head bone (where the game parents it), stripped of scripts and physics, in the given colour.</summary>
        private static void AttachHair(Transform head, GameObject prefab, Color colour, Transform inactiveHolder)
        {
            bool diag = _hairDiag < 3;
            if (diag)
            {
                _hairDiag++;
                var d = new StringBuilder("Spectators: hair prefab '" + prefab.name + "' root local pos " + prefab.transform.localPosition + " rot " + prefab.transform.localEulerAngles + " scale " + prefab.transform.localScale + "; parts:");
                foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true))
                {
                    var comps = new StringBuilder();
                    foreach (Component c in t.GetComponents<Component>()) if (c != null && !(c is Transform)) comps.Append(c.GetType().Name).Append(',');
                    d.Append("\n    ").Append(t.name).Append(" pos ").Append(t.localPosition).Append(" rot ").Append(t.localEulerAngles).Append(" scale ").Append(t.localScale).Append(" [").Append(comps).Append(']');
                }
                Log.Info(d.ToString());
            }
            GameObject h = (GameObject)UnityEngine.Object.Instantiate(prefab, inactiveHolder);   // inactive parent: nothing on it starts
            Strip(h, new StringBuilder());
            h.transform.SetParent(head, false);   // keeps the prefab's own local placement, as the game does
            h.name = "hair";
            foreach (Renderer r in h.GetComponentsInChildren<Renderer>(true))
            {
                Material m = r.material;
                if (m != null && m.HasProperty("_Color")) m.SetColor("_Color", colour);
            }
            if (diag)
            {
                var d = new StringBuilder("Spectators: hair attached to '" + head.name + "' at world " + head.position + " (head lossyScale " + head.lossyScale + ", up " + head.up + ", forward " + head.forward + "):");
                foreach (Renderer r in h.GetComponentsInChildren<Renderer>(true))
                    d.Append("\n    ").Append(r.GetType().Name).Append(" '").Append(r.name).Append("' enabled ").Append(r.enabled).Append(" bounds centre offset from head ").Append(r.bounds.center - head.position).Append(" size ").Append(r.bounds.size)
                     .Append(" shader ").Append(r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "?");
                Log.Info(d.ToString());
            }
        }

        private static int _hairDiag;
        /// <summary>
        /// Head accessories from the game's own catalogue (CustomizationItem, slot 11 = Accessories_Head): hats, crowns, mohawks, animal heads.
        /// Only items that hide nothing (so no full-face masks) and are not props such as a knife or a camera. The item's assetPath is a Resources path.
        /// </summary>
        private static GameObject[] BuildHatTemplates(Transform holder)
        {
            var result = new List<GameObject>();
            try
            {
                Type it = FindType("CustomizationItem");
                if (object.ReferenceEquals(it, null)) return result.ToArray();
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                FieldInfo fName = it.GetField("displayName", bf), fSlot = it.GetField("slot", bf), fPath = it.GetField("assetPath", bf), fHide = it.GetField("hideSlots", bf);
                string[] notWanted = { "mask", "knife", "holder", "camera", "wings", "blindfold", "none" };
                var loaded = new StringBuilder();
                int tried = 0, failed = 0;
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(it))
                {
                    if (Convert.ToInt32(fSlot.GetValue(o)) != 11) continue;
                    Array hide = fHide.GetValue(o) as Array;
                    if (hide != null && hide.Length > 0) continue;
                    string name = (string)fName.GetValue(o), path = (string)fPath.GetValue(o);
                    string lower = (name ?? "").ToLowerInvariant();
                    bool skip = false;
                    foreach (string w in notWanted) if (lower.Contains(w)) skip = true;
                    if (skip || string.IsNullOrEmpty(path)) continue;
                    tried++;
                    GameObject prefab = Resources.Load<GameObject>(path + "_prefab");   // the game loads CustomizationItem.assetPath + "_prefab" (BundleManager.ResourcesLoad -> Resources.Load)
                    if (prefab == null) { failed++; if (failed <= 3) Log.Info("Spectators: hat '" + name + "' did not load from Resources path " + path + "_prefab"); continue; }
                    GameObject copy = (GameObject)UnityEngine.Object.Instantiate(prefab, holder);   // inactive parent: nothing on it starts
                    Strip(copy, new StringBuilder());
                    copy.name = "hat_template_" + name;
                    result.Add(copy);
                    loaded.Append(name).Append(", ");
                }
                Log.Info("Spectators: " + result.Count + " of " + tried + " head accessories loaded (" + failed + " failed): " + loaded);
            }
            catch (Exception ex) { Log.Warn("Spectators: loading head accessories failed: " + ex.Message); }
            return result.ToArray();
        }

        /// <summary>Copy a stripped template onto the head bone, keeping its own local placement, as the game does.</summary>
        private static void AttachTemplate(Transform head, GameObject template, Transform inactiveHolder, string name, float dropM)
        {
            GameObject h = (GameObject)UnityEngine.Object.Instantiate(template, inactiveHolder);
            h.transform.SetParent(head, false);
            h.name = name;
            // the game's head accessories are made to go over a helmet, so they float by about its thickness on a bare head: lower them
            if (dropM != 0f) h.transform.position += Vector3.down * dropM;
        }
        private const int GroupSize = 4;

        private struct Spot { public float S, Side, Off; }

        /// <summary>
        /// Small groups rather than an even spread: about 30 % of the groups gather near the finish end, 20 % near the start end, the rest anywhere.
        /// Each member is a few metres from its group's centre, on either side of the path.
        /// </summary>
        private static List<Spot> BuildSpots(float length, float margin, int want, System.Random rng)
        {
            var list = new List<Spot>();
            float lo = margin, hi = length - margin;
            if (hi <= lo) return list;
            int groups = Mathf.CeilToInt(want / (float)GroupSize);
            for (int g = 0; g < groups && list.Count < want; g++)
            {
                double r = rng.NextDouble();
                float centre = r < 0.30 ? hi - (float)rng.NextDouble() * Mathf.Min(80f, hi - lo)
                             : r < 0.50 ? lo + (float)rng.NextDouble() * Mathf.Min(80f, hi - lo)
                             : lo + (float)rng.NextDouble() * (hi - lo);
                for (int k = 0; k < GroupSize && list.Count < want; k++)
                {
                    float s = Mathf.Clamp(centre + ((float)rng.NextDouble() - 0.5f) * 14f, lo, hi);
                    list.Add(new Spot { S = s, Side = rng.NextDouble() < 0.5 ? 1f : -1f, Off = Config.SpectatorOffsetM + (float)rng.NextDouble() * 3.5f });
                }
            }
            return list;
        }

        // ---- clothing --------------------------------------------------------------------------------------------------------------------
        // The game equips a garment by instantiating the item prefab under the slot's spawn point and copying rootBone and bones from the rider's
        // reference skinned mesh onto every skinned mesh in the prefab (PlayerCustomization equip routine, IL). That is all a spectator needs.

        private sealed class Cloth { public string Name, Path; }

        private readonly Dictionary<string, int> _clothSlotByPrefab = new Dictionary<string, int>();   // "haow_noir_short_prefab" -> slot 2 / 3
        private readonly List<Cloth> _torso = new List<Cloth>(), _legs = new List<Cloth>();
        private readonly Dictionary<string, GameObject> _clothCache = new Dictionary<string, GameObject>();
        private bool _clothesLoaded;

        private void LoadClothingCatalogue()
        {
            if (_clothesLoaded) return;
            _clothesLoaded = true;
            _clothSlotByPrefab.Clear(); _torso.Clear(); _legs.Clear();
            try
            {
                Type it = FindType("CustomizationItem");
                if (object.ReferenceEquals(it, null)) return;
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                FieldInfo fName = it.GetField("displayName", bf), fSlot = it.GetField("slot", bf), fPath = it.GetField("assetPath", bf), fHide = it.GetField("hideSlots", bf);
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(it))
                {
                    int slot = Convert.ToInt32(fSlot.GetValue(o));
                    if (slot != 2 && slot != 3) continue;   // Torso, Legs
                    string path = (string)fPath.GetValue(o), name = (string)fName.GetValue(o);
                    if (string.IsNullOrEmpty(path)) continue;
                    string last = path.Substring(path.LastIndexOf('/') + 1);
                    _clothSlotByPrefab[last + "_prefab"] = slot;   // so the copy's own shirt / trousers can be recognised by their mesh name and removed
                    Array hide = fHide.GetValue(o) as Array;
                    if ((hide != null && hide.Length > 0) || (name ?? "").ToLowerInvariant().Contains("none")) continue;
                    (slot == 2 ? _torso : _legs).Add(new Cloth { Name = name, Path = path });
                }
                Log.Info("Spectators: clothing catalogue: " + _torso.Count + " torso and " + _legs.Count + " leg items usable, " + _clothSlotByPrefab.Count + " garment names known");
            }
            catch (Exception ex) { Log.Warn("Spectators: reading the clothing catalogue failed: " + ex.Message); }
        }

        /// <summary>Take off the copy's shirt and trousers and dress it in a random torso and legs item.</summary>
        private void SwapClothes(GameObject g, System.Random rng, Transform holder, bool log)
        {
            SkinnedMeshRenderer body = null;
            foreach (SkinnedMeshRenderer r in g.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (r.name == "character_base") { body = r; break; }
            if (body == null) return;
            Transform meshParent = body.transform.parent;
            int removed = 0;
            foreach (SkinnedMeshRenderer r in meshParent.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string n = r.name.EndsWith("(Clone)", StringComparison.Ordinal) ? r.name.Substring(0, r.name.Length - 7) : r.name;
                if (_clothSlotByPrefab.ContainsKey(n)) { UnityEngine.Object.Destroy(r.gameObject); removed++; }
            }
            string top = Dress(meshParent, body, _torso, rng, holder), legs = Dress(meshParent, body, _legs, rng, holder);
            int alpha = AlphaMapIndex(top, legs);
            Material skin = BodyMaterial(body, alpha);
            if (skin != null) body.sharedMaterial = skin;
            if (log) Log.Info("Spectators: first spectator: took off " + removed + " garment mesh(es), now wearing " + top + " + " + legs + ", body alpha map " + alpha
                + (skin != null ? " (" + skin.mainTexture.name + ")" : " (not available, kept the rider's)"));
        }

        // The body skin is an alpha-cutout material; its main texture is cut away wherever the outfit covers the body, one texture per jersey / legs
        // combination. The game picks it in CyclistModel.FixAlphaMaps from the item display names (IL); a copy keeps the rider's own, so a spectator
        // in shorts had no shins. The textures come from the real rider's CyclistModel (its only Texture2D[] field); one material per texture.
        private Texture2D[] _alphaMaps;
        private readonly Dictionary<int, Material> _bodyMats = new Dictionary<int, Material>();

        /// <summary>CyclistModel.FixAlphaMaps: 1 long top + pants, 2 long top + shorts, 3 short jersey + pants, 4 short jersey + shorts, else 0.</summary>
        private static int AlphaMapIndex(string top, string legs)
        {
            bool longTop = top.Contains("Jersey Long") || top.Contains("Hoodie"), shortTop = top.Contains("Jersey Short");
            bool pants = legs.Contains("Pants"), shorts = legs.Contains("Shorts");
            if (longTop && pants) return 1;
            if (longTop && shorts) return 2;
            if (shortTop && pants) return 3;
            if (shortTop && shorts) return 4;
            return 0;
        }

        private Material BodyMaterial(SkinnedMeshRenderer body, int index)
        {
            Material riderMat = body.sharedMaterial;
            if (riderMat == null || _alphaMaps == null || index < 0 || index >= _alphaMaps.Length || _alphaMaps[index] == null) return null;
            Material m;
            if (!_bodyMats.TryGetValue(index, out m) || m == null)
            {
                m = new Material(riderMat) { name = "spectator_body_" + index, mainTexture = _alphaMaps[index] };   // a copy: the rider's own material is never touched
                _bodyMats[index] = m;
            }
            return m;
        }

        // Rider body types (GameData -> RiderBodyTypes._bodyTypes, each BodyType.meshes). The game applies one by swapping a skinned mesh for the
        // same-named mesh of that body type (PlayerCustomization, IL: the body and the torso item); the skeleton is shared. A spectator gets a body type
        // other than the rider's own: every skinned mesh on it whose name the body type has is swapped, before the pose (the bind pose is the mesh's).
        private Mesh[][] _bodyTypes;
        private readonly List<int> _otherBodies = new List<int>();

        private void FindBodyTypes()
        {
            _bodyTypes = null; _otherBodies.Clear();
            if (Config.SpectatorFemaleShare <= 0f) return;
            try
            {
                Type gdT = FindType("GameData"), rbT = FindType("RiderBodyTypes"), btT = FindType("BodyType");
                if (object.ReferenceEquals(gdT, null) || object.ReferenceEquals(rbT, null) || object.ReferenceEquals(btT, null)) { Log.Warn("Spectators: body type classes not found"); return; }
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic;
                object gd = null;
                foreach (FieldInfo f in gdT.GetFields(all | BindingFlags.Static)) if (object.ReferenceEquals(f.FieldType, gdT)) { gd = f.GetValue(null); if (gd != null) break; }
                object rb = null;
                if (gd != null) foreach (FieldInfo f in gdT.GetFields(all | BindingFlags.Instance)) if (object.ReferenceEquals(f.FieldType, rbT)) { rb = f.GetValue(gd); break; }
                FieldInfo list = rbT.GetField("_bodyTypes", all | BindingFlags.Instance), meshes = btT.GetField("meshes", all | BindingFlags.Instance);
                Array types = rb != null && !object.ReferenceEquals(list, null) ? list.GetValue(rb) as Array : null;
                if (types == null || object.ReferenceEquals(meshes, null)) { Log.Warn("Spectators: no rider body types found (GameData " + (gd != null) + ", RiderBodyTypes " + (rb != null) + ")"); return; }

                // the rider's own body type: the one holding a mesh the rider wears (compare by reference, the names are shared between body types)
                var riderMeshes = new List<Mesh>();
                GameObject rider = GameObject.Find("Player_Human");
                if (rider != null) foreach (SkinnedMeshRenderer r in rider.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (r.sharedMesh != null) riderMeshes.Add(r.sharedMesh);
                _bodyTypes = new Mesh[types.Length][];
                var sb = new StringBuilder("Spectators: " + types.Length + " rider body types:");
                for (int i = 0; i < types.Length; i++)
                {
                    object bt = types.GetValue(i);
                    _bodyTypes[i] = (bt != null ? meshes.GetValue(bt) as Mesh[] : null) ?? new Mesh[0];
                    bool own = false;
                    sb.Append("\n    ").Append(i).Append(": ");
                    foreach (Mesh m in _bodyTypes[i])
                    {
                        if (m == null) { sb.Append("null, "); continue; }
                        if (riderMeshes.Contains(m)) own = true;
                        sb.Append(m.name).Append(" (").Append(m.vertexCount).Append("), ");
                    }
                    if (own) sb.Append(" <- the rider's");
                    else if (_bodyTypes[i].Length > 0) _otherBodies.Add(i);
                }
                Log.Info(sb.ToString());
                if (_otherBodies.Count == 0) { _bodyTypes = null; Log.Info("Spectators: no other body type to use"); }
            }
            catch (Exception ex) { Log.Warn("Spectators: reading the body types failed: " + ex.Message); _bodyTypes = null; }
        }

        // The game has one head and one face for everyone (no blend shapes), so a female spectator is also a little smaller overall (women are about
        // 7 % shorter on average) and gets a slightly narrower, shorter head, which softens the square jaw. Hair and hats sit under the head bone
        // and scale with it.
        private const float FemaleScale = 0.94f;
        private static readonly Vector3 FemaleHead = new Vector3(0.92f, 0.96f, 0.96f);   // width, height, depth

        /// <summary>Scale the head bone by FemaleHead along the character's own axes (the bone's local axes are found from the rest pose).</summary>
        private static void NarrowHead(Cheerer c)
        {
            if (c.Head == null) return;
            Quaternion inv = Quaternion.Inverse(c.Head.rotation);
            Vector3 side = inv * c.Left, up = inv * Vector3.up;
            var s = new Vector3(FemaleHead.z, FemaleHead.z, FemaleHead.z);   // depth unless the axis is the side or the up one
            for (int a = 0; a < 3; a++)
            {
                if (Mathf.Abs(side[a]) > 0.7f) s[a] = FemaleHead.x;
                else if (Mathf.Abs(up[a]) > 0.7f) s[a] = FemaleHead.y;
            }
            c.Head.localScale = Vector3.Scale(c.Head.localScale, s);
        }

        private static void ApplyBodyType(GameObject g, Mesh[] set, ref bool logged)
        {
            var swapped = new StringBuilder();
            foreach (SkinnedMeshRenderer r in g.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh cur = r.sharedMesh;
                if (cur == null) continue;
                foreach (Mesh m in set)
                    if (m != null && m.name == cur.name && !object.ReferenceEquals(m, cur)) { r.sharedMesh = m; swapped.Append(r.name).Append('=').Append(m.name).Append(' '); break; }
            }
            if (!logged) { logged = true; Log.Info("Spectators: first spectator with another body type (" + g.name + "): swapped " + (swapped.Length > 0 ? swapped.ToString() : "nothing (no mesh names matched)")); }
        }

        private void FindAlphaMaps(GameObject rider)
        {
            _alphaMaps = null;
            foreach (Material m in _bodyMats.Values) if (m != null) UnityEngine.Object.Destroy(m);
            _bodyMats.Clear();
            try
            {
                Type cm = FindType("CyclistModel");
                if (object.ReferenceEquals(cm, null)) { Log.Warn("Spectators: CyclistModel type not found, body alpha maps unavailable"); return; }
                Component model = rider.GetComponentInChildren(cm, true);
                if (model == null) { Log.Warn("Spectators: no CyclistModel on the rider, body alpha maps unavailable"); return; }
                foreach (FieldInfo f in cm.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (object.ReferenceEquals(f.FieldType, typeof(Texture2D[]))) { _alphaMaps = f.GetValue(model) as Texture2D[]; break; }
                var names = new StringBuilder();
                if (_alphaMaps != null) foreach (Texture2D t in _alphaMaps) names.Append(t != null ? t.name : "null").Append(", ");
                Log.Info("Spectators: body alpha maps: " + (_alphaMaps != null ? _alphaMaps.Length + " [" + names + "]" : "Texture2D[] field not found"));
            }
            catch (Exception ex) { Log.Warn("Spectators: reading the body alpha maps failed: " + ex.Message); }
        }

        private string Dress(Transform meshParent, SkinnedMeshRenderer body, List<Cloth> items, System.Random rng, Transform holder)
        {
            for (int attempt = 0; attempt < 8 && items.Count > 0; attempt++)
            {
                Cloth c = items[rng.Next(items.Count)];
                GameObject template = LoadCloth(c, holder);
                if (template == null) continue;
                GameObject worn = (GameObject)UnityEngine.Object.Instantiate(template, meshParent);   // keeps the prefab's own local placement, as the game does
                worn.name = "garment_" + c.Name;
                foreach (SkinnedMeshRenderer r in worn.GetComponentsInChildren<SkinnedMeshRenderer>(true)) { r.rootBone = body.rootBone; r.bones = body.bones; }
                return c.Name;
            }
            return "(nothing loaded)";
        }

        private GameObject LoadCloth(Cloth c, Transform holder)
        {
            GameObject t;
            if (_clothCache.TryGetValue(c.Path, out t)) return t;
            GameObject prefab = Resources.Load<GameObject>(c.Path + "_prefab");
            if (prefab != null && prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
            {
                t = (GameObject)UnityEngine.Object.Instantiate(prefab, holder);   // inactive parent: nothing on it starts
                Strip(t, new StringBuilder());
                t.name = "garment_template_" + c.Name;
            }
            _clothCache[c.Path] = t;   // null is remembered too, so a bad item is not retried
            return t;
        }
        private static bool _itemsListed;

        /// <summary>Log every customization item that goes on the head (slot Head = 1, Accessories_Head = 11) and which slots it hides: is there anything besides helmets?</summary>
        private static void ListHeadItems()
        {
            try
            {
                Type it = FindType("CustomizationItem");
                if (object.ReferenceEquals(it, null)) return;
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                FieldInfo fName = it.GetField("displayName", bf), fSlot = it.GetField("slot", bf), fPath = it.GetField("assetPath", bf), fHide = it.GetField("hideSlots", bf), fId = it.GetField("itemID", bf);
                var sb = new StringBuilder();
                int n = 0, total = 0;
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(it))
                {
                    total++;
                    int slot = Convert.ToInt32(fSlot.GetValue(o));
                    if (slot != 1 && slot != 11) continue;
                    var hide = new StringBuilder();
                    Array h = fHide.GetValue(o) as Array;
                    if (h != null) foreach (object x in h) hide.Append(Convert.ToInt32(x)).Append(",");
                    sb.Append("\n    slot ").Append(slot).Append("  id ").Append(fId.GetValue(o)).Append("  '").Append(fName.GetValue(o)).Append("'  hides [").Append(hide).Append("]  ").Append(fPath.GetValue(o));
                    n++;
                }
                Log.Info("Spectators: " + total + " customization items loaded, " + n + " for the head (slot 1 Head, 11 Accessories_Head; hideSlots 11 hides hair):" + sb);
            }
            catch (Exception ex) { Log.Warn("Spectators: listing head items failed: " + ex.Message); }
        }

        private static Type FindType(string name)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t = asm.GetType(name, false); if (!object.ReferenceEquals(t, null)) return t; }
                catch (Exception) { }
            }
            return null;
        }

        private const float SoleBelowBoneM = 0.06f;   // guess: lowest bone (ankle) to the sole of the shoe; tune from the log / by eye

        /// <summary>
        /// A natural cheer: the upper arm goes out and up (about 40 degrees above horizontal) and the forearm bends to point almost straight up.
        /// Straight-up arms stretch the sleeve at the armpit and look stiff.
        /// </summary>
        private static void RaiseArm(Transform shoulder, Transform elbow, Transform wrist)
        {
            Vector3 d = elbow.position - shoulder.position;
            if (d.sqrMagnitude < 1e-8f) return;
            Vector3 outward = new Vector3(d.x, 0f, d.z);
            outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : Vector3.right;
            shoulder.rotation = Quaternion.FromToRotation(d, (outward + Vector3.up * 0.8f).normalized) * shoulder.rotation;
            if (wrist == null) return;
            Vector3 f = wrist.position - elbow.position;
            if (f.sqrMagnitude < 1e-8f) return;
            elbow.rotation = Quaternion.FromToRotation(f, (Vector3.up + outward * 0.2f).normalized) * elbow.rotation;
        }

        /// <summary>The finger chains of one hand (knuckle to tip; the thumb has 3 joints, the others 4), each only as far as its bones were found.</summary>
        private static Transform[][] Fingers(Transform[] bones, string side)
        {
            var hand = new List<Transform[]>();
            foreach (string finger in new[] { "Index", "Middle", "Ring", "Pink", "Thumb" })
            {
                var chain = new List<Transform>();
                for (int j = 1; j <= 4; j++)
                {
                    Transform b = Bone(bones, side + finger + j);
                    if (b == null) break;
                    chain.Add(b);
                }
                if (chain.Count >= 2) hand.Add(chain.ToArray());
            }
            return hand.ToArray();
        }

        /// <summary>
        /// The palm normal in the wrist's local frame, from the hand's own geometry: fingers F (wrist to middle knuckle) and the thumb side A (little-finger
        /// knuckle to index knuckle). For a left hand the palm faces -(F x A), for a right hand +(F x A): the hands are mirror images.
        /// </summary>
        private static Vector3 PalmLocal(Transform[] bones, string side, Transform wrist, float sign)
        {
            Transform mid = Bone(bones, side + "Middle1"), index = Bone(bones, side + "Index1"), pink = Bone(bones, side + "Pink1");
            if (wrist == null || mid == null || index == null || pink == null) return Vector3.zero;
            Vector3 palm = -sign * Vector3.Cross(mid.position - wrist.position, index.position - pink.position);
            return palm.sqrMagnitude > 1e-10f ? Quaternion.Inverse(wrist.rotation) * palm.normalized : Vector3.zero;
        }

        private static Transform Bone(Transform[] bones, string suffix)
        {
            foreach (Transform b in bones) if (b != null && b.name.EndsWith(suffix, StringComparison.Ordinal)) return b;
            return null;
        }

        private static int Depth(Transform t)
        {
            int d = 0;
            for (Transform p = t.parent; p != null; p = p.parent) d++;
            return d;
        }
    }
}
