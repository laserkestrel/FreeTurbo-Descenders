using System;
using System.Reflection;
using MelonLoader;
using UnityEngine;

[assembly: AssemblyTitle(FreeTurboDescenders.Names.Mod)]
[assembly: AssemblyVersion("0.1.0")]
[assembly: AssemblyFileVersion("0.1.0")]
[assembly: MelonInfo(typeof(FreeTurboDescenders.FreeTurboDescendersMod), FreeTurboDescenders.Names.Mod, "0.1.0", "Andy")]
[assembly: MelonColor(ConsoleColor.Cyan)]
[assembly: MelonGame("RageSquid", "Descenders")]

namespace FreeTurboDescenders
{
    public class FreeTurboDescendersMod : MelonMod
    {
        private PlayerProbe _probe;
        private TerrainSampler _terrain;
        private WriteTest _writeTest;
        private readonly PedalTest _pedalTest = new PedalTest();
        private readonly FitnessMode _fitness = new FitnessMode();
        private Hud _hud;
        private ErgControl _erg;   // created after Config.Load; never reset on scene loads
        private readonly RideStats _stats = new RideStats();
        private BridgeLink _bridge;
        private readonly BridgeLauncher _launcher = new BridgeLauncher();
        private AutoSteer _auto;
        private readonly AutoStart _autoStart = new AutoStart();
        private readonly VehicleInputs _inputs = new VehicleInputs();
        private readonly WorldGate _world = new WorldGate();
        private readonly PauseProbe _pause = new PauseProbe();

        private GameState _state;
        private float _nextTelemetry;
        private float _censusAt = -1f;
        private readonly Spectators _spectators = new Spectators();
        private readonly DayCycle _dayCycle = new DayCycle();   // night and day during a ride, with party mode
        private bool _autoWant, _hadData;   // auto-engage: a data edge was seen and steering is still to be engaged
        private float _lastDataAt;
        private long _seq;

        public override void OnInitializeMelon()
        {
            Log.Info(Names.Mod + " v0.1.0 starting. Unity " + Application.unityVersion + ", gravity " + Physics.gravity);
            // BuildStamp.Text: commit, branch, +changes, build time (BuildStamp.targets); tools\WhatsInstalled.ps1 reads the same from the DLL
            Log.Info("Build: " + BuildStamp.Text + " (features: fitness, u-turn, zone map, ascender events, bridge launcher)");
            try
            {
                Config.Load();
                _erg = new ErgControl();
                _probe = new PlayerProbe();
                _terrain = new TerrainSampler();
                _writeTest = new WriteTest();
                AutoSteerPatch.PedalTest = _pedalTest;
                _fitness.PedalTest = _pedalTest;
                AutoSteerPatch.Fitness = _fitness;
                _auto = new AutoSteer();
                _auto.Turn = new EndTurn(_fitness);
                try { AscenderEvents.Install(_auto, _probe); }
                catch (Exception ex) { Log.Error("Ascender patch install failed", ex); }
                try { CadenceAnim.Install(_fitness, _probe); }
                catch (Exception ex) { Log.Error("CadenceAnim patch install failed", ex); }
                try { AutoSteerPatch.Install(_auto, _probe); }
                catch (Exception ex) { Log.Error("AutoSteer patch install failed", ex); }
                _hud = new Hud { Launcher = _launcher, World = _world, Crowd = _spectators };
                _probe.PlayerAcquired += OnPlayerAcquired;

                if (Config.IpcEnabled)
                {
                    _bridge = new BridgeLink(Config.TelemetryPort, Config.CommandPort, Config.TelemetryHz);
                    _bridge.Start();
                    _bridge.SetHello(Hello.Message(BuildStamp.Text));   // tells the bridge which game is connected
                    try { _launcher.Start(Config.TelemetryPort); }
                    catch (Exception ex) { Log.Error("Bridge launcher failed", ex); }
                }
                else Log.Info("Bridge: disabled in preferences");

                Log.Info("AutoStart: " + (Config.AutoStart ? "on, seed " + Config.AutoStartSeed : "off"));
                Log.Info("Keys: " + Config.HudKey + " = toggle HUD, " + Config.ErgToggleKey + " = ride mode, " + Config.AutoSteerKey + " = auto-steer, " + Config.SpectatorKey + " = party mode (spectators, night and day), " + Config.WriteTestKey + " = " + Config.TestCapKmh + " km/h speed-cap test for " + Config.TestCapSeconds + " s, " + Config.DumpKey + " = diagnostics dump, " + Config.RayProbeKey + " = ray probe, " + Config.PedalTestKey + " = T2 pedal-force test");
            }
            catch (Exception ex) { Log.Error("Startup failed", ex); }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Log.Info("Scene loaded: '" + sceneName + "' (build index " + buildIndex + ")");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            Log.Info("Scene initialized: '" + sceneName + "' (build index " + buildIndex + ")");
            if (_probe == null) return;
            _probe.Reset();
            NewCourse("scene change");
        }

        /// <summary>Everything that belongs to one course starts again: after a scene load, and when the game builds the next level of a
        /// session inside the same scene (the old path and the old crowd stayed, the crowd in the air over the new ground).</summary>
        private void NewCourse(string why)
        {
            _terrain.Reset();
            _auto.Reset();
            _writeTest.Stop(why);
            _pedalTest.Stop(why);
            AscenderEvents.Reset();
            _fitness.Reset();
            _stats.Reset();
            _spectators.NewCourse(why, Time.unscaledTime);
            _dayCycle.NewCourse(why);
            _hadData = false; _autoWant = false;   // a new course: the next HR / cadence reading engages auto-steer again
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            Log.Info("Scene unloaded: '" + sceneName + "'");
        }

        private void OnPlayerAcquired()
        {
            // Developer option (off by default): census a few seconds after the load, terrain colliders may not exist yet.
            _censusAt = DumpFiles.CensusAt(Config.AutoCensusOnLoad, Time.unscaledTime);
        }

        public override void OnUpdate()
        {
            if (_probe == null) return;
            try { _autoStart.Tick(); }
            catch (Exception ex) { Log.ErrorLimited("autostart", "AutoStart", ex); }
            try { WorkoutShot.Tick(); }   // the picture of a completed workout, when it is due
            catch (Exception ex) { Log.ErrorLimited("shot", "Workout picture", ex); }
            try
            {
                HandleKeys();
                DrainCommands();
                _launcher.Tick();

                _probe.Refresh();
                _state = _probe.Read();
                _world.Tick(_state);
                _pause.Tick();
                _inputs.Read(_probe.Vehicle);
                _pedalTest.CheckReadback(_inputs.Pedal);
                if (_auto.Track.CourseChanged()) NewCourse("new course in the same scene");
                _auto.Track.EnsureLoaded();
                if (!_auto.Engaged && _probe.Player != null && _probe.Rb != null)
                    _auto.Evaluate(_probe.Player.transform.position, _probe.Rb.velocity, _probe.Player.transform.forward, 0f);
                _terrain.Update(_state, _probe.Player != null ? _probe.Player.transform : null);
                float riderKg, bikeKg, ftpW, cdA, crr;
                string bikeType;
                if (_bridge != null && _bridge.TryGetRider(out riderKg, out bikeKg, out ftpW, out bikeType, out cdA, out crr)) Config.ApplyBridgeRider(riderKg, bikeKg, ftpW, bikeType, cdA, crr);
                _fitness.UpdateTrainer(_bridge);
                _erg.Tick(_state.RigidbodyFound, _bridge);
                TickAutoEngage();
                _spectators.TickOwed(_probe, _auto.Track, _world.Ready, Time.unscaledTime);
                _spectators.TickBuild(_probe, Time.unscaledTime);
                _spectators.Tick(Time.time, _probe.Player != null ? _probe.Player.transform.position : Vector3.zero, _probe.Player != null);
                _stats.Update(_state.VehicleFound, _state.Position);
                // party mode brings night and day with it, only on a course with a path, where its crowd stands
                _dayCycle.FollowParty(DayCycleRules.PartyInEffect(_spectators.Wanted, _auto.Track.Loaded), _world.Ready && _probe.Player != null);
                _dayCycle.Tick();
                _fitness.Grounded = !_state.Grounded.HasValue || _state.Grounded.Value;
                _fitness.Brake = float.IsNaN(_inputs.Brake) ? 0f : _inputs.Brake;
                _fitness.GradePct = _terrain.CurrentGradientPct; _fitness.PathGradPct = _auto.OnPath ? _auto.PathGradPct : float.NaN;

                if (_censusAt > 0f && Time.unscaledTime >= _censusAt)
                {
                    _censusAt = -1f;
                    Diagnostics.TerrainCensus(_probe, _terrain);
                }

                PublishTelemetry();
                _hud.Rebuild(_stats, _state,_terrain, _writeTest, _pedalTest, _fitness, _erg, _bridge, _auto,
                    Config.HudKey.ToString(), Config.WriteTestKey.ToString(), Config.DumpKey.ToString());
            }
            catch (Exception ex) { Log.ErrorLimited("update", "OnUpdate", ex); }
        }

        public override void OnFixedUpdate()
        {
            if (_writeTest == null || _probe == null) return;
            try { _writeTest.FixedTick(_probe.Rb); }
            catch (Exception ex) { Log.ErrorLimited("fixed", "OnFixedUpdate", ex); }
        }

        public override void OnGUI()
        {
            if (_hud == null) return;
            try { _hud.OnGUI(); }
            catch (Exception ex) { Log.ErrorLimited("gui", "OnGUI", ex); }
            try { _spectators.OnGUI(); }
            catch (Exception ex) { Log.ErrorLimited("guispec", "Spectator labels", ex); }
            try { Confetti.OnGUI(); }   // last, so it falls over the HUD
            catch (Exception ex) { Log.ErrorLimited("confetti", "Confetti", ex); }
        }

        public override void OnApplicationQuit()
        {
            Log.Info("OnApplicationQuit: shutting down");
            // `bye`: a hidden bridge the game started stops now (trainer released first) instead of 10 minutes later
            try { if (_bridge != null) _bridge.Stop(Hello.Bye()); }
            catch (Exception ex) { Log.Error("Bridge shutdown", ex); }
        }

        /// <summary>
        /// Engage auto-steer once when the trainer link starts delivering heart-rate or cadence data (a rider is on the bike), as soon as the rider is on the path.
        /// Edge-triggered: turning it off with the key stays off until the data has been absent for 10 s or the course reloads.
        /// </summary>
        private void TickAutoEngage()
        {
            if (!Config.AsAutoEngage) return;
            float now = Time.unscaledTime;
            bool data = _fitness.Active && !_fitness.FailSafe && (_fitness.HasCadence || _fitness.HeartRate > 0f);
            if (data)
            {
                if (!_hadData) { _autoWant = true; Log.Info("AutoSteer: HR / cadence data detected, will engage as soon as the rider is on the path"); }
                _hadData = true; _lastDataAt = now;
            }
            else if (_hadData && now - _lastDataAt > 10f) { _hadData = false; _autoWant = false; }
            if (_autoWant && _auto.CanEngageNow(_probe))
            {
                Log.Info("AutoSteer: auto-engaging");
                _auto.Toggle(_probe);
            }
            if (_auto.Engaged) _autoWant = false;
        }

        private void HandleKeys()
        {
            if (Input.GetKeyDown(Config.HudKey))
            {
                _hud.Visible = !_hud.Visible;
                Log.Info("HUD " + (_hud.Visible ? "shown" : "hidden"));
            }
            if (Config.WriteTestKey != KeyCode.None && Input.GetKeyDown(Config.WriteTestKey))   // no key (Config.WriteTestKey)
            {
                if (_state.RigidbodyFound) _writeTest.Toggle(Config.TestCapKmh, Config.TestCapSeconds);
                else Log.Warn("WriteTest: no Rigidbody yet (are you riding?)");
            }
            if (Input.GetKeyDown(Config.AutoSteerKey)) { _autoWant = false; _auto.Toggle(_probe); }   // the rider decides: no automatic engage until the next data edge
            if (Input.GetKeyDown(Config.PedalTestKey))
            {
                if (_pedalTest.Active) _pedalTest.Stop("cancelled");
                else if (_state.RigidbodyFound) _pedalTest.Start(PedalTest.ParseLevels(Config.PedalTestLevels), Config.PedalTestSeconds, Config.PedalTestLeadInSeconds, Config.PedalTestMaxKmh, "key");
                else Log.Warn("PedalTest: no Rigidbody yet (are you riding?)");
            }
            if (Input.GetKeyDown(Config.ZoneMapKey)) ZoneMap.Run(_auto, _probe);
            if (Input.GetKeyDown(Config.SpectatorKey)) _spectators.Toggle(_probe, _auto.Track);
            if (Input.GetKeyDown(Config.SpectatorLabelKey)) { Config.SpectatorLabels = !Config.SpectatorLabels; Log.Info("Spectator labels " + (Config.SpectatorLabels ? "on" : "off")); }
            if (Config.RayProbeKey != KeyCode.None && Input.GetKeyDown(Config.RayProbeKey))   // no key (Config.RayProbeKey)
                Diagnostics.RayProbe(_probe, _terrain);
            if (Config.DumpKey != KeyCode.None && Input.GetKeyDown(Config.DumpKey))   // no key (Config.DumpKey)
            {
                Diagnostics.DumpVehicle(_probe);
                Diagnostics.TerrainCensus(_probe, _terrain);
            }
        }

        private void DrainCommands()
        {
            if (_bridge == null) return;
            BridgeCommand c;
            int guard = 0;
            while (guard++ < 16 && _bridge.TryDequeue(out c))
            {
                bool ok = true; string detail = "";
                switch (c.Name)
                {
                    case "ping":
                        detail = "pong";
                        break;
                    case "set_test_speed":
                        if (!_state.RigidbodyFound) { ok = false; detail = "no rigidbody (not riding)"; }
                        else if (!c.Value.HasValue) { ok = false; detail = "missing numeric 'value' (km/h)"; }
                        else
                        {
                            _writeTest.Start((float)c.Value.Value, c.DurationSec.HasValue ? (float)c.DurationSec.Value : Config.TestCapSeconds, "bridge");
                            detail = "cap " + _writeTest.CapKmh.ToString("F1") + " km/h for " + _writeTest.RemainingSec.ToString("F1") + " s";
                        }
                        break;
                    case "clear_test_speed":
                        _writeTest.Stop("bridge");
                        detail = "cleared";
                        break;
                    case "set_test_pedal":
                    case "test_pedal_sequence":
                        if (!_state.RigidbodyFound) { ok = false; detail = "no rigidbody (not riding)"; }
                        else if (c.Name == "set_test_pedal" && !c.Value.HasValue) { ok = false; detail = "missing numeric 'value' (0..1)"; }
                        else
                        {
                            float[] levels = c.Name == "set_test_pedal" ? new float[] { Mathf.Clamp01((float)c.Value.Value) } : PedalTest.ParseLevels(Config.PedalTestLevels);
                            float hold = c.DurationSec.HasValue ? (float)c.DurationSec.Value : Config.PedalTestSeconds;
                            _pedalTest.Start(levels, hold, 0f, Config.PedalTestMaxKmh, "bridge");
                            detail = _pedalTest.Active ? levels.Length + " level(s), " + hold.ToString("F1") + " s each" : "pedal property unavailable";
                            ok = _pedalTest.Active;
                        }
                        break;
                    case "clear_test_pedal":
                        _pedalTest.Stop("bridge");
                        detail = "cleared";
                        break;
                    case "press_start":   // tools\SmokeTest.ps1: a virtual "any button" in the start cabin (StartGate.cs)
                        ok = StartGate.PressStart(out detail);
                        if (!ok) Log.Info("StartGate: not pressed: " + detail);
                        break;
                    case "crowd":   // developer: what the spectator key does
                        _spectators.Toggle(_probe, _auto.Track);
                        detail = _spectators.Active ? "placing the crowd" : "crowd removed (or nowhere to place it: see the log)";
                        break;
                    case "crowd_shot":   // developer: pictures of the nearest spectator playing a motion-capture clip (docs/SPECTATORS.md "Motion capture")
                        detail = _spectators.StartShots(_probe.Player != null ? _probe.Player.transform.position : Vector3.zero, c.Value.HasValue ? (int)c.Value.Value : 12, "",
                            System.IO.Path.Combine(System.IO.Path.Combine("UserData", Names.Mod), "crowd_shots"));
                        ok = detail.StartsWith("spectator #", StringComparison.Ordinal);
                        break;
                    case "crowd_style":   // developer: that spectator does style number value (excited); one picture when its cycle passes durationSec (a phase, 0..1)
                        detail = _spectators.StartStyleShot(_probe.Player != null ? _probe.Player.transform.position : Vector3.zero, c.Value.HasValue ? (int)c.Value.Value : -1,
                            c.DurationSec.HasValue ? (float)c.DurationSec.Value : 0f, System.IO.Path.Combine(System.IO.Path.Combine("UserData", Names.Mod), "crowd_shots"));
                        ok = detail.StartsWith("spectator #", StringComparison.Ordinal);
                        break;
                    case "crowd_hands":   // developer: one close picture of that spectator's hands with their axes drawn, held at value seconds into the clip
                        detail = _spectators.StartHandShot(_probe.Player != null ? _probe.Player.transform.position : Vector3.zero, c.Value.HasValue ? (float)c.Value.Value : 0f,
                            System.IO.Path.Combine(System.IO.Path.Combine("UserData", Names.Mod), "crowd_shots"));
                        ok = detail.StartsWith("spectator #", StringComparison.Ordinal);
                        break;
                    case "crowd_hand_roll":   // developer: the hands on top of a clip (Spectators.ClipHandRoll ...), for trying out; back to Config's at the next start
                    case "crowd_hand_flex":
                    case "crowd_hand_side":
                    case "crowd_hand_curl":
                        if (!c.Value.HasValue) { ok = false; detail = "missing numeric 'value'"; break; }
                        if (c.Name == "crowd_hand_roll") Spectators.ClipHandRoll = (float)c.Value.Value;
                        else if (c.Name == "crowd_hand_flex") Spectators.ClipHandFlex = (float)c.Value.Value;
                        else if (c.Name == "crowd_hand_side") Spectators.ClipHandSide = (float)c.Value.Value;
                        else Spectators.ClipHandCurl = Mathf.Clamp01((float)c.Value.Value);
                        detail = "hands roll " + Spectators.ClipHandRoll.ToString("F0") + " flex " + Spectators.ClipHandFlex.ToString("F0") + " side " + Spectators.ClipHandSide.ToString("F0")
                            + " curl " + Spectators.ClipHandCurl.ToString("F2");
                        break;
                    case "daycycle":   // developer: the sky's clock round the clock, value = seconds from midnight to noon (default Config.DayCycleNightToDayS), 0 = stop
                        detail = _dayCycle.Command(c.Value);
                        ok = !detail.StartsWith("no sky", StringComparison.Ordinal);
                        break;
                    case "daycycle_night":   // developer: value 1 = the game's night setting goes on while the sun is down, 0 = never
                        detail = _dayCycle.SwitchNight(!c.Value.HasValue || c.Value.Value != 0);
                        break;
                    case "daycycle_trace":   // developer: one line per frame while the sun is near the horizon, UserData\<mod>\day_shots\day_trace.csv; again = stop
                        detail = _dayCycle.Trace(System.IO.Path.Combine(System.IO.Path.Combine("UserData", Names.Mod), "day_shots"));
                        ok = !detail.StartsWith("no trace", StringComparison.Ordinal);
                        break;
                    case "daycycle_shots":   // developer: pictures round sunrise and sunset (DayCycleRules.ShotHours) into UserData\<mod>\day_shots\
                        detail = _dayCycle.StartShots(System.IO.Path.Combine(System.IO.Path.Combine("UserData", Names.Mod), "day_shots"));
                        ok = !detail.StartsWith("no sky", StringComparison.Ordinal) && !detail.StartsWith("already", StringComparison.Ordinal);
                        break;
                    case "quit":   // tools\SmokeTest.ps1: clean exit (OnApplicationQuit, prefs saved); Unity quits at the end of this frame, after the ack below
                        Log.Info("Quit requested by bridge command");
                        Application.Quit();
                        detail = "quitting";
                        break;
                    default:
                        ok = false; detail = "unknown command";
                        Log.Warn("Bridge: unknown command '" + c.Name + "'");
                        break;
                }
                _bridge.Reply(new JsonWriter().Obj().Prop("type", "ack").Prop("name", c.Name).Prop("ok", ok).Prop("detail", detail).End().ToString());
            }
        }

        private void PublishTelemetry()
        {
            if (_bridge == null) return;
            float now = Time.unscaledTime;
            if (now < _nextTelemetry) return;
            _nextTelemetry = now + 1f / Config.TelemetryHz;

            var s = _state;
            float grad = _terrain.CurrentGradientPct;
            var w = new JsonWriter().Obj()
                .Prop("type", "telemetry")
                .Prop("seq", ++_seq)
                .Prop("timestamp", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds)
                .Prop("scene", s.Scene)
                .Prop("playerFound", s.RigidbodyFound)
                .Prop("riding", s.RigidbodyFound && !_pause.Paused)   // the bridge's workout clock runs only while this is true
                .Prop("speedKmh", s.SpeedKmh)
                .Prop("speedMs", s.SpeedMs)
                .Prop("gameSpeedKmh", s.GameSpeedKmh)
                .Obj("position").Prop("x", s.Position.x).Prop("y", s.Position.y).Prop("z", s.Position.z).End()
                .Obj("velocity").Prop("x", s.Velocity.x).Prop("y", s.Velocity.y).Prop("z", s.Velocity.z).End()
                .PropBool("grounded", s.Grounded)
                .Prop("elevation", _terrain.GroundY);
            if (float.IsNaN(grad)) w.PropNull("gradient"); else w.Prop("gradient", grad / 100f);
            w.Prop("gradientDirection", _terrain.DirSource);
            w.Arr("gradientAhead");
            for (int i = 0; i < TerrainSampler.AheadDistances.Length; i++)
            {
                w.Obj().Prop("distance", TerrainSampler.AheadDistances[i]);
                float a = _terrain.AheadPct[i];
                if (float.IsNaN(a)) w.PropNull("gradient"); else w.Prop("gradient", a / 100f);
                w.End();
            }
            w.EndArr();
            w.Prop("testCapActive", _writeTest.Active);
            var au = _auto;
            w.Obj("path").Prop("loaded", au.Track.Loaded);
            if (au.Track.Loaded)
            {
                w.Prop("points", au.Track.Points.Length).Prop("lengthM", au.Track.Length).Prop("onPath", au.OnPath);
                if (au.OnPath)
                {
                    w.Prop("lineOffsetM", au.LineOff).Prop("progressM", au.Progress).Prop("direction", au.Dir).Prop("offLineM", au.XTrack);
                    if (float.IsNaN(au.PathGradPct)) w.PropNull("gradient"); else w.Prop("gradient", au.PathGradPct / 100f);
                    w.Arr("gradientAhead");
                    for (int i = 0; i < au.PathAheadPct.Length; i++)
                    {
                        w.Obj().Prop("distance", i == 0 ? 5f : i == 1 ? 10f : i == 2 ? 20f : 30f);
                        if (float.IsNaN(au.PathAheadPct[i])) w.PropNull("gradient"); else w.Prop("gradient", au.PathAheadPct[i] / 100f);
                        w.End();
                    }
                    w.EndArr();
                }
            }
            w.End();
            w.Obj("inputs").Prop("steer", _inputs.Steer).Prop("slide", _inputs.Slide).Prop("brake", _inputs.Brake).Prop("pedal", _inputs.Pedal).Prop("tilt", _inputs.Tilt).End();
            var pt = _pedalTest;
            w.Obj("pedalTest").Prop("active", pt.Active).Prop("target", pt.Target).Prop("level", pt.LevelIndex + 1).Prop("levels", pt.LevelCount)
                .Prop("stepRemainingSec", pt.StepRemainingSec).Prop("padRaw", pt.PadRaw).Prop("padRawMax", pt.PadRawMax)
                .Prop("forcedSteps", pt.ForcedSteps).Prop("skippedSteps", pt.SkippedSteps).Prop("padOverrideSteps", pt.PadOverrideSteps)
                .Prop("writeMismatch", pt.WriteMismatch).Prop("readbackChecks", pt.ReadbackChecks).Prop("readbackMismatch", pt.ReadbackMismatch).End();
            var fx = _fitness;
            w.Obj("fitness").Prop("active", fx.Active).Prop("state", fx.State).Prop("powerW", fx.RawW).Prop("smoothW", fx.SmoothW).Prop("avg3W", fx.Avg3W).Prop("cadenceRpm", fx.Cadence).Prop("ageSec", fx.AgeSec).Prop("failsafe", fx.FailSafe)
                .Prop("padOverride", fx.PadOverride).Prop("padRaw", fx.PadRaw).Prop("vModelMs", fx.VModel).Prop("gradeSin", fx.GradeSin).Prop("roadGradient", fx.RoadGradient).Prop("weight", fx.Weight).Prop("governedSteps", fx.GovernedSteps).Prop("yieldedSteps", fx.YieldedSteps).Prop("rollbackSteps", fx.RollbackSteps)
                .Prop("waitingForRider", fx.WaitingForRider).Prop("riderKnown", Config.Rider.Known).Prop("riderWeightKg", Config.Rider.RiderKg).Prop("bikeWeightKg", Config.Rider.BikeKg).Prop("ftpW", Config.Rider.FtpW)
                .Prop("bikeType", Config.Rider.BikeType).Prop("cdA", Config.Rider.CdA).Prop("crr", Config.Rider.Crr).End();
            // The session as the mod last received it from the bridge, so recordings still show mode, target and workout.
            var ss = _erg.Session;
            w.Obj("session").Prop("fresh", ss != null);
            if (ss != null)
            {
                w.Prop("mode", ss.Mode).Prop("targetW", ss.TargetW).Prop("eventSeq", ss.EventSeq);
                var wr = ss.Workout;
                if (wr == null) w.PropNull("workout");
                else
                    w.Obj("workout").Prop("name", wr.Name).Prop("started", wr.Started).Prop("paused", wr.Paused).Prop("elapsedS", wr.ElapsedS).Prop("totalS", wr.TotalS)
                        .Prop("step", wr.Step).Prop("steps", wr.Steps).Prop("label", wr.Label).Prop("stepRemainingS", wr.StepRemainingS).Prop("biasPct", wr.BiasPct).Prop("freeRide", wr.FreeRide).Prop("held", wr.Held).End();
            }
            w.End();
            w.Obj("workout").Prop("active", _erg.Workout != null).End();
            w.Obj("cadenceAnim").Prop("driving", CadenceAnim.Driving).Prop("revPerSec", CadenceAnim.PhaseRevPerSec).End();
            w.Obj("uturn").Prop("phase", _auto.Turn != null ? _auto.Turn.Phase : "off").Prop("end", _auto.Turn != null ? _auto.Turn.End : "").Prop("capMs", _auto.Turn != null ? _auto.Turn.CapMs : float.PositiveInfinity).Prop("turns", _auto.Turn != null ? _auto.Turn.Turns : 0).Prop("turnSec", _auto.Turn != null ? _auto.Turn.TurnSeconds : 0f).End();
            w.Obj("ascender").Prop("startHits", AscenderEvents.StartHits).Prop("finishHits", AscenderEvents.FinishHits).Prop("madeClimb", AscenderEvents.MadeClimb).Prop("last", AscenderEvents.LastEvent).Prop("lastProgressM", AscenderEvents.LastProgressM).Prop("lastSpeedKmh", AscenderEvents.LastSpeedKmh).Prop("startTriggerM", au.Track.StartS).Prop("finishTriggerM", au.Track.FinishS).End();
            w.Obj("autoSteer").Prop("engaged", au.Engaged).Prop("cmd", au.Cmd).Prop("alphaDeg", au.AlphaDeg).Prop("lookaheadM", au.Lookahead).Prop("endReached", au.EndReached);
            if (_probe.Vehicle != null) w.Prop("wheelAngleDeg", _probe.Vehicle.steeringAmount);
            w.End();
            w.End();
            _bridge.PublishTelemetry(w.ToString());
        }
    }
}
