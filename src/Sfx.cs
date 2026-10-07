using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FMODUnity;
using UnityEngine;

namespace DescendersCyclingPOC
{
    internal enum Cue { Engage, Disengage, Warn, HandOver }

    /// <summary>
    /// Audio cues for auto-steer state changes. Preferred: one of the game's own FMOD UI events, found at runtime by
    /// listing every event in the loaded banks (the game stores event names in a compressed tree, so they cannot be read
    /// from the bank file) and matching keywords, e.g. "checkbox_on" for engage. Fallback: a short synthesised tone.
    /// Mode (Config.SfxMode): "game" (default), "beep", "off". Explicit event paths can be set per cue in the preferences.
    /// </summary>
    internal static class Sfx
    {
        private static bool _init;
        private static readonly List<string> Events = new List<string>();
        private static readonly string[] Chosen = new string[4];
        private static readonly AudioClip[] Clips = new AudioClip[4];
        private static AudioSource _src;

        // Keyword preference lists, tried in order against the game's event paths ("event:/ui/..." preferred).
        private static readonly string[][] Keywords =
        {
            new[] { "checkbox_on", "button_confirm", "confirm" },              // Engage
            new[] { "checkbox_off", "button_back", "back" },                   // Disengage
            new[] { "checkpoint", "objective_pass", "invalid" },               // Warn: finish/start approaching
            new[] { "invalid", "objective_fail", "checkbox_off" },             // HandOver: control is yours again
        };

        public static void Play(Cue cue)
        {
            if (Config.SfxMode == "off") return;
            try
            {
                Init();
                string path = Config.SfxMode == "game" ? Resolve(cue) : null;
                if (path != null) { RuntimeManager.PlayOneShot(path); return; }
                Beep(cue);
            }
            catch (Exception ex)
            {
                Log.ErrorLimited("sfx", "Sfx.Play(" + cue + ")", ex, 30f);
                try { Beep(cue); } catch (Exception) { /* nothing else to try */ }
            }
        }

        /// <summary>Play a game event at the listener (full level whether the event is 3D or 2D). Blank path = nothing.</summary>
        public static void PlayHere(string path)
        {
            Camera cam = Camera.main;
            if (cam != null) PlayAt(path, cam.transform.position, 1f);
        }

        private static readonly Dictionary<string, bool> Is3D = new Dictionary<string, bool>();

        /// <summary>
        /// Play a game event at a world position (spectator noisemakers). A 3D event is placed there and FMOD fades it with distance; a 2D one
        /// gets the fade here instead (full up to 5 m from the camera, silent at 60 m). volume 0..4 (FMOD amplifies above 1) on top of the event's own level.
        /// </summary>
        public static void PlayAt(string path, Vector3 pos, float volume)
        {
            if (Config.SfxMode == "off" || string.IsNullOrEmpty(path)) return;
            try
            {
                bool is3d;
                if (!Is3D.TryGetValue(path, out is3d))
                {
                    FMOD.Studio.EventDescription desc = RuntimeManager.GetEventDescription(path);
                    float min, max;
                    desc.is3D(out is3d); desc.getMinimumDistance(out min); desc.getMaximumDistance(out max);
                    Is3D[path] = is3d;
                    Log.Info("Sfx: " + path + (is3d ? " is 3D (" + min.ToString("F0") + "-" + max.ToString("F0") + " m)" : " is 2D (distance fade done by the mod)"));
                }
                if (!is3d)
                {
                    Camera cam = Camera.main;
                    float d = cam != null ? (cam.transform.position - pos).magnitude : 0f;
                    volume *= 1f - Mathf.Clamp01((d - 5f) / 55f);
                    if (volume < 0.02f) return;
                }
                FMOD.Studio.EventInstance inst = RuntimeManager.CreateInstance(path);
                inst.set3DAttributes(RuntimeUtils.To3DAttributes(pos));
                inst.setVolume(volume);
                inst.start();
                inst.release();   // freed by FMOD when it finishes
            }
            catch (Exception ex) { Log.ErrorLimited("sfx", "Sfx.PlayAt(" + path + ")", ex, 30f); }
        }

        private static void Init()
        {
            if (_init) return;
            _init = true;
            try
            {
                FMOD.Studio.Bank[] banks;
                if (RuntimeManager.StudioSystem.getBankList(out banks) == FMOD.RESULT.OK)
                {
                    foreach (var b in banks)
                    {
                        FMOD.Studio.EventDescription[] evs;
                        if (b.getEventList(out evs) != FMOD.RESULT.OK) continue;
                        foreach (var e in evs)
                        {
                            string p;
                            if (e.getPath(out p) == FMOD.RESULT.OK && !string.IsNullOrEmpty(p)) Events.Add(p);
                        }
                    }
                }
                Log.Info("Sfx: " + Events.Count + " FMOD events found");
                Directory.CreateDirectory(Path.Combine("UserData", "DescendersCyclingPOC"));
                File.WriteAllText(Path.Combine(Path.Combine("UserData", "DescendersCyclingPOC"), "fmod_events.txt"), string.Join("\n", Events.ToArray()));
            }
            catch (Exception ex) { Log.Error("Sfx: listing FMOD events failed (falling back to tones)", ex); }
        }

        private static string Resolve(Cue cue)
        {
            // Disengage reuses the engage sound unless a specific event is configured (the rider hears the engage cue clearly).
            int k = (int)(cue == Cue.Disengage && string.IsNullOrEmpty(Config.SfxOverride[(int)Cue.Disengage]) ? Cue.Engage : cue);
            if (Chosen[k] != null) return Chosen[k].Length == 0 ? null : Chosen[k];
            string over = Config.SfxOverride[k];
            string pick = null;
            if (!string.IsNullOrEmpty(over)) pick = over;
            else
            {
                foreach (string kw in Keywords[k])
                {
                    string best = null;
                    foreach (string e in Events)
                    {
                        if (e.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (best == null || (e.StartsWith("event:/ui", StringComparison.OrdinalIgnoreCase) && !best.StartsWith("event:/ui", StringComparison.OrdinalIgnoreCase))) best = e;
                    }
                    if (best != null) { pick = best; break; }
                }
            }
            Chosen[k] = pick ?? "";
            Log.Info("Sfx: cue " + cue + " -> " + (pick ?? "(no game event matched; using a tone)"));
            return pick;
        }

        // ---------------------------------------------------------------- fallback tones
        private static void Beep(Cue cue)
        {
            int k = (int)cue;
            if (_src == null)
            {
                var go = new GameObject("CyclingPOC_Audio");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _src = go.AddComponent<AudioSource>();
                _src.playOnAwake = false; _src.spatialBlend = 0f;
            }
            if (Clips[k] == null) Clips[k] = Build(cue);
            _src.PlayOneShot(Clips[k], 0.7f);
        }

        private static AudioClip Build(Cue cue)
        {
            const int rate = 44100;
            float[][] tones;   // {freqStart, freqEnd, seconds}
            switch (cue)
            {
                case Cue.Engage: tones = new[] { new[] { 600f, 900f, 0.14f } }; break;
                case Cue.Disengage: tones = new[] { new[] { 900f, 600f, 0.14f } }; break;
                case Cue.Warn: tones = new[] { new[] { 1100f, 1100f, 0.09f }, new[] { 0f, 0f, 0.06f }, new[] { 1100f, 1100f, 0.09f }, new[] { 0f, 0f, 0.06f }, new[] { 1100f, 1100f, 0.09f } }; break;
                default: tones = new[] { new[] { 520f, 420f, 0.45f } }; break;
            }
            var samples = new List<float>();
            foreach (var t in tones)
            {
                int n = (int)(t[2] * rate);
                for (int i = 0; i < n; i++)
                {
                    float f = i / (float)n;
                    float env = Mathf.Min(1f, f * 12f) * Mathf.Min(1f, (1f - f) * 12f);
                    float freq = Mathf.Lerp(t[0], t[1], f);
                    samples.Add(t[0] <= 0f ? 0f : Mathf.Sin(2f * Mathf.PI * freq * i / rate) * env * 0.5f);
                }
            }
            var clip = AudioClip.Create("cyclingpoc_" + cue, samples.Count, 1, rate, false);
            clip.SetData(samples.ToArray(), 0);
            return clip;
        }
    }
}
