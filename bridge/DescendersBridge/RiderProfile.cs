using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DescendersBridge.Ant;
using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// The rider profile rules: the `rider` section of the per-user %APPDATA%\TrainerBridge\TrainerBridgeConfig.json (read and written by
/// <see cref="TrainerBridgeConfig"/>), its range checks, and the `rider` message to the mod.
/// </summary>
public static class RiderProfile
{
    public const double MinRiderKg = 30, MaxRiderKg = 200, MinBikeKg = 3, MaxBikeKg = 40, MinFtpW = 50, MaxFtpW = 600;   // same limits as the mod's cfg
    public const double MinWheelM = 0.1, MaxWheelM = 2.54, MaxGearRatio = 7.62;                                           // FE-C page 0x37 field ranges (gear 0 = invalid / not sent)

    /// <summary>Range-check every field; <paramref name="clamped"/> names the ones that were changed ("" = none).</summary>
    internal static RiderConfig Clamp(RiderConfig r, out string clamped)
    {
        var names = new List<string>();
        double C(double v, double lo, double hi, double dflt, string name)
        {
            double o = double.IsNaN(v) ? dflt : Math.Clamp(v, lo, hi);
            if (o != v) names.Add(name);
            return o;
        }
        var d = new RiderConfig();
        var c = new RiderConfig
        {
            RiderWeightKg = C(r.RiderWeightKg, MinRiderKg, MaxRiderKg, d.RiderWeightKg, "riderWeightKg"),
            BikeWeightKg = C(r.BikeWeightKg, MinBikeKg, MaxBikeKg, d.BikeWeightKg, "bikeWeightKg"),
            FtpW = C(r.FtpW, MinFtpW, MaxFtpW, d.FtpW, "ftpW"),
            WheelDiameterM = C(r.WheelDiameterM, MinWheelM, MaxWheelM, d.WheelDiameterM, "wheelDiameterM"),
            GearRatio = C(r.GearRatio, 0, MaxGearRatio, d.GearRatio, "gearRatio"),
            BikeType = BikePresets.Normalize(r.BikeType) ?? d.BikeType,   // unknown or missing: road
        };
        if (!string.Equals(c.BikeType, r.BikeType, StringComparison.OrdinalIgnoreCase)) names.Add("bikeType");
        clamped = string.Join(", ", names);
        return c;
    }

    public static string Describe(RiderConfig r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.RiderWeightKg:0.#} kg + {r.BikeWeightKg:0.#} kg, FTP {r.FtpW:0} W, wheel {r.WheelDiameterM:0.00} m, gear {r.GearRatio:0.##}, {r.BikeType} bike");

    /// <summary>The `rider` message (docs/IPC.md). Wheel and gear stay in the bridge: only the trainer uses them. The bike type goes with its
    /// resolved CdA / Crr, so a game needs no preset table of its own.</summary>
    public static string Message(RiderConfig r)
    {
        BikePresets.Resolve(r.BikeType, out float cdA, out float crr);
        string type = BikePresets.Normalize(r.BikeType) ?? BikePresets.Default;
        return string.Create(CultureInfo.InvariantCulture,
            $"{{\"type\":\"rider\",\"riderWeightKg\":{r.RiderWeightKg:0.##},\"bikeWeightKg\":{r.BikeWeightKg:0.##},\"ftpW\":{r.FtpW:0.#},\"bikeType\":\"{type}\",\"cdA\":{cdA:0.###},\"crr\":{crr:0.####}}}");
    }
}

/// <summary>
/// Sends the `rider` message to the mod (127.0.0.1:9872) at once and then every <see cref="IntervalSeconds"/> s until cancelled: UDP gives no
/// delivery guarantee and the mod may start after the bridge. No ack; the mod keeps the latest values. Own socket, like <see cref="ModStatus"/>,
/// since it runs in every phase (no stick, scanning, riding).
/// </summary>
public static class ModRider
{
    public const int IntervalSeconds = 5;
    private static readonly UdpClient Tx = Loopback.Sender();
    private static readonly IPEndPoint ModEndpoint = new(IPAddress.Loopback, 9872);

    public static void Start(RiderConfig rider, CancellationToken ct)
    {
        byte[] b = Encoding.UTF8.GetBytes(RiderProfile.Message(rider));
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { Tx.Send(b, b.Length, ModEndpoint); } catch { /* mod not listening: UDP, nothing to do */ }
                try { await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), ct); } catch (OperationCanceledException) { return; }
            }
        });
    }
}
