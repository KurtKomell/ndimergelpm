using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NdiMerger.Sources;

/// <summary>
/// Reads this process's TCP/UDP sockets and maps local IPv4s to NICs
/// so the UI can show which card is actually carrying NDI send traffic.
/// </summary>
public static class NdiSendPathProbe
{
    private const int AfInet = 2;
    private const uint TcpListen = 2;
    private const uint TcpEstablished = 5;
    private const int ErrorInsufficientBuffer = 122;

    public static string Describe(int viewerCount = 0, IReadOnlyList<string>? boundIpv4s = null)
    {
        var pid = (uint)Environment.ProcessId;
        var hits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in EnumerateTcpRows(pid))
            Add(hits, row.ip, weight: row.established ? 3 : 1);

        foreach (var ip in EnumerateUdpLocalIpv4s(pid))
            Add(hits, ip, weight: 1);

        if (boundIpv4s is not null)
        {
            foreach (var ip in boundIpv4s)
            {
                if (!string.IsNullOrWhiteSpace(ip) && !hits.ContainsKey(ip))
                    Add(hits, ip, weight: 0);
            }
        }

        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Select(nic => (nic, ip: FirstIpv4(nic)))
            .Where(x => x.ip is not null)
            .ToArray();

        string LabelFor(string ip)
        {
            var match = adapters.FirstOrDefault(a => a.ip == ip);
            return match.nic is null ? ip : $"{match.nic.Name}  ·  {ip}";
        }

        var live = hits.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToArray();
        var prefix = viewerCount > 0 ? "Viewer connected: " : "Waiting for viewer: ";

        if (live.Length > 0)
            return prefix + string.Join("  +  ", live.Select(LabelFor));

        if (boundIpv4s is { Count: > 0 })
            return prefix + string.Join("  +  ", boundIpv4s.Select(LabelFor));

        return viewerCount > 0 ? "Viewer connected" : "Waiting for viewer";
    }

    private static void Add(Dictionary<string, int> hits, string ip, int weight)
    {
        hits[ip] = hits.GetValueOrDefault(ip) + weight;
    }

    private static string? FirstIpv4(NetworkInterface nic)
    {
        return nic.GetIPProperties().UnicastAddresses
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Select(a => a.ToString())
            .FirstOrDefault();
    }

    private static IEnumerable<(string ip, bool established)> EnumerateTcpRows(uint pid)
    {
        if (!TryReadTable(GetExtendedTcpTable, AfInet, TcpTableOwnerPidAll, out var buffer, out var count, 24))
            yield break;

        try
        {
            var offset = 4;
            for (var i = 0; i < count; i++)
            {
                var state = (uint)Marshal.ReadInt32(buffer, offset);
                var localAddr = unchecked((uint)Marshal.ReadInt32(buffer, offset + 4));
                var localPort = PortFromNbo(unchecked((uint)Marshal.ReadInt32(buffer, offset + 8)));
                var remotePort = PortFromNbo(unchecked((uint)Marshal.ReadInt32(buffer, offset + 16)));
                var owner = unchecked((uint)Marshal.ReadInt32(buffer, offset + 20));
                offset += 24;

                if (owner != pid || (state != TcpEstablished && state != TcpListen))
                    continue;
                if (!IsNdiPort(localPort) && !IsNdiPort(remotePort))
                    continue;

                var local = ToIpv4(localAddr);
                if (local is null || !IsUsable(local))
                    continue;

                yield return (local.ToString(), state == TcpEstablished);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IEnumerable<string> EnumerateUdpLocalIpv4s(uint pid)
    {
        if (!TryReadTable(GetExtendedUdpTable, AfInet, UdpTableOwnerPid, out var buffer, out var count, 12))
            yield break;

        try
        {
            var offset = 4;
            for (var i = 0; i < count; i++)
            {
                var localAddr = unchecked((uint)Marshal.ReadInt32(buffer, offset));
                var localPort = PortFromNbo(unchecked((uint)Marshal.ReadInt32(buffer, offset + 4)));
                var owner = unchecked((uint)Marshal.ReadInt32(buffer, offset + 8));
                offset += 12;

                if (owner != pid || !IsNdiPort(localPort))
                    continue;

                var local = ToIpv4(localAddr);
                if (local is null || !IsUsable(local))
                    continue;

                yield return local.ToString();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // NDI video/control typically 5960+, not mDNS 5353 or HTTPS 443.
    private static bool IsNdiPort(int port) =>
        port is >= 5950 and <= 7999 or >= 59600 and <= 59699;

    private static int PortFromNbo(uint raw) =>
        (int)((raw & 0xFF) << 8 | (raw >> 8) & 0xFF);

    private static bool TryReadTable(
        GetExtendedTable fn,
        int family,
        int tableClass,
        out IntPtr buffer,
        out int count,
        int rowSize)
    {
        buffer = IntPtr.Zero;
        count = 0;
        var size = 0;
        var err = fn(IntPtr.Zero, ref size, true, family, tableClass, 0);
        if (err != 0 && err != ErrorInsufficientBuffer)
            return false;

        buffer = Marshal.AllocHGlobal(size);
        err = fn(buffer, ref size, true, family, tableClass, 0);
        if (err != 0)
        {
            Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
            return false;
        }

        count = Marshal.ReadInt32(buffer);
        if (count < 0 || 4 + count * rowSize > size)
        {
            Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
            count = 0;
            return false;
        }

        return true;
    }

    private static IPAddress? ToIpv4(uint networkOrder)
    {
        if (networkOrder == 0)
            return null;
        var bytes = BitConverter.GetBytes(networkOrder);
        return new IPAddress(bytes);
    }

    private static bool IsUsable(IPAddress addr) =>
        addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr);

    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;

    private delegate uint GetExtendedTable(
        IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);
}
