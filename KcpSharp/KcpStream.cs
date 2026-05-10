using System.IO.Pipelines;

namespace KcpSharp;

/// <summary>
///     A stream wrapper of <see cref="KcpConversation" /> or <see cref="PipeReader"/>/<see cref="PipeWriter"/>.
/// </summary>
public sealed class KcpStream : Stream
{
    private readonly bool _ownsConversation;
    private KcpConversation? _conversation;
    private readonly PipeReader? _input;
    private readonly PipeWriter? _output;

    /// <summary>
    ///     Create a stream wrapper over an existing <see cref="KcpConversation" /> instance.
    /// </summary>
    /// <param name="conversation">The conversation instance. It must be in stream mode.</param>
    /// <param name="ownsConversation">
    ///     Whether to dispose the <see cref="KcpConversation" /> instance when
    ///     <see cref="KcpStream" /> is disposed.
    /// </param>
    public KcpStream(KcpConversation conversation, bool ownsConversation)
    {
        if (conversation is null) throw new ArgumentNullException(nameof(conversation));
        if (!conversation.StreamMode)
            throw new ArgumentException("Non-stream mode conversation is not supported.", nameof(conversation));
        _conversation = conversation;
        _ownsConversation = ownsConversation;
    }

    /// <summary>
    ///     Create a stream wrapper over a PipeReader and PipeWriter.
    /// </summary>
    /// <param name="input">The pipe reader.</param>
    /// <param name="output">The pipe writer.</param>
    public KcpStream(PipeReader input, PipeWriter output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <summary>
    ///     The length of the stream. This always throws <see cref="NotSupportedException" />.
    /// </summary>
    public override long Length => throw new NotSupportedException();

    /// <summary>
    ///     The position of the stream. This always throws <see cref="NotSupportedException" />.
    /// </summary>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    ///     Indicates data is available on the stream to be read. This property checks to see if at least one byte of data is
    ///     currently available.
    ///     Note: Evaluating this property is not strictly thread-safe with respect to concurrent reads from the same stream.
    ///     In highly concurrent scenarios, data may be consumed by another thread immediately after evaluating this property.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if a concurrent receive operation is active.
    /// </exception>
    public bool DataAvailable
    {
        get
        {
            if (_conversation is not null)
            {
                try
                {
                    return _conversation.TryPeek(out var result) && result.BytesReceived != 0;
                }
                catch (InvalidOperationException)
                {
                    throw; // Documented exception
                }
            }
            if (_input is not null)
            {
                if (_input.TryRead(out var readResult))
                {
                    bool hasData = !readResult.Buffer.IsEmpty;
                    // AdvanceTo(Start, Start): consumed=nothing, examined=nothing.
                    // This preserves all data in the pipe so future reads can still see it.
                    // Using examined=End would block the next TryRead until new data arrives,
                    // causing a livelock even when existing data is present.
                    _input.AdvanceTo(readResult.Buffer.Start, readResult.Buffer.Start);
                    return hasData;
                }
                return false;
            }
            ThrowHelper.ThrowObjectDisposedForKcpStreamException();
            return false;
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <strong>WARNING:</strong> This synchronous method wraps asynchronous operations using `.GetAwaiter().GetResult()`.
    ///         Calling this method from a thread that is bound to a SynchronizationContext (such as a UI thread in WPF/WinForms
    ///         or the main thread in Unity) may result in a classic deadlock. Use the asynchronous overload (<see cref="FlushAsync(CancellationToken)"/>) instead whenever possible.
    ///     </para>
    /// </remarks>
    public override void Flush()
    {
        throw new NotSupportedException("KcpStream does not support synchronous I/O. Use FlushAsync instead.");
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_conversation is not null)
        {
            return _conversation.FlushAsync(cancellationToken).AsTask();
        }
        else if (_output is not null)
        {
            return _output.FlushAsync(cancellationToken).AsTask();
        }
        return Task.FromException(ThrowHelper.NewObjectDisposedForKcpStreamException());
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <strong>WARNING:</strong> This synchronous method wraps asynchronous operations using `.GetAwaiter().GetResult()`.
    ///         Calling this method from a thread that is bound to a SynchronizationContext (such as a UI thread in WPF/WinForms
    ///         or the main thread in Unity) may result in a classic deadlock. Use the asynchronous overload (<see cref="ReadAsync(byte[], int, int, CancellationToken)"/>) instead whenever possible.
    ///     </para>
    /// </remarks>
    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Synchronous I/O operations (Read/Write/Flush) are not supported by KcpStream to prevent thread-pool starvation and classic deadlocks. Please use the asynchronous equivalents (ReadAsync, WriteAsync, FlushAsync).");
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <strong>WARNING:</strong> This synchronous method wraps asynchronous operations using `.GetAwaiter().GetResult()`.
    ///         Calling this method from a thread that is bound to a SynchronizationContext (such as a UI thread in WPF/WinForms
    ///         or the main thread in Unity) may result in a classic deadlock. Use the asynchronous overload (<see cref="WriteAsync(byte[], int, int, CancellationToken)"/>) instead whenever possible.
    ///     </para>
    /// </remarks>
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Synchronous I/O operations (Read/Write/Flush) are not supported by KcpStream to prevent thread-pool starvation and classic deadlocks. Please use the asynchronous equivalents (ReadAsync, WriteAsync, FlushAsync).");
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <strong>WARNING:</strong> This synchronous method wraps asynchronous operations using `.GetAwaiter().GetResult()`.
    ///         Calling this method from a thread that is bound to a SynchronizationContext (such as a UI thread in WPF/WinForms
    ///         or the main thread in Unity) may result in a classic deadlock. Use the asynchronous overloads instead whenever possible.
    ///     </para>
    /// </remarks>
    public override int ReadByte()
    {
        throw new NotSupportedException("Synchronous I/O operations (Read/Write/Flush) are not supported by KcpStream to prevent thread-pool starvation and classic deadlocks. Please use the asynchronous equivalents (ReadAsync, WriteAsync, FlushAsync).");
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <strong>WARNING:</strong> This synchronous method wraps asynchronous operations using `.GetAwaiter().GetResult()`.
    ///         Calling this method from a thread that is bound to a SynchronizationContext (such as a UI thread in WPF/WinForms
    ///         or the main thread in Unity) may result in a classic deadlock. Use the asynchronous overloads instead whenever possible.
    ///     </para>
    /// </remarks>
    public override void WriteByte(byte value)
    {
        throw new NotSupportedException("Synchronous I/O operations (Read/Write/Flush) are not supported by KcpStream to prevent thread-pool starvation and classic deadlocks. Please use the asynchronous equivalents (ReadAsync, WriteAsync, FlushAsync).");
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
#pragma warning disable CS0618
        if (disposing && _ownsConversation) _conversation?.Dispose();
#pragma warning restore CS0618
        _conversation = null;
        base.Dispose(disposing);
    }

    /// <summary>
    ///     Asynchronously reads data into the buffer.
    /// </summary>
    /// <param name="buffer">The buffer to read data into.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>The number of bytes read.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the stream is disposed.</exception>
    /// <remarks>WARNING: Do NOT await this ValueTask more than once.</remarks>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_conversation is not null)
        {
            return await _conversation.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        else if (_input is not null)
        {
            var result = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (result.Buffer.IsEmpty && result.IsCompleted)
                return 0;

            int toCopy = (int)Math.Min(buffer.Length, result.Buffer.Length);
            var slice = result.Buffer.Slice(0, toCopy);
            int copied = 0;
            foreach (var segment in slice)
            {
                segment.Span.CopyTo(buffer.Span.Slice(copied, segment.Length));
                copied += segment.Length;
            }
            _input.AdvanceTo(slice.End);

            return toCopy;
        }

        throw new ObjectDisposedException(nameof(KcpStream));
    }

    /// <summary>
    ///     Asynchronously writes data from the buffer.
    /// </summary>
    /// <param name="buffer">The buffer containing the data to write.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous write operation.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the stream is disposed.</exception>
    /// <remarks>WARNING: Do NOT await this ValueTask more than once.</remarks>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_conversation is not null)
        {
            await _conversation.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        else if (_output is not null)
        {
            var result = await _output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.IsCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        else
        {
            throw new ObjectDisposedException(nameof(KcpStream));
        }
    }

    /// <summary>
    ///     Asynchronously releases the resources used by the <see cref="KcpStream" />.
    /// </summary>
    /// <returns>A task that represents the asynchronous dispose operation.</returns>
    public override async ValueTask DisposeAsync()
    {
        if (_conversation is not null)
        {
            if (_ownsConversation)
                await _conversation.DisposeAsync().ConfigureAwait(false);
            _conversation = null;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <strong>WARNING:</strong> This synchronous method wraps asynchronous operations using `.GetAwaiter().GetResult()`.
    ///         Calling this method from a thread that is bound to a SynchronizationContext (such as a UI thread in WPF/WinForms
    ///         or the main thread in Unity) may result in a classic deadlock. Use the asynchronous overload (<see cref="ReadAsync(Memory{byte}, CancellationToken)"/>) instead whenever possible.
    ///     </para>
    /// </remarks>
    public override int Read(Span<byte> buffer)
    {
        throw new NotSupportedException("Synchronous I/O operations (Read/Write/Flush) are not supported by KcpStream to prevent thread-pool starvation and classic deadlocks. Please use the asynchronous equivalents (ReadAsync, WriteAsync, FlushAsync).");
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <strong>WARNING:</strong> This synchronous method wraps asynchronous operations using `.GetAwaiter().GetResult()`.
    ///         Calling this method from a thread that is bound to a SynchronizationContext (such as a UI thread in WPF/WinForms
    ///         or the main thread in Unity) may result in a classic deadlock. Use the asynchronous overload (<see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>) instead whenever possible.
    ///     </para>
    /// </remarks>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        throw new NotSupportedException("Synchronous I/O operations (Read/Write/Flush) are not supported by KcpStream to prevent thread-pool starvation and classic deadlocks. Please use the asynchronous equivalents (ReadAsync, WriteAsync, FlushAsync).");
    }
}