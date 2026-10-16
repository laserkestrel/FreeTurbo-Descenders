using UnityEngine;

namespace FreeTurboDescenders
{
    /// <summary>Ride totals for the rider HUD. Climbed metres = sum of real height gains (game world metres), ignoring bumps below ClimbHysteresisM.</summary>
    internal sealed class RideStats
    {
        private const float ClimbHysteresisM = 0.5f;   // a rise must exceed this above the lowest point since the last credit
        private const float TeleportM = 20f;           // a jump this large in one frame is a respawn/reload, not riding

        public float ClimbedM;
        private float _ref = float.NaN;
        private Vector3 _lastPos;
        private bool _havePos;

        public void Reset() { ClimbedM = 0f; _ref = float.NaN; _havePos = false; }

        public void Update(bool haveVehicle, Vector3 pos)
        {
            if (!haveVehicle) { _havePos = false; _ref = float.NaN; return; }
            if (_havePos && (pos - _lastPos).magnitude > TeleportM) _ref = float.NaN;
            _lastPos = pos; _havePos = true;

            float y = pos.y;
            if (float.IsNaN(_ref)) { _ref = y; return; }
            if (y < _ref) _ref = y;
            else if (y - _ref >= ClimbHysteresisM) { ClimbedM += y - _ref; _ref = y; }
        }
    }
}
