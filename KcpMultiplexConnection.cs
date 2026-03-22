using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace HyacineCore.Server.Kcp.KcpSharp;

/// <summary>
///     Multiplex many channels or conversations over the same transport.
/// </summary>
/// <typeparam name="T">The state of the channel.</typeparam>
public sealed class KcpMultiplexConnection<T> : IKcpTransport, IKcpBatchTransport, IKcpConversation, IKcpMultiplexConnection<T>, IKcpPacketSink
{
    private readonly ConcurrentDictionary<long, (IKcpConversation Conversation, T? State)> _conversations = new();

    private readonly Action<T?>? _disposeAction;
    private readonly IKcpTransport _transport;
    private readonly IKcpBatchTransport? _batchTransport;
    private bool _disposed;
    private bool _transportClosed;

    /// <summary>
    ///     Construct a multiplexed connection over a transport.
    /// </summary>
    /// <param name="transport">The underlying transport.</param>
    public KcpMultiplexConnection(IKcpTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _batchTransport = transport as IKcpBatchTransport;
        _disposeAction = null;
    }

    /// <summary>
    ///     Construct a multiplexed connection over a transport.
    /// </summary>
    /// <param name="transport">The underlying transport.</param>
    /// <param name="disposeAction">The action to invoke when state object is removed.</param>
    public KcpMultiplexConnection(IKcpTransport transport, Action<T?>? disposeAction)
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
    ValueTask IKcpPacketSink.InputPacketAsync(ReadOnlyMemory<byte> packet, IPEndPoint remoteEndPoint, IDisposable? bufferOwner, CancellationToken cancellationToken)
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

        var id = BinaryPrimitives.ReadInt64BigEndian(span);
        if (_conversations.TryGetValue(id, out var value))
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
    public void Dispose()
    {
        if (_disposed) return;
        _transportClosed = true;
        _disposed = true;
        while (!_conversations.IsEmpty)
            foreach (var id in _conversations.Keys)
                if (_conversations.TryRemove(id, out var value))
                {
                    value.Conversation.Dispose();
                    if (_disposeAction is not null) _disposeAction.Invoke(value.State);
                }
    }

    /// <summary>
    ///     Determine whether the multiplex connection contains a conversation with the specified id.
    /// </summary>
    /// <param name="id">The conversation ID.</param>
    /// <returns>True if the multiplex connection contains the specified conversation. Otherwise false.</returns>
    public bool Contains(long id)
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
    public KcpRawChannel CreateRawChannel(long id, IPEndPoint remoteEndpoint, KcpRawChannelOptions? options = null)
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
            if (channel is not null) channel.Dispose();
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
    public KcpRawChannel CreateRawChannel(long id, IPEndPoint remoteEndpoint, T state,
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
            if (channel is not null) channel.Dispose();
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
    public KcpConversation CreateConversation(long id, IPEndPoint remoteEndpoint,
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
            if (conversation is not null) conversation.Dispose();
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
    public KcpConversation CreateConversation(long id, IPEndPoint remoteEndpoint, T state,
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
            if (conversation is not null) conversation.Dispose();
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
    public void RegisterConversation(IKcpConversation conversation, long id)
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
    public void RegisterConversation(IKcpConversation conversation, long id, T? state)
    {
        if (conversation is null) throw new ArgumentNullException(nameof(conversation));

        CheckDispose();
        var (addedConversation, _) = _conversations.GetOrAdd(id, (conversation, state));
        if (!ReferenceEquals(addedConversation, conversation))
            throw new InvalidOperationException("Duplicated conversation.");
        if (_disposed)
        {
            if (_conversations.TryRemove(id, out var value) && _disposeAction is not null)
                _disposeAction.Invoke(value.State);
            ThrowObjectDisposedException();
        }

        // Hook up the scheduler if this is a multiplexed socket transport
        if (_transport is KcpSocketTransport<KcpMultiplexConnection<T>> multiplexTransport && conversation is KcpConversation kcpConv)
        {
            multiplexTransport.ScheduleConversation(kcpConv);
        }
    }

    /// <summary>
    ///     Unregister a conversation or channel with the specified conversation ID.
    /// </summary>
    /// <param name="id">The conversation ID.</param>
    /// <returns>The conversation unregistered. Returns null when the conversation with the specified ID is not found.</returns>
    public IKcpConversation? UnregisterConversation(long id)
    {
        return UnregisterConversation(id, out _);
    }

    /// <summary>
    ///     Unregister a conversation or channel with the specified conversation ID.
    /// </summary>
    /// <param name="id">The conversation ID.</param>
    /// <param name="state">The user state.</param>
    /// <returns>The conversation unregistered. Returns null when the conversation with the specified ID is not found.</returns>
    public IKcpConversation? UnregisterConversation(long id, out T? state)
    {
        if (!_transportClosed && !_disposed && _conversations.TryRemove(id, out var value))
        {
            if (_transport is KcpSocketTransport<KcpMultiplexConnection<T>> multiplexTransport && value.Conversation is KcpConversation kcpConv)
            {
                multiplexTransport.UnscheduleConversation(kcpConv);
            }
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

    bool IKcpBatchTransport.TryGetBatchSlice(int requiredSize, out Memory<byte> slice, out int slotIndex)
    {
        if (_batchTransport is not null)
        {
            return _batchTransport.TryGetBatchSlice(requiredSize, out slice, out slotIndex);
        }
        slice = default;
        slotIndex = -1;
        return false;
    }

    void IKcpBatchTransport.CommitBatchSlot(int slotIndex, int actualSize, IPEndPoint endpoint)
    {
        _batchTransport?.CommitBatchSlot(slotIndex, actualSize, endpoint);
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