using System.Net;
using System.Net.Sockets;

namespace HyacineCore.Server.Kcp.KcpSharp;

/// <summary>
///     Socket transport for KCP conversation.
/// </summary>
internal sealed class KcpSocketTransportForConversation : KcpSocketTransport<KcpConversation>,
    IKcpTransport<KcpConversation>
{
    private readonly long? _conversationId;
    private readonly KcpConversationOptions? _options;
    private readonly IPEndPoint _remoteEndPoint;

    private Func<Exception, IKcpTransport<KcpConversation>, object?, bool>? _exceptionHandler;
    private object? _exceptionHandlerState;


    internal KcpSocketTransportForConversation(UdpClient listener, IPEndPoint endPoint, long? conversationId,
        KcpConversationOptions? options, int receiveBufferPoolSize = 8)
        : base(listener, options?.Mtu ?? KcpConversationOptions.MtuDefaultValue, receiveBufferPoolSize)
    {
        _conversationId = conversationId;
        _remoteEndPoint = endPoint;
        _options = options;
    }

    KcpConversation IKcpTransport<KcpConversation>.Connection => Connection;

    void IKcpTransport<KcpConversation>.Start() => Start();

    public void SetExceptionHandler(Func<Exception, IKcpTransport<KcpConversation>, object?, bool> handler,
        object? state)
    {
        _exceptionHandler = handler;
        _exceptionHandlerState = state;
    }

    protected override KcpConversation Activate()
    {
        return _conversationId.HasValue
            ? new KcpConversation(_remoteEndPoint, this, _conversationId.GetValueOrDefault(), _options)
            : new KcpConversation(_remoteEndPoint, this, _options);
    }

    protected override bool HandleException(Exception ex)
    {
        if (_exceptionHandler is not null) return _exceptionHandler.Invoke(ex, this, _exceptionHandlerState);
        return false;
    }
}