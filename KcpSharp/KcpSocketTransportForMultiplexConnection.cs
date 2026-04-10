using System.Net.Sockets;

namespace KcpSharp;

internal sealed class KcpSocketTransportForMultiplexConnection<T> : KcpSocketTransport<KcpMultiplexConnection<T>>,
    IKcpTransport<IKcpMultiplexConnection<T>>
{
    private readonly Action<T?>? _disposeAction;
    private Func<Exception, IKcpTransport<IKcpMultiplexConnection<T>>, object?, bool>? _exceptionHandler;
    private object? _exceptionHandlerState;
    private Func<ReadOnlyMemory<byte>, System.Net.IPEndPoint, bool>? _rawPacketHandler;

    internal KcpSocketTransportForMultiplexConnection(Socket socket, int mtu, int receiveBufferPoolSize = 8)
        : base(socket, mtu, 16, receiveBufferPoolSize) // Default multiplex max batch size to 16 if not configured
    {
    }

    private readonly bool _ownsSocket;

    internal KcpSocketTransportForMultiplexConnection(Socket socket, int mtu, Action<T?>? disposeAction, int receiveBufferPoolSize = 8, bool ownsSocket = false)
        : base(socket, mtu, 16, receiveBufferPoolSize) // Default multiplex max batch size to 16 if not configured
    {
        _disposeAction = disposeAction;
        _ownsSocket = ownsSocket;
    }

    IKcpMultiplexConnection<T> IKcpTransport<IKcpMultiplexConnection<T>>.Connection => Connection;

    void IKcpTransport<IKcpMultiplexConnection<T>>.Start() => Start();

    internal void SetRawPacketHandler(Func<ReadOnlyMemory<byte>, System.Net.IPEndPoint, bool> handler)
    {
        _rawPacketHandler = handler;
    }

    protected override bool OnRawPacketReceived(ReadOnlyMemory<byte> packet, System.Net.IPEndPoint remoteEndPoint)
    {
        return _rawPacketHandler?.Invoke(packet, remoteEndPoint) ?? false;
    }

    public void SetExceptionHandler(Func<Exception, IKcpTransport<IKcpMultiplexConnection<T>>, object?, bool> handler,
        object? state)
    {
        _exceptionHandler = handler;
        _exceptionHandlerState = state;
    }

    protected override KcpMultiplexConnection<T> Activate()
    {
        return new KcpMultiplexConnection<T>(this, _disposeAction);
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