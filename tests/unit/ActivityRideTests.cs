using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DescendersBridge.Ant;

namespace DescendersBridge;

/// <summary>
/// The real <see cref="RideLoop.Tick"/> on a fake trainer writes one .fit activity file per workout; however
/// short the ride was, one per free ride (freeride, plain ERG) too, and none when nobody pedalled. A simulated clock (1 s ticks), a real <see cref="ModLink"/> on ephemeral UDP ports with the test as the game (telemetry with
/// `riding`, `speedMs` and `gameSpeedKmh`), the recorder on a temp folder. The files are decoded again with <see cref="FitReader"/>. No radio, no game.
/// </summary>
public static class ActivityRideTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 17, 0, 0, DateTimeKind.Utc);

    private static string Zwo(string name, int seconds) =>
        "<workout_file><name>" + name + "</name><workout><SteadyState Duration='" + seconds + "' Power='0.75'/></workout></workout_file>";

    private sealed class Trainer : ITrainerControl
    {
        public Task<bool> SetTrackResistanceAsync(double g, double crr, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetTargetPowerAsync(double w, CancellationToken ct = default) => Task.FromResult(true);
    }

    /// <summary>A trainer with power and cadence and no heart-rate strap; <see cref="Pedalling"/> false = the rider sits still.</summary>
    private sealed class FakeSource : ITrainerSource
    {
        private readonly Trainer _trainer = new();
        public bool Pedalling = true;
        public bool LinkLost => false;
        public string LinkLostText => "";
        public ITrainerControl? Control => _trainer;
        public bool TrainerPaired => true;
        public int? PowerW => Pedalling ? 150 : 0;
        public double? CadenceRpm => Pedalling ? 85 : 0;
        public int? HeartRateBpm => null;
        public string HeartRateDiagnosis => "";
        public bool Muted => false;
    }

    private sealed class Rig : IDisposable
    {
        public readonly UdpClient Game = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly ModLink Mod;
        public readonly RideLoop Loop;
        public readonly ActivityRecorder Recorder;
        public readonly List<string> Log = new();
        public readonly string ActivitiesDir;
        public readonly FakeSource Source = new();
        public DateTime Now = T0;

        public Session Session => Loop.Session;

        public Rig(string dir)
        {
            ActivitiesDir = Path.Combine(dir, ActivityFiles.FolderName);
            Mod = new ModLink(telemetryPort: 0, commandPort: ((IPEndPoint)Game.Client.LocalEndPoint!).Port);
            var cfg = new ResistanceConfig();
            var training = new TrainingConfig();
            Loop = new RideLoop(Source, Mod, cfg, null, training, new Session(SessionConfig.From(training, cfg), () => 200, Log.Add));
            Loop.Activities = Recorder = new ActivityRecorder(() => ActivitiesDir, Log.Add);
        }

        /// <summary>Fresh telemetry at 8 m/s real speed = 16.2 km/h on the game's speedometer (4.5 m/s), waited for (the link's
        /// age is real time), then one tick a second after the last.</summary>
        public void Tick(bool riding = true)
        {
            byte[] b = Encoding.UTF8.GetBytes("{\"type\":\"telemetry\",\"playerFound\":true,\"riding\":" + (riding ? "true" : "false") + ",\"speedMs\":8.0,\"gameSpeedKmh\":16.2,\"fitness\":{\"active\":true,\"roadGradient\":0.02}}");
            var sw = Stopwatch.StartNew();
            Game.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, Mod.ListenPort));
            while (sw.ElapsedMilliseconds < 1000)
            {
                var m = Mod.State;
                if (m.Seen && m.AgeSeconds <= sw.Elapsed.TotalSeconds && m.Riding == riding && m.SpeedMs == 8.0 && m.GameSpeedMs is not null) break;
                Thread.Sleep(0);
            }
            Now = Now.AddSeconds(1);
            Loop.Tick(Now);
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (Game.Available > 0) Game.Receive(ref from);   // the `trainer` / `session` messages: not looked at here
        }

        public void Ticks(int n) { for (int i = 0; i < n; i++) Tick(); }

        /// <summary>Tick until the session has let go of its workout (at most <paramref name="max"/> ticks).</summary>
        public bool UntilOver(int max)
        {
            for (int i = 0; i < max && Session.Run is not null; i++) Tick();
            return Session.Run is null;
        }

        public string[] Files => Directory.Exists(ActivitiesDir) ? Directory.GetFiles(ActivitiesDir) : Array.Empty<string>();

        /// <summary>The files written so far whose name ends with "_<paramref name="name"/>.fit" or "_<paramref name="name"/>_2.fit" ...</summary>
        public List<string> Of(string name) => Recorder.Written.Where(p => Path.GetFileName(p).Contains("_" + name + ".fit", StringComparison.Ordinal)
            || Path.GetFileName(p).Contains("_" + name + "_", StringComparison.Ordinal)).ToList();

        /// <summary>The log's "Activity file:" line for <paramref name="path"/>, "" = none.</summary>
        public string Line(string path) => Log.FirstOrDefault(l => l.StartsWith("Activity file: " + path + " (", StringComparison.Ordinal)) ?? "";

        /// <summary>Every file written is in the folder and nothing else is.</summary>
        public bool FilesMatch => Files.Length == Recorder.Written.Count && Recorder.Written.All(File.Exists);

        public void Dispose() { Mod.Dispose(); Game.Dispose(); }
    }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Activity file: the real ride loop on a fake trainer writes one .fit per workout and per free ride, however short");
        string dir = Path.Combine(Path.GetTempPath(), "descenders_activity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var r = new Rig(dir);
            string Write(string name, int seconds)
            {
                string path = Path.Combine(dir, name.Replace(' ', '_') + ".zwo");
                File.WriteAllText(path, Zwo(name, seconds));
                return path;
            }

            FitReader Read(string? path) => path is not null ? FitReader.Read(File.ReadAllBytes(path)) : new FitReader { Error = "no file" };

            // ---- in the game and never pedalled: nothing
            r.Source.Pedalling = false;
            r.Ticks(10);
            r.Loop.StopActivity(r.Now);
            check(r.Files.Length == 0 && r.Recorder.Written.Count == 0 && !r.Recorder.Recording && !r.Log.Any(l => l.StartsWith("Activity", StringComparison.Ordinal)),
                "10 s in the game without a pedal stroke, then the ride stopped: no activity file, nothing recorded");
            r.Source.Pedalling = true;

            // ---- freeride and plain ERG: one file for the free ride
            r.Ticks(20);
            check(r.Recorder.Recording && r.Log.Count(l => l.StartsWith("Activity: recording this free ride", StringComparison.Ordinal)) == 1, "pedalling in freeride: the free ride is recorded from its first pedal stroke");
            r.Session.Apply(new Intent("erg_toggle"), r.Now);
            r.Ticks(20);
            r.Session.Apply(new Intent("erg_toggle"), r.Now);
            check(r.Recorder.Written.Count == 0, "freeride, manual ERG and freeride again are one free ride: nothing is written meanwhile");
            r.Loop.StopActivity(r.Now);
            var free = r.Of("Free-ride");
            check(free.Count == 1 && r.Recorder.Written.Count == 1 && r.FilesMatch && !r.Recorder.Recording && Path.GetFileName(free[0]) == ActivityFiles.FileName(T0.AddSeconds(11).ToLocalTime(), ActivityFiles.FreeRideName),
                "40 s of freeride and manual ERG, then the ride stopped: exactly one file, named Free-ride (got " + string.Join(", ", r.Recorder.Written.Select(p => Path.GetFileName(p))) + ")");
            var ff = Read(free.FirstOrDefault());
            var fs = ff.Of(Fit.MesgSession).FirstOrDefault() ?? new();
            check(ff.Ok && fs.Count > 0 && fs[8] is >= 38000 and <= 41000 && ff.Of(Fit.MesgRecord).Count() is >= 38 and <= 41 && fs[20] == 150
                && free.Count == 1 && r.Line(free[0]).Length > 0 && !r.Line(free[0]).Contains("ended early"),
                $"it covers the 40 s ridden ({(fs.Count > 0 ? fs[8] / 1000.0 : 0):0} s, {ff.Of(Fit.MesgRecord).Count()} records), and its log line is the normal one");

            // ---- a short workout to its end, with the game paused for 4 s in the middle
            check(r.Session.StartWorkout(Write("Activity test", 20), r.Now) && !r.Recorder.Recording, "a 20 s workout is loaded; nothing is recorded while it waits for the first pedal stroke");
            r.Ticks(3);
            DateTime started = r.Now.AddSeconds(-2);   // the first of these ticks started the workout's clock
            check(r.Recorder.Recording && r.Session.Run is { Started: true }, "pedalling with the game riding: the workout runs and is recorded");
            for (int i = 0; i < 4; i++) r.Tick(riding: false);
            float held = r.Session.Run?.Elapsed ?? -1;
            check(r.UntilOver(40), "the workout reaches its end");
            check(r.Of("Activity-test").Count == 1 && r.Recorder.Written.Count == 2 && r.FilesMatch, $"exactly one more file in activities\\ (now {r.Files.Length})");
            string file = r.Of("Activity-test").FirstOrDefault() ?? "";
            check(Path.GetFileName(file) == ActivityFiles.FileName(started.ToLocalTime(), "Activity test") && Path.GetFileName(file).EndsWith("_Activity-test.fit", StringComparison.Ordinal),
                "named with the workout's start (date, time) and its name: " + Path.GetFileName(file));
            check(file.Length > 0 && r.Log.Count(l => l.StartsWith("Activity file: " + file, StringComparison.Ordinal)) == 1 && !r.Line(file).Contains("ended early"), "the log says the file's full path, once");

            var fit = Read(file.Length > 0 ? file : null);
            check(fit.Ok, "the file decodes and both CRCs are right" + (fit.Error is { } e ? " (" + e + ")" : ""));
            var recs = fit.Of(Fit.MesgRecord).ToList();
            var ses = fit.Of(Fit.MesgSession).FirstOrDefault() ?? new();
            uint ts = Fit.Timestamp(started);
            check(recs.Count is >= 18 and <= 21 && recs.Count > 0 && recs[0][253] == ts && recs.Select(f => f[253]).Distinct().Count() == recs.Count,
                $"one record per second of riding, from the workout's start ({recs.Count} records for 20 s)");
            check(recs.Count > 0 && recs.All(f => f[7] == 150 && f[4] == 85 && f[3] == 0xFF && f[6] == 4500),
                "each record: the trainer's power and cadence, the speed on the game's speedometer (16.2 km/h = 4.5 m/s, not the real 8 m/s), and no heart rate = the invalid value (no strap)");
            check(recs.Count > 1 && recs[^1][5] > recs[0][5] && recs[^1][5] is >= 6800 and <= 9600 && ses.GetValueOrDefault((byte)9) == recs[^1][5],
                $"distance is that speed added up: about 4.5 m/s x 20 s ({(recs.Count > 0 ? recs[^1][5] / 100.0 : 0):0} m, not the 160 m of the real speed), and the session's total is the last record's");
            check(ses.Count > 0 && ses[15] == 4500 && ses[14] is >= 3400 and <= 4700 && !r.Log.Any(l => l.StartsWith("WARNING", StringComparison.Ordinal)),
                $"session: maximum speed 4.5 m/s and the average about that ({(ses.Count > 0 ? ses[14] / 1000.0 : 0):0.00} m/s); no warning, the game sends its speedometer speed");
            var timer = fit.Of(Fit.MesgEvent).Select(f => (At: (int)(f[253] - ts), Type: f[1])).ToList();
            check(timer.Count == 4 && timer[0] == (0, 0u) && timer[1].Type == 1 && timer[2].Type == 0 && timer[2].At - timer[1].At == 4 && timer[3].Type == 4,
                "the game paused for 4 s: timer stop and start events 4 s apart (got " + string.Join(", ", timer.Select(x => x.At + " s type " + x.Type)) + ")");
            check(held >= 0 && ses.Count > 0 && ses[7] - ses[8] == 4000 && ses[8] is >= 18000 and <= 21000 && !recs.Any(f => timer.Count == 4 && f[253] - ts >= timer[1].At && f[253] - ts < timer[2].At),
                $"the pause is not riding time: timer time {(ses.Count > 0 ? ses[8] / 1000.0 : 0):0.#} s, elapsed 4 s more, and no record during it");
            check(ses.Count > 0 && ses[5] == Fit.SportCycling && ses[6] == Fit.SubSportVirtualActivity && ses[20] == 150 && ses[16] == 0xFF, "session: cycling, virtual activity, average power 150 W, no average heart rate");

            // ---- ended early after a short ride: a file each (replaced by another workout, then the ride stopped)
            check(r.Recorder.Recording, "still pedalling after the workout: that is a free ride again");
            string longer = Write("Long test", 900);
            check(r.Session.StartWorkout(longer, r.Now), "a 15 min workout is started");
            r.Ticks(60);
            check(r.Of("Free-ride").Count == 2 && r.Recorder.Written.Count == 3, "the free ride between the two workouts ended when the next one started, with its own file");
            check(r.Recorder.Recording && r.Session.StartWorkout(Write("Other test", 900), r.Now), "after 1 min another workout is started in its place");
            r.Ticks(60);
            var lng = r.Of("Long-test");
            check(lng.Count == 1 && r.Recorder.Written.Count == 4 && r.FilesMatch, $"the replaced workout had 1 min of riding: exactly one file for it ({r.Files.Length} files now)");
            check(lng.Count == 1 && r.Line(lng[0]).Contains(", the workout ended early)") && !r.Log.Any(l => l.Contains("no activity file")),
                "its log line is the normal one, with 'the workout ended early': " + (lng.Count == 1 ? r.Line(lng[0]) : ""));
            var early = Read(lng.FirstOrDefault());
            var es = early.Of(Fit.MesgSession).FirstOrDefault() ?? new();
            check(early.Ok && es.Count > 0 && es[8] is >= 58000 and <= 62000 && early.Of(Fit.MesgRecord).Count() is >= 58 and <= 62,
                $"it covers the minute ridden ({(es.Count > 0 ? es[8] / 1000.0 : 0):0} s, {early.Of(Fit.MesgRecord).Count()} records)");
            r.Loop.StopActivity(r.Now);
            var other = r.Of("Other-test");
            check(other.Count == 1 && r.Recorder.Written.Count == 5 && r.FilesMatch && !r.Recorder.Recording && r.Line(other[0]).Contains(", the workout ended early)"),
                "the ride stopped 1 min into the second one (the window closed, the bridge stopped): exactly one file for it too");
            r.Session.ManualErg(r.Now);

            // ---- armed and never pedalled: nothing
            r.Source.Pedalling = false;
            check(r.Session.StartWorkout(Write("Armed test", 600), r.Now), "a 10 min workout is loaded and the rider does not pedal");
            r.Ticks(30);
            check(r.Session.Run is { Started: false } && !r.Recorder.Recording, "30 s in the game: it still waits for the first pedal stroke, nothing is recorded");
            r.Loop.StopActivity(r.Now);
            check(r.Of("Armed-test").Count == 0 && r.Recorder.Written.Count == 5 && r.FilesMatch && !r.Log.Any(l => l.StartsWith("Activity", StringComparison.Ordinal) && l.Contains("Armed test")),
                "the ride stopped: a workout that was armed and never pedalled writes no file");
            r.Session.ManualErg(r.Now);
            r.Source.Pedalling = true;

            // ---- a free ride (here plain ERG) ends by itself when the game has not been riding for 10 minutes
            r.Ticks(5);
            r.Tick(riding: false);
            r.Tick(riding: false);
            check(r.Recorder.Recording && r.Recorder.Written.Count == 5, "5 s of plain ERG, then the game is paused: the free ride waits");
            r.Now = r.Now.AddSeconds(ActivityFiles.FreeRideIdleSeconds);
            r.Tick(riding: false);
            var idle = Read(r.Recorder.Written.Count == 6 ? r.Recorder.Written[5] : null);
            var ids = idle.Of(Fit.MesgSession).FirstOrDefault() ?? new();
            check(!r.Recorder.Recording && r.Of("Free-ride").Count == 3 && idle.Ok && ids.Count > 0 && ids[7] is >= 4000 and <= 6000 && ids[8] is >= 4000 and <= 6000,
                $"10 min later it is over: its file ends where the riding stopped ({(ids.Count > 0 ? ids[7] / 1000.0 : 0):0} s elapsed), not 10 min later");
            r.Tick(riding: false);
            check(!r.Recorder.Recording, "nothing new is recorded while the game is not riding");

            // ---- skipped to the end: a file, however short
            r.Session.Apply(new Intent("erg_toggle"), r.Now);   // manual ERG -> freeride
            check(r.Session.StartWorkout(Write("Skip test", 600), r.Now), "a 10 min workout is started");
            r.Ticks(10);
            r.Session.Apply(new Intent("skip"), r.Now);
            check(r.Session.Run is null, "skip in its only section ends it");
            r.Tick();
            var skip = r.Of("Skip-test");
            check(skip.Count == 1 && r.Recorder.Written.Count == 7 && r.FilesMatch && !r.Line(skip[0]).Contains("ended early"),
                "skipped to the end after 10 s: that counts as completed, it gets its file");
            r.Ticks(5);
            r.Loop.StopActivity(r.Now);
            check(r.Of("Free-ride").Count == 4 && r.Recorder.Written.Count == 8 && r.FilesMatch && !r.Recorder.Recording, "the free ride after it gets its file when the ride stops");
            r.Loop.StopActivity(r.Now);
            check(r.Recorder.Written.Count == 8, "stopping again writes nothing more");
        }
        catch (SocketException ex) { check(false, "could not open a UDP port for the test: " + ex.Message); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
