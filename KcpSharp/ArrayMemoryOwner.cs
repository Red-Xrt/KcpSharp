
using System.Buffers;

namespace KcpSharp;

internal sealed class ArrayMemoryOwner : IMemoryOwner<byte>
{
    private byte[]? _buffer;

    public ArrayMemoryOwner(byte[] buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    /// <summary>
    /// Gets the memory belonging to this owner.
    /// <para>
    /// WARNING: Do not hold onto the returned <see cref="Memory{T}"/> beyond the lifetime of this <see cref="IMemoryOwner{T}"/>.
    /// Once <see cref="Dispose"/> is called, the underlying array is returned to the <see cref="ArrayPool{T}"/>.
    /// Stale references to the returned memory will not throw an <see cref="ObjectDisposedException"/> and will lead to memory corruption.
    /// </para>
    /// </summary>
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

