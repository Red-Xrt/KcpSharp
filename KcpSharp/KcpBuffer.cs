namespace KcpSharp;

internal struct KcpBuffer
{
    private object? _owner;
    private readonly Memory<byte> _memory;

    internal ReadOnlyMemory<byte> DataRegion => _memory.Slice(0, Length);

    internal int Length { get; }

    private KcpBuffer(object? owner, Memory<byte> memory, int length)
    {
        _owner = owner;
        _memory = memory;
        Length = length;
    }

    internal static KcpBuffer CreateFromSpan(KcpRentedBuffer buffer, ReadOnlySpan<byte> dataSource)
    {
        var memory = buffer.Memory;
        if (dataSource.Length > memory.Length) ThrowRentedBufferTooSmall();
        dataSource.CopyTo(memory.Span);
        return new KcpBuffer(buffer.Owner, memory, dataSource.Length);
    }

    internal static KcpBuffer FromRetainedOwner(IRefCountedBuffer buffer, Memory<byte> slice, int length)
    {
        return new KcpBuffer(buffer, slice, length);
    }

    internal KcpBuffer Retain()
    {
        if (_owner is IRefCountedBuffer refCounted)
        {
            return new KcpBuffer(refCounted.Retain(), _memory, Length);
        }
        else if (_owner is null)
        {
            return this; // No owner to retain, safe to pass along
        }
        else
        {
            // Owner doesn't support ref-counting (e.g. ArrayPool without a shared owner wrapper).
            // We must defensively duplicate the buffer memory to ensure the copy outlives the original.
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Length);
            _memory.Span.Slice(0, Length).CopyTo(rented.AsSpan(0, Length));
            return new KcpBuffer(System.Buffers.ArrayPool<byte>.Shared, rented, Length);
        }
    }

    internal KcpBuffer AppendData(ReadOnlySpan<byte> data)
    {
        if (Length + data.Length > _memory.Length) ThrowRentedBufferTooSmall();
        data.CopyTo(_memory.Span.Slice(Length));
        return new KcpBuffer(_owner, _memory, Length + data.Length);
    }

    /// <summary>
    /// Attempts to append the data from another buffer to this one.
    /// Note: The returned combined buffer shares the same underlying memory owner as the original buffer.
    /// The caller must ensure that `Release()` is called only once for the shared ownership (e.g., call `Release()` on the combined buffer but not the original).
    /// </summary>
    internal bool TryAppend(ref KcpBuffer buffer, out KcpBuffer combined)
    {
        if (Length + buffer.Length <= _memory.Length)
        {
            buffer.DataRegion.Span.CopyTo(_memory.Span.Slice(Length));
            combined = new KcpBuffer(_owner, _memory, Length + buffer.Length);
            _owner = null; // Enforce single ownership
            return true;
        }

        combined = default;
        return false;
    }

    internal KcpBuffer Consume(int length)
    {
        if ((uint)length > (uint)Length) ThrowLengthArgumentOutOfRange();
        return new KcpBuffer(_owner, _memory.Slice(length), Length - length);
    }

    internal void Release()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is not null)
        {
            new KcpRentedBuffer(owner, _memory).Dispose();
        }
#if DEBUG
        else
        {
            System.Diagnostics.Debug.Fail("KcpBuffer.Release() called on already-released buffer");
        }
#endif
    }

    private static void ThrowRentedBufferTooSmall()
    {
        throw new InvalidOperationException("The rented buffer is not large enough to hold the data.");
    }

    private static void ThrowLengthArgumentOutOfRange()
    {
        throw new InvalidOperationException("The length to consume exceeds the buffer length.");
    }
}