using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.PostProcessing;

namespace FreeTurboDescenders
{
    /// <summary>
    /// The day-cycle probe (docs/GAME_ANALYSIS.md "Time of day"): night and day during one ride. The game's sky is the TOD_Sky
    /// asset, which draws whatever TOD_Sky.Cycle.Hour says every frame (sun, moon, stars, light colour, ambient, fog); the game itself never
    /// changes that hour, each level's Night / Dawn / Storm sky is a prefab with its hour built in. So this runs the hour round the clock
    /// (DayCycleRules.Advance: slower through dawn and dusk, quicker the rest of the day) and blends in the game's own night look as the sun goes down (DayCycleRules.NightAmount, 0 .. 1):
    /// - the level's VisualModifier is Night while there is any night at all, so the game switches on its night-only objects (EnvironmentToggle,
    ///   the rider's head torch among them) and its lamps (NightElement), and gives the camera a fresh copy of the night profile
    ///   (CameraEffects.UpdateCameraEffects);
    /// - every frame the night amount changes, that copy gets the day and night profiles' settings blended (FieldBlend), the fog and the sun
    ///   rays the day and night VisualSetups' numbers blended, and every night light its own brightness times the night amount;
    /// - the sky's moon, stars and night colours are the game's Night sky's for the whole cycle (the day sky's moon is ten times dimmer); the
    ///   sky itself fades from its day to its night settings with the sun.
    /// Put back as it was when stopped. It goes with party mode (FollowParty, F10), from the level's own hour; the developer's mod commands
    /// `daycycle [seconds]`, `daycycle_night 0|1`, `daycycle_shots`, `daycycle_trace`.
    /// </summary>
    internal sealed class DayCycle
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const int Night = (int)VisualModifier.Night;

        private TOD_Sky _sky;
        private CameraEffects _fx;
        private bool _on;
        private float _secs;
        private double _hour;   // double: a 12-minute day adds about 0.0006 h a frame, too fine for a float hour
        private float _sunrise = float.NaN, _sunset = float.NaN;   // the sky's own, for slowing the clock round them
        private float _ownHour = float.NaN;   // the level's own hour, put back at the end
        private int _ownModifier = -1;        // the level's own VisualModifier, put back at the end
        private int _dayModifier = -1;        // the one for daytime: the level's own, None on a Night level
        private bool _nightOn;                // the level's VisualModifier is Night because of us
        private bool _switchNight = Config.DayCycleSwitchesNight;
        private bool _hadProgress;            // TOD_Time.ProgressTime as it was
        private bool _described;

        // the level's VisualModifier, found from the fields CameraEffects.UpdateCameraEffects reads (obfuscated names change with game updates)
        private bool _looked;
        private MethodInfo _smSp;
        private FieldInfo _fSession, _fLevel, _fModifier;

        // the night blend
        private sealed class ModelBlend
        {
            public FieldInfo Field;              // PostProcessingProfile.bloom, .colorGrading ...
            public MethodInfo Set;               // set_settings
            public object DaySettings, NightSettings;
            public bool DayOn, NightOn, SettingsDiffer;
        }
        private bool _blendPrepared;
        private List<ModelBlend> _models;     // null: no blend (the setups were not found)
        private VisualSetup _daySetup, _nightSetup;
        private TOD_Sky _nightSky;            // the game's Night sky prefab: its moon, stars and night colours
        private TOD_Sky _daySky;              // on a Night level only: the daytime setup's sky prefab, for the sun and the day colours (the Night sky's day light is 0)
        private TOD_DayParameters _ownDay;
        private TOD_SunParameters _ownSun;
        private TOD_NightParameters _ownNight;
        private TOD_MoonParameters _ownMoon;
        private TOD_StarParameters _ownStars;
        private bool _skySwapped;
        private PostProcessingBehaviour _ppb;
        private FieldInfo _ppbProfile, _toggleVisuals;
        private TOD_Scattering _scattering;
        private TOD_Rays _rays;
        private readonly Dictionary<Light, float> _lights = new Dictionary<Light, float>();   // night lights and their own brightness
        private float _applied = -1f, _nextLightScan;

        // daycycle_shots
        private float[] _shotHours;
        private int _shotIdx = -1;
        private float _shotAt;
        private string _shotDir = "";
        private bool _resumeAfterShots;

        public bool Active { get { return _on || _shotIdx >= 0; } }

        // ---- party mode (F10) brings the day cycle with it: one switch for "the game is different" (the rider 2026-10-16) ------

        private bool _party, _partyOwed;
        private float _partyRetryAt;
        private bool _returning;      // party mode went off: on the way back to the level's own hour (DayCycleRules.ReturnPlan)
        private int _returnDir;
        private float _returnSecs;
        private double _returnLeft;

        /// <summary>Every frame: party mode <paramref name="wanted"/> on or off (DayCycleRules.PartyInEffect: party mode on a course with a path); <paramref name="ready"/> = the course and
        /// the rider are there. The cycle starts at the level's own hour, so a daytime ride stays daytime for now.</summary>
        public void FollowParty(bool wanted, bool ready)
        {
            bool waiting = Time.unscaledTime < _partyRetryAt;
            switch (DayCycleRules.Party(wanted, ref _party, ref _partyOwed, ready && !waiting, _on && !_returning))
            {
                case DayCycleRules.PartyAct.Start:
                    string d = Start(Config.DayCycleNightToDayS);
                    if (!d.StartsWith("on:", StringComparison.Ordinal)) { _partyOwed = true; _partyRetryAt = Time.unscaledTime + 2f; Log.WarnLimited("daycycle-party", "DayCycle: party mode, not started yet: " + d); }
                    break;
                case DayCycleRules.PartyAct.Stop:
                    BeginReturn();
                    break;
            }
        }

        /// <summary>The mod command `daycycle [seconds from midnight to noon]`; the answer says what happened.</summary>
        public string Command(double? value)
        {
            float secs;
            if (DayCycleRules.Command(_on, value, Config.DayCycleNightToDayS, out secs) == DayCycleRules.Act.Stop)
            {
                if (!_on) return "not running";
                Stop("command", true);
                return "stopped, sky back to " + DayCycleRules.Clock(_ownHour);
            }
            return Start(secs);
        }

        /// <summary>The mod command `daycycle_night 0|1`: whether the game's night look comes in while the sun is down.</summary>
        public string SwitchNight(bool on)
        {
            _switchNight = on;
            if (!on) { if (_nightOn) SetNight(false); SwapSky(false); }
            else if (Active) SwapSky(true);
            return "the game's night look " + (on ? "comes in as the sun goes down" : "stays out, only the sky changes");
        }

        /// <summary>Party mode off: the clock runs back to the level's own hour over Config.DayCycleReturnS, the shorter way round, still slowed
        /// through any dawn or dusk on the way, and the night look follows the sun; then the level's own look is put back (nothing left to jump).</summary>
        private void BeginReturn()
        {
            if (!_on || _sky == null || float.IsNaN(_ownHour)) { Stop("party mode off", true); return; }
            _returnSecs = DayCycleRules.ReturnPlan(_hour, _ownHour, Config.DayCycleReturnS, _sunrise, _sunset, out _returnDir, out _returnLeft);
            if (_returnLeft <= 0) { Stop("party mode off", true); return; }
            _returning = true;
            Log.Info("DayCycle: party mode off: from " + DayCycleRules.Clock((float)_hour) + " back to the course's own " + DayCycleRules.Clock(_ownHour) + ", "
                + (_returnDir > 0 ? "forward " : "back ") + _returnLeft.ToString("F1") + " h in " + Config.DayCycleReturnS.ToString("F0") + " s");
        }
        private string Start(float secs)
        {
            if (!FindSky()) return "no sky (TOD_Sky) in this scene";
            if (_shotIdx >= 0) return "taking pictures, try again when they are done";
            Describe();
            if (!_on) { TakeOver(); _hour = DayCycleRules.Advance((double)_ownHour, 0, secs, _sunrise, _sunset); }   // on from the level's own hour: no sudden dark in a daytime ride
            _on = true; _secs = secs; _returning = false;   // party mode on again on the way back: on from where the sky is
            string d = "on: a day of " + (2f * secs / 60f).ToString("F1") + " min from " + DayCycleRules.Clock((float)_hour) + ", dawn and dusk slowed so the night look takes "
                + DayCycleRules.FadeSeconds(secs, _sunrise, _sunset).ToString("F1") + " s to come in or go (sunrise " + DayCycleRules.Clock(_sunrise) + ", sunset " + DayCycleRules.Clock(_sunset) + "; the level's own hour was " + DayCycleRules.Clock(_ownHour)
                + (_ownModifier >= 0 ? ", setting " + Enum.GetName(typeof(VisualModifier), _ownModifier) : ", its night setting not found") + ")";
            Log.Info("DayCycle: " + d);
            return d;
        }

        /// <summary>Remember the level's own hour and setting, stop the sky's own clock (it would move the hour on as well), get the blend ready.</summary>
        private void TakeOver()
        {
            _ownHour = _sky.Cycle.Hour;
            _sunrise = _sky.SunriseTime; _sunset = _sky.SunsetTime;   // the same all day for the sky's date and place
            _ownModifier = ReadModifier();
            _dayModifier = _ownModifier == Night ? (int)VisualModifier.None : _ownModifier;
            TOD_Time time = _sky.Components != null ? _sky.Components.Time : null;
            _hadProgress = time != null && time.ProgressTime;
            if (time != null) time.ProgressTime = false;
            PrepareBlend();
            if (_switchNight) SwapSky(true);
            _applied = -1f;
        }

        /// <summary>Back to the level's own hour, sky and setting (<paramref name="restore"/>), or just let go of the game's settings (a new course has set itself up).</summary>
        private void Stop(string why, bool restore)
        {
            if (_sky != null)
            {
                if (!float.IsNaN(_ownHour)) _sky.Cycle.Hour = _ownHour;
                TOD_Time time = _sky.Components != null ? _sky.Components.Time : null;
                if (time != null) time.ProgressTime = _hadProgress;
                SwapSky(false);
            }
            RestoreLights();
            if (restore && _ownModifier >= 0 && (_nightOn || _dayModifier != _ownModifier)) SetModifier(_ownModifier);
            _on = false; _nightOn = false; _shotIdx = -1; _applied = -1f; _returning = false;
            CloseTrace();
            Log.Info("DayCycle: stopped (" + why + ")");
        }

        /// <summary>A new course (scene change or the next level of a session): stop without touching the setting the new level chose.</summary>
        public void NewCourse(string why)
        {
            if (Active) Stop(why, false);
            _sky = null; _fx = null; _ppb = null; _scattering = null; _rays = null; _described = false; _ownHour = float.NaN; _ownModifier = -1; _dayModifier = -1;
            _blendPrepared = false; _models = null; _daySetup = null; _nightSetup = null; _nightSky = null; _daySky = null; _lights.Clear();
            _partyOwed = _party;   // party mode stays on across courses: the new one gets the cycle too, from its own hour
        }

        /// <summary>Every frame (OnUpdate, before the sky's LateUpdate draws the hour).</summary>
        public void Tick()
        {
            if (!Active) return;
            if (_sky == null) { Stop("the sky is gone", false); return; }
            if (_shotIdx >= 0) { TickShots(); return; }
            if (_returning)
            {
                _hour = DayCycleRules.Return(_hour, Time.deltaTime, _returnDir, _returnSecs, _sunrise, _sunset, ref _returnLeft);
                if (_returnLeft <= 0) _hour = _ownHour;
            }
            else _hour = DayCycleRules.Advance(_hour, Time.deltaTime, _secs, _sunrise, _sunset);   // game time: a paused game holds the clock
            _sky.Cycle.Hour = (float)_hour;
            FollowSun();
            if (_returning && _returnLeft <= 0) { Stop("party mode off, back at the course's own " + DayCycleRules.Clock(_ownHour), true); return; }
            TraceFrame();
        }

        // ---- every frame of light round dawn and dusk, for finding what jumps (the mod command `daycycle_trace`) ----------------------------

        private System.IO.StreamWriter _trace;
        private string _tracePath = "";

        /// <summary>The mod command `daycycle_trace`: start (or stop) writing one line per frame while the sun is between +12 and -15 deg.</summary>
        public string Trace(string dir)
        {
            if (_trace != null) { CloseTrace(); return "trace stopped: " + _tracePath; }
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                _tracePath = System.IO.Path.Combine(dir, "day_trace.csv");
                _trace = new System.IO.StreamWriter(_tracePath, false);
                _trace.AutoFlush = true;
                _trace.WriteLine("clock,frame,hour,sunAlt,moonAlt,night,isDay,isNight,lerp,sunVis,moonVis,skyLight,lightOn,lightI,lightR,lightG,lightB,lightDirY,shadow,ambMode,ambI,ambR,ambG,ambB,shR,shG,shB,fogR,fogG,fogB,fogD,reflI,todAmbR,todAmbG,todAmbB");
                return "tracing every frame with the sun between +12 and -15 deg into " + _tracePath;
            }
            catch (Exception ex) { _trace = null; return "no trace: " + ex.Message; }
        }

        private void CloseTrace()
        {
            if (_trace == null) return;
            try { _trace.Close(); } catch { }
            _trace = null;
            Log.Info("DayCycle: trace written to " + _tracePath);
        }

        private void TraceFrame()
        {
            if (_trace == null || _sky == null) return;
            float alt = _sky.SunAltitude;
            if (alt > 12f || alt < -15f) return;
            try
            {
                Light l = _sky.Components != null ? _sky.Components.LightSource : null;
                UnityEngine.Rendering.SphericalHarmonicsL2 sh = RenderSettings.ambientProbe;
                Color amb = RenderSettings.ambientLight, fog = RenderSettings.fogColor, ta = _sky.AmbientColor;
                var c = System.Globalization.CultureInfo.InvariantCulture;
                string[] v = {
                    DateTime.Now.ToString("HH:mm:ss.fff"), Time.frameCount.ToString(), _hour.ToString("F4", c), alt.ToString("F3", c), _sky.MoonAltitude.ToString("F3", c),
                    (_nightOn ? _applied : 0f).ToString("F4", c), _sky.IsDay ? "1" : "0", _sky.IsNight ? "1" : "0", _sky.LerpValue.ToString("F4", c),
                    _sky.SunVisibility.ToString("F4", c), _sky.MoonVisibility.ToString("F4", c), _sky.LightIntensity.ToString("F4", c),
                    l != null && l.enabled ? "1" : "0", l != null ? l.intensity.ToString("F4", c) : "", l != null ? l.color.r.ToString("F4", c) : "", l != null ? l.color.g.ToString("F4", c) : "",
                    l != null ? l.color.b.ToString("F4", c) : "", l != null ? l.transform.forward.y.ToString("F4", c) : "", l != null ? l.shadowStrength.ToString("F3", c) : "",
                    RenderSettings.ambientMode.ToString(), RenderSettings.ambientIntensity.ToString("F4", c), amb.r.ToString("F4", c), amb.g.ToString("F4", c), amb.b.ToString("F4", c),
                    sh[0, 0].ToString("F4", c), sh[1, 0].ToString("F4", c), sh[2, 0].ToString("F4", c),
                    fog.r.ToString("F4", c), fog.g.ToString("F4", c), fog.b.ToString("F4", c), RenderSettings.fogDensity.ToString("F5", c), RenderSettings.reflectionIntensity.ToString("F3", c),
                    ta.r.ToString("F4", c), ta.g.ToString("F4", c), ta.b.ToString("F4", c) };
                _trace.WriteLine(string.Join(",", v));
            }
            catch (Exception ex) { Log.ErrorLimited("daycycle-trace", "DayCycle: trace", ex); CloseTrace(); }
        }

        /// <summary>The night amount for where the sun is now, and the game's look to match.</summary>
        private void FollowSun()
        {
            if (!_switchNight || _dayModifier < 0) return;
            float n = DayCycleRules.NightAmount(_sky.SunAltitude);
            bool want = n > 0f;
            if (want != _nightOn) SetNight(want);
            if (!_nightOn) return;
            if (Time.unscaledTime >= _nextLightScan) { CollectLights(); _nextLightScan = Time.unscaledTime + 2f; _applied = -1f; }   // a respawned rider has a new head torch
            if (Math.Abs(n - _applied) >= 0.002f) ApplyBlend(n);
        }

        /// <summary>The level's VisualModifier Night (any night at all) or back to the daytime one, then the camera effects and the lamps follow.</summary>
        private void SetNight(bool on)
        {
            if (!SetModifier(on ? Night : _dayModifier)) return;
            _nightOn = on;
            _applied = -1f;
            if (on) { CollectLights(); _nextLightScan = Time.unscaledTime + 2f; }
            else RestoreLights();
            Log.Info("DayCycle: " + DayCycleRules.Clock(_sky.Cycle.Hour) + " sun " + _sky.SunAltitude.ToString("F1") + " deg: night look " + (on ? "coming in" : "gone")
                + " (setting " + Enum.GetName(typeof(VisualModifier), on ? Night : _dayModifier) + (_fx == null ? ", no CameraEffects found" : "") + ", " + _lights.Count + " night light(s))");
        }

        private bool SetModifier(int m)
        {
            object level = Level();
            if (level == null || m < 0) return false;
            try
            {
                _fModifier.SetValue(level, Enum.ToObject(typeof(VisualModifier), m));
                if (_fx == null) _fx = UnityEngine.Object.FindObjectOfType<CameraEffects>();
                if (_fx != null) _fx.UpdateCameraEffects();   // a fresh copy of that setup's profile, its fog and rays, then every NightElement.UpdateState
                return true;
            }
            catch (Exception ex) { Log.ErrorLimited("daycycle-night", "DayCycle: night setting", ex); _dayModifier = -1; return false; }
        }

        // ---- the night blend -------------------------------------------------------------------------------------------------------------

        /// <summary>The daytime and Night VisualSetups of this world, the post-processing models whose settings differ between them, the Night sky.</summary>
        private void PrepareBlend()
        {
            if (_blendPrepared) return;
            _blendPrepared = true;
            try
            {
                VisualSetup[] setups = Setups();
                if (setups == null || _dayModifier < 0 || _dayModifier >= setups.Length || Night >= setups.Length || setups[_dayModifier] == null || setups[Night] == null)
                { Log.Warn("DayCycle: no daytime / Night setups for this world: the night look will switch, not blend"); return; }
                _daySetup = setups[_dayModifier]; _nightSetup = setups[Night];
                _nightSky = _nightSetup.skydomePrefab != null ? _nightSetup.skydomePrefab.GetComponentInChildren<TOD_Sky>(true) : null;
                if (_ownModifier == Night && _daySetup.skydomePrefab != null) _daySky = _daySetup.skydomePrefab.GetComponentInChildren<TOD_Sky>(true);
                _models = new List<ModelBlend>();
                var names = new List<string>();
                if (_daySetup.profile != null && _nightSetup.profile != null)
                    foreach (FieldInfo pf in typeof(PostProcessingProfile).GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!typeof(PostProcessingModel).IsAssignableFrom(pf.FieldType)) continue;
                        var day = pf.GetValue(_daySetup.profile) as PostProcessingModel;
                        var night = pf.GetValue(_nightSetup.profile) as PostProcessingModel;
                        MethodInfo get = pf.FieldType.GetMethod("get_settings", Inst), set = pf.FieldType.GetMethod("set_settings", Inst);
                        if (day == null || night == null || object.ReferenceEquals(get, null) || object.ReferenceEquals(set, null)) continue;
                        if (!day.enabled && !night.enabled) continue;
                        var b = new ModelBlend { Field = pf, Set = set, DaySettings = get.Invoke(day, null), NightSettings = get.Invoke(night, null), DayOn = day.enabled, NightOn = night.enabled };
                        b.SettingsDiffer = !FieldBlend.Same(b.DaySettings, b.NightSettings);
                        if (!b.SettingsDiffer && b.DayOn == b.NightOn) continue;
                        _models.Add(b);
                        names.Add(pf.Name + (b.DayOn == b.NightOn ? "" : " (day " + (b.DayOn ? "on" : "off") + ", night " + (b.NightOn ? "on" : "off") + ")"));
                        var hard = new List<string>();
                        int floats = FieldBlend.Differences(b.DaySettings, b.NightSettings, "", hard);
                        Log.Info("DayCycle: " + pf.Name + ": " + floats + " number(s) blend" + (hard.Count == 0 ? "" : "; switched half way: " + string.Join("; ", hard.ToArray())));
                    }
                Log.Info("DayCycle: night blend from '" + _daySetup.name + "' to '" + _nightSetup.name + "': post-processing " + (names.Count == 0 ? "none differ" : string.Join(", ", names.ToArray()))
                    + "; fog " + _daySetup.GlobalDensity.ToString("F4") + " -> " + _nightSetup.GlobalDensity.ToString("F4") + ", rays " + _daySetup.Intensity.ToString("F2") + " -> " + _nightSetup.Intensity.ToString("F2")
                    + (_nightSky == null ? "; no Night sky prefab" : "; moon / stars / night colours from '" + _nightSetup.skydomePrefab.name + "'"));
            }
            catch (Exception ex) { Log.Error("DayCycle: preparing the night blend", ex); _models = null; }
        }

        /// <summary>The sky's moon, stars and night colours: the game's Night sky's (<paramref name="night"/>) or its own again; on a Night level
        /// (its sky is the Night one, unlit by day) the sun and day colours of the daytime sky instead.</summary>
        private void SwapSky(bool night)
        {
            if (_sky == null) return;
            if (night && !_skySwapped && _ownModifier != Night && _nightSky != null)
            {
                _ownNight = _sky.Night; _ownMoon = _sky.Moon; _ownStars = _sky.Stars;
                _sky.Night = _nightSky.Night; _sky.Moon = _nightSky.Moon; _sky.Stars = _nightSky.Stars;   // the prefab's own objects, never changed here
                _skySwapped = true;
            }
            else if (night && !_skySwapped && _ownModifier == Night && _daySky != null)   // a Night level: its sky is a copy of the Night prefab already
            {
                _ownDay = _sky.Day; _ownSun = _sky.Sun;
                _sky.Day = _daySky.Day; _sky.Sun = _daySky.Sun;
                _skySwapped = true;
            }
            else if (!night && _skySwapped)
            {
                if (_daySky != null) { _sky.Day = _ownDay; _sky.Sun = _ownSun; }
                else { _sky.Night = _ownNight; _sky.Moon = _ownMoon; _sky.Stars = _ownStars; }
                _skySwapped = false;
            }
        }

        /// <summary>Night amount <paramref name="n"/> (0 .. 1) on the camera's profile copy, the fog, the sun rays and every night light.</summary>
        private void ApplyBlend(float n)
        {
            _applied = n;
            try
            {
                if (_models != null)
                {
                    PostProcessingProfile live = LiveProfile();
                    if (live != null)
                        foreach (ModelBlend b in _models)
                        {
                            var model = b.Field.GetValue(live) as PostProcessingModel;
                            if (model == null) continue;
                            if (b.SettingsDiffer) b.Set.Invoke(model, new object[] { FieldBlend.Lerp(b.DaySettings, b.NightSettings, n, MixCurve) });   // colour grading re-bakes its table on a change
                            if (b.DayOn != b.NightOn) model.enabled = n < 0.5f ? b.DayOn : b.NightOn;
                        }
                    if (_fx != null && _scattering == null) _scattering = _fx.GetComponent<TOD_Scattering>();
                    if (_fx != null && _rays == null) _rays = _fx.GetComponent<TOD_Rays>();
                    if (_scattering != null)
                    {
                        _scattering.PublicGlobalDensity = Mathf.Lerp(_daySetup.GlobalDensity, _nightSetup.GlobalDensity, n);
                        _scattering.HeightFalloff = Mathf.Lerp(_daySetup.HeightFalloff, _nightSetup.HeightFalloff, n);
                    }
                    if (_rays != null)
                    {
                        _rays.Intensity = Mathf.Lerp(_daySetup.Intensity, _nightSetup.Intensity, n);
                        _rays.BlurRadius = Mathf.Lerp(_daySetup.BlurRadius, _nightSetup.BlurRadius, n);
                        _rays.MaxRadius = Mathf.Lerp(_daySetup.MaxRadius, _nightSetup.MaxRadius, n);
                    }
                }
                foreach (KeyValuePair<Light, float> kv in _lights)
                    if (kv.Key != null) kv.Key.intensity = kv.Value * n;
            }
            catch (Exception ex) { Log.ErrorLimited("daycycle-blend", "DayCycle: night blend", ex); }
        }

        // ---- the colour-grading curves (master, red, green, blue, hue / saturation): the day and night profiles each have their own curve
        // objects, and the master curve sets how bright every pixel ends up, so taking one or the other half way was a visible step (third run).
        // A new curve is made through 33 points, each the day and night curves' values mixed; the game re-bakes its colour table from it.

        private const int CurvePoints = 33;
        private static FieldInfo _cgLoop, _cgZero, _cgRange;
        private static bool _cgLooked;

        private static object MixCurve(object a, object b, float t)
        {
            var ca = a as ColorGradingCurve;
            var cb = b as ColorGradingCurve;
            if (ca == null || cb == null) return null;
            if (!_cgLooked)
            {
                _cgLooked = true;
                _cgLoop = typeof(ColorGradingCurve).GetField("m_Loop", Inst);
                _cgZero = typeof(ColorGradingCurve).GetField("m_ZeroValue", Inst);
                _cgRange = typeof(ColorGradingCurve).GetField("m_Range", Inst);
                if (object.ReferenceEquals(_cgLoop, null) || object.ReferenceEquals(_cgZero, null) || object.ReferenceEquals(_cgRange, null))
                    Log.Warn("DayCycle: ColorGradingCurve's m_Loop / m_ZeroValue / m_Range not found: its curves switch half way");
            }
            if (object.ReferenceEquals(_cgLoop, null) || object.ReferenceEquals(_cgZero, null) || object.ReferenceEquals(_cgRange, null)) return null;
            float zeroA = (float)_cgZero.GetValue(ca), zeroB = (float)_cgZero.GetValue(cb);
            bool emptyA = ca.curve == null || ca.curve.length == 0, emptyB = cb.curve == null || cb.curve.length == 0;
            if (emptyA && emptyB && zeroA == zeroB) return t < 0.5f ? a : b;   // both "no change"
            var x = new float[CurvePoints];
            var y = new float[CurvePoints];
            for (int i = 0; i < CurvePoints; i++)
            {
                x[i] = i / (float)(CurvePoints - 1);
                float ya = emptyA ? zeroA : ca.curve.Evaluate(x[i]), yb = emptyB ? zeroB : cb.curve.Evaluate(x[i]);
                y[i] = ya + (yb - ya) * t;
            }
            var keys = new Keyframe[CurvePoints];
            for (int i = 0; i < CurvePoints; i++)
            {
                int lo = Math.Max(0, i - 1), hi = Math.Min(CurvePoints - 1, i + 1);
                float slope = (y[hi] - y[lo]) / (x[hi] - x[lo]);
                keys[i] = new Keyframe(x[i], y[i], slope, slope);
            }
            float zero = zeroA + (zeroB - zeroA) * t;
            bool loop = (bool)_cgLoop.GetValue(t < 0.5f ? ca : cb);
            float range = (float)_cgRange.GetValue(t < 0.5f ? ca : cb);
            return new ColorGradingCurve(new AnimationCurve(keys), zero, loop, new Vector2(range, 0f));   // the constructor keeps the bounds' length as its range
        }

        /// <summary>The copy of the profile the camera draws with (CameraEffects.SetProfile gives PostProcessingBehaviour a fresh one on every change).</summary>
        private PostProcessingProfile LiveProfile()
        {
            if (_fx == null) _fx = UnityEngine.Object.FindObjectOfType<CameraEffects>();
            if (_fx == null) return null;
            if (_ppb == null) _ppb = _fx.GetComponent<PostProcessingBehaviour>();
            if (_ppb == null) return null;
            if (object.ReferenceEquals(_ppbProfile, null))
                foreach (FieldInfo f in typeof(PostProcessingBehaviour).GetFields(Inst))
                    if (object.ReferenceEquals(f.FieldType, typeof(PostProcessingProfile))) { _ppbProfile = f; break; }
            return object.ReferenceEquals(_ppbProfile, null) ? null : _ppbProfile.GetValue(_ppb) as PostProcessingProfile;
        }

        /// <summary>The lights that only shine at night: under every EnvironmentToggle whose visual list has Night (the head torch), and every lamp (NightElement).
        /// Each keeps the brightness it had when first seen.</summary>
        private void CollectLights()
        {
            try
            {
                if (object.ReferenceEquals(_toggleVisuals, null))
                    foreach (FieldInfo f in typeof(EnvironmentToggle).GetFields(Inst))
                        if (object.ReferenceEquals(f.FieldType, typeof(List<VisualModifier>))) { _toggleVisuals = f; break; }
                if (!object.ReferenceEquals(_toggleVisuals, null))
                    foreach (EnvironmentToggle t in UnityEngine.Object.FindObjectsOfType<EnvironmentToggle>())
                    {
                        var list = _toggleVisuals.GetValue(t) as List<VisualModifier>;
                        if (list == null || !list.Contains(VisualModifier.Night)) continue;
                        foreach (Light l in t.GetComponentsInChildren<Light>(true)) Keep(l);
                    }
                foreach (NightElement e in UnityEngine.Object.FindObjectsOfType<NightElement>()) Keep(e.GetComponent<Light>());
            }
            catch (Exception ex) { Log.ErrorLimited("daycycle-lights", "DayCycle: night lights", ex); }
        }

        private void Keep(Light l)
        {
            if (l == null || _lights.ContainsKey(l)) return;
            _lights[l] = l.intensity;
            if (_nightOn) l.intensity = 0f;   // ApplyBlend brings it up
        }

        private void RestoreLights()
        {
            foreach (KeyValuePair<Light, float> kv in _lights)
                if (kv.Key != null) kv.Key.intensity = kv.Value;
        }

        /// <summary>This world's VisualSetups by VisualModifier (CameraEffects' PostProcWorldInfo[], by world - 1), or null.</summary>
        private VisualSetup[] Setups()
        {
            if (_fx == null) _fx = UnityEngine.Object.FindObjectOfType<CameraEffects>();
            if (_fx == null) return null;
            int world = World();
            foreach (FieldInfo f in typeof(CameraEffects).GetFields(Any))
            {
                if (!object.ReferenceEquals(f.FieldType, typeof(PostProcWorldInfo[]))) continue;
                PostProcWorldInfo[] worlds = (PostProcWorldInfo[])f.GetValue(_fx);
                if (worlds == null || world - 1 >= worlds.Length || worlds[world - 1] == null) return null;
                return worlds[world - 1].setups;
            }
            return null;
        }

        private int World()
        {
            int world = 0;
            try
            {
                object sm = Lookup() ? _smSp.Invoke(null, null) : null;
                if (sm is SessionManager) world = Convert.ToInt32(((SessionManager)sm).GetWorld());
            }
            catch (Exception ex) { Log.ErrorLimited("daycycle-world", "DayCycle: world", ex); }
            return world <= 0 ? 1 : world;   // UpdateCameraEffects does the same
        }

        // ---- the level's VisualModifier --------------------------------------------------------------------------------------------------

        /// <summary>CameraEffects.UpdateCameraEffects reads SessionManager.(session).(level).(VisualModifier): the first field on SessionManager,
        /// the next one on that field's type, and the first VisualModifier field, in its IL order.</summary>
        private bool Lookup()
        {
            if (_looked) return !object.ReferenceEquals(_fModifier, null);
            _looked = true;
            try
            {
                _smSp = typeof(SessionManager).GetMethod("get_SP", Any);
                MethodInfo uce = typeof(CameraEffects).GetMethod("UpdateCameraEffects", BindingFlags.Public | BindingFlags.Instance);
                if (object.ReferenceEquals(_smSp, null) || object.ReferenceEquals(uce, null)) { Log.Warn("DayCycle: SessionManager.SP or CameraEffects.UpdateCameraEffects not found"); return false; }
                List<FieldInfo> reads = IlReader.FieldLoads(uce);
                foreach (FieldInfo f in reads)
                {
                    if (object.ReferenceEquals(f, null)) continue;
                    if (object.ReferenceEquals(_fSession, null)) { if (object.ReferenceEquals(f.DeclaringType, typeof(SessionManager)) && !f.IsStatic) _fSession = f; continue; }
                    if (object.ReferenceEquals(_fLevel, null)) { if (object.ReferenceEquals(f.DeclaringType, _fSession.FieldType)) _fLevel = f; continue; }
                    if (object.ReferenceEquals(f.FieldType, typeof(VisualModifier)) && object.ReferenceEquals(f.DeclaringType, _fLevel.FieldType)) { _fModifier = f; break; }
                }
                if (object.ReferenceEquals(_fModifier, null)) { Log.Warn("DayCycle: the level's night setting was not found in UpdateCameraEffects (" + reads.Count + " field reads)"); return false; }
                if (_fLevel.FieldType.IsValueType || _fSession.FieldType.IsValueType) { Log.Warn("DayCycle: the level's setting sits in a struct, not switched"); _fModifier = null; return false; }
                Log.Info("DayCycle: the level's night setting is SessionManager." + _fSession.Name + "." + _fLevel.Name + "." + _fModifier.Name);
                return true;
            }
            catch (Exception ex) { Log.Error("DayCycle: lookup", ex); _fModifier = null; return false; }
        }

        private object Level()
        {
            if (!Lookup()) return null;
            try
            {
                object sm = _smSp.Invoke(null, null);
                object session = sm == null ? null : _fSession.GetValue(sm);
                return session == null ? null : _fLevel.GetValue(session);
            }
            catch (Exception ex) { Log.ErrorLimited("daycycle-level", "DayCycle: level", ex); return null; }
        }

        private int ReadModifier()
        {
            object level = Level();
            if (level == null) return -1;
            try { return Convert.ToInt32(_fModifier.GetValue(level)); }
            catch (Exception ex) { Log.ErrorLimited("daycycle-read", "DayCycle: read setting", ex); return -1; }
        }

        private bool FindSky()
        {
            if (_sky == null) _sky = TOD_Sky.Instance;
            return _sky != null;
        }

        /// <summary>Once per course: what the sky and the game's own Night / Dawn / Storm skies say, for docs/GAME_ANALYSIS.md.</summary>
        private void Describe()
        {
            if (_described) return;
            _described = true;
            try
            {
                TOD_Time time = _sky.Components != null ? _sky.Components.Time : null;
                Log.Info("DayCycle: sky '" + _sky.gameObject.name + "' at " + DayCycleRules.Clock(_sky.Cycle.Hour) + " (" + _sky.Cycle.Hour.ToString("F2") + " h, day " + _sky.Cycle.Day + "/" + _sky.Cycle.Month + "/" + _sky.Cycle.Year
                    + "), latitude " + _sky.World.Latitude.ToString("F1") + " longitude " + _sky.World.Longitude.ToString("F1") + " UTC " + _sky.World.UTC.ToString("F1")
                    + ", sunrise " + DayCycleRules.Clock(_sky.SunriseTime) + " sunset " + DayCycleRules.Clock(_sky.SunsetTime) + ", sun " + _sky.SunAltitude.ToString("F1") + " deg"
                    + ", day light " + _sky.Day.LightIntensity.ToString("F2") + " night light " + _sky.Night.LightIntensity.ToString("F2")
                    + (time == null ? ", no TOD_Time" : ", TOD_Time progress " + time.ProgressTime + " day length " + time.DayLengthInMinutes.ToString("F0") + " min"));
                VisualSetup[] setups = Setups();
                if (setups == null) { Log.Info("DayCycle: no setups for world " + World() + (_fx == null ? " (no CameraEffects)" : "")); return; }
                for (int i = 0; i < setups.Length; i++)
                {
                    VisualSetup s = setups[i];
                    if (s == null) { Log.Info("DayCycle: world " + World() + " setup " + i + ": none"); continue; }
                    TOD_Sky prefabSky = s.skydomePrefab != null ? s.skydomePrefab.GetComponentInChildren<TOD_Sky>(true) : null;
                    Log.Info("DayCycle: world " + World() + " " + Enum.GetName(typeof(VisualModifier), i) + ": '" + s.name + "', profile '" + (s.profile != null ? s.profile.name : "-")
                        + "', sky '" + (s.skydomePrefab != null ? s.skydomePrefab.name : "-") + "'"
                        + (prefabSky == null ? "" : " at " + DayCycleRules.Clock(prefabSky.Cycle.Hour) + " latitude " + prefabSky.World.Latitude.ToString("F1") + " day light " + prefabSky.Day.LightIntensity.ToString("F2")
                            + " night light " + prefabSky.Night.LightIntensity.ToString("F2"))
                        + ", fog density " + s.GlobalDensity.ToString("F3") + " falloff " + s.HeightFalloff.ToString("F3") + ", rays " + s.Intensity.ToString("F2"));
                }
            }
            catch (Exception ex) { Log.Error("DayCycle: describe", ex); }
        }

        // ---- pictures at fixed hours (the mod command `daycycle_shots`) --------------------------------------------------------------------

        /// <summary>A picture of the game camera at each of DayCycleRules.ShotHours, Config.DayCycleShotSettleS apart, into <paramref name="dir"/>.</summary>
        public string StartShots(string dir)
        {
            if (!FindSky()) return "no sky (TOD_Sky) in this scene";
            if (_shotIdx >= 0) return "already taking pictures";
            Describe();
            _resumeAfterShots = _on;
            if (!_on) TakeOver();
            _shotHours = DayCycleRules.ShotHours(_sky.SunriseTime, _sky.SunsetTime);
            _shotIdx = 0; _shotDir = dir; _shotAt = Time.unscaledTime + Config.DayCycleShotSettleS;
            string d = _shotHours.Length + " pictures, " + Config.DayCycleShotSettleS.ToString("F1") + " s apart, into " + dir;
            Log.Info("DayCycle: " + d);
            return d;
        }

        private void TickShots()
        {
            float hour = _shotHours[_shotIdx];
            _sky.Cycle.Hour = hour;
            FollowSun();
            if (Time.unscaledTime < _shotAt) return;
            Picture(DayCycleRules.ShotFile(_shotIdx, hour));
            _shotIdx++;
            _shotAt = Time.unscaledTime + Config.DayCycleShotSettleS;
            if (_shotIdx < _shotHours.Length) return;
            _shotIdx = -1;
            Log.Info("DayCycle: pictures done");
            if (!_resumeAfterShots) Stop("pictures done", true);
        }

        /// <summary>The game's camera drawn into a picture of its own (with its colour grading and sky), without the mod's HUD.</summary>
        private void Picture(string file)
        {
            RenderTexture rt = null, was = RenderTexture.active, target = null;
            Texture2D tex = null;
            Camera cam = null;
            try
            {
                if (_fx == null) _fx = UnityEngine.Object.FindObjectOfType<CameraEffects>();
                cam = _fx != null ? _fx.GetComponent<Camera>() : null;
                if (cam == null) cam = Camera.main;
                if (cam == null) { Log.Warn("DayCycle: no camera for the picture"); return; }
                const int w = 1280, h = 720;
                rt = RenderTexture.GetTemporary(w, h, 24);
                target = cam.targetTexture;
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = target;
                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
                System.IO.Directory.CreateDirectory(_shotDir);
                string path = System.IO.Path.Combine(_shotDir, file);
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                Log.Info("DayCycle: picture at " + DayCycleRules.Clock(_sky.Cycle.Hour) + " (sun " + _sky.SunAltitude.ToString("F1") + " deg, night " + (_nightOn ? _applied.ToString("F2") : "0") + "): " + path);
            }
            catch (Exception ex) { Log.Warn("DayCycle: picture not taken (" + ex.GetType().Name + ": " + ex.Message + ")"); }
            finally
            {
                if (cam != null && cam.targetTexture == rt) cam.targetTexture = target;
                RenderTexture.active = was;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }
    }
}
