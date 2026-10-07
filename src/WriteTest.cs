using UnityEngine;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// Reversible WRITE proof: a temporary speed cap on the player's Rigidbody. It only ever scales
    /// rb.velocity down while active, and does nothing once it expires or is cancelled, so the game's
    /// own physics simply resume. Same class of operation as Sandbox's SpeedLimiter.
    /// </summary>
    internal sealed class WriteTest
    {
        public bool Active { get; private set; }
        public float CapKmh { get; private set; }
        public float RemainingSec { get { return Active ? Mathf.Max(0f, _endTime - Time.unscaledTime) : 0f; } }
        public int ClampCount { get; private set; }

        private const float MaxDecelMs2 = 5f;   // gentle, roughly a light brake application

        private float _endTime;
        private float _firstBeforeKmh;
        private bool _loggedFirstClamp;

        public void Toggle(float capKmh, float seconds)
        {
            if (Active) Stop("cancelled");
            else Start(capKmh, seconds, "key");
        }

        public void Start(float capKmh, float seconds, string source)
        {
            CapKmh = Mathf.Clamp(capKmh, 1f, 200f);
            _endTime = Time.unscaledTime + Mathf.Clamp(seconds, 0.5f, 60f);
            Active = true;
            ClampCount = 0;
            _loggedFirstClamp = false;
            Log.Info("WriteTest: speed cap " + CapKmh.ToString("F1") + " km/h for " + (_endTime - Time.unscaledTime).ToString("F1") + " s (" + source + ")");
        }

        public void Stop(string why)
        {
            if (!Active) return;
            Active = false;
            Log.Info("WriteTest: cap ended (" + why + "); clamped " + ClampCount + " physics steps. Nothing to restore, the game's own velocity handling resumes.");
        }

        /// <summary>Call from OnFixedUpdate.</summary>
        public void FixedTick(Rigidbody rb)
        {
            if (!Active) return;
            if (Time.unscaledTime >= _endTime) { Stop("expired"); return; }
            if (rb == null) return;

            float limitMs = CapKmh / 3.6f;
            Vector3 v = rb.velocity;
            float mag = v.magnitude;
            if (mag <= limitMs) return;

            // Ease down at a bounded deceleration instead of snapping to the cap: an instant 40 km/h loss
            // pitches the rider over the bars (the rider's body isn't slowed with the rigidbody).
            float target = Mathf.Max(limitMs, mag - MaxDecelMs2 * Time.fixedDeltaTime);
            Vector3 after = v * (target / mag);
            if (!_loggedFirstClamp)
            {
                _loggedFirstClamp = true;
                _firstBeforeKmh = mag * 3.6f;
                Log.Info("WriteTest: first clamp, velocity " + v + " (" + _firstBeforeKmh.ToString("F1") + " km/h) -> " + after + " (" + (after.magnitude * 3.6f).ToString("F1") + " km/h, easing toward " + CapKmh.ToString("F1") + ")");
            }
            rb.velocity = after;
            ClampCount++;
        }
    }
}
