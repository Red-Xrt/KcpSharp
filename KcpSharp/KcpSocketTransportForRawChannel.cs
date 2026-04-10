using System.Net;
using System.Net.Sockets;

namespace KcpSharp;

internal sealed class KcpSocketTransportForRawChannel : KcpSocketTransport<KcpRawChannel>, IKcpTransport<KcpRawChannel>
{
    private readonly uint? _conversationId;
    private readonly KcpRawChannelOptions? _options;
    private readonly IPEndPoint _remoteEndPoint;

    private Func<Exception, IKcpTransport<KcpRawChannel>, object?, bool>? _exceptionHandler;
    private object? _exceptionHandlerState;
    private readonly bool _ownsSocket;


    internal KcpSocketTransportForRawChannel(Socket socket, IPEndPoint endPoint, uint? conversationId,
        KcpRawChannelOptions? options, int receiveBufferPoolSize = 8, bool ownsSocket = false)
        : base(socket, options?.Mtu ?? KcpConversationOptions.MtuDefaultValue,
               options?.EnableBatching == false ? 0 : (options?.MaxBatchSize ?? 16),
               receiveBufferPoolSize)
    {
        _conversationId = conversationId;
        _remoteEndPoint = endPoint;
        _options = options;
        _ownsSocket = ownsSocket;
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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsSocket)
        {
            _socket.Dispose();
        }
    }
}