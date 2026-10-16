using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FreeTurboDescenders
{
    /// <summary>One frame of game state read by the probe.</summary>
    internal struct GameState
    {
        public string Scene;
        public bool PlayerFound, VehicleFound, RigidbodyFound;
        public Vector3 Position, Velocity, Forward;
        public float SpeedMs;          // rb.velocity.magnitude
        public float SpeedKmh;         // SpeedMs * 3.6  (Sandbox's formula)
        public float GameSpeedKmh;     // SpeedKmh * 9.81 / |Physics.gravity| (what the game's own speedo shows)
        public bool? Grounded;         // null = ground property not identified / read failed
    }

    /// <summary>
    /// Discovers Player_Human -> Vehicle -> Rigidbody using the same techniques as DescendersSandbox
    /// (GameObject.Find, GetComponent, reflection for the obfuscated ground flag) and logs everything it finds.
    /// </summary>
    internal sealed class PlayerProbe
    {
        private const float RefreshInterval = 0.5f;

        public GameObject Player { get; private set; }
        public Vehicle Vehicle { get; private set; }
        public Rigidbody Rb { get; private set; }
        public PropertyInfo GroundProp { get; private set; }

        private float _nextRefresh;
        private bool _groundSearched;
        private bool _groundReadFailed;
        private bool _hadPlayer;

        // Own delegate type: System.Action lives in System.Core on this game's old Mono, not mscorlib.
        public delegate void PlayerAcquiredHandler();
        public event PlayerAcquiredHandler PlayerAcquired;

        public void Reset()
        {
            Player = null; Vehicle = null; Rb = null; GroundProp = null;
            _groundSearched = false; _groundReadFailed = false; _nextRefresh = 0f;
            if (_hadPlayer) Log.Info("Probe: reset (scene change), player reference dropped");
            _hadPlayer = false;
        }

        /// <summary>Re-run discovery if anything is missing or was destroyed. Cheap when everything is alive.</summary>
        public void Refresh()
        {
            bool alive = Player != null && Vehicle != null && Rb != null;
            if (alive) return;

            float now = Time.unscaledTime;
            if (now < _nextRefresh) return;
            _nextRefresh = now + RefreshInterval;

            bool hadPlayer = _hadPlayer;
            if (Player == null)
            {
                Player = GameObject.Find("Player_Human");
                if (Player != null)
                    Log.Info("Probe: Player_Human FOUND -> '" + Player.name + "' (" + Player.GetType().FullName + "), scene '" + SceneName() + "', pos " + Player.transform.position);
                else if (hadPlayer)
                    Log.Warn("Probe: Player_Human lost (destroyed?)");
                else
                    Log.WarnLimited("noplayer", "Probe: Player_Human NOT FOUND in scene '" + SceneName() + "' (menu/loading?)", 30f);
            }

            if (Player == null) { Vehicle = null; Rb = null; _hadPlayer = false; return; }

            if (Vehicle == null)
            {
                Vehicle = Player.GetComponent<Vehicle>();
                if (Vehicle != null)
                    Log.Info("Probe: Vehicle FOUND -> " + Vehicle.GetType().FullName + " (base " + Vehicle.GetType().BaseType.FullName + ")");
                else
                    Log.WarnLimited("novehicle", "Probe: Vehicle component NOT FOUND on Player_Human", 30f);
            }

            if (Vehicle != null && Rb == null)
            {
                Rb = Vehicle.GetComponent<Rigidbody>();
                string how = "GetComponent";
                if (Rb == null) { Rb = Vehicle.GetComponentInChildren<Rigidbody>(); how = "GetComponentInChildren"; }
                if (Rb != null)
                    Log.Info("Probe: Rigidbody FOUND via " + how + " on '" + Rb.gameObject.name + "' mass=" + Rb.mass + " useGravity=" + Rb.useGravity + " isKinematic=" + Rb.isKinematic + " interpolation=" + Rb.interpolation);
                else
                    Log.WarnLimited("norb", "Probe: Rigidbody NOT FOUND on Vehicle", 30f);
            }

            if (Vehicle != null && !_groundSearched)
            {
                _groundSearched = true;
                GroundProp = FindGroundProperty(Vehicle.GetType());
            }

            _hadPlayer = Player != null;
            if (!hadPlayer && Player != null && Vehicle != null && Rb != null)
            {
                Log.Info("Probe: all three found. Physics.gravity=" + Physics.gravity + " |g|=" + Physics.gravity.magnitude.ToString("F2")
                         + " Time.fixedDeltaTime=" + Time.fixedDeltaTime);
                if (PlayerAcquired != null) PlayerAcquired();
            }
        }

        public GameState Read()
        {
            var s = new GameState();
            s.Scene = SceneName();
            s.PlayerFound = Player != null;
            s.VehicleFound = s.PlayerFound && Vehicle != null;
            s.RigidbodyFound = s.VehicleFound && Rb != null;
            if (s.PlayerFound)
            {
                s.Position = Player.transform.position;
                s.Forward = Player.transform.forward;
            }
            if (s.RigidbodyFound)
            {
                s.Velocity = Rb.velocity;
                s.SpeedMs = s.Velocity.magnitude;
                s.SpeedKmh = s.SpeedMs * 3.6f;
                float g = Physics.gravity.magnitude;
                s.GameSpeedKmh = g > 0.01f ? s.SpeedKmh * 9.81f / g : s.SpeedKmh;
            }
            s.Grounded = ReadGround();
            return s;
        }

        private bool? ReadGround()
        {
            if (Vehicle == null || (object)GroundProp == null || _groundReadFailed) return null;
            try { return (bool)GroundProp.GetValue(Vehicle, null); }
            catch (Exception ex)
            {
                _groundReadFailed = true;
                Log.Error("Probe: reading ground property '" + GroundProp.Name + "' failed; ground state disabled until scene reload", ex);
                return null;
            }
        }

        public static string SceneName()
        {
            try { return SceneManager.GetActiveScene().name; }
            catch (Exception ex) { Log.ErrorLimited("scenename", "SceneManager.GetActiveScene", ex); return "?"; }
        }

        /// <summary>
        /// Sandbox's SessionTrackers picks a public instance Boolean property whose name starts with 'T'.
        /// The names are obfuscated, so this is fragile. We log ALL boolean properties, prefer the 'T' one
        /// (matches Sandbox), and if there is exactly one boolean property we accept that regardless of name.
        /// </summary>
        private static PropertyInfo FindGroundProperty(Type t)
        {
            var bools = new List<PropertyInfo>();
            try
            {
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (object.ReferenceEquals(p.PropertyType, typeof(bool)) && p.CanRead && p.GetIndexParameters().Length == 0) bools.Add(p);
            }
            catch (Exception ex) { Log.Error("Probe: reflecting Vehicle properties failed", ex); return null; }

            var names = new List<string>();
            foreach (var p in bools) names.Add(p.Name);
            Log.Info("Probe: Vehicle public bool properties (" + bools.Count + "): [" + string.Join(", ", names.ToArray()) + "]");

            PropertyInfo pick = null;
            foreach (var p in bools) if (p.Name.StartsWith("T", StringComparison.Ordinal)) { pick = p; break; }
            if ((object)pick == null && bools.Count == 1) pick = bools[0];

            if ((object)pick != null)
                Log.Info("Probe: on-ground property = '" + pick.Name + "' (declared on " + pick.DeclaringType.FullName + ")");
            else
                Log.Warn("Probe: could not identify an on-ground boolean property; Grounded will show UNKNOWN");
            return pick;
        }
    }
}
