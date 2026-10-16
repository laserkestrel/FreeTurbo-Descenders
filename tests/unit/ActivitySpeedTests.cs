using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DescendersBridge;

/// <summary>
/// The activity file's speed and distance are the game's speedometer scale (telemetry `gameSpeedKmh` / 3.6), not the real
/// rigidbody speed (`speedMs`), so the file says what the rider saw on screen: the garage ride of 2026-10-12 showed 79.6 km/h on 80 W,
/// the speedometer about 45. The <see cref="ActivityRecorder"/> is fed both speeds by hand (a free ride, a simulated clock, a temp
/// folder) and its file is decoded again with <see cref="FitReader"/>; a mod that sends no `gameSpeedKmh` falls back to `speedMs` with
/// one WARNING. Then the real <see cref="ModLink"/> reads both from a telemetry packet. No radio, no game.
/// </summary>
public static class ActivitySpeedTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 18, 44, 0, DateTimeKind.Utc);

    /// <summary>One free ride of <paramref name="seconds"/> s, a tick a second, fed <paramref name="game"/> and <paramref name="real"/>
    /// (by second, m/s); the decoded file and the recorder's log.</summary>
    private static (FitReader Fit, List<string> Log) Ride(string dir, int seconds, Func<int, double?> game, Func<int, double?> real)
    {
        var log = new List<string>();
        var training = new TrainingConfig();
        var session = new Session(SessionConfig.From(training, new ResistanceConfig()), () => 200);
        var rec = new ActivityRecorder(() => dir, log.Add);
        for (int i = 0; i <= seconds; i++) rec.Tick(session, true, 80, 85, null, game(i), real(i), i == 0 ? 0 : 1, T0.AddSeconds(i));
        rec.Stop(T0.AddSeconds(seconds));
        var fit = rec.Written.Count == 1 ? FitReader.Read(File.ReadAllBytes(rec.Written[0])) : new FitReader { Error = rec.Written.Count + " files" };
        return (fit, log);
    }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Activity file: speed and distance are the game's speedometer scale, not the real speed");

        check(ActivityFiles.Speed(12.5, 22.1, out bool fb) == 12.5 && !fb, "both speeds known: the speedometer's goes into the file");
        check(ActivityFiles.Speed(null, 22.1, out fb) == 22.1 && fb, "no speedometer speed (an older mod): the real speed, and that is a fall-back");
        check(ActivityFiles.Speed(null, null, out fb) is null && !fb, "no speed at all (no telemetry yet): none, and nothing to warn about");
        check(ActivityFiles.Speed(0, 22.1, out fb) == 0 && !fb, "a speedometer speed of 0 is a reading: standing still, not a fall-back");

        string dir = Path.Combine(Path.GetTempPath(), "descenders_activity_speed_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // ---- the garage ride's descent: real speed climbing to 22.1 m/s (79.6 km/h), the speedometer 0.56 x that (12.4 m/s, 44.6 km/h)
            const double scale = 9.81 / 17.5;
            double Real(int i) => Math.Min(22.1, 2.0 + 2.0 * i);
            var (fit, log) = Ride(Path.Combine(dir, "both"), 20, i => Real(i) * scale, i => Real(i));
            var recs = fit.Of(Fit.MesgRecord).ToList();
            var ses = fit.Of(Fit.MesgSession).FirstOrDefault() ?? new();
            var lap = fit.Of(Fit.MesgLap).FirstOrDefault() ?? new();
            check(fit.Ok && recs.Count == 21, "20 s ridden with both speeds: the file decodes, one record per second" + (fit.Error is { } e ? " (" + e + ")" : ""));
            bool each = recs.Count == 21;
            for (int i = 0; each && i < recs.Count; i++) each = recs[i][6] == (uint)Math.Round(Real(i) * scale * 1000);
            check(each, "each record's speed is the speedometer's, never the real one"
                + (recs.Count > 0 ? $" (top {recs.Max(f => f[6]) / 1000.0 * 3.6:0.0} km/h, the real speed was {22.1 * 3.6:0.0})" : ""));
            double sumGame = 0, sumReal = 0;
            for (int i = 1; i <= 20; i++) { sumGame += Real(i) * scale; sumReal += Real(i); }
            uint last = recs.Count > 0 ? recs[^1][5] : 0;
            check(recs.Count > 0 && Math.Abs(last / 100.0 - sumGame) < 0.02 && Math.Abs(last / 100.0 - sumReal) > 100,
                $"distance is the speedometer speed added up: {last / 100.0:0.0} m (the real speed would give {sumReal:0.0} m)");
            uint max = (uint)Math.Round(22.1 * scale * 1000);
            check(ses.Count > 0 && ses[15] == max && ses[9] == last && ses[14] == (uint)Math.Round(sumGame / 20 * 1000) && lap.Count > 0 && lap[14] == max && lap[9] == last && lap[13] == ses[14],
                $"session and lap: maximum speed {(ses.Count > 0 ? ses[15] / 1000.0 * 3.6 : 0):0.0} km/h, average = that distance / riding time, total distance the last record's");
            check(!log.Any(l => l.StartsWith("WARNING", StringComparison.Ordinal)), "no warning when the game sends its speedometer speed");

            // ---- an older mod: no gameSpeedKmh, the real speed as before and one WARNING
            (fit, log) = Ride(Path.Combine(dir, "old"), 20, _ => null, _ => 8.0);
            recs = fit.Of(Fit.MesgRecord).ToList();
            ses = fit.Of(Fit.MesgSession).FirstOrDefault() ?? new();
            check(fit.Ok && recs.Count == 21 && recs.All(f => f[6] == 8000) && recs[^1][5] == 16000 && ses[15] == 8000 && ses[9] == 16000,
                "a mod that sends no speedometer speed: the file has the real speed (8 m/s) and its distance (160 m), not nothing");
            check(log.Count(l => l == ActivityFiles.NoGameSpeedWarning) == 1 && log.Count(l => l.StartsWith("WARNING", StringComparison.Ordinal)) == 1,
                "and the log says so in one WARNING, not one per record");

            // ---- neither speed: the invalid value, no warning (nothing to fall back to)
            (fit, log) = Ride(Path.Combine(dir, "none"), 5, _ => null, _ => null);
            recs = fit.Of(Fit.MesgRecord).ToList();
            check(fit.Ok && recs.Count == 6 && recs.All(f => f[6] == 0xFFFF && f[5] == 0) && !log.Any(l => l.StartsWith("WARNING", StringComparison.Ordinal)),
                "no speed at all: each record's speed is the invalid value, the distance 0, and no warning");

            // ---- the real ModLink reads both from telemetry
            using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var mod = new ModLink(telemetryPort: 0, commandPort: ((IPEndPoint)game.Client.LocalEndPoint!).Port);
            ModState Send(string json, Func<ModState, bool> arrived)
            {
                byte[] b = Encoding.UTF8.GetBytes(json);
                game.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, mod.ListenPort));
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2000 && !arrived(mod.State)) Thread.Sleep(1);
                return mod.State;
            }
            var m = Send("{\"type\":\"telemetry\",\"playerFound\":true,\"riding\":true,\"speedKmh\":79.6,\"speedMs\":22.1,\"gameSpeedKmh\":44.6}", s => s.SpeedMs == 22.1);
            check(m.SpeedMs == 22.1 && m.GameSpeedMs is { } g && Math.Abs(g - 44.6 / 3.6) < 1e-9,
                "telemetry with speedMs 22.1 and gameSpeedKmh 44.6: the link carries both, the speedometer's as 12.39 m/s; speedMs is untouched");
            m = Send("{\"type\":\"telemetry\",\"playerFound\":true,\"riding\":true,\"speedKmh\":28.8,\"speedMs\":8.0}", s => s.SpeedMs == 8.0);
            check(m.SpeedMs == 8.0 && m.GameSpeedMs is null, "telemetry from an older mod, no gameSpeedKmh: the speedometer speed is absent, not 0");
        }
        catch (SocketException ex) { check(false, "could not open a UDP port for the test: " + ex.Message); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
