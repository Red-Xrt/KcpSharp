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
    ///     Creates an underlying UDP Socket automatically.
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

        KcpConversation conversation;

        if (_socket != null)
        {
            if (_localEndPoint != null && !_socket.IsBound)
            {
                _socket.Bind(_localEndPoint);
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
