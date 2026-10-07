using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DescendersBridge.Ant;

namespace DescendersBridge;

/// <summary>
/// A trainer write that takes 2 s and is not acknowledged (a poor radio link) must never delay the game-facing side of the ride
/// loop. Drives the real <see cref="RideLoop.Tick"/> on a simulated clock (250 ms ticks) with a fake trainer whose writes end when the test
/// says so, and a real <see cref="ModLink"/> on ephemeral UDP ports (never 9871 / 9872) with the test as the game: it counts the `trainer` /
/// `session` messages of every tick. Run by `turbotrainer-bridge selftest`; no radio, no game.
/// </summary>
public static class RideLoopTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private const string Zwo = "<workout_file><name>Clock test</name><workout><SteadyState Duration='120' Power='0.5'/></workout></workout_file>";

    /// <summary>A trainer whose write stays in flight until <see cref="End"/>.</summary>
    private sealed class SlowTrainer : ITrainerControl
    {
        public readonly List<(string Kind, double Value, int Tick)> Writes = new();
        public int Tick, InFlight, MaxInFlight;
        private TaskCompletionSource<bool>? _tcs;

        public bool Pending => _tcs is not null;
        public Task<bool> SetTrackResistanceAsync(double g, double crr, CancellationToken ct = default) => Begin("grade", g);
        public Task<bool> SetTargetPowerAsync(double w, CancellationToken ct = default) => Begin("erg", w);

        private Task<bool> Begin(string kind, double value)
        {
            Writes.Add((kind, value, Tick));
            MaxInFlight = Math.Max(MaxInFlight, ++InFlight);
            _tcs = new TaskCompletionSource<bool>();
            return _tcs.Task;
        }

        /// <summary>The write in flight ends: acknowledged or not.</summary>
        public void End(bool ack)
        {
            var t = _tcs;
            if (t is null) return;
            _tcs = null; InFlight--;
            t.SetResult(ack);
        }
    }

    private sealed class FakeSource : ITrainerSource
    {
        public readonly SlowTrainer Trainer = new();
        public bool Paired = true;
        public bool LinkLost => false;
        public string LinkLostText => "";
        public ITrainerControl? Control => Trainer;
        public bool TrainerPaired => Paired;
        public int? PowerW => 150;
        public double? CadenceRpm => 85;
        public int? HeartRateBpm => 120;
        public string HeartRateDiagnosis => "";
        public bool Muted => false;
    }

    /// <summary>The loop, its link and the test's "game" socket.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly UdpClient Game = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly ModLink Mod;
        public readonly FakeSource Src = new();
        public readonly RideLoop Loop;
        public DateTime Now = T0;
        public int N;
        public double Gradient = 0.05;
        /// <summary>Ticks whose messages were not exactly one `trainer` and one `session`.</summary>
        public int BadTicks;

        public SlowTrainer Tr => Src.Trainer;

        public Rig()
        {
            Mod = new ModLink(telemetryPort: 0, commandPort: ((IPEndPoint)Game.Client.LocalEndPoint!).Port);
            var cfg = new ResistanceConfig();
            var training = new TrainingConfig { TrainerDifficulty = 1 };   // the road gradient reaches the trainer as is
            Loop = new RideLoop(Src, Mod, cfg, null, training, new Session(SessionConfig.From(training, cfg), () => 200, _ => { }));
        }

        private void ToBridge(string json)
        {
            byte[] b = Encoding.UTF8.GetBytes(json);
            Game.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, Mod.ListenPort));
        }

        /// <summary>Fresh telemetry (riding, the current gradient), waited for: the link's age is real time.</summary>
        private void Telemetry()
        {
            var sw = Stopwatch.StartNew();
            ToBridge("{\"type\":\"telemetry\",\"playerFound\":true,\"riding\":true,\"fitness\":{\"active\":true,\"roadGradient\":"
                + Gradient.ToString("0.###", CultureInfo.InvariantCulture) + "}}");
            while (sw.ElapsedMilliseconds < 1000)
            {
                var m = Mod.State;
                if (m.Seen && m.AgeSeconds <= sw.Elapsed.TotalSeconds && m.RoadGradient == Gradient) return;
                Thread.Sleep(1);
            }
        }

        /// <summary>A button press from the game, waited for like the loop does between ticks.</summary>
        public bool Intent(string name)
        {
            ToBridge("{\"type\":\"intent\",\"name\":\"" + name + "\"}");
            return Mod.WaitIntentAsync(TimeSpan.FromSeconds(1), CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>What the game has received since the last call (waits up to 1 s for <paramref name="atLeast"/> messages).</summary>
        public List<(string Type, string Json)> Received(int atLeast)
        {
            var got = new List<(string, string)>();
            var sw = Stopwatch.StartNew();
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                while (Game.Available > 0)
                {
                    string s = Encoding.UTF8.GetString(Game.Receive(ref from));
                    using var d = JsonDocument.Parse(s);
                    got.Add((d.RootElement.GetProperty("type").GetString() ?? "", s));
                }
                if (got.Count >= atLeast || sw.ElapsedMilliseconds > 1000) return got;
                Thread.Sleep(1);
            }
        }

        /// <summary>One tick <paramref name="seconds"/> after the last; returns the messages it sent to the game.</summary>
        public List<(string Type, string Json)> Tick(double seconds = 0.25)
        {
            Telemetry();
            Now = Now.AddSeconds(seconds);
            Tr.Tick = ++N;
            Loop.Tick(Now);
            var got = Received(2);
            if (got.Count(x => x.Type == "trainer") != 1 || got.Count(x => x.Type == "session") != 1) BadTicks++;
            return got;
        }

        /// <summary><paramref name="n"/> ticks; every write takes 2 s (ends after its 8th tick in flight) and is not acknowledged.</summary>
        public void Ticks(int n)
        {
            for (int i = 0; i < n; i++)
            {
                Tick();
                if (Tr.Pending && N - Tr.Writes[^1].Tick >= 8) Tr.End(false);
            }
        }

        /// <summary>Tick (writes failing after 2 s, as in <see cref="Ticks"/>) until a write is in flight.</summary>
        public bool UntilPending()
        {
            for (int i = 0; i < 12 && !Tr.Pending; i++) Tick();
            return Tr.Pending;
        }

        public void Dispose()
        {
            Tr.End(false);
            Mod.Dispose();
            Game.Dispose();
        }
    }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Ride loop: trainer writes that take 2 s and fail do not delay the game-facing tick");
        string dir = Path.Combine(Path.GetTempPath(), "descenders_rideloop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var r = new Rig();
            var tr = r.Tr;

            r.Ticks(24);   // 6 s
            check(r.BadTicks == 0, $"one `trainer` and one `session` message on every one of 24 ticks ({r.BadTicks} ticks differed)");
            check(tr.Writes.Count == 3 && tr.Writes[0].Tick == 1 && tr.Writes[1].Tick == 10 && tr.Writes[2].Tick == 19,
                $"a write starts only when the one before has ended: ticks 1, 10, 19 (got {string.Join(", ", tr.Writes.Select(w => w.Tick))})");
            check(tr.MaxInFlight == 1 && r.Loop.WritePending, "never two writes in flight; ticks that find one pending skip");
            check(tr.Writes.All(w => w.Kind == "grade" && Math.Abs(w.Value - 5.0) < 1e-9) && r.Loop.LastSentGrade is null,
                "the 5 % road is retried, and never counts as sent while unacknowledged");

            Console.WriteLine("Ride loop: the next write uses the latest target, never a queued one");
            r.Gradient = 0.08; r.Tick();
            r.Gradient = 0.11; r.Tick(); r.Tick();   // ticks 25..27, write 3 still in flight
            check(tr.Writes.Count == 3, "the road changing under a pending write starts nothing");
            tr.End(false);
            r.Gradient = 0.03; r.Tick();
            check(tr.Writes.Count == 4 && tr.Writes[3].Tick == r.N && Math.Abs(tr.Writes[3].Value - 3.0) < 1e-9,
                $"the tick after it ended writes that tick's 3 %, not the 8 % or 11 % passed meanwhile (got {tr.Writes[^1].Value:0.0} %)");
            tr.End(true);
            r.Tick();
            check(r.Loop.LastSentGrade is { } g3 && Math.Abs(g3 - 3.0) < 1e-9 && !r.Loop.WritePending && tr.Writes.Count == 4,
                "acknowledged later: taken on the next tick, then the deadband holds it");

            Console.WriteLine("Ride loop: a button press is answered at once while a write is pending");
            r.Gradient = 0.10;
            check(r.UntilPending(), "a write for the new 10 % road is in flight");
            r.Received(0);
            check(r.Intent("erg_toggle"), "the intent arrives");
            r.Loop.TakeIntents(r.Now, sendNow: true);
            var reply = r.Received(1);
            check(reply.Count == 1 && reply[0].Type == "session" && reply[0].Json.Contains("\"mode\":\"erg\"") && r.Loop.Session.Erg,
                $"the `session` in ERG comes straight back, no tick needed (got {string.Join(", ", reply.Select(x => x.Type))})");
            var after = r.Tick();
            check(after.Count(x => x.Type == "session") == 1 && after.All(x => x.Type != "menu") && tr.Pending, "the `session` goes out with the tick while the write is still pending; there is no `menu` message any more");
            check(r.Intent("erg_toggle"), "the second press arrives");
            r.Loop.TakeIntents(r.Now, sendNow: true);
            var back = r.Received(1);
            check(back.Count == 1 && back[0].Json.Contains("\"mode\":\"freeride\"") && !r.Loop.Session.Erg, "ERG off is answered at once too");

            Console.WriteLine("Ride loop: a press in the bridge window is taken on the next tick, pending write or not");
            string picked = Path.Combine(dir, "picked.zwo");
            File.WriteAllText(picked, Zwo);
            var press = r.Loop.Post((s, now) => Menu.StartWorkout(s, picked, now));
            check(!press.IsCompleted && r.Loop.Session.Run == null, "posted from the window's thread: nothing happens until the loop's tick");
            var taken = r.Tick();
            check(press.IsCompleted && press.Result.Ok && r.Loop.Session.Run?.W.Name == "Clock test" && tr.Pending, "the tick starts the workout and answers the window, with the write still pending");
            check(taken.Count(x => x.Type == "session" && x.Json.Contains("\"name\":\"Clock test\"")) == 1 && r.Loop.SessionLine.Contains("\"Clock test\""),
                "the same tick's `session` shows it to the game, and the window's line names it");
            r.Loop.Session.Apply(new Intent("erg_toggle"), r.Now);   // on hold: the tests below start from freeride
            check(r.Loop.Session.Run != null && r.Loop.Session.Held && !r.Loop.Session.Erg, "the mode button in the game puts it on hold (V2.22): freeride, the workout kept");

            Console.WriteLine("Ride loop: the workout clock matches the simulated clock");
            string zwo = Path.Combine(dir, "clock.zwo");
            File.WriteAllText(zwo, Zwo);
            r.BadTicks = 0;
            check(r.Loop.Session.StartWorkout(zwo, r.Now) && r.Loop.Session.ErgTargetW == 100, "workout started: ERG 100 W (0.5 x FTP 200)");
            DateTime started = r.Now;
            r.Ticks(20);   // 5 s, every write 2 s and not acknowledged
            float elapsed = r.Loop.Session.Run?.Elapsed ?? -1f;
            check(Math.Abs(elapsed - (r.Now - started).TotalSeconds) < 0.01, $"5 s of ticks with failing writes: the workout clock reads {elapsed:0.00} s");
            check(r.BadTicks == 0 && tr.MaxInFlight == 1, "`trainer` and `session` on every tick, still one write at a time");
            check(tr.Writes[^1].Kind == "erg" && tr.Writes[^1].Value == 100 && r.Loop.ErgApplied is null, "the 100 W target is retried and not reported as applied while unacknowledged");
            r.Tick(2.5);
            elapsed = r.Loop.Session.Run?.Elapsed ?? -1f;
            check(Math.Abs(elapsed - (r.Now - started).TotalSeconds) < 0.01, $"a tick 2.5 s late loses no time ({elapsed:0.00} s after {(r.Now - started).TotalSeconds:0.00} s)");
            check(r.UntilPending(), "an ERG write is in flight");
            tr.End(true);
            r.Tick();
            check(r.Loop.ErgApplied == 100, "acknowledged: applied");

            Console.WriteLine("Ride loop: ERG ending under a pending write is released as soon as that write ends");
            check(r.UntilPending() && tr.Writes[^1].Kind == "erg", "the 1/s ERG refresh is in flight");
            r.Loop.Session.Apply(new Intent("erg_toggle"), r.Now);
            int before = tr.Writes.Count;
            r.Tick(); r.Tick();
            check(tr.Writes.Count == before && tr.MaxInFlight == 1, "the release waits for the write in flight (no second write)");
            tr.End(true);
            r.Tick();
            check(tr.Writes.Count == before + 1 && tr.Writes[^1].Kind == "grade" && tr.Writes[^1].Tick == r.N && Math.Abs(tr.Writes[^1].Value - 10.0) < 1e-9 && r.Loop.ErgApplied is null,
                "the very next tick writes the resistance page (the 10 % road), ignoring the write interval");

            Console.WriteLine("Ride loop: the trainer link dropping under a pending write");
            r.Src.Paired = false;
            r.Tick();
            tr.End(true);   // an acknowledgement from before the drop says nothing about what the trainer holds after it
            r.Tick();
            check(r.Loop.LastSentGrade is null && !r.Loop.WritePending, "its late acknowledgement is not taken as the trainer's state");
            r.Tick();       // 1 s since the last write started: the write interval is over
            before = tr.Writes.Count;
            r.Src.Paired = true;
            r.Tick();
            check(tr.Writes.Count == before + 1 && tr.Writes[^1].Tick == r.N, "re-paired: the first write goes out on the next tick");
            check(r.BadTicks == 0, "the messages kept going out on every tick throughout");
        }
        catch (SocketException ex) { check(false, "could not open a UDP port for the test: " + ex.Message); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
