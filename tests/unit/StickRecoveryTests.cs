using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DescendersBridge.Ant;

namespace DescendersBridge;

/// <summary>
/// Garage test (2026-10-03): the ANT+ stick dropped a few seconds after the game started, the ride ended with "ANT+ stick unplugged
/// during the ride", and the workout started in the bridge window was gone, so the game showed no workout panel until it was started again.
/// A lost stick must not end the ride: <see cref="RecoveringSource"/> reads as "no trainer" until a new session opens, and the real
/// <see cref="RideLoop.RunAsync"/> keeps ticking with its session (mode, workout) untouched. Fakes only: no stick, no game; a real
/// <see cref="ModLink"/> on ephemeral UDP ports (never 9871 / 9872). Run by `turbotrainer-bridge selftest`.
/// </summary>
public static class StickRecoveryTests
{
    private const string Zwo = "<workout_file><name>Stick test</name><workout><SteadyState Duration='120' Power='0.5'/></workout></workout_file>";

    private sealed class FakeTrainer : ITrainerControl
    {
        public int Writes;
        public Task<bool> SetTrackResistanceAsync(double g, double crr, CancellationToken ct = default) { Writes++; return Task.FromResult(true); }
        public Task<bool> SetTargetPowerAsync(double w, CancellationToken ct = default) { Writes++; return Task.FromResult(true); }
    }

    /// <summary>One "ANT session": a stick that can be pulled.</summary>
    private sealed class FakeSession : ITrainerSource, IDisposable
    {
        public readonly FakeTrainer Trainer = new();
        public volatile bool Lost, Disposed;
        public bool LinkLost => Lost;
        public string LinkLostText => Lost ? "stick lost" : "";
        public ITrainerControl? Control => Trainer;
        public bool TrainerPaired => !Lost;
        public int? PowerW => 150;
        public double? CadenceRpm => 85;
        public int? HeartRateBpm => 120;
        public string HeartRateDiagnosis => "strap ok";
        public bool Muted => false;
        public void Dispose() => Disposed = true;
    }

    private static bool Wait(Func<bool> until, double seconds = 5)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds) { if (until()) return true; Thread.Sleep(5); }
        return until();
    }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Stick recovery: a lost ANT+ stick is reopened, the source reads as 'no trainer' meanwhile");
        {
            var first = new FakeSession();
            var second = new FakeSession();
            var log = new List<string>();
            var seen = new List<ITrainerSource?>();
            int opens = 0;
            using var src = new RecoveringSource(first, () =>
            {
                if (++opens < 3) throw new InvalidOperationException("no stick");   // two tries fail: the stick is still re-enumerating
                return Task.FromResult<ITrainerSource>(second);
            }, s => { lock (log) log.Add(s); }, s => { lock (seen) seen.Add(s); }, retrySeconds: 0.02);

            check(!src.LinkLost && src.PowerW == 150 && src.TrainerPaired && ReferenceEquals(src.Control, first.Trainer) && src.HeartRateDiagnosis == "strap ok" && opens == 0,
                "stick present: the first session's readings and trainer pass straight through, nothing is reopened");
            first.Lost = true;
            check(src.LinkLost && src.PowerW is null && src.CadenceRpm is null && src.HeartRateBpm is null && !src.TrainerPaired && src.Control is null && src.HeartRateDiagnosis == "",
                "stick lost: no trainer, no readings (absent is never zero), and it says the stick is gone");
            check(Wait(() => src.Recovery.IsCompleted && !src.LinkLost), "it comes back by itself once the stick opens again");
            check(opens == 3 && src.Recoveries == 1 && first.Disposed && !second.Disposed, $"the dead session was closed and the open retried until it worked (3 tries, got {opens})");
            check(ReferenceEquals(src.Inner, second) && ReferenceEquals(src.Control, second.Trainer) && src.PowerW == 150 && src.TrainerPaired, "the new session is the one read and written from then on");
            lock (seen) check(seen.Count == 2 && seen[0] is null && ReferenceEquals(seen[1], second), "the window is told: gone, then the new session");
            lock (log) check(log.Exists(l => l.StartsWith("ANT+ stick lost")) && log.Exists(l => l.StartsWith("ANT+ stick not back yet")) && log.Exists(l => l.StartsWith("ANT+ stick back after")),
                "lost, still gone and back are each logged");

            second.Lost = true;
            _ = src.LinkLost;
            check(Wait(() => src.Recovery.IsCompleted && src.Recoveries == 2) && second.Disposed, "a second loss in the same ride is recovered the same way");
        }

        Console.WriteLine("Stick recovery: stopping the ride while the stick is gone");
        {
            var first = new FakeSession();
            int opens = 0;
            var src = new RecoveringSource(first, () => { opens++; throw new InvalidOperationException("no stick"); }, _ => { }, retrySeconds: 0.02);
            first.Lost = true;
            check(src.LinkLost && Wait(() => opens >= 2), "it keeps trying while the stick stays away");
            src.Dispose();
            int after = opens;
            Thread.Sleep(100);
            check(src.Recovery.IsCompleted && opens == after && src.LinkLost && src.Inner is null, "Dispose (the ride stopped) ends the retries");
        }

        Console.WriteLine("Stick recovery: the ride loop and the workout started in the window survive the loss");
        string dir = Path.Combine(Path.GetTempPath(), "descenders_stick_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string zwo = Path.Combine(dir, "stick.zwo");
            File.WriteAllText(zwo, Zwo);
            using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var mod = new ModLink(telemetryPort: 0, commandPort: ((IPEndPoint)game.Client.LocalEndPoint!).Port);
            var first = new FakeSession();
            var second = new FakeSession();
            using var src = new RecoveringSource(first, () => Task.FromResult<ITrainerSource>(second), _ => { }, retrySeconds: 0.3);
            var cfg = new ResistanceConfig();
            var training = new TrainingConfig();
            var loop = new RideLoop(src, mod, cfg, null, training, new Session(SessionConfig.From(training, cfg), () => 200, _ => { }));
            using var cts = new CancellationTokenSource();
            Task run = Task.Run(() => loop.RunAsync(cts.Token));

            var started = loop.Post((s, now) => Menu.StartWorkout(s, zwo, now));
            check(started.Wait(3000) && started.Result.Ok && loop.Session.Run?.W.Name == "Stick test", "a workout is started from the window, as before the game was launched");
            first.Lost = true;   // the game starts, the stick drops
            check(Wait(() => src.Inner is null), "the loop notices the stick is gone");
            Thread.Sleep(2 * RideLoop.TickMs);
            check(!run.IsCompleted, "the ride loop does not end when the stick is lost");
            check(loop.Session.Run?.W.Name == "Stick test" && loop.Session.Erg && loop.SessionLine.Contains("\"Stick test\""), "the workout is still loaded while the stick is gone");
            check(Wait(() => !src.LinkLost && ReferenceEquals(src.Inner, second)), "the stick comes back under the running loop");
            check(Wait(() => second.Trainer.Writes > 0), "and the trainer is written again through the new session");
            check(!run.IsCompleted && loop.Session.Run?.W.Name == "Stick test" && ReferenceEquals(RideLoop.Current, loop),
                "same loop, same session, same workout: nothing for the rider to restart");

            cts.Cancel();
            check(run.Wait(5000), "Stop still ends the loop");
        }
        catch (SocketException ex) { check(false, "could not open a UDP port for the test: " + ex.Message); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
