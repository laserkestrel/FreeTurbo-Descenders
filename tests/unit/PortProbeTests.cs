using System.Net;
using System.Net.Sockets;
using DescendersCyclingPOC;

namespace DescendersBridge;

/// <summary>
/// The mod's bridge launcher skips starting a bridge when UDP 9871 is taken (BridgeLauncher, src\PortProbe.cs). A listener on
/// 0.0.0.0 (BridgeSim.ps1 before the smoke-test branch) was missed by the old loopback probe, so a real bridge started beside it
/// and took the telemetry. The probe reads Windows' UDP table instead of binding 0.0.0.0, and this test binds only
/// 127.0.0.1 too: a socket on 0.0.0.0 makes Windows Firewall ask about every freshly built bridge exe, which broke the QA runs.
/// The 0.0.0.0 case is covered on the table's bytes. Run by `turbotrainer-bridge selftest`.
/// </summary>
public static class PortProbeTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("PortProbe: a listener on either address counts as in use, and nothing binds 0.0.0.0");

        // two rows as GetUdpTable writes them: 0.0.0.0:9871 and 127.0.0.1:9872 (ports big-endian), then a row the buffer cuts off
        byte[] table =
        {
            3, 0, 0, 0,
            0, 0, 0, 0,     0x26, 0x8F, 0, 0,
            127, 0, 0, 1,   0x26, 0x90, 0, 0,
            10, 0, 0, 5,    0x26,
        };
        var rows = PortProbe.ParseUdpTable(table);
        check(rows.Count == 2, $"the UDP table's whole rows are read, a cut-off row is left out ({rows.Count})");
        check(rows.Count == 2 && rows[0].Address.Equals(IPAddress.Any) && rows[0].Port == 9871, "row 1 = 0.0.0.0:9871 (port in network byte order)");
        check(rows.Count == 2 && rows[1].Address.Equals(IPAddress.Loopback) && rows[1].Port == 9872, "row 2 = 127.0.0.1:9872");
        check(PortProbe.ParseUdpTable(new byte[2]).Count == 0 && PortProbe.ParseUdpTable(new byte[4]).Count == 0, "an empty or too short table = no rows");
        check(PortProbe.Holds(rows, 9871), "listener on 0.0.0.0: in use (the case the loopback probe missed)");
        check(PortProbe.Holds(rows, 9872), "listener on 127.0.0.1: in use");
        check(!PortProbe.Holds(rows, 9873) && !PortProbe.Holds(new List<IPEndPoint>(), 9871), "another port, or nothing bound: free");

        // the real table, with sockets on 127.0.0.1 only
        int port;
        using (var free = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)free.Client.LocalEndPoint!).Port;   // a port that was free a moment ago
        check(!PortProbe.UdpInUse(port), $"nothing bound on {port}: free");
        check(PortProbe.LastError is null, "Windows' UDP table was read (no fall back to the loopback bind)" + (PortProbe.LastError is null ? "" : ": " + PortProbe.LastError));
        using (var lo = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)))
        {
            check(PortProbe.UdpInUse(port), "real listener on 127.0.0.1: in use");
            var mine = PortProbe.BoundUdpEndpoints().FindAll(e => e.Port == port);
            check(mine.Count == 1 && mine[0].Address.Equals(IPAddress.Loopback), "and the table shows it on 127.0.0.1, not 0.0.0.0");
        }
        check(!PortProbe.UdpInUse(port), "after it closed: free again");

        // every sender in the bridge is bound to 127.0.0.1 before its first Send (an unbound one gets 0.0.0.0 from Windows)
        using (var tx = Loopback.Sender())
        {
            var ep = (IPEndPoint)tx.Client.LocalEndPoint!;
            check(ep.Address.Equals(IPAddress.Loopback) && ep.Port != 0, $"a sender socket is bound to 127.0.0.1 ({ep})");
        }
    }
}
