using System.Buffers;
using Microsoft.Extensions.ObjectPool;

namespace KcpSharp;

internal class KcpPacketOwner : System.Buffers.IMemoryOwner<byte>, IRefCountedBuffer
{
    private byte[]? _array;
    private ObjectPool<KcpPacketOwner>? _pool;
    private int _refCount;

    public Memory<byte> Memory => _array ?? throw new ObjectDisposedException(nameof(KcpPacketOwner));

    public KcpPacketOwner()
    {
    }

    public int GetRefCount() => Volatile.Read(ref _refCount);

    public void Initialize(ObjectPool<KcpPacketOwner> pool, int minimumLength)
    {
        _pool = pool;
        _array = ArrayPool<byte>.Shared.Rent(minimumLength);
        _refCount = 1;
    }

    public IRefCountedBuffer Retain()
    {
        var newCount = Interlocked.Increment(ref _refCount);
        if (newCount <= 1)
        {
            Interlocked.Decrement(ref _refCount);
            throw new ObjectDisposedException(nameof(KcpPacketOwner));
        }
        return this;
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            var array = Interlocked.Exchange(ref _array, null);
            if (array != null)
            {
                ArrayPool<byte>.Shared.Return(array);
                if (_pool is not null)
                {
                    var p = Interlocked.Exchange(ref _pool, null);
                    if (p is not null)
                    {
                        p.Return(this);
                    }
                }
            }
        }
    }
}
