using System;
using System.Net;
using System.Net.Sockets;

namespace KcpSharp;

/// <summary>
///     A fluent builder for creating <see cref="KcpConversation"/> and other Kcp objects.
/// </summary>
public sealed class KcpBuilder
{
    private IPEndPoint? _remoteEndPoint;
    private IKcpTransport? _transport;
    private KcpConversationOptions? _options;
    private Func<Exception, KcpConversation, object?, bool>? _exceptionHandler;
    private object? _exceptionHandlerState;
    private uint? _conversationId;

    /// <summary>
    ///     Initialize a builder for a KcpConversation.
    /// </summary>
    public static KcpBuilder ForConversation()
    {
        return new KcpBuilder();
    }

    /// <summary>
    ///     Sets the remote endpoint.
    /// </summary>
    public KcpBuilder WithRemoteEndPoint(IPEndPoint remoteEndPoint)
    {
        _remoteEndPoint = remoteEndPoint ?? throw new ArgumentNullException(nameof(remoteEndPoint));
        return this;
    }

    /// <summary>
    ///     Sets the KCP transport.
    /// </summary>
    internal KcpBuilder WithTransport(IKcpTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        return this;
    }

    private Socket? _socket;

    private IPEndPoint? _localEndPoint;

    /// <summary>
    ///     Creates an underlying UDP Socket automatically and binds it to the specified local endpoint.
    /// </summary>
    public KcpBuilder WithUdpSocket(System.Net.IPEndPoint localEndPoint, System.Net.Sockets.AddressFamily addressFamily, out System.Net.Sockets.Socket socket)
    {
        socket = new System.Net.Sockets.Socket(addressFamily, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            try { socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }
        }
        socket.Blocking = false;
        socket.Bind(localEndPoint);
        _socket = socket;
        return this;
    }

    /// <summary>
    ///     Creates a UDP socket for the conversation.
    ///     If the socket is not bound manually and <see cref="WithLocalEndPoint"/> is not used,
    ///     <see cref="Build"/> will automatically bind the socket to the ephemeral endpoint 0.0.0.0:0.
    /// </summary>
    public KcpBuilder WithUdpSocket(AddressFamily addressFamily, out Socket socket)
    {
        socket = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
        // Typical optimizations
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            try { socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }
        }
        socket.Blocking = false;
        _socket = socket;
        return this;
    }

    /// <summary>
    ///     Sets the local endpoint. The created socket will be bound to this endpoint automatically.
    /// </summary>
    public KcpBuilder WithLocalEndPoint(IPEndPoint localEndPoint)
    {
        _localEndPoint = localEndPoint;
        return this;
    }

    /// <summary>
    ///     Sets the Conversation ID. If not set, a zero-ID (pure channel) will be used.
    /// </summary>
    public KcpBuilder WithConversationId(uint conversationId)
    {
        _conversationId = conversationId;
        return this;
    }

    /// <summary>
    ///     Sets the configuration options.
    /// </summary>
    public KcpBuilder WithOptions(KcpConversationOptions options)
    {
        _options = options;
        return this;
    }

    /// <summary>
    ///     Sets the exception handler for background tasks.
    /// </summary>
    public KcpBuilder WithExceptionHandler(Func<Exception, KcpConversation, object?, bool> handler, object? state = null)
    {
        _exceptionHandler = handler;
        _exceptionHandlerState = state;
        return this;
    }

    /// <summary>
    ///     Builds the KcpConversation.
    /// </summary>
    public KcpConversation Build()
    {
        if (_transport == null && _socket == null)
            throw new InvalidOperationException("Transport or Socket is required.");
        if (_remoteEndPoint == null)
            throw new InvalidOperationException("RemoteEndPoint is required.");

        if (_socket != null && _socket.AddressFamily != _remoteEndPoint.AddressFamily)
        {
            // Allow DualMode sockets (IPv6 socket talking to IPv4 mapped address)
            if (!(_socket.AddressFamily == AddressFamily.InterNetworkV6 && _socket.DualMode))
            {
                throw new InvalidOperationException("The socket's AddressFamily must match the RemoteEndPoint's AddressFamily.");
            }
        }

        KcpConversation conversation;

        if (_socket != null)
        {
            if (_localEndPoint != null && !_socket.IsBound)
            {
                _socket.Bind(_localEndPoint);
            }
            else if (_localEndPoint == null && !_socket.IsBound)
            {
                _socket.Bind(new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            }
            var transport = KcpSocketTransport.CreateConversation(_socket, _remoteEndPoint, _conversationId.GetValueOrDefault(), _options);
            conversation = transport.Connection;
            transport.Start();
        }
        else
        {
            conversation = _conversationId.HasValue
                ? new KcpConversation(_remoteEndPoint, _transport!, _conversationId.Value, _options)
                : new KcpConversation(_remoteEndPoint, _transport!, _options);
        }

        if (_exceptionHandler != null)
        {
            conversation.SetExceptionHandler(_exceptionHandler, _exceptionHandlerState);
        }

        return conversation;
    }
}
