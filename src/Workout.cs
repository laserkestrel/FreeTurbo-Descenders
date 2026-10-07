#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

// Pure logic (no Unity, no logging): also compiled into the bridge's `selftest` (bridge/DescendersBridge.csproj links this file).
namespace DescendersCyclingPOC
{
    /// <summary>One flat step of a workout. IntervalsT is expanded into alternating "on" / "off" steps.</summary>
    internal sealed class WorkoutStep
    {
        public string Kind;                 // warmup | cooldown | ramp | steady | on | off | freeride
        public int Index, Of;               // "on" / "off": effort number and how many efforts the workout has
        public float StartS, DurationS;     // absolute position in the workout
        public float StartW, EndW;          // target at the start / end of the step (watts, before any bias); unused for freeride

        public bool FreeRide { get { return Kind == "freeride"; } }
        public float EndS { get { return StartS + DurationS; } }

        public float WattsAt(float t)
        {
            float f = DurationS <= 0f ? 1f : Math.Min(1f, Math.Max(0f, (t - StartS) / DurationS));
            return StartW + (EndW - StartW) * f;
        }

        public string Label
        {
            get
            {
                switch (Kind)
                {
                    case "warmup": return "Warm-up";
                    case "cooldown": return "Cool-down";
                    case "ramp": return "Ramp";
                    case "steady": return "Steady";
                    case "on": return "Interval " + Index + " of " + Of;
                    case "off": return "Recovery " + Index + " of " + Of;
                    case "freeride": return "Free ride";
                    default: return Kind;
                }
            }
        }
    }

    internal sealed class WorkoutText { public float AtS; public string Message; }

    /// <summary>Row text and zone for the workout HUD panel (V2.18); pure so it can be tested.</summary>
    internal static class WorkoutRows
    {
        /// <summary>30sec, 3min, 1min 30sec.</summary>
        public static string Dur(float s)
        {
            int i = (int)Math.Round(Math.Max(0f, s));
            if (i < 60) return i + "sec";
            return i % 60 == 0 ? (i / 60) + "min" : (i / 60) + "min " + (i % 60) + "sec";
        }

        /// <summary>Row title, e.g. "30sec @ 180w"; watts are the plan times the bias, whole watts.</summary>
        public static string Title(WorkoutStep s, float bias)
        {
            switch (s.Kind)
            {
                case "warmup": return Dur(s.DurationS) + " warmup";
                case "cooldown": return Dur(s.DurationS) + " cool down";
                case "ramp": return Dur(s.DurationS) + (s.EndW >= s.StartW ? " ramp up" : " ramp down");
                case "freeride": return Dur(s.DurationS) + " free ride";
                default: return Dur(s.DurationS) + " @ " + Math.Round(s.StartW * bias) + "w";
            }
        }

        /// <summary>Small range text for a ramping row ("45-135w"), else "".</summary>
        public static string Range(WorkoutStep s, float bias)
        {
            if (s.Kind == "steady" || s.Kind == "on" || s.Kind == "off" || s.FreeRide) return "";
            return Math.Round(s.StartW * bias) + "-" + Math.Round(s.EndW * bias) + "w";
        }

        /// <summary>Zone by planned % of FTP: 0 grey, 1 blue, 2 green, 3 yellow, 4 orange, 5 red; -1 = neutral (ramps, free ride).</summary>
        public static int Zone(WorkoutStep s, float ftpW)
        {
            if (s.Kind == "warmup" || s.Kind == "cooldown" || s.Kind == "ramp" || s.FreeRide || ftpW <= 0f) return -1;
            float pct = s.StartW / ftpW * 100f;
            if (pct < 56f) return 0;
            if (pct < 76f) return 1;
            if (pct < 91f) return 2;
            if (pct < 106f) return 3;
            if (pct <= 120f) return 4;
            return 5;
        }
    }

    internal sealed class Workout
    {
        public string Name = "", FileName = "", Author = "", Description = "", Error;
        public readonly List<WorkoutStep> Steps = new List<WorkoutStep>();
        public readonly List<WorkoutText> Texts = new List<WorkoutText>();
        public readonly List<string> Warnings = new List<string>();
        public float TotalS;
        public bool Ok { get { return Error == null && Steps.Count > 0; } }

        /// <summary>The workout's own target at time t (before bias); NaN in a free-ride step or past the end.</summary>
        public float WattsAt(float t)
        {
            for (int i = 0; i < Steps.Count; i++)
                if (t < Steps[i].EndS) return Steps[i].FreeRide ? float.NaN : Steps[i].WattsAt(t);
            return float.NaN;
        }
    }

    /// <summary>.zwo workout XML -> <see cref="Workout"/> (docs/REQUIREMENTS.md V2.12 / V2.13). Power values are fractions of FTP; never throws.</summary>
    internal static class WorkoutParser
    {
        private static readonly string[] KnownAttrs = { "Duration", "PowerLow", "PowerHigh", "Power", "Repeat", "OnDuration", "OffDuration", "OnPower", "OffPower", "timeoffset", "message" };

        /// <summary>Display name from a file name: "workout30.zwo" -> "workout30" (the extension is not shown in the game).</summary>
        public static string Stem(string fileName) { return string.IsNullOrEmpty(fileName) ? "" : Path.GetFileNameWithoutExtension(fileName); }

        public static Workout Load(string path, float ftpW)
        {
            try { return Parse(File.ReadAllText(path), Path.GetFileName(path), ftpW); }
            catch (Exception ex)
            {
                var w = new Workout { FileName = Path.GetFileName(path), Error = ex.Message };
                w.Name = Stem(w.FileName);
                return w;
            }
        }

        public static Workout Parse(string xml, string fileName, float ftpW)
        {
            var w = new Workout { FileName = fileName ?? "" };
            var ignoredEls = new List<string>();
            var ignoredAttrs = new List<string>();
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                doc.LoadXml(xml);
                XmlNode root = doc.DocumentElement;
                if (root == null || root.Name != "workout_file") throw new FormatException("root element is not <workout_file>");
                XmlNode nameNode = root.SelectSingleNode("name");
                string name = nameNode != null ? nameNode.InnerText.Trim() : "";
                w.Name = name.Length > 0 ? name : (w.FileName.Length > 0 ? Stem(w.FileName) : "workout");
                XmlNode authorNode = root.SelectSingleNode("author"), descNode = root.SelectSingleNode("description");
                w.Author = authorNode != null ? authorNode.InnerText.Trim() : "";
                w.Description = descNode != null ? descNode.InnerText.Trim() : "";   // shown in the picker's detail pane
                XmlNode body = root.SelectSingleNode("workout");
                if (body == null) throw new FormatException("no <workout> element");

                float t = 0f;
                int efforts = 0;
                foreach (XmlNode n in body.ChildNodes)
                {
                    if (n.NodeType != XmlNodeType.Element) continue;
                    NoteAttrs(n, ignoredAttrs);
                    float startOfBlock = t;
                    switch (n.Name)
                    {
                        case "Warmup":
                        case "Cooldown":
                        case "Ramp":
                        {
                            float d = Num(n, "Duration", 0f);
                            if (!(d > 0f)) { w.Warnings.Add("<" + n.Name + "> without a positive Duration skipped"); break; }
                            string kind = n.Name == "Warmup" ? "warmup" : n.Name == "Cooldown" ? "cooldown" : "ramp";
                            Add(w, kind, ref t, d, Num(n, "PowerLow", 0f) * ftpW, Num(n, "PowerHigh", 0f) * ftpW);
                            break;
                        }
                        case "SteadyState":
                        {
                            float d = Num(n, "Duration", 0f);
                            if (!(d > 0f)) { w.Warnings.Add("<SteadyState> without a positive Duration skipped"); break; }
                            float p = Num(n, "Power", 0f) * ftpW;
                            Add(w, "steady", ref t, d, p, p);
                            break;
                        }
                        case "FreeRide":
                        {
                            float d = Num(n, "Duration", 0f);
                            if (!(d > 0f)) { w.Warnings.Add("<FreeRide> without a positive Duration skipped"); break; }
                            Add(w, "freeride", ref t, d, 0f, 0f);
                            break;
                        }
                        case "IntervalsT":
                        {
                            int rep = (int)Math.Round(Num(n, "Repeat", 1f));
                            float on = Num(n, "OnDuration", 0f), off = Num(n, "OffDuration", 0f);
                            float onW = Num(n, "OnPower", 0f) * ftpW, offW = Num(n, "OffPower", 0f) * ftpW;
                            if (rep < 1 || rep > 1000 || !(on > 0f || off > 0f)) { w.Warnings.Add("<IntervalsT> with a bad Repeat or durations skipped"); break; }
                            for (int i = 0; i < rep; i++)
                            {
                                efforts++;
                                if (on > 0f) { Add(w, "on", ref t, on, onW, onW); w.Steps[w.Steps.Count - 1].Index = efforts; }
                                if (off > 0f) { Add(w, "off", ref t, off, offW, offW); w.Steps[w.Steps.Count - 1].Index = efforts; }
                            }
                            break;
                        }
                        case "textevent":   // a stray top-level text event: attach at its own offset
                            AddText(w, n, 0f);
                            break;
                        default:
                            if (!ignoredEls.Contains(n.Name)) ignoredEls.Add(n.Name);
                            continue;
                    }
                    // <textevent timeoffset= message=> children are relative to the start of their block
                    foreach (XmlNode c in n.ChildNodes)
                        if (c.NodeType == XmlNodeType.Element && c.Name == "textevent") AddText(w, c, startOfBlock);
                }
                w.TotalS = t;
                foreach (var s in w.Steps) if (s.Kind == "on" || s.Kind == "off") s.Of = efforts;
                if (w.Steps.Count == 0) throw new FormatException("no supported steps");
            }
            catch (Exception ex) { w.Error = ex.Message; if (w.Name.Length == 0) w.Name = Stem(w.FileName); }
            if (ignoredEls.Count > 0) w.Warnings.Add("ignored elements: " + string.Join(", ", ignoredEls.ToArray()));
            if (ignoredAttrs.Count > 0) w.Warnings.Add("ignored attributes: " + string.Join(", ", ignoredAttrs.ToArray()));
            return w;
        }

        private static void Add(Workout w, string kind, ref float t, float dur, float startW, float endW)
        {
            w.Steps.Add(new WorkoutStep { Kind = kind, StartS = t, DurationS = dur, StartW = startW, EndW = endW });
            t += dur;
        }

        private static void AddText(Workout w, XmlNode n, float blockStart)
        {
            string msg = Attr(n, "message");
            if (msg.Length == 0) return;
            w.Texts.Add(new WorkoutText { AtS = blockStart + Num(n, "timeoffset", 0f), Message = msg });
        }

        private static string Attr(XmlNode n, string name)
        {
            XmlAttribute a = n.Attributes != null ? n.Attributes[name] : null;
            return a != null ? a.Value.Trim() : "";
        }

        private static float Num(XmlNode n, string name, float dflt)
        {
            string s = Attr(n, name);
            float v;
            if (s.Length == 0 || !float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || float.IsNaN(v) || float.IsInfinity(v)) return dflt;
            return v;
        }

        private static void NoteAttrs(XmlNode n, List<string> ignored)
        {
            if (n.Attributes == null) return;
            foreach (XmlAttribute a in n.Attributes)
                if (Array.IndexOf(KnownAttrs, a.Name) < 0 && !ignored.Contains(a.Name)) ignored.Add(a.Name);
        }
    }

    /// <summary>
    /// Plays a <see cref="Workout"/> (V2.16 / V2.17): time advances only while the rider pedals (paused after PauseSeconds of nothing),
    /// the target is the current step's power times the bias, and free-ride steps have no target.
    /// </summary>
    internal sealed class WorkoutRunner
    {
        public readonly Workout W;
        public float Elapsed;
        public int StepIndex;
        public bool Started, Paused, Finished;
        public int BiasSteps;

        private readonly float _biasStepPct, _pauseS;
        private float _idleS;
        private readonly bool[] _textDone;   // per W.Texts entry: already shown (or jumped over by Skip)

        public WorkoutRunner(Workout w, float biasStepPct, float pauseSeconds) { W = w; _biasStepPct = biasStepPct; _pauseS = pauseSeconds; _textDone = new bool[w.Texts.Count]; }

        public WorkoutStep Step { get { return W.Steps[Math.Min(StepIndex, W.Steps.Count - 1)]; } }
        public WorkoutStep NextStep { get { return StepIndex + 1 < W.Steps.Count ? W.Steps[StepIndex + 1] : null; } }
        public bool InFreeRide { get { return !Finished && Step.FreeRide; } }
        public float BiasFactor { get { return Math.Min(1.5f, Math.Max(0.5f, 1f + BiasSteps * _biasStepPct / 100f)); } }
        public float StepRemainingS { get { return Math.Max(0f, Step.EndS - Elapsed); } }
        public float TotalRemainingS { get { return Math.Max(0f, W.TotalS - Elapsed); } }

        /// <summary>The workout's power right now before bias (NaN in a free-ride step).</summary>
        public float BaseTargetW { get { return Finished || Step.FreeRide ? float.NaN : Step.WattsAt(Elapsed); } }
        /// <summary>The target to hold: base x bias (NaN in a free-ride step).</summary>
        public float TargetW { get { float b = BaseTargetW; return float.IsNaN(b) ? b : b * BiasFactor; } }


        public void Tick(float dt, bool pedalling)
        {
            if (Finished || dt <= 0f) return;
            if (!Started)
            {
                if (!pedalling) return;   // armed: waits for the first pedal stroke
                Started = true;
            }
            if (pedalling) { _idleS = 0f; Paused = false; }
            else { _idleS += dt; if (_idleS > _pauseS) Paused = true; }
            if (!Paused) Elapsed += dt;
            Settle();
        }

        /// <summary>Jump to the start of the next step (the last step ends the workout).</summary>
        public void Skip()
        {
            if (Finished) return;
            Started = true; Paused = false; _idleS = 0f;
            Elapsed = Step.EndS;
            for (int i = 0; i < _textDone.Length; i++) if (W.Texts[i].AtS < Elapsed) _textDone[i] = true;   // no burst of skipped messages
            Settle();
        }

        /// <summary>Adds the text events that came due since the last call (each one once, only after the first pedal stroke) to <paramref name="into"/>.</summary>
        public void TakeDueTexts(List<string> into)
        {
            if (!Started) return;
            for (int i = 0; i < _textDone.Length; i++)
                if (!_textDone[i] && W.Texts[i].AtS <= Elapsed) { _textDone[i] = true; into.Add(W.Texts[i].Message); }
        }

        public void AdjustBias(int steps) { BiasSteps += steps; }

        private void Settle()
        {
            while (StepIndex < W.Steps.Count && Elapsed >= W.Steps[StepIndex].EndS) StepIndex++;
            if (StepIndex >= W.Steps.Count) { Finished = true; StepIndex = W.Steps.Count - 1; Elapsed = W.TotalS; }
        }

        public static string Fmt(float s)
        {
            int i = (int)Math.Ceiling(Math.Max(0f, s));
            return (i / 60) + ":" + (i % 60).ToString("00");
        }
    }
}
