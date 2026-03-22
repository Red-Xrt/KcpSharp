using System.Net.Sockets;

namespace HyacineCore.Server.Kcp.KcpSharp;

internal sealed class KcpSocketTransportForMultiplexConnection<T> : KcpSocketTransport<KcpMultiplexConnection<T>>,
    IKcpTransport<IKcpMultiplexConnection<T>>
{
    private readonly Action<T?>? _disposeAction;
    private Func<Exception, IKcpTransport<IKcpMultiplexConnection<T>>, object?, bool>? _exceptionHandler;
    private object? _exceptionHandlerState;
    private Func<ReadOnlyMemory<byte>, System.Net.IPEndPoint, bool>? _rawPacketHandler;

    internal KcpSocketTransportForMultiplexConnection(UdpClient listener, int mtu, int receiveBufferPoolSize = 8)
        : base(listener, mtu, receiveBufferPoolSize)
    {
    }

    internal KcpSocketTransportForMultiplexConnection(UdpClient listener, int mtu, Action<T?>? disposeAction, int receiveBufferPoolSize = 8)
        : base(listener, mtu, receiveBufferPoolSize)
    {
        _disposeAction = disposeAction;
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
}