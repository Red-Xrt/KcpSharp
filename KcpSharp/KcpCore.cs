using System.Net;
using System.Net.Sockets;

namespace KcpSharp;

/// <summary>
///     Low-level core factory for direct KCP access.
/// </summary>
public static class KcpCore
{
    /// <summary>
    ///     Creates a pure KCP session (P2P or Client-to-Server).
    ///     Automatically creates and configures the underlying UDP Socket and runs the I/O loop.
    /// </summary>
    /// <param name="localEndPoint">The local endpoint to bind to.</param>
    /// <param name="remoteEndPoint">The remote endpoint to connect to.</param>
    /// <param name="conversationId">The unique conversation ID, if any.</param>
    /// <param name="options">Configuration options for the KCP conversation.</param>
    /// <returns>The KCP conversation instance.</returns>
    [Obsolete("Use KcpBuilder instead")]
    public static KcpConversation CreateConversation(
        IPEndPoint localEndPoint,
        IPEndPoint remoteEndPoint,
        uint? conversationId = null,
        KcpConversationOptions? options = null)
    {
        var socket = new Socket(localEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(localEndPoint);

        var transport = new KcpSocketTransportForConversation(socket, remoteEndPoint, conversationId, options, ownsSocket: true);
        ((IKcpTransport<KcpConversation>)transport).Start();

        return transport.Connection;
    }
}
