using System.Net;
using System.Net.Sockets;

namespace HyacineCore.Server.Kcp.KcpSharp;

internal sealed class KcpSocketTransportForRawChannel : KcpSocketTransport<KcpRawChannel>, IKcpTransport<KcpRawChannel>
{
    private readonly long? _conversationId;
    private readonly KcpRawChannelOptions? _options;
    private readonly IPEndPoint _remoteEndPoint;

    private Func<Exception, IKcpTransport<KcpRawChannel>, object?, bool>? _exceptionHandler;
    private object? _exceptionHandlerState;


    internal KcpSocketTransportForRawChannel(UdpClient listener, IPEndPoint endPoint, long? conversationId,
        KcpRawChannelOptions? options, int receiveBufferPoolSize = 8)
        : base(listener, options?.Mtu ?? KcpConversationOptions.MtuDefaultValue, receiveBufferPoolSize)
    {
        _conversationId = conversationId;
        _remoteEndPoint = endPoint;
        _options = options;
    }

    KcpRawChannel IKcpTransport<KcpRawChannel>.Connection => Connection;

    void IKcpTransport<KcpRawChannel>.Start() => Start();

    public void SetExceptionHandler(Func<Exception, IKcpTransport<KcpRawChannel>, object?, bool> handler, object? state)
    {
        _exceptionHandler = handler;
        _exceptionHandlerState = state;
    }

    protected override KcpRawChannel Activate()
    {
        return _conversationId.HasValue
            ? new KcpRawChannel(_remoteEndPoint, this, _conversationId.GetValueOrDefault(), _options)
            : new KcpRawChannel(_remoteEndPoint, this, _options);
    }

    protected override bool HandleException(Exception ex)
    {
        if (_exceptionHandler is not null) return _exceptionHandler.Invoke(ex, this, _exceptionHandlerState);
        return false;
    }
}