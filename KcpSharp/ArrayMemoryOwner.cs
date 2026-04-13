
using System.Buffers;

namespace KcpSharp;

internal sealed class ArrayMemoryOwner : IMemoryOwner<byte>
{
    private byte[]? _buffer;

    public ArrayMemoryOwner(byte[] buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    public Memory<byte> Memory
    {
        get
        {
            var b = Volatile.Read(ref _buffer);
            if (b is null) throw new ObjectDisposedException(nameof(ArrayMemoryOwner));
            return b;
        }
    }

    public void Dispose()
    {
        var b = Interlocked.Exchange(ref _buffer, null);
        if (b is not null)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }
}

