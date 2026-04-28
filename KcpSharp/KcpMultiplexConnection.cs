using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace KcpSharp;

/// <summary>
///     Multiplexes multiple logical channels or conversations over a single underlying transport.
///     This connection reads incoming packets, identifies the target conversation via its ID, and routes the packet accordingly.
/// </summary>
/// <typeparam name="T">The type of the user-defined state associated with each channel or conversation.</typeparam>
internal sealed class KcpMultiplexConnection<T> : IKcpTransport, IKcpBatchTransport, IKcpConversation, IKcpMultiplexConnection<T>, IKcpPacketSink
{
    private readonly ConcurrentDictionary<uint, (IKcpConversation Conversation, T? State)> _conversations = new();

    private readonly Action<T?>? _disposeAction;
    private readonly IKcpTransport _transport;
    private readonly IKcpBatchTransport? _batchTransport;
    private volatile bool _disposed;
    private volatile bool _transportClosed;
    private int _disposeFlag;

    /// <summary>
    ///     Initializes a new instance of the <see cref="KcpMultiplexConnection{T}"/> class using the specified transport.
    /// </summary>
    /// <param name="transport">The underlying transport used for sending and receiving data.</param>
    internal KcpMultiplexConnection(IKcpTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _batchTransport = transport as IKcpBatchTransport;
        _disposeAction = null;
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="KcpMultiplexConnection{T}"/> class with an optional dispose action for state objects.
    /// </summary>
    /// <param name="transport">The underlying transport used for sending and receiving data.</param>
    /// <param name="disposeAction">An action to invoke when a conversation's state object is removed or disposed.</param>
    internal KcpMultiplexConnection(IKcpTransport transport, Action<T?>? disposeAction)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _batchTransport = transport as IKcpBatchTransport;
        _disposeAction = disposeAction;
    }

    /// <summary>
    ///     Process a newly received packet from the transport.
    /// </summary>
    /// <param name="packet">The content of the packet with conversation ID.</param>
    /// <param name="remoteEndPoint">The remote endpoint that sent the packet.</param>
    /// <param name="bufferOwner">The buffer owner to be disposed of when no longer needed.</param>
    /// <param name="cancellationToken">A token to cancel this operation.</param>
    /// <returns>
    ///     A <see cref="ValueTask" /> that completes when the packet is handled by the corresponding channel or
    ///     conversation.
    /// </returns>
    ValueTask IKcpPacketSink.InputPacketAsync(ReadOnlyMemory<byte> packet, IPEndPoint remoteEndPoint, System.Buffers.IMemoryOwner<byte>? bufferOwner, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> span = packet.Span;
        if (span.Length < KcpGlobalVars.CONVID_LENGTH)
        {
            bufferOwner?.Dispose();
            return default;
        }

        if (_transportClosed || _disposed)
        {
            bufferOwner?.Dispose();
            return default;
        }

        var id = BinaryPrimitives.ReadUInt32LittleEndian(span);

        if (!_conversations.TryGetValue(id, out var value))
        {
            bufferOwner?.Dispose();
            return default;
        }

        if (value.Conversation is IKcpPacketSink sink)
            return sink.InputPacketAsync(packet, remoteEndPoint, bufferOwner, cancellationToken);

        bufferOwner?.Dispose();
        return default;
    }

    /// <inheritdoc />
    public void SetTransportClosed()
    {
        _transportClosed = true;
        foreach (var (conversation, _) in _conversations.Values) conversation.SetTransportClosed();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeFlag, 1) == 1) return;
        _transportClosed = true;
        _disposed = true;
        while (!_conversations.IsEmpty)
        {
            var keys = _conversations.Keys.ToArray();
            if (keys.Length == 0) break;
            foreach (var id in keys)
                if (_conversations.TryRemove(id, out var value))
                {
                    if (value.Conversation is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        value.Conversation.Dispose();
                    }
                    if (_disposeAction is not null) _disposeAction.Invoke(value.State);
                }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeFlag, 1) == 1) return;
        _transportClosed = true;
        _disposed = true;
        while (!_conversations.IsEmpty)
        {
            var keys = _conversations.Keys.ToArray();
            if (keys.Length == 0) break;
            foreach (var id in keys)
                if (_conversations.TryRemove(id, out var value))
                {
                    value.Conversation.Dispose();
                    if (_disposeAction is not null) _disposeAction.Invoke(value.State);
                }
        }
    }

    /// <summary>
    ///     Determine whether the multiplex connection contains a conversation with the specified id.
    /// </summary>
    /// <param name="id">The conversation ID.</param>
    /// <returns>True if the multiplex connection contains the specified conversation. Otherwise false.</returns>
    public bool Contains(uint id)
    {
        CheckDispose();
        return _conversations.ContainsKey(id);
    }

    /// <summary>
    ///     Create a raw channel with the specified conversation ID.
    /// </summary>
    /// <param name="id">The conversation ID.</param>
    /// <param name="remoteEndpoint">The remote Endpoint</param>
    /// <param name="options">The options of the <see cref="KcpRawChannel" />.</param>
    /// <returns>The raw channel created.</returns>
    /// <exception cref="ObjectDisposedException">The current instance is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another channel or conversation with the same ID was already registered.</exception>
    public KcpRawChannel CreateRawChannel(uint id, IPEndPoint remoteEndpoint, KcpRawChannelOptions? options = null)
    {
        KcpRawChannel? channel = new(remoteEndpoint, this, id, options);
        try
        {
            RegisterConversation(channel, id, default);
            if (_transportClosed) channel.SetTransportClosed();
            return Interlocked.Exchange(ref channel, null)!;
        }
        finally
        {
            if (channel is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await channel.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to dispose channel: {ex.Message}"); }
                });
            }
        }
    }

    /// <summary>
    ///     Create a raw channel with the specified conversation ID.
    /// </summary>
    /// <param name="id">The conversation ID.</param>
    /// <param name="remoteEndpoint">The remote Endpoint</param>
    /// <param name="state">The user state of this channel.</param>
    /// <param name="options">The options of the <see cref="KcpRawChannel" />.</param>
    /// <returns>The raw channel created.</returns>
    /// <exception cref="ObjectDisposedException">The current instance is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another channel or conversation with the same ID was already registered.</exception>
    public KcpRawChannel CreateRawChannel(uint id, IPEndPoint remoteEndpoint, T state,
        KcpRawChannelOptions? options = null)
    {
        KcpRawChannel? channel = new(remoteEndpoint, this, id, options);
        try
        {
            RegisterConversation(channel, id, state);
            if (_transportClosed) channel.SetTransportClosed();
            return Interlocked.Exchange(ref channel, null)!;
        }
        finally
        {
            if (channel is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await channel.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to dispose channel: {ex.Message}"); }
                });
            }
        }
    }

    /// <summary>
    ///     Create a conversation with the specified conversation ID.
    /// </summary>
    /// <param name="id">The conversation ID.</param>
    /// <param name="remoteEndpoint">The remote Endpoint</param>
    /// <param name="options">The options of the <see cref="KcpConversation" />.</param>
    /// <returns>The KCP conversation created.</returns>
    /// <exception cref="ObjectDisposedException">The current instance is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another channel or conversation with the same ID was already registered.</exception>
    public KcpConversation CreateConversation(uint id, IPEndPoint remoteEndpoint,
        KcpConversationOptions? options = null)
    {
        KcpConversation? conversation = new(remoteEndpoint, this, id, options);
        try
        {
            RegisterConversation(conversation, id, default);
            if (_transportClosed) conversation.SetTransportClosed();
            return Interlocked.Exchange(ref conversation, null)!;
        }
        finally
        {
            if (conversation is not null)
            {
                // To avoid deadlocks synchronously calling DisposeAsync
                _ = Task.Run(async () =>
                {
                    try { await conversation.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to dispose conversation: {ex.Message}"); }
                });
            }
        }
    }

    /// <summary>
    ///     Create a conversation with the specified conversation ID.
    /// </summary>
    /// <param name="id">The conversation ID.</param>
    /// <param name="remoteEndpoint">The remote Endpoint</param>
    /// <param name="state">The user state of this conversation.</param>
    /// <param name="options">The options of the <see cref="KcpConversation" />.</param>
    /// <returns>The KCP conversation created.</returns>
    /// <exception cref="ObjectDisposedException">The current instance is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another channel or conversation with the same ID was already registered.</exception>
    public KcpConversation CreateConversation(uint id, IPEndPoint remoteEndpoint, T state,
        KcpConversationOptions? options = null)
    {
        KcpConversation? conversation = new(remoteEndpoint, this, id, options);
        try
        {
            RegisterConversation(conversation, id, state);
            if (_transportClosed) conversation.SetTransportClosed();
            return Interlocked.Exchange(ref conversation, null)!;
        }
        finally
        {
            if (conversation is not null)
            {
                // To avoid deadlocks synchronously calling DisposeAsync
                _ = Task.Run(async () =>
                {
                    try { await conversation.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to dispose conversation: {ex.Message}"); }
                });
            }
        }
    }

    /// <summary>
    ///     Register a conversation or channel with the specified conversation ID and user state.
    /// </summary>
    /// <param name="conversation">The conversation or channel to register.</param>
    /// <param name="id">The conversation ID.</param>
    /// <exception cref="ArgumentNullException"><paramref name="conversation" /> is not provided.</exception>
    /// <exception cref="ObjectDisposedException">The current instance is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another channel or conversation with the same ID was already registered.</exception>
    public void RegisterConversation(IKcpConversation conversation, uint id)
    {
        RegisterConversation(conversation, id, default);
    }

    /// <summary>
    ///     Register a conversation or channel with the specified conversation ID and user state.
    /// </summary>
    /// <param name="conversation">The conversation or channel to register.</param>
    /// <param name="id">The conversation ID.</param>
    /// <param name="state">The user state</param>
    /// <exception cref="ArgumentNullException"><paramref name="conversation" /> is not provided.</exception>
    /// <exception cref="ObjectDisposedException">The current instance is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another channel or conversation with the same ID was already registered.</exception>
    public void RegisterConversation(IKcpConversation conversation, uint id, T? state)
    {
        if (conversation is null) throw new ArgumentNullException(nameof(conversation));

        CheckDispose();
        var (addedConversation, _) = _conversations.GetOrAdd(id, (conversation, state));

        if (!ReferenceEquals(addedConversation, conversation))
            throw new InvalidOperationException("Duplicated conversation.");

        if (_disposed)
        {
            if (_conversations.TryRemove(id, out var value) && ReferenceEquals(value.Conversation, addedConversation))
            {
                // To prevent TOCTOU race condition (H-5) where Dispose completes before we reach here
                // causing the conversation to be leaked and never disposed, we must dispose it ourselves
                // because we just registered it into a disposed dictionary.
                value.Conversation.Dispose();
            }
            ThrowObjectDisposedException();
        }
    }

    /// <summary>
    ///     Unregister a conversation or channel with the specified conversation ID.
    /// </summary>
    /// <param name="id">The conversation ID.</param>
    /// <returns>The conversation unregistered. Returns null when the conversation with the specified ID is not found.</returns>
    public IKcpConversation? UnregisterConversation(uint id)
    {
        return UnregisterConversation(id, out _);
    }

    /// <summary>
    ///     Unregister a conversation or channel with the specified conversation ID.
    /// </summary>
    /// <param name="id">The conversation ID.</param>
    /// <param name="state">The user state.</param>
    /// <returns>The conversation unregistered. Returns null when the conversation with the specified ID is not found.</returns>
    public IKcpConversation? UnregisterConversation(uint id, out T? state)
    {
        if (!_transportClosed && !_disposed && _conversations.TryRemove(id, out var value))
        {
            value.Conversation.SetTransportClosed();
            state = value.State;
            if (_disposeAction is not null) _disposeAction.Invoke(state);
            return value.Conversation;
        }

        state = default;
        return default;
    }

    /// <inheritdoc />
    public ValueTask SendPacketAsync(Memory<byte> packet, IPEndPoint remoteEndpoint,
        CancellationToken cancellationToken = default)
    {
        if (_transportClosed || _disposed) return default;
        return _transport.SendPacketAsync(packet, remoteEndpoint, cancellationToken);
    }

    int IKcpBatchTransport.BatchCapacity => _batchTransport?.BatchCapacity ?? 0;

    bool IKcpBatchTransport.TryGetBatchSliceAndCommit(int requiredSize, IPEndPoint endpoint, Action<Memory<byte>> dataWriter)
    {
        if (_batchTransport is not null)
        {
            return _batchTransport.TryGetBatchSliceAndCommit(requiredSize, endpoint, dataWriter);
        }
        return false;
    }

    ValueTask IKcpBatchTransport.FlushBatchAsync(CancellationToken cancellationToken)
    {
        if (_batchTransport is not null)
            return _batchTransport.FlushBatchAsync(cancellationToken);
        return default;
    }

    private void CheckDispose()
    {
        if (_disposed) ThrowObjectDisposedException();
    }

    private static void ThrowObjectDisposedException()
    {
        throw new ObjectDisposedException(nameof(KcpMultiplexConnection<T>));
    }
}