using System;
using System.Collections.Generic;
using MelonLoader;

namespace DescendersCyclingPOC
{
    /// <summary>Thin wrapper over MelonLogger with rate limiting so per-frame failures don't flood the log.</summary>
    internal static class Log
    {
        private static readonly Dictionary<string, float> _lastLogged = new Dictionary<string, float>();

        public static void Info(string msg) { MelonLogger.Msg(msg); }
        public static void Warn(string msg) { MelonLogger.Warning(msg); }
        public static void Error(string msg) { MelonLogger.Error(msg); }

        public static void Error(string context, Exception ex)
        {
            MelonLogger.Error(context + ": " + ex);
        }

        private static bool Due(string key, float intervalSec)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            float last;
            if (_lastLogged.TryGetValue(key, out last) && now - last < intervalSec) return false;
            _lastLogged[key] = now;
            return true;
        }

        /// <summary>Log an error at most once per interval for the given key.</summary>
        public static void ErrorLimited(string key, string context, Exception ex, float intervalSec = 10f)
        {
            if (Due(key, intervalSec)) Error(context, ex);
        }

        public static void WarnLimited(string key, string msg, float intervalSec = 10f)
        {
            if (Due(key, intervalSec)) Warn(msg);
        }
    }
}
