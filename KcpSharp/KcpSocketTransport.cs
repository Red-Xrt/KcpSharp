using System.Net;
using System.Net.Sockets;

namespace KcpSharp;

/// <summary>
///     Helper methods to create socket transports for KCP conversations.
/// </summary>
internal static class KcpSocketTransport
{
    /// <summary>
    ///     Create a socket transport for KCP covnersation.
    /// </summary>
    /// <param name="socket">The udp socket instance.</param>
    /// <param name="endPoint">The remote endpoint.</param>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="options">The options of the <see cref="KcpConversation" />.</param>
    /// <returns>The created socket transport instance.</returns>
    public static IKcpTransport<KcpConversation> CreateConversation(Socket socket, IPEndPoint endPoint,
        uint conversationId, KcpConversationOptions? options)
    {
        if (socket is null) throw new ArgumentNullException(nameof(socket));
        if (endPoint is null) throw new ArgumentNullException(nameof(endPoint));

        return new KcpSocketTransportForConversation(socket, endPoint, conversationId, options);
    }

    /// <summary>
    ///     Create a socket transport for KCP covnersation with no conversation ID.
    /// </summary>
    /// <param name="socket">The udp socket instance.</param>
    /// <param name="endPoint">The remote endpoint.</param>
    /// <param name="options">The options of the <see cref="KcpConversation" />.</param>
    /// <returns>The created socket transport instance.</returns>
    public static IKcpTransport<KcpConversation> CreateConversation(Socket socket, IPEndPoint endPoint,
        KcpConversationOptions? options)
    {
        if (socket is null) throw new ArgumentNullException(nameof(socket));
        if (endPoint is null) throw new ArgumentNullException(nameof(endPoint));

        return new KcpSocketTransportForConversation(socket, endPoint, null, options);
    }

    /// <summary>
    ///     Create a socket transport for raw channel.
    /// </summary>
    /// <param name="socket">The udp socket instance.</param>
    /// <param name="endPoint">The remote endpoint.</param>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="options">The options of the <see cref="KcpRawChannel" />.</param>
    /// <returns>The created socket transport instance.</returns>
    public static IKcpTransport<KcpRawChannel> CreateRawChannel(Socket socket, IPEndPoint endPoint,
        uint conversationId, KcpRawChannelOptions? options)
    {
        if (socket is null) throw new ArgumentNullException(nameof(socket));
        if (endPoint is null) throw new ArgumentNullException(nameof(endPoint));

        return new KcpSocketTransportForRawChannel(socket, endPoint, conversationId, options);
    }

    /// <summary>
    ///     Create a socket transport for raw channel with no conversation ID.
    /// </summary>
    /// <param name="socket">The udp socket instance.</param>
    /// <param name="endPoint">The remote endpoint.</param>
    /// <param name="options">The options of the <see cref="KcpRawChannel" />.</param>
    /// <returns>The created socket transport instance.</returns>
    public static IKcpTransport<KcpRawChannel> CreateRawChannel(Socket socket, IPEndPoint endPoint,
        KcpRawChannelOptions? options)
    {
        if (socket is null) throw new ArgumentNullException(nameof(socket));
        if (endPoint is null) throw new ArgumentNullException(nameof(endPoint));

        return new KcpSocketTransportForRawChannel(socket, endPoint, null, options);
    }

    /// <summary>
    ///     Create a socket transport for multiplex connection.
    /// </summary>
    /// <param name="socket">The udp socket instance.</param>
    /// <param name="mtu">The maximum packet size that can be transmitted over the socket.</param>
    /// <returns>The created socket transport instance.</returns>
    public static IKcpTransport<IKcpMultiplexConnection> CreateMultiplexConnection(Socket socket, int mtu)
    {
        if (socket is null) throw new ArgumentNullException(nameof(socket));

        return new KcpSocketTransportForMultiplexConnection<object>(socket, mtu);
    }

    /// <summary>
    ///     Create a socket transport for multiplex connection.
    /// </summary>
    /// <typeparam name="T">The type of the user state.</typeparam>
    /// <param name="socket">The udp socket instance.</param>
    /// <param name="endPoint">The remote endpoint.</param>
    /// <param name="mtu">The maximum packet size that can be transmitted over the socket.</param>
    /// <returns>The created socket transport instance.</returns>
    public static IKcpTransport<IKcpMultiplexConnection<T>> CreateMultiplexConnection<T>(Socket socket,
        IPEndPoint endPoint, int mtu)
    {
        if (socket is null) throw new ArgumentNullException(nameof(socket));
        if (endPoint is null) throw new ArgumentNullException(nameof(endPoint));

        return new KcpSocketTransportForMultiplexConnection<T>(socket, mtu);
    }

    /// <summary>
    ///     Create a socket transport for multiplex connection.
    /// </summary>
    /// <typeparam name="T">The type of the user state.</typeparam>
    /// <param name="socket">The udp socket instance.</param>
    /// <param name="endPoint">The remote endpoint.</param>
    /// <param name="mtu">The maximum packet size that can be transmitted over the socket.</param>
    /// <param name="disposeAction">The action to invoke when state object is removed.</param>
    /// <returns>The created socket transport instance.</returns>
    public static IKcpTransport<IKcpMultiplexConnection<T>> CreateMultiplexConnection<T>(Socket socket,
        EndPoint endPoint, int mtu, Action<T?>? disposeAction)
    {
        if (socket is null) throw new ArgumentNullException(nameof(socket));
        if (endPoint is null) throw new ArgumentNullException(nameof(endPoint));

        return new KcpSocketTransportForMultiplexConnection<T>(socket, mtu, disposeAction);
    }
}