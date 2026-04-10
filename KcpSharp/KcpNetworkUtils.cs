using System.Net;
using System.Net.Sockets;

namespace KcpSharp;

internal static class KcpNetworkUtils
{
    public static bool EndPointEquals(IPEndPoint ep, SocketAddress sa)
    {
        if (ep.AddressFamily != sa.Family) return false;

        bool isIpv4 = ep.AddressFamily == AddressFamily.InterNetwork;
        int addressSize = isIpv4 ? 4 : 16;
        if (sa.Size < 2 + addressSize) return false;

        // Port is typically at offset 2 and 3 in big-endian
        int port = (sa.Buffer.Span[2] << 8) | sa.Buffer.Span[3];
        if (ep.Port != port) return false;

        Span<byte> ipBytes = stackalloc byte[16];
        if (!ep.Address.TryWriteBytes(ipBytes, out int bytesWritten) || bytesWritten != addressSize)
        {
            return false;
        }

        // IP starts at offset 4 for IPv4, offset 8 for IPv6
        int ipOffset = isIpv4 ? 4 : 8;
        if (sa.Size < ipOffset + addressSize) return false;

        for (int i = 0; i < addressSize; i++)
        {
            if (sa.Buffer.Span[ipOffset + i] != ipBytes[i]) return false;
        }

        return true;
    }
}
