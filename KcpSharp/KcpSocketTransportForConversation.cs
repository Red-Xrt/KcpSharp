using System.Net;
using System.Net.Sockets;

namespace KcpSharp;

/// <summary>
///     Socket transport for KCP conversation.
/// </summary>
internal sealed class KcpSocketTransportForConversation : KcpSocketTransport<KcpConversation>,
    IKcpTransport<KcpConversation>
{
    private readonly uint? _conversationId;
    private readonly KcpConversationOptions? _options;
    private readonly IPEndPoint _remoteEndPoint;

    private Func<Exception, IKcpTransport<KcpConversation>, object?, bool>? _exceptionHandler;
    private object? _exceptionHandlerState;
    private readonly bool _ownsSocket;

    internal KcpSocketTransportForConversation(Socket socket, IPEndPoint endPoint, uint? conversationId,
        KcpConversationOptions? options, int receiveBufferPoolSize = 8, bool ownsSocket = false)
        : base(socket, options?.Mtu ?? KcpConversationOptions.MtuDefaultValue,
               options?.EnableBatching == false ? 0 : (options?.MaxBatchSize ?? 16),
               receiveBufferPoolSize)
    {
        _conversationId = conversationId;
        _remoteEndPoint = endPoint;
        _options = options;
        _ownsSocket = ownsSocket;
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

    private volatile Func<ReadOnlyMemory<byte>, IPEndPoint, bool>? _rawPacketHandler;

    internal void SetRawPacketHandler(Func<ReadOnlyMemory<byte>, IPEndPoint, bool> handler)
    {
        _rawPacketHandler = handler;
    }

    protected override bool OnRawPacketReceived(ReadOnlyMemory<byte> packet, IPEndPoint remoteEndPoint)
    {
        if (!Equals(_remoteEndPoint, remoteEndPoint))
        {
            return true; // Drop packets not from target endpoint
        }

        if (_rawPacketHandler is not null && packet.Length >= 4)
        {
            var convId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Span);
            // Note: If the conversation ID is present and non-zero, packets prefixed with 0 are routed
            // as raw packets. If the conversation ID is not present, we do not bypass KCP with 0 conv-ID.
            if (convId == 0 && _conversationId.HasValue && _conversationId.Value != 0) // Treat 0 as raw only if conv is not 0
            {
                var handled = _rawPacketHandler.Invoke(packet.Slice(4), remoteEndPoint);
                if (handled) return true;
            }
        }
        return base.OnRawPacketReceived(packet, remoteEndPoint);
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