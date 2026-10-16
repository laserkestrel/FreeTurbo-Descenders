using System;
using System.Collections;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>
    /// A picture of the screen when a workout is completed, for the rider to add to the ride's upload: the game view with the
    /// confetti and the workout panel, saved as last-workout.png beside the user's settings file and overwritten after each workout
    /// (WorkoutShotRules.cs). Read at the end of the frame (WaitForEndOfFrame + ReadPixels), after the IMGUI confetti and panel are drawn.
    /// A failure is one log line, never an exception into the game.
    /// </summary>
    internal static class WorkoutShot
    {
        private static float _at = -1f;
        private static bool _busy;

        /// <summary>A workout was completed: take the picture a moment later, when the confetti is across the screen.</summary>
        public static void Schedule(float confettiS)
        {
            float delay = WorkoutShotRules.DelayS(confettiS, Config.WorkoutShotDelayS);
            _at = Time.unscaledTime + delay;
            Log.Info("Workout picture: in " + delay.ToString("F1") + " s");
        }

        /// <summary>Every frame (OnUpdate).</summary>
        public static void Tick()
        {
            if (_at < 0f || _busy || Time.unscaledTime < _at) return;
            _at = -1f;
            _busy = true;
            try { MelonCoroutines.Start(new Capture()); }
            catch (Exception ex) { _busy = false; Log.Warn("Workout picture: not taken (" + ex.GetType().Name + ": " + ex.Message + ")"); }
        }

        /// <summary>The coroutine "wait for the end of the frame, then save", written out by hand: a C# `yield` method needs
        /// IteratorStateMachineAttribute, which the game's old Mono does not have (tools\CheckCompat.ps1).</summary>
        private sealed class Capture : IEnumerator
        {
            private int _step;
            public object Current { get; private set; }

            public bool MoveNext()
            {
                if (_step++ == 0) { Current = new WaitForEndOfFrame(); return true; }
                Current = null;
                Save();
                _busy = false;
                return false;
            }

            public void Reset() { }
        }

        private static void Save()
        {
            string path = "";
            Texture2D tex = null;
            try
            {
                path = WorkoutShotRules.FilePath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                if (path.Length == 0) { Log.Warn("Workout picture: not saved, Windows gave no %APPDATA% folder"); return; }
                int w = Screen.width, h = Screen.height;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
                byte[] png = tex.EncodeToPNG();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, png);
                Log.Info("Workout picture: " + path + " (" + w + " x " + h + ", replaced after each workout)");
            }
            catch (Exception ex) { Log.Warn("Workout picture: not saved" + (path.Length > 0 ? " to " + path : "") + " (" + ex.GetType().Name + ": " + ex.Message + ")"); }
            finally { if (tex != null) UnityEngine.Object.Destroy(tex); }
        }
    }
}
