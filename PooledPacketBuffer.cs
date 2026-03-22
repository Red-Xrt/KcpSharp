using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace HyacineCore.Server.Kcp.KcpSharp;

internal sealed class PooledPacketBuffer : IDisposable
{
    private readonly Channel<PooledPacketBuffer> _pool;
    private readonly byte[] _array;
    private int _refCount;

    public Memory<byte> Memory => _array;

    public PooledPacketBuffer(Channel<PooledPacketBuffer> pool, int mtu)
    {
        _pool = pool;
        _array = GC.AllocateUninitializedArray<byte>(mtu, pinned: true);
        _refCount = 1;
    }

    public PooledPacketBuffer Retain()
    {
        Interlocked.Increment(ref _refCount);
        return this;
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            _refCount = 1; // reset for next reuse
            _pool.Writer.TryWrite(this);
        }
    }
}

internal sealed class PacketBufferPool
{
    private readonly Channel<PooledPacketBuffer> _channel;
    private readonly int _mtu;

    public PacketBufferPool(int count, int mtu)
    {
        _mtu = mtu;
        _channel = Channel.CreateUnbounded<PooledPacketBuffer>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        for (int i = 0; i < count; i++)
        {
            _channel.Writer.TryWrite(new PooledPacketBuffer(_channel, mtu));
        }
    }

    public ValueTask<PooledPacketBuffer> RentAsync(CancellationToken ct)
    {
        if (_channel.Reader.TryRead(out var buffer))
        {
            return new ValueTask<PooledPacketBuffer>(buffer);
        }

        // If the pool is temporarily exhausted, dynamically allocate a new one.
        // It will be returned to the unbounded channel upon disposal, growing the pool to meet high-watermark demand.
        return new ValueTask<PooledPacketBuffer>(new PooledPacketBuffer(_channel, _mtu));
    }
}
