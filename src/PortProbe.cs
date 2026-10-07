#nullable disable   // also compiled into the bridge (nullable enabled); the mod has nullable off
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// Is a local UDP port already bound? Pure .NET (no Unity), also compiled into the bridge for `selftest`.
    /// Reads Windows' table of bound UDP sockets, so a listener on 0.0.0.0 counts as well as one on 127.0.0.1 (a loopback
    /// bind beside a socket on 0.0.0.0 succeeds, so a loopback probe misses it and the launcher starts a second listener).
    /// It never binds 0.0.0.0 itself: a socket on that address is reachable from the network, so Windows
    /// Firewall asks about the program once per exe path, and every freshly built bridge is a new path. Nothing here
    /// binds anything but 127.0.0.1.
    /// </summary>
    public static class PortProbe
    {
        /// <summary>Why the table could not be read on the last call (the loopback bind was used instead), null = it was read.</summary>
        public static string LastError;

        public static bool UdpInUse(int port)
        {
            List<IPEndPoint> bound;
            try { bound = BoundUdpEndpoints(); LastError = null; }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return LoopbackBindFails(port);
            }
            return Holds(bound, port);
        }

        /// <summary>The rule: a socket bound to the port on any address (0.0.0.0, 127.0.0.1, ...) holds it.</summary>
        public static bool Holds(IEnumerable<IPEndPoint> bound, int port)
        {
            foreach (IPEndPoint ep in bound)
                if (ep.Port == port) return true;
            return false;
        }

        /// <summary>
        /// MIB_UDPTABLE as GetUdpTable fills it: a 4-byte row count, then 8 bytes per row = the IPv4 address (4 bytes) and
        /// the port in network byte order in the first 2 bytes of a 4-byte field. Rows cut off by a short buffer are left out.
        /// </summary>
        public static List<IPEndPoint> ParseUdpTable(byte[] table)
        {
            var list = new List<IPEndPoint>();
            if (table == null || table.Length < 4) return list;
            int rows = BitConverter.ToInt32(table, 0);
            for (int i = 0; i < rows; i++)
            {
                int o = 4 + i * 8;
                if (o + 8 > table.Length) break;
                var addr = new IPAddress(new byte[] { table[o], table[o + 1], table[o + 2], table[o + 3] });
                list.Add(new IPEndPoint(addr, (table[o + 4] << 8) | table[o + 5]));
            }
            return list;
        }

        /// <summary>Every bound IPv4 UDP socket on this machine. Throws when the table cannot be read.</summary>
        public static List<IPEndPoint> BoundUdpEndpoints()
        {
            const int ERROR_INSUFFICIENT_BUFFER = 122;
            var buf = new byte[8192];
            for (int attempt = 0; attempt < 6; attempt++)
            {
                int size = buf.Length;
                int rc = GetUdpTable(buf, ref size, false);
                if (rc == 0) return ParseUdpTable(buf);
                if (rc != ERROR_INSUFFICIENT_BUFFER) throw new InvalidOperationException("GetUdpTable returned " + rc);
                buf = new byte[Math.Max(size, buf.Length) + 1024];   // room for sockets opened between the two calls
            }
            throw new InvalidOperationException("GetUdpTable: the table kept growing");
        }

        // The table could not be read: the probe from before the table, an exclusive bind on 127.0.0.1 (misses a listener on 0.0.0.0).
        private static bool LoopbackBindFails(int port)
        {
            Socket s = null;
            try
            {
                s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.ExclusiveAddressUse = true;
                s.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return false;
            }
            catch (SocketException) { return true; }
            finally { if (s != null) s.Close(); }
        }

        [DllImport("iphlpapi.dll")]
        private static extern int GetUdpTable([In, Out] byte[] table, ref int size, bool order);
    }
}
