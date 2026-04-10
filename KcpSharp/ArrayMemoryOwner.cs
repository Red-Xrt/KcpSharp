
using System.Buffers;

namespace KcpSharp;

internal sealed class ArrayMemoryOwner : IMemoryOwner<byte>
{
    private readonly byte[] _buffer;

    public ArrayMemoryOwner(byte[] buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    public Memory<byte> Memory => _buffer;

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
    }
}

