using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace KcpSharp;

/// <summary>
///     Represents a reliable data channel built on top of an underlying unreliable transport using the KCP protocol.
/// </summary>
public sealed partial class KcpConversation : IKcpConversation, IKcpExceptionProducer<KcpConversation>, IKcpPacketSink, IAsyncDisposable
{
    private readonly System.Threading.Lock _sndBufLock = new();
    private readonly System.Threading.Lock _rtoLock = new();
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
    private readonly KcpPacketHeader[] _cachedBatchHeaders;
    private readonly KcpBuffer[] _cachedBatchData;
    private readonly System.Threading.Lock _rcvBufLock = new();

    private readonly IKcpBufferPool _bufferPool;
    private readonly IKcpTransport _transport;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly uint? _id;

    private readonly int _mtu;
    private readonly int _mss;
    private readonly int _preBufferSize;
    private readonly int _postBufferSize;

    private volatile uint _snd_una;
    private volatile uint _snd_nxt;
    private volatile uint _rcv_nxt;
    private volatile uint _max_ack_sn;
    private volatile int _max_ack_has_value;

    private uint _ssthresh;

    private int _rx_rttval;
    private int _rx_srtt;
    private uint _rx_rto;
    private readonly uint _rx_minrto;

    private readonly uint _snd_wnd;
    private readonly uint _rcv_wnd;
    private uint _rmt_wnd;
    private uint _cwnd;
    /// <remarks>
    /// THREADING: Only accessed from RunUpdateOnActivationCore's single-threaded loop.
    /// Do NOT access from multiple threads without synchronization.
    /// </remarks>
    private volatile int _probe;

    private readonly uint _interval;
    private uint _ts_flush;

    private readonly bool _nodelay;
    private uint _ts_probe;
    private uint _probe_wait;

    private uint _incr;


    private readonly KcpSendReceiveQueueItemCacheUnsafe _receiveQueueItemCache;
    private readonly KcpSendQueue _sendQueue;
    private readonly KcpReceiveQueue _receiveQueue;

    private readonly KcpSendReceiveBufferItem[] _sndBufArray;
    private readonly (KcpBuffer, byte)[] _flushDequeueBuffer;

    private readonly KcpSendReceiveBufferItem[] _rcvBufArray;

    private readonly KcpAcknowledgeList _ackList;
    private (uint, uint)[] _cachedAckSnapshotArray;

    private readonly int _fastresend;
    private readonly int _fastlimit;
    private readonly bool _nocwnd;

    private readonly bool _keepAliveEnabled;
    private readonly uint _keepAliveInterval;
    private readonly uint _keepAliveGracePeriod;
    private uint _lastReceiveTick;
    private uint _lastSendTick;

    private readonly KcpReceiveWindowNotificationOptions? _receiveWindowNotificationOptions;
    private uint _ts_rcv_notify;
    private uint _ts_rcv_notify_wait;

    private KcpConversationUpdateActivation? _updateActivation;
    private CancellationTokenSource? _updateLoopCts;
    private int _disposed;




    private KcpRentedBuffer _cachedFlushBuffer;
    private KcpRentedBuffer _cachedAckFlushBuffer;
        private Func<Exception, KcpConversation, object?, bool>? _exceptionHandler;
    private object? _exceptionHandlerState;

    private const uint IKCP_RTO_MAX = 60000;
    private const int IKCP_THRESH_MIN = 2;
    private const uint IKCP_PROBE_INIT = 7000; // 7 secs to probe window size
    private const uint IKCP_PROBE_LIMIT = 120000; // up to 120 secs to probe window

    /// <summary>
    ///     Initializes a new instance of the <see cref="KcpConversation"/> class, establishing a reliable channel using the KCP protocol.
    /// </summary>
    /// <param name="remoteEndpoint">The endpoint of the remote peer.</param>
    /// <param name="transport">The underlying transport implementation used to send and receive raw data.</param>
    /// <param name="options">Configuration options for this <see cref="KcpConversation" />.</param>
    internal KcpConversation(IPEndPoint remoteEndpoint, IKcpTransport transport, KcpConversationOptions? options)
        : this(remoteEndpoint, transport, null, options)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="KcpConversation"/> class with a specific conversation ID.
    /// </summary>
    /// <param name="remoteEndpoint">The endpoint of the remote peer.</param>
    /// <param name="transport">The underlying transport implementation.</param>
    /// <param name="conversationId">The unique ID for this conversation, used for multiplexing. Must match the remote peer.</param>
    /// <param name="options">Configuration options for this <see cref="KcpConversation" />.</param>
    internal KcpConversation(IPEndPoint remoteEndpoint, IKcpTransport transport, uint? conversationId,
        KcpConversationOptions? options)
    {
        _bufferPool = options?.BufferPool ?? DefaultArrayPoolBufferAllocator.Default;
        _transport = transport;
        _remoteEndPoint = remoteEndpoint;
        _id = conversationId;

        if (options is null)
            _mtu = KcpConversationOptions.MtuDefaultValue;
        else if (options.Mtu < 50)
            throw new ArgumentOutOfRangeException(nameof(options.Mtu), "MTU must be at least 50 bytes.");
        else
            _mtu = options.Mtu;

        if (options?.ReceiveWindow > 65535)
            throw new ArgumentOutOfRangeException(
                nameof(options.ReceiveWindow),
                "ReceiveWindow must not exceed 65535 (KCP header limit).");
        if (options?.SendWindow > 65535)
            throw new ArgumentOutOfRangeException(
                nameof(options.SendWindow),
                "SendWindow must not exceed 65535 (KCP header limit).");

        _preBufferSize = options?.PreBufferSize ?? 0;
        _postBufferSize = options?.PostBufferSize ?? 0;
        if (_preBufferSize < 0)
            throw new ArgumentException("PreBufferSize must be a non-negative integer.", nameof(options));
        if (_postBufferSize < 0)
            throw new ArgumentException("PostBufferSize must be a non-negative integer.", nameof(options));
        if ((uint)(_preBufferSize + _postBufferSize) >= (uint)(_mtu - KcpGlobalVars.HEADER_LENGTH_WITHOUT_CONVID))
            throw new ArgumentException(
                "The sum of PreBufferSize and PostBufferSize is too large. There is not enough space in the packet for the KCP header.",
                nameof(options));
        if (conversationId.HasValue && (uint)(_preBufferSize + _postBufferSize) >=
            (uint)(_mtu - KcpGlobalVars.HEADER_LENGTH_WITH_CONVID))
            throw new ArgumentException(
                "The sum of PreBufferSize and PostBufferSize is too large. There is not enough space in the packet for the KCP header.",
                nameof(options));

        _mss = conversationId.HasValue
            ? _mtu - KcpGlobalVars.HEADER_LENGTH_WITH_CONVID
            : _mtu - KcpGlobalVars.HEADER_LENGTH_WITHOUT_CONVID;
        _mss = _mss - _preBufferSize - _postBufferSize;

        _ssthresh = (options is null || options.InitialSsthresh <= 0) ? 32 : (uint)options.InitialSsthresh;
        _cwnd = 1;

        _nodelay = options is not null && options.NoDelay;
        if (_nodelay)
        {
            _rx_rto = 30;
            _rx_minrto = 30;
        }
        else
        {
            _rx_rto = 200;
            _rx_minrto = 100;
        }

        _snd_wnd = options is null || options.SendWindow <= 0
            ? KcpConversationOptions.SendWindowDefaultValue
            : (uint)options.SendWindow;
        _rcv_wnd = options is null || options.ReceiveWindow <= 0
            ? KcpConversationOptions.ReceiveWindowDefaultValue
            : (uint)options.ReceiveWindow;
        _rmt_wnd = options is null || options.RemoteReceiveWindow <= 0
            ? KcpConversationOptions.RemoteReceiveWindowDefaultValue
            : (uint)options.RemoteReceiveWindow;
        _rcv_nxt = 0;

        _interval = options is null || options.UpdateInterval < 10
            ? KcpConversationOptions.UpdateIntervalDefaultValue
            : (uint)options.UpdateInterval;

        _fastresend = options is null ? 0 : options.FastResend;
        _fastlimit = 5;
        _nocwnd = options is not null && options.DisableCongestionControl;
        StreamMode = options is not null && options.StreamMode;
        _flushDequeueBuffer = new (KcpBuffer, byte)[Math.Min((int)_snd_wnd, 256)];

        int sndBufCapacity = 16;
        while (sndBufCapacity < _snd_wnd)
        {
            sndBufCapacity *= 2;
        }

        int rcvBufCapacity = 16;
        while (rcvBufCapacity < _rcv_wnd)
        {
            rcvBufCapacity *= 2;
        }
        _rcvBufArray = new KcpSendReceiveBufferItem[rcvBufCapacity];
        for (int i = 0; i < rcvBufCapacity; i++)
        {
            _rcvBufArray[i].IsEmpty = true;
        }

        _sndBufArray = new KcpSendReceiveBufferItem[sndBufCapacity];
        // Initialize the array elements' IsEmpty fields to true to denote "empty slot"
        for (int i = 0; i < sndBufCapacity; i++)
        {
            _sndBufArray[i].IsEmpty = true;
        }




        int maxWaitListSize = Math.Max((int)_rcv_wnd, 256);
        _updateActivation = new KcpConversationUpdateActivation((int)_interval, maxWaitListSize);
        _receiveQueueItemCache = new KcpSendReceiveQueueItemCacheUnsafe();
        _sendQueue = new KcpSendQueue(_bufferPool, _updateActivation, StreamMode,
            options is null || options.SendQueueSize <= 0
                ? KcpConversationOptions.SendQueueSizeDefaultValue
                : options.SendQueueSize, _mss);
        _receiveQueue = new KcpReceiveQueue(StreamMode,
            options is null || options.ReceiveQueueSize <= 0
                ? KcpConversationOptions.ReceiveQueueSizeDefaultValue
                : options.ReceiveQueueSize, _receiveQueueItemCache);
        int batchSizeForFlush = Math.Max((int)_snd_wnd, 32);
        _cachedBatchHeaders = new KcpPacketHeader[batchSizeForFlush];
        _cachedBatchData = new KcpBuffer[batchSizeForFlush];
        _ackList = new KcpAcknowledgeList(_sendQueue, (int)_rcv_wnd);
        _cachedAckSnapshotArray = new (uint, uint)[Math.Max(128, (int)_rcv_wnd)];

        _updateLoopCts = new CancellationTokenSource();

        _ts_flush = GetTimestamp();

        _lastSendTick = _ts_flush;
        Volatile.Write(ref _lastReceiveTick, _ts_flush);
        var keepAliveOptions = options?.KeepAliveOptions;
        if (keepAliveOptions is not null)
        {
            _keepAliveEnabled = true;
            _keepAliveInterval = (uint)keepAliveOptions.SendInterval;
            _keepAliveGracePeriod = (uint)keepAliveOptions.GracePeriod;
        }

        _receiveWindowNotificationOptions = options?.ReceiveWindowNotificationOptions;
        if (_receiveWindowNotificationOptions is not null)
        {
            _ts_rcv_notify_wait = 0;
            _ts_rcv_notify = _ts_flush + (uint)_receiveWindowNotificationOptions.InitialInterval;
        }

        _cachedFlushBuffer = _bufferPool.Rent(new KcpBufferPoolRentOptions(_mtu, true));
        _cachedAckFlushBuffer = _bufferPool.Rent(new KcpBufferPoolRentOptions(_mtu, true));

        try
        {
            RunUpdateOnActivation();
        }
        catch
        {
            _updateActivation?.Dispose();
            _cachedFlushBuffer.Dispose();
            _cachedAckFlushBuffer.Dispose();
            _updateLoopCts?.Dispose();
            _sendQueue?.Dispose();
            _receiveQueue?.Dispose();
            _flushSemaphore?.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Set the handler to invoke when exception is thrown during flushing packets to the transport. Return true in the
    ///     handler to ignore the error and continue running. Return false in the handler to abort the operation and mark the
    ///     transport as closed.
    /// </summary>
    /// <param name="handler">The exception handler.</param>
    /// <param name="state">The state object to pass into the exception handler.</param>
    public void SetExceptionHandler(Func<Exception, KcpConversation, object?, bool> handler, object? state)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        _exceptionHandler = handler;
        _exceptionHandlerState = state;
    }

    /// <summary>
    ///     Get the ID of the current conversation.
    /// </summary>
    public uint? ConversationId => _id;

    /// <summary>
    ///     Get whether the transport is marked as closed.
    /// </summary>
    public bool TransportClosed => Volatile.Read(ref _transportClosedFlag) == 1;

    /// <summary>
    ///     Gets the underlying transport.
    /// </summary>
    internal IKcpTransport Transport => _transport;

    private int _transportClosedFlag;

    /// <summary>
    ///     Get whether the conversation is in stream mode.
    /// </summary>
    public bool StreamMode { get; }

    /// <summary>
    ///     Get the available byte count and available segment count in the send queue.
    /// </summary>
    /// <param name="byteCount">The available byte count in the send queue.</param>
    /// <param name="segmentCount">The available segment count in the send queue.</param>
    /// <returns>True if the transport is not closed. Otherwise false.</returns>
    public bool TryGetSendQueueAvailableSpace(out int byteCount, out int segmentCount)
    {
        return _sendQueue.TryGetAvailableSpace(out byteCount, out segmentCount);
    }

    /// <summary>
    ///     Try to put message into the send queue.
    /// </summary>
    /// <param name="buffer">The content of the message.</param>
    /// <returns>
    ///     True if the message is put into the send queue. False if the message is too large to fit in the send queue, or
    ///     the transport is closed.
    /// </returns>
    /// <exception cref="ArgumentException">
    ///     The size of the message is larger than 256 * mtu, thus it can not be correctly
    ///     fragmented and sent. This exception is never thrown in stream mode.
    /// </exception>
    /// <exception cref="InvalidOperationException">The send or flush operation is initiated concurrently.</exception>
    public bool TrySend(ReadOnlySpan<byte> buffer)
    {
        return _sendQueue.TrySend(buffer, false, out _);
    }

    /// <summary>
    ///     Try to put message into the send queue.
    /// </summary>
    /// <param name="buffer">The content of the message.</param>
    /// <param name="allowPartialSend">
    ///     Whether partial sending is allowed in stream mode. This must not be true in non-stream
    ///     mode.
    /// </param>
    /// <param name="bytesWritten">
    ///     The number of bytes put into the send queue. This is always the same as the size of the
    ///     <paramref name="buffer" /> unless <paramref name="allowPartialSend" /> is set to true.
    /// </param>
    /// <returns>
    ///     True if the message is put into the send queue. False if the message is too large to fit in the send queue, or
    ///     the transport is closed.
    /// </returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="allowPartialSend" /> is set to true in non-stream mode. Or the size
    ///     of the message is larger than 256 * mtu, thus it can not be correctly fragmented and sent. This exception is never
    ///     thrown in stream mode.
    /// </exception>
    /// <exception cref="InvalidOperationException">The send or flush operation is initiated concurrently.</exception>
    public bool TrySend(ReadOnlySpan<byte> buffer, bool allowPartialSend, out int bytesWritten)
    {
        return _sendQueue.TrySend(buffer, allowPartialSend, out bytesWritten);
    }

    /// <summary>
    ///     Wait until the send queue contains at least <paramref name="minimumBytes" /> bytes of free space, and also
    ///     <paramref name="minimumSegments" /> available segments.
    /// </summary>
    /// <param name="minimumBytes">The number of bytes in the available space.</param>
    /// <param name="minimumSegments">The count of segments in the available space.</param>
    /// <param name="cancellationToken">The token to cancel this operation.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="minimumBytes" /> or <paramref name="minimumSegments" />
    ///     is larger than the total space of the send queue.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    ///     The <paramref name="cancellationToken" /> is fired before send operation
    ///     is completed. Or <see cref="CancelPendingSend(Exception?, CancellationToken)" /> is called before this operation is
    ///     completed.
    /// </exception>
    /// <returns>
    ///     A <see cref="ValueTask{Boolean}" /> that completes when there is enough space in the send queue. The result of
    ///     the task is false when the transport is closed.
    /// </returns>
    /// <remarks>WARNING: This method returns a ValueTask. Do not await it multiple times or store the ValueTask directly.</remarks>
    public ValueTask<bool> WaitForSendQueueAvailableSpaceAsync(int minimumBytes, int minimumSegments,
        CancellationToken cancellationToken = default)
    {
        return _sendQueue.WaitForAvailableSpaceAsync(minimumBytes, minimumSegments, cancellationToken);
    }

    /// <summary>
    ///     Put message into the send queue.
    /// </summary>
    /// <param name="buffer">The content of the message.</param>
    /// <param name="cancellationToken">The token to cancel this operation.</param>
    /// <exception cref="ArgumentException">
    ///     The size of the message is larger than 256 * mtu, thus it can not be correctly
    ///     fragmented and sent. This exception is never thrown in stream mode.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    ///     The <paramref name="cancellationToken" /> is fired before send operation
    ///     is completed. Or <see cref="CancelPendingSend(Exception?, CancellationToken)" /> is called before this operation is
    ///     completed.
    /// </exception>
    /// <exception cref="InvalidOperationException">The send or flush operation is initiated concurrently.</exception>
    /// <returns>
    ///     A <see cref="ValueTask{Boolean}" /> that completes when the entire message is put into the queue. The result
    ///     of the task is false when the transport is closed.
    /// </returns>
    /// <remarks>WARNING: This method returns a ValueTask. Do not await it multiple times or store the ValueTask directly.</remarks>
    public ValueTask<bool> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return _sendQueue.SendAsync(buffer, cancellationToken);
    }

    internal ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        return _sendQueue.WriteAsync(buffer, cancellationToken);
    }

    /// <summary>
    ///     Cancel the current send operation or flush operation.
    /// </summary>
    /// <returns>True if the current operation is canceled. False if there is no active send operation.</returns>
    public bool CancelPendingSend()
    {
        return _sendQueue.CancelPendingOperation(null, default);
    }

    /// <summary>
    ///     Cancel the current send operation or flush operation.
    /// </summary>
    /// <param name="innerException">
    ///     The inner exception of the <see cref="OperationCanceledException" /> thrown by the
    ///     <see cref="SendAsync(ReadOnlyMemory{byte}, CancellationToken)" /> method or
    ///     <see cref="FlushAsync(CancellationToken)" /> method.
    /// </param>
    /// <param name="cancellationToken">
    ///     The <see cref="CancellationToken" /> in the <see cref="OperationCanceledException" />
    ///     thrown by the <see cref="SendAsync(ReadOnlyMemory{byte}, CancellationToken)" /> method or
    ///     <see cref="FlushAsync(CancellationToken)" /> method.
    /// </param>
    /// <returns>True if the current operation is canceled. False if there is no active send operation.</returns>
    public bool CancelPendingSend(Exception? innerException, CancellationToken cancellationToken)
    {
        return _sendQueue.CancelPendingOperation(innerException, cancellationToken);
    }

    /// <summary>
    ///     Gets the count of bytes not yet sent to the remote host or not acknowledged by the remote host.
    ///     Note: This value is an approximation. It may be slightly larger than the actual unflushed bytes
    ///     due to concurrent thread accesses.
    /// </summary>
    public long UnflushedBytes => _sendQueue.GetUnflushedBytes();

    /// <summary>
    ///     Wait until all messages are sent and acknowledged by the remote host, as well as all the acknowledgements are sent.
    /// </summary>
    /// <param name="cancellationToken">The token to cancel this operation.</param>
    /// <exception cref="OperationCanceledException">
    ///     The <paramref name="cancellationToken" /> is fired before send operation
    ///     is completed. Or <see cref="CancelPendingSend(Exception?, CancellationToken)" /> is called before this operation is
    ///     completed.
    /// </exception>
    /// <exception cref="InvalidOperationException">The send or flush operation is initiated concurrently.</exception>
    /// <exception cref="ObjectDisposedException">The <see cref="KcpConversation" /> instance is disposed.</exception>
    /// <returns>
    ///     A <see cref="ValueTask{Boolean}" /> that completes when the all messages are sent and acknowledged. The result
    ///     of the task is false when the transport is closed.
    /// </returns>
    /// <remarks>WARNING: This method returns a ValueTask. Do not await it multiple times or store the ValueTask directly.</remarks>
    public ValueTask<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        return _sendQueue.FlushAsync(cancellationToken);
    }

    /// <summary>
    ///     Flushes the send queue.
    ///     Unlike <see cref="FlushAsync"/> which flushes individual packets, this method is
    ///     optimized for stream-oriented operations where data might be buffered and
    ///     flushed together to reduce overhead.
    /// </summary>
    internal ValueTask FlushForStreamAsync(CancellationToken cancellationToken)
    {
        return _sendQueue.FlushForStreamAsync(cancellationToken);
    }

    private async ValueTask<bool> TrySendOrBatchAsync(
        Memory<byte> buffer, int size, int postBufferSize,
        IKcpBatchTransport? batch, CancellationToken cancellationToken)
    {
        try
        {
            bool sentDirectly = await SendOrBatch(buffer, size, postBufferSize, batch, cancellationToken).ConfigureAwait(false);
            if (sentDirectly)
                _lastSendTick = GetTimestamp();
            return true;
        }
        catch (Exception ex)
        {
            return HandleFlushException(ex);
        }
    }

    private async ValueTask<bool> SendOrBatch(
        Memory<byte> buffer, int size, int postBufferSize,
        IKcpBatchTransport? batch, CancellationToken cancellationToken)
    {
        var packet = buffer.Slice(0, size + postBufferSize);
        
        if (batch != null)
        {
            if (batch.TryGetBatchSliceAndCommit(packet.Length, _remoteEndPoint, slice => packet.CopyTo(slice)))
            {
                return false;
            }
            // Batch is full or packet is too large for batch slot. Flush existing batched packets first to preserve strict ordering.
            await batch.FlushBatchAsync(cancellationToken).ConfigureAwait(false);
            _lastSendTick = GetTimestamp();
            
            // Try again after flush
            if (batch.TryGetBatchSliceAndCommit(packet.Length, _remoteEndPoint, slice => packet.CopyTo(slice)))
            {
                return false;
            }
        }
        
        // Either batch is null, or packet size > _mtu (TryGetBatchSlice returns false even when empty).
        // If batch was not null, it was just flushed above, so ordering is still preserved.
        await _transport
            .SendPacketAsync(packet, _remoteEndPoint, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        s_currentObject = this;

        // 1. Fast-path flush for ACKs only, outside of the semaphore.
        // This avoids delaying time-critical ACKs when data flush is congested.
        bool ackPushed = await FlushAcksFastAsync(cancellationToken).ConfigureAwait(false);

        // 2. Main data flush loop (semaphore protected)
        await FlushCore2Async(ackPushed, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> FlushAcksFastAsync(CancellationToken cancellationToken)
    {
        if (TransportClosed) return false;

        int snapshotLimit = Math.Min(_ackList.Count, (int)_rcv_wnd);
        if (snapshotLimit <= 0) return false;

        if (snapshotLimit < _ackList.Count)
        {
            KcpMetrics.AckSnapshotPartial.Add(1);
        }

        // Check if we need to resize the cached array
        if (_cachedAckSnapshotArray.Length < snapshotLimit)
        {
            _cachedAckSnapshotArray = new (uint, uint)[Math.Max(_cachedAckSnapshotArray.Length * 2, snapshotLimit)];
        }
        var ackSnapshotArray = _cachedAckSnapshotArray;
        try
        {
            var ackCount = _ackList.SnapshotAndClear(ackSnapshotArray.AsSpan(0, snapshotLimit));
            if (ackCount == 0)
            {
                return false;
            }

            var batch = _transport as IKcpBatchTransport;
            var preBufferSize = _preBufferSize;
            var postBufferSize = _postBufferSize;
            int packetHeaderSize = _id.HasValue
                ? KcpGlobalVars.HEADER_LENGTH_WITH_CONVID
                : KcpGlobalVars.HEADER_LENGTH_WITHOUT_CONVID;
            var sizeLimitBeforePostBuffer = _mtu - _postBufferSize;

            var windowSize = (ushort)GetUnusedReceiveWindow();
            var unacknowledged = _rcv_nxt;

            // Use the dedicated pre-allocated buffer to flush ACKs without holding _flushSemaphore
            var buffer = _cachedAckFlushBuffer.Memory;

            var size = preBufferSize;
            if (preBufferSize > 0)
            {
                buffer.Span.Slice(0, size).Clear();
            }

            for (int i = 0; i < ackCount; i++)
            {
                var (serialNumber, timestamp) = ackSnapshotArray[i];
                if (size + packetHeaderSize > sizeLimitBeforePostBuffer)
                {
                    if (postBufferSize > 0)
                    {
                        buffer.Span.Slice(size, postBufferSize).Clear();
                    }
                    if (!await TrySendOrBatchAsync(buffer, size, postBufferSize, batch, cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }
                    size = preBufferSize;
                    if (preBufferSize > 0)
                    {
                        buffer.Span.Slice(0, size).Clear();
                    }
                }

                windowSize = (ushort)GetUnusedReceiveWindow();
                KcpPacketHeader header = new(KcpCommand.Ack, 0, windowSize, timestamp, serialNumber, unacknowledged);
                header.EncodeHeader(_id, 0, buffer.Span.Slice(size), out var bytesWritten);
                size += bytesWritten;
            }

            if (size > preBufferSize)
            {
                if (postBufferSize > 0)
                {
                    buffer.Span.Slice(size, postBufferSize).Clear();
                }
                await TrySendOrBatchAsync(buffer, size, postBufferSize, batch, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            // ArrayPool is no longer used, so nothing to return
        }
    }

    [AsyncMethodBuilder(typeof(KcpFlushAsyncMethodBuilder))]
    private async ValueTask FlushCore2Async(bool ackPushed, CancellationToken cancellationToken)
    {
        await _flushSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TransportClosed) return;

            var batch = _transport as IKcpBatchTransport;
            var preBufferSize = _preBufferSize;
            var postBufferSize = _postBufferSize;
            int packetHeaderSize = _id.HasValue
                ? KcpGlobalVars.HEADER_LENGTH_WITH_CONVID
                : KcpGlobalVars.HEADER_LENGTH_WITHOUT_CONVID;
            var sizeLimitBeforePostBuffer = _mtu - _postBufferSize;
            var anyPacketSent = ackPushed; // Consider ACKs as packet sent

            var windowSize = (ushort)GetUnusedReceiveWindow();
            var unacknowledged = _rcv_nxt;

            var buffer = _cachedFlushBuffer.Memory;
            var size = preBufferSize;

            if (preBufferSize > 0)
            {
                buffer.Span.Slice(0, size).Clear();
            }

            var current = GetTimestamp();

            // calculate window size
            var cwnd = Math.Min(_snd_wnd, Volatile.Read(ref _rmt_wnd));
            if (!_nocwnd) cwnd = Math.Min(_cwnd, cwnd);

#pragma warning disable CS0420 // volatile ref bypass ok for Volatile.Read
            // move data from snd_queue to snd_buf
            int availableSlots = TimeDiff(Volatile.Read(ref _snd_una) + cwnd, Volatile.Read(ref _snd_nxt));
            while (availableSlots > 0)
            {
                int batchSize = Math.Min(availableSlots, _flushDequeueBuffer.Length);
                var dequeueBufferArray = _flushDequeueBuffer;

                int dequeuedCount = 0;
                int processedCount = 0;

                try
                {
                    lock (_sndBufLock)
                    {
                        if (TransportClosed)
                        {
                            return;
                        }

                        // Re-verify inside lock before popping to avoid data loss
                        int actualAvailable = TimeDiff(_snd_una + cwnd, _snd_nxt);
                        int toProcess = Math.Min(batchSize, actualAvailable);

                        if (toProcess <= 0)
                        {
                            break;
                        }

                        dequeuedCount = _sendQueue.TryDequeueBatch(dequeueBufferArray.AsSpan(0, toProcess), toProcess);
                        if (dequeuedCount == 0)
                        {
                            break;
                        }

                        toProcess = dequeuedCount; // we only process what we successfully pulled

                        for (int i = 0; i < toProcess; i++)
                        {
                            var (data, fragment) = dequeueBufferArray[i];
                            uint currentSn = _snd_nxt++;
                            int index = (int)(currentSn % (uint)_sndBufArray.Length);

                            // The slot must be empty
                            // If it's not, we have a serious issue (window size exceeded capacity).
                            if (!_sndBufArray[index].IsEmpty)
                            {
                                throw new InvalidOperationException($"CRITICAL: Ring buffer aliasing detected! Overwriting unacknowledged segment. SN: {currentSn}, Index: {index}, Capacity: {_sndBufArray.Length}, SND_UNA: {_snd_una}, SND_NXT: {_snd_nxt}. Congestion window bounds exceeded.");
                            }
                            _sndBufArray[index] = new KcpSendReceiveBufferItem
                            {
                                Data = data,
                                Segment = new KcpPacketHeader(KcpCommand.Push, fragment, windowSize, current, currentSn, unacknowledged),
                                Stats = new KcpSendSegmentStats(current, _rx_rto, 0, 0),
                                IsEmpty = false
                            };

                            processedCount++;
                        }
                    }
                }
                finally
                {
                    // Prevent leaking the remaining un-processed elements in this batch chunk
                    if (processedCount < dequeuedCount)
                    {
                        for (int j = processedCount; j < dequeuedCount; j++)
                        {
                            dequeueBufferArray[j].Item1.Release();
                        }
                    }

                    // Clear references so memory can be GC'd when done
                    Array.Clear(dequeueBufferArray, 0, dequeuedCount);
                }

                if (dequeuedCount < batchSize) break;

                // Recalculate available slots dynamically in case of concurrent ACKs
                availableSlots = TimeDiff(Volatile.Read(ref _snd_una) + cwnd, Volatile.Read(ref _snd_nxt));
            }
#pragma warning restore CS0420

            // calculate resent
            var resent = _fastresend > 0 ? (uint)_fastresend : 0xffffffff;
            var rtomin = !_nodelay ? _rx_rto >> 3 : 0;

            // flush data segments
            var lost = false;
            var change = false;

            uint? nextSn = null;

            unacknowledged = _rcv_nxt;

            lock (_sndBufLock)
            {
                nextSn = _snd_una;
            }

            try
            {
                long flushStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                long flushBudgetTicks = TimeSpan.FromMilliseconds(2).Ticks; // 2ms budget before yielding

                while (nextSn.HasValue && TimeDiff(nextSn.Value, _snd_nxt) < 0 && !TransportClosed)
                {
                    bool needsFlush = false;
                    var batchHeaders = _cachedBatchHeaders;
                    var batchData = _cachedBatchData;
                    int BatchSize = _cachedBatchHeaders.Length;
                    int batchCount = 0;

                    lock (_sndBufLock)
                    {
                        unacknowledged = _rcv_nxt;
                        int processed = 0;

                        #pragma warning disable CS0420
                        bool hasMaxAck = Volatile.Read(ref _max_ack_has_value) == 1;
                        uint maxAckSn = Volatile.Read(ref _max_ack_sn);
#pragma warning restore CS0420

                        while (nextSn.HasValue && TimeDiff(nextSn.Value, _snd_nxt) < 0 && processed < BatchSize)
                        {
                            uint sn = nextSn.Value;
                            int index = (int)(sn % (uint)_sndBufArray.Length);
                            ref var item = ref _sndBufArray[index];

                            // Check if this slot is currently occupied and matches the expected serial number
                            if (item.IsEmpty || item.Segment.SerialNumber != sn)
                            {
                                // Slot empty or belongs to a different sequence (already ACKed and not replaced, or old)
                                nextSn = sn + 1;
                                continue;
                            }

                            processed++;
                            var needsend = false;
                            ref var stats = ref item.Stats;

                            // Apply FastAck deferral
                            if (hasMaxAck && TimeDiff(maxAckSn, sn) > 0)
                            {
                                if (stats.TransmitCount <= _fastlimit || _fastlimit == 0)
                                {
                                    // We use the distance from maxAck as a proxy for FastAck count,
                                    // or just set it to the distance directly if it's larger.
                                    uint distance = (uint)TimeDiff(maxAckSn, sn);
                                    if (distance > stats.FastAck)
                                    {
                                        stats = new KcpSendSegmentStats(stats.ResendTimestamp, stats.Rto, distance, stats.TransmitCount);
                                    }
                                }
                            }

                            if (stats.TransmitCount == 0)
                            {
                                needsend = true;
                            }
                            else if (TimeDiff(current, stats.ResendTimestamp) >= 0)
                            {
                                needsend = true;
                            }
                            else if (stats.FastAck > resent)
                            {
                                if (stats.TransmitCount <= _fastlimit || _fastlimit == 0)
                                {
                                    needsend = true;
                                }
                            }

                            if (needsend)
                            {
                                var data = item.Data;
                                var need = packetHeaderSize + data.Length;

                                if (size + need > sizeLimitBeforePostBuffer)
                                {
                                    needsFlush = true;
                                    break;
                                }

                                bool incrementRetransmission = false;
                                bool incrementFastRetransmission = false;

                                if (stats.TransmitCount == 0)
                                {
                                    stats = new KcpSendSegmentStats(current + stats.Rto + rtomin,
                                        _rx_rto, stats.FastAck, stats.TransmitCount + 1);
                                }
                                else if (TimeDiff(current, stats.ResendTimestamp) >= 0)
                                {
                                    var rto = stats.Rto;
                                    if (!_nodelay) rto += Math.Max(stats.Rto, _rx_rto);
                                    else rto += rto / 2;

                                    stats = new KcpSendSegmentStats(current + rto, rto, stats.FastAck, stats.TransmitCount + 1);
                                    incrementRetransmission = true;
                                    lost = true;
                                }
                                else if (stats.FastAck > resent)
                                {
                                    stats = new KcpSendSegmentStats(current, stats.Rto, 0, stats.TransmitCount + 1);
                                    incrementFastRetransmission = true;
                                    change = true;
                                }

                                var header = DuplicateHeader(ref item.Segment, current, windowSize, unacknowledged);

                                if (incrementRetransmission) KcpMetrics.RetransmissionCount.Add(1);
                                if (incrementFastRetransmission) KcpMetrics.FastRetransmissionCount.Add(1);

                                // Snapshot for out-of-lock encoding
                                batchHeaders[batchCount] = header;
                                batchData[batchCount] = data;
                                batchCount++;
                            }

                            nextSn = sn + 1;
                        }

                        if (!needsFlush && (!nextSn.HasValue || TimeDiff(nextSn.Value, _snd_nxt) >= 0))
                        {
                            nextSn = null;
                        }
                    } // Unlock _sndBufLock

                    // Encode outside lock (H-1)
                    for (int i = 0; i < batchCount; i++)
                    {
                        var header = batchHeaders[i];
                        var data = batchData[i];

                        header.EncodeHeader(_id, data.Length, buffer.Span.Slice(size), out var bytesWritten);
                        size += bytesWritten;

                        if (data.Length > 0)
                        {
                            data.DataRegion.CopyTo(buffer.Slice(size));
                            size += data.Length;
                        }
                    }

                    // ArrayPool no longer used, using pre-allocated arrays

                    // 2. Flush asynchronously outside the lock
                    if (needsFlush)
                    {
                        if (postBufferSize > 0)
                        {
                            buffer.Span.Slice(size, postBufferSize).Clear();
                        }
                        if (!await TrySendOrBatchAsync(buffer, size, postBufferSize, batch, cancellationToken).ConfigureAwait(false)) return;
                        size = preBufferSize;
                        if (preBufferSize > 0)
                        {
                            buffer.Span.Slice(0, size).Clear();
                        }
                        anyPacketSent = true;
                    }
                    else if (nextSn.HasValue)
                    {
                        // Yield if we still have more nodes to process in the next batch,
                        // but only if we have exhausted our time budget.
                        if (System.Diagnostics.Stopwatch.GetTimestamp() - flushStartTicks >= flushBudgetTicks)
                        {
                            await Task.Yield();
                            flushStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                        }
                    }
                }
            }
            finally
            {
            }

            unacknowledged = _rcv_nxt;

            // probe window size (if remote window size equals zero)
            if (Volatile.Read(ref _rmt_wnd) == 0)
            {
                if (_probe_wait == 0)
                {
                    _probe_wait = IKCP_PROBE_INIT;
                    _ts_probe = current + _probe_wait;
                }
                else
                {
                    if (TimeDiff(current, _ts_probe) >= 0)
                    {
                        if (_probe_wait < IKCP_PROBE_INIT) _probe_wait = IKCP_PROBE_INIT;
                        _probe_wait += _probe_wait / 2;
                        if (_probe_wait > IKCP_PROBE_LIMIT) _probe_wait = IKCP_PROBE_LIMIT;
                        _ts_probe = current + _probe_wait;
                        System.Threading.Interlocked.Or(ref _probe, (int)KcpProbeType.AskSend);
                    }
                }
            }
            else
            {
                _ts_probe = 0;
                _probe_wait = 0;
            }

            // flush window probing command
            var processedProbe = (KcpProbeType)System.Threading.Interlocked.Exchange(ref _probe, 0);
            if ((processedProbe & KcpProbeType.AskSend) != 0)
            {
                if (size + packetHeaderSize > sizeLimitBeforePostBuffer)
                {
                    buffer.Span.Slice(size, postBufferSize).Clear();
                    if (!await TrySendOrBatchAsync(buffer, size, postBufferSize, batch, cancellationToken).ConfigureAwait(false)) return;
                    size = preBufferSize;
                    buffer.Span.Slice(0, size).Clear();
                    anyPacketSent = true;
                }

                windowSize = (ushort)GetUnusedReceiveWindow();
                KcpPacketHeader header = new(KcpCommand.WindowProbe, 0, windowSize, 0, 0, unacknowledged);
                header.EncodeHeader(_id, 0, buffer.Span.Slice(size), out var bytesWritten);
                size += bytesWritten;
            }

            // flush window probing response
            if ((processedProbe & KcpProbeType.AskTell) != 0)
            {
                if (size + packetHeaderSize > sizeLimitBeforePostBuffer)
                {
                    buffer.Span.Slice(size, postBufferSize).Clear();
                    if (!await TrySendOrBatchAsync(buffer, size, postBufferSize, batch, cancellationToken).ConfigureAwait(false)) return;
                    size = preBufferSize;
                    buffer.Span.Slice(0, size).Clear();
                    anyPacketSent = true;
                }

                windowSize = (ushort)GetUnusedReceiveWindow();
                KcpPacketHeader header = new(KcpCommand.WindowSize, 0, windowSize, 0, 0, unacknowledged);
                header.EncodeHeader(_id, 0, buffer.Span.Slice(size), out var bytesWritten);
                size += bytesWritten;
            }

            // periodic window notification
            if (!anyPacketSent && ShouldSendWindowSize(current) && ((KcpProbeType)_probe & KcpProbeType.AskTell) == 0)
            {
                if (size + packetHeaderSize > sizeLimitBeforePostBuffer)
                {
                    buffer.Span.Slice(size, postBufferSize).Clear();
                    if (!await TrySendOrBatchAsync(buffer, size, postBufferSize, batch, cancellationToken).ConfigureAwait(false)) return;
                    size = preBufferSize;
                    buffer.Span.Slice(0, size).Clear();
                }

                windowSize = (ushort)GetUnusedReceiveWindow();
                KcpPacketHeader header = new(KcpCommand.WindowSize, 0, windowSize, 0, 0, unacknowledged);
                header.EncodeHeader(_id, 0, buffer.Span.Slice(size), out var bytesWritten);
                size += bytesWritten;
            }

            // flush remaining segments
            if (size > preBufferSize)
            {
                if (postBufferSize > 0)
                {
                    buffer.Span.Slice(size, postBufferSize).Clear();
                }
                if (!await TrySendOrBatchAsync(buffer, size, postBufferSize, batch, cancellationToken).ConfigureAwait(false)) return;
                anyPacketSent = true;
            }
            size = preBufferSize;
            if (preBufferSize > 0)
            {
                buffer.Span.Slice(0, size).Clear();
            }

            // _probe was cleared securely at the start of probe evaluation

            if (batch is not null)
            {
                var committed = batch as IKcpBatchTransport2;
                if (committed?.AnyPacketCommitted == true)
                {
                    anyPacketSent = true;
                }
                await batch.FlushBatchAsync(cancellationToken).ConfigureAwait(false);
                _lastSendTick = current;
            }

            // update window
            var updatedCwnd = _cwnd;
            var incr = _incr;

            // update sshthresh
            if (lost)
            {
                _ssthresh = Math.Max(cwnd / 2, IKCP_THRESH_MIN);
                updatedCwnd = 1;
                incr = (uint)_mss;
            }
            else if (change)
            {
                var inflight = _snd_nxt - _snd_una;
                _ssthresh = Math.Max(inflight / 2, IKCP_THRESH_MIN);
                updatedCwnd = _ssthresh + resent;
                incr = updatedCwnd * (uint)_mss;
            }

            if (updatedCwnd < 1)
            {
                updatedCwnd = 1;
                incr = (uint)_mss;
            }

            if (updatedCwnd > Volatile.Read(ref _rmt_wnd)) updatedCwnd = Volatile.Read(ref _rmt_wnd);

            _cwnd = updatedCwnd;
            _incr = incr;

            // send keep-alive
            if (_keepAliveEnabled)
                if ((uint)TimeDiff(current, _lastSendTick) > _keepAliveInterval)
                {
                    if (size + packetHeaderSize > sizeLimitBeforePostBuffer)
                    {
                        if (postBufferSize > 0)
                        {
                            buffer.Span.Slice(size, postBufferSize).Clear();
                        }
                        if (!await TrySendOrBatchAsync(buffer, size, postBufferSize, batch, cancellationToken).ConfigureAwait(false)) return;
                        size = preBufferSize;
                        if (preBufferSize > 0)
                        {
                            buffer.Span.Slice(0, size).Clear();
                        }
                    }

                    windowSize = (ushort)GetUnusedReceiveWindow();
                    KcpPacketHeader header = new(KcpCommand.WindowSize, 0, windowSize, 0, 0, unacknowledged);
                    header.EncodeHeader(_id, 0, buffer.Span.Slice(size), out var bytesWritten);
                    size += bytesWritten;
                    if (postBufferSize > 0)
                    {
                        buffer.Span.Slice(size, postBufferSize).Clear();
                    }
                    if (!await TrySendOrBatchAsync(buffer, size, postBufferSize, batch, cancellationToken).ConfigureAwait(false)) return;
                    if (batch is not null)
                        await batch.FlushBatchAsync(cancellationToken).ConfigureAwait(false);
                    size = preBufferSize;
                    if (preBufferSize > 0)
                    {
                        buffer.Span.Slice(0, size).Clear();
                    }
                }

        }
        catch (Exception ex)
        {
            HandleFlushException(ex);
        }
        finally
        {
            try
            {
                _flushSemaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // Ignore ObjectDisposedException
            }
        }
    }

    private bool ShouldSendWindowSize(uint current)
    {
        var options = _receiveWindowNotificationOptions;
        if (options is null) return false;

        if (TimeDiff(current, _ts_rcv_notify) < 0) return false;

        var initial = (uint)options.InitialInterval;
        var maximum = (uint)options.MaximumInterval;
        if (_ts_rcv_notify_wait < initial)
            _ts_rcv_notify_wait = initial;
        else if (_ts_rcv_notify_wait >= maximum)
            _ts_rcv_notify_wait = maximum;
        else
            _ts_rcv_notify_wait = Math.Min(maximum, _ts_rcv_notify_wait + _ts_rcv_notify_wait / 2);
        _ts_rcv_notify = current + _ts_rcv_notify_wait;

        return true;
    }

    private static KcpPacketHeader DuplicateHeader(ref KcpPacketHeader header, uint timestamp, ushort windowSize,
        uint unacknowledged)
    {
        return new KcpPacketHeader(header.Command, header.Fragment, windowSize, timestamp, header.SerialNumber,
            unacknowledged);
    }

    private uint GetUnusedReceiveWindow()
    {
        var count = (uint)_receiveQueue.GetQueueSize();
        if (count < _rcv_wnd) return _rcv_wnd - count;
        return 0;
    }

    private Task? _updateLoopTask;

    private void RunUpdateOnActivation()
    {
        _updateLoopTask = Task.Run(RunUpdateOnActivationCore_Wrapped);
    }



    private async Task RunUpdateOnActivationCore_Wrapped()
    {
        try
        {
            await RunUpdateOnActivationCore().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected shutdown
        }
        catch (Exception ex)
        {
            try { HandleFlushException(ex); } catch { SetTransportClosed(); }
        }
    }

    private async Task RunUpdateOnActivationCore()
    {
        var cancellationToken = _updateLoopCts?.Token ?? new CancellationToken(true);
        var activation = _updateActivation;
        if (activation is null) return;

        while (!cancellationToken.IsCancellationRequested)
        {
            var anyUpdate = false;
            var current = GetTimestamp();

            long drainStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            long drainBudgetTicks = TimeSpan.FromMilliseconds(2).Ticks;

            while (true)
            {
                var waitTask = activation.WaitAsync(cancellationToken);
                KcpConversationUpdateNotification notification;
                if (!waitTask.IsCompletedSuccessfully)
                {
                    notification = await waitTask.ConfigureAwait(false);
                }
                else
                {
                    notification = waitTask.Result;
                }

                using (notification)
                {
                    if (TransportClosed) return;

                    var packet = notification.Packet;
                    var rawOwner = notification.BufferOwner;

                    if (!packet.IsEmpty)
                    {
                        try
                        {
                            anyUpdate |= SetInput(packet.Span, rawOwner, current);
                        }
                        catch (Exception ex)
                        {
                            if (!HandleFlushException(ex)) return;
                        }
                    }

                    if (TransportClosed) return;

                    anyUpdate |= notification.TimerNotification;
                }

                if (!activation.HasPendingPackets) break;

                // Yield if we have exhausted our time budget draining pending packets
                if (System.Diagnostics.Stopwatch.GetTimestamp() - drainStartTicks >= drainBudgetTicks)
                {
                    await Task.Yield();
                    drainStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }

            try
            {
                if (anyUpdate) await UpdateCoreAsync(cancellationToken, current).ConfigureAwait(false);

                // Trigger the unified flush loop locally if any updates happened or ACKs pending
                if (anyUpdate || _sendQueue.GetUnflushedBytes() > 0 || _ackList.Count > 0)
                {
                    await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!HandleFlushException(ex)) break;
            }

            if (_keepAliveEnabled && (uint)TimeDiff(current, Volatile.Read(ref _lastReceiveTick)) > _keepAliveGracePeriod)
                SetTransportClosed();
        }
    }

    private ValueTask UpdateCoreAsync(CancellationToken cancellationToken, uint current)
    {
        int slap = TimeDiff(current, _ts_flush);
        if (slap > 10000 || slap < -10000)
        {
            _ts_flush = current;
            slap = 0;
        }

        if (slap >= 0 || _nodelay)
        {
            _ts_flush += _interval;
            if (TimeDiff(current, _ts_flush) >= 0) _ts_flush = current + _interval;

        }

        return default;
    }

    private bool HandleFlushException(Exception ex)
    {
        var handler = _exceptionHandler;
        var state = _exceptionHandlerState;
        var result = false;
        if (handler is not null)
            try
            {
                result = handler.Invoke(ex, this, state);
            }
            catch
            {
                result = false;
            }

        if (!result) SetTransportClosed();
        return result;
    }

    ValueTask IKcpPacketSink.InputPacketAsync(ReadOnlyMemory<byte> packet, IPEndPoint remoteEndPoint, System.Buffers.IMemoryOwner<byte>? bufferOwner, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                bufferOwner?.Dispose();
                return new ValueTask(Task.FromCanceled(cancellationToken));
            }

            int packetHeaderSize = _id.HasValue
                ? KcpGlobalVars.HEADER_LENGTH_WITH_CONVID
                : KcpGlobalVars.HEADER_LENGTH_WITHOUT_CONVID;
            if (packet.Length < packetHeaderSize)
            {
                bufferOwner?.Dispose();
                return default;
            }

            ReadOnlySpan<byte> packetSpan = packet.Span;
            if (_id.HasValue)
            {
                var conversationId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Span);
                if (conversationId != _id.GetValueOrDefault())
                {
                    bufferOwner?.Dispose();
                    return default;
                }
                packetSpan = packetSpan.Slice(4);
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(packetSpan.Slice(16));
            if (length > (uint)(packetSpan.Length - 20)) // implicitly checked for (int)length < 0
            {
                bufferOwner?.Dispose();
                return default;
            }

            bool hasPush = false;
                        var currentSpan = packetSpan;
            while (currentSpan.Length >= 20)
            {
                KcpCommand cmd = (KcpCommand)currentSpan[0];
                int pktLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(currentSpan.Slice(16));

                if (cmd == KcpCommand.Push)
                {
                    hasPush = true;
                    break;
                }

                if (currentSpan.Length >= 20 + pktLength)
                {
                    currentSpan = currentSpan.Slice(20 + pktLength);
                }
                else
                {
                    break;
                }
            }

            if (!hasPush)
            {
                // Pure ACK/Probe packet, process inline to avoid queue hop
                uint current = GetTimestamp();
                bool mutated = ProcessInlineAcksAndProbes(packet.Span, current);
                if (mutated)
                {
                    _updateActivation?.Notify();
                }
                bufferOwner?.Dispose();
                return default;
            }

            var activation = _updateActivation;
            if (activation is null)
            {
                bufferOwner?.Dispose();
                return default;
            }

            return activation.InputPacketAsync(packet, bufferOwner, cancellationToken);
        }
        catch
        {
            bufferOwner?.Dispose();
            throw;
        }
    }

        private bool ProcessInlineAcksAndProbes(ReadOnlySpan<byte> packet, uint current)
    {
        var packetHeaderSize = _id.HasValue
            ? KcpGlobalVars.HEADER_LENGTH_WITH_CONVID
            : KcpGlobalVars.HEADER_LENGTH_WITHOUT_CONVID;

        uint maxack = 0, latest_ts = 0;
        var flag = false;
        var mutated = false;

        try
        {
            if (_id.HasValue)
            {
                if (packet.Length < 4 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet) != _id.GetValueOrDefault())
                    return mutated;
                packet = packet.Slice(4);
            }
            int segmentHeaderSize = 20;

            while (true)
            {
                if (packet.Length < segmentHeaderSize) break;

                KcpPacketHeader header;
                int length;
                try
                {
                    header = KcpPacketHeader.Parse(packet);
                    length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(16));

                    packet = packet.Slice(segmentHeaderSize);
                    if ((uint)length > (uint)packet.Length) return mutated;
                }
                catch
                {
                    return mutated;
                }

                Volatile.Write(ref _lastReceiveTick, current);
                var newRmtWnd = header.WindowSize;
                if (newRmtWnd != Volatile.Read(ref _rmt_wnd))
                {
                    Volatile.Write(ref _rmt_wnd, newRmtWnd);
                }
                mutated = HandleUnacknowledged(header.Unacknowledged) | mutated;

                if (header.Command == KcpCommand.Ack)
                {
                    var rtt = TimeDiff(current, header.Timestamp);
                    if (rtt >= 0) UpdateRtoThreadSafe(rtt);

                    bool ackMutated = HandleAck(header.SerialNumber, out int bytesFreed);
                    mutated |= ackMutated;
                    if (bytesFreed > 0)
                        _sendQueue.SubtractUnflushedBytes(bytesFreed);

                    if (!flag)
                    {
                        flag = true;
                        maxack = header.SerialNumber;
                        latest_ts = header.Timestamp;
                    }
                    else
                    {
                        if (TimeDiff(header.SerialNumber, maxack) > 0)
                        {
                            maxack = header.SerialNumber;
                            latest_ts = header.Timestamp;
                        }
                    }
                }
                else if (header.Command == KcpCommand.WindowProbe)
                {
                    // Thread-safe update of _probe (assuming it's casted to int for Interlocked)
                    UpdateProbeThreadSafe(KcpProbeType.AskTell);
                }

                packet = packet.Slice(length);
            }

            if (flag)
            {
                while (true)
                {
#pragma warning disable CS0420
                    var currentMaxAck = Volatile.Read(ref _max_ack_sn);
                    if (Volatile.Read(ref _max_ack_has_value) == 1 && TimeDiff(maxack, currentMaxAck) <= 0) break;

                    if (Interlocked.CompareExchange(ref _max_ack_sn, maxack, currentMaxAck) == currentMaxAck)
                    {
                        Volatile.Write(ref _max_ack_has_value, 1);
#pragma warning restore CS0420
                        break;
                    }
                }
            }

            return mutated;
        }
        catch
        {
            return mutated;
        }
    }

    private void UpdateRtoThreadSafe(int rtt)
    {
        lock (_rtoLock)
        {
            UpdateRto(rtt);
        }
    }

    private void UpdateProbeThreadSafe(KcpProbeType flags)
    {
        System.Threading.Interlocked.Or(ref _probe, (int)flags);
    }


    private bool SetInput(ReadOnlySpan<byte> packet, System.Buffers.IMemoryOwner<byte>? originalBuffer, uint current)
    {
        var packetHeaderSize = _id.HasValue
            ? KcpGlobalVars.HEADER_LENGTH_WITH_CONVID
            : KcpGlobalVars.HEADER_LENGTH_WITHOUT_CONVID;
        int packetOffset = 0;

        var prev_una = _snd_una;
        uint maxack = 0, latest_ts = 0;
        var flag = false;
        var mutated = false;

        try
        {
            while (true)
            {
                if (packet.Length < packetHeaderSize) break;

                KcpPacketHeader header;
                int length;
                try
                {
                    if (_id.HasValue)
                    {
                        if (BinaryPrimitives.ReadUInt32LittleEndian(packet) != _id.GetValueOrDefault()) return mutated;
                        packet = packet.Slice(4);
                        packetOffset += 4;
                    }

                    header = KcpPacketHeader.Parse(packet);
                    length = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(16));

                    packet = packet.Slice(20);
                    packetOffset += 20;
                    if ((uint)length > (uint)packet.Length) return mutated;
                }
                catch
                {
                    originalBuffer?.Dispose();
                    throw;
                }

                if (header.Command != KcpCommand.Push &&
                    header.Command != KcpCommand.Ack &&
                    header.Command != KcpCommand.WindowProbe &&
                    header.Command != KcpCommand.WindowSize)
                    return mutated;

                Volatile.Write(ref _lastReceiveTick, current);
                var newRmtWnd = header.WindowSize;
                if (newRmtWnd != Volatile.Read(ref _rmt_wnd))
                {
                    Volatile.Write(ref _rmt_wnd, newRmtWnd);
                }
                mutated = HandleUnacknowledged(header.Unacknowledged) | mutated;
                // removed UpdateSendUnacknowledged() here

                if (header.Command == KcpCommand.Ack)
                {
                    var rtt = TimeDiff(current, header.Timestamp);
                    if (rtt >= 0) UpdateRto(rtt);

                    bool ackMutated = HandleAck(header.SerialNumber, out int bytesFreed);
                    mutated |= ackMutated;
                    if (bytesFreed > 0)
                        _sendQueue.SubtractUnflushedBytes(bytesFreed);

                    // removed UpdateSendUnacknowledged() here

                    if (!flag)
                    {
                        flag = true;
                        maxack = header.SerialNumber;
                        latest_ts = header.Timestamp;
                    }
                    else
                    {
                        if (TimeDiff(header.SerialNumber, maxack) > 0)
                        {
                            maxack = header.SerialNumber;
                            latest_ts = header.Timestamp;
                        }
                    }
                }
                else if (header.Command == KcpCommand.Push)
                {
                    if (TimeDiff(header.SerialNumber, _rcv_nxt + _rcv_wnd) < 0)
                    {
                        AckPush(header.SerialNumber, header.Timestamp);
                        if (TimeDiff(header.SerialNumber, _rcv_nxt) >= 0)
                        {
                            mutated = HandleData(header, packet.Slice(0, length), originalBuffer, packetOffset) | mutated;
                        }

                        if (_receiveWindowNotificationOptions is not null)
                        {
                            _ts_rcv_notify_wait = 0;
                            _ts_rcv_notify = current + (uint)_receiveWindowNotificationOptions.InitialInterval;
                        }
                    }
                }
                else if (header.Command == KcpCommand.WindowProbe)
                {
                    UpdateProbeThreadSafe(KcpProbeType.AskTell);
                }
                else if (header.Command == KcpCommand.WindowSize)
                {
                    // do nothing
                }
                else
                {
                    return mutated;
                }

                packet = packet.Slice(length);
                packetOffset += length;
            }

            if (flag)
            {
                while (true)
                {
                    #pragma warning disable CS0420
                    var currentMaxAck = Volatile.Read(ref _max_ack_sn);
                    if (Volatile.Read(ref _max_ack_has_value) == 1 && TimeDiff(maxack, currentMaxAck) <= 0) break;

                    if (Interlocked.CompareExchange(ref _max_ack_sn, maxack, currentMaxAck) == currentMaxAck)
                    {
                        Volatile.Write(ref _max_ack_has_value, 1);
#pragma warning restore CS0420
                        break;
                    }
                }
            }

            if (mutated)
            {
                mutated = UpdateSendUnacknowledged() | mutated;
            }

            if (TimeDiff(_snd_una, prev_una) > 0)
            {
                var cwnd = _cwnd;
                var incr = _incr;

                var rmt_wnd = Volatile.Read(ref _rmt_wnd);
                if (cwnd < rmt_wnd)
                {
                    var mss = (uint)_mss;
                    if (cwnd < _ssthresh)
                    {
                        cwnd++;
                        incr += mss;
                    }
                    else
                    {
                        if (incr < mss) incr = mss;
                        incr += (uint)mss * (uint)mss / Math.Max(1u, incr) + (uint)(mss / 16);
                        cwnd = (incr + mss - 1) / (mss > 0 ? mss : 1);
                    }

                    if (cwnd > rmt_wnd)
                    {
                        cwnd = rmt_wnd;
                        incr = rmt_wnd * mss;
                    }
                }

                _cwnd = cwnd;
                _incr = incr;
            }

            return mutated;
        }
        catch
        {
            throw;
        }
    }

    private bool HandleUnacknowledged(uint una)
    {
        var mutated = false;
        long totalBytesFreed = 0;
        lock (_sndBufLock)
        {
            while (TimeDiff(una, _snd_una) > 0 && TimeDiff(_snd_una, _snd_nxt) < 0)
            {
                uint sn = _snd_una;
                int index = (int)(sn % (uint)_sndBufArray.Length);
                ref var item = ref _sndBufArray[index];

                if (!item.IsEmpty && item.Segment.SerialNumber == sn)
                {
                    totalBytesFreed += item.Data.Length;
                    item.Data.Release();
                    item.Data = default;
                    item.IsEmpty = true; // mark as empty
                    mutated = true;
                }

                _snd_una++;
            }
        }

        if (totalBytesFreed > 0)
            _sendQueue.SubtractUnflushedBytes(totalBytesFreed);

        return mutated;
    }

    private bool UpdateSendUnacknowledged()
    {
        lock (_sndBufLock)
        {
            var snd_una = _snd_una;

            while (TimeDiff(snd_una, _snd_nxt) < 0)
            {
                int index = (int)(snd_una % (uint)_sndBufArray.Length);
                if (!_sndBufArray[index].IsEmpty && _sndBufArray[index].Segment.SerialNumber == snd_una)
                {
                    break;
                }
                snd_una++;
            }

            var old_snd_una = _snd_una;
            if (old_snd_una != snd_una)
            {
                _snd_una = snd_una;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    ///     Updates the Retransmission TimeOut based on the measured RTT.
    ///     Note: RTT values are validated by callers (e.g., <c>if (rtt >= 0)</c>) to prevent overflow issues
    ///     where RTT exceeds int.MaxValue ticks and wraps around to a negative number.
    /// </summary>
    private void UpdateRto(int rtt)
    {
        KcpMetrics.RoundTripTime.Record(rtt);
        lock (_rtoLock)
        {
            if (_rx_srtt == 0)
            {
                _rx_srtt = rtt;
                _rx_rttval = rtt / 2;
            }
            else
            {
                var delta = rtt - _rx_srtt;
                if (delta < 0) delta = -delta;
                _rx_rttval = (3 * _rx_rttval + delta) / 4;
                _rx_srtt = (7 * _rx_srtt + rtt) / 8;
                if (_rx_srtt < 1) _rx_srtt = 1;
            }

            var rto = _rx_srtt + Math.Max((int)_interval, 4 * _rx_rttval);
            _rx_rto = Math.Clamp((uint)rto, _rx_minrto, IKCP_RTO_MAX);
        }
    }

    private bool HandleAck(uint serialNumber, out int bytesFreed)
    {
        bytesFreed = 0;
        if (TimeDiff(serialNumber, _snd_una) < 0 || TimeDiff(serialNumber, _snd_nxt) >= 0) return false;

        lock (_sndBufLock)
        {
            int index = (int)(serialNumber % (uint)_sndBufArray.Length);
            ref var item = ref _sndBufArray[index];

            if (item.IsEmpty || item.Segment.SerialNumber != serialNumber)
            {
                return false;
            }

            bytesFreed = item.Data.Length;
            item.Data.Release();
            item.Data = default;
            item.IsEmpty = true; // mark empty
            return true;
        }
    }

    private bool HandleData(KcpPacketHeader header, ReadOnlySpan<byte> data, System.Buffers.IMemoryOwner<byte>? originalBuffer, int dataOffsetInBuffer)
    {
        var serialNumber = header.SerialNumber;
        if (TimeDiff(serialNumber, _rcv_nxt + _rcv_wnd) >= 0 || TimeDiff(serialNumber, _rcv_nxt) < 0) return false;

        var mutated = false;
        lock (_rcvBufLock)
        {
            if (TransportClosed) return false;

            int index = (int)(serialNumber % (uint)_rcvBufArray.Length);
            ref var itemRef = ref _rcvBufArray[index];

            if (!itemRef.IsEmpty && itemRef.Segment.SerialNumber == serialNumber)
            {
                return false; // Duplicate
            }

            // Copy data and insert
            KcpBuffer kcpBuffer = default;
            if (data.Length > 0)
            {
                if (originalBuffer is IRefCountedBuffer refCounted)
                {
                    // We keep a reference to the same buffer and share memory ownership
                    kcpBuffer = KcpBuffer.FromRetainedOwner(refCounted.Retain(), originalBuffer.Memory.Slice(dataOffsetInBuffer), data.Length);
                }
                else
                {
                    // If it came from a pooled array but without a shared owner, we must rent our own and copy it
                    var rented = _bufferPool.Rent(new KcpBufferPoolRentOptions(data.Length, false));
                    data.CopyTo(rented.Memory.Span);
                    kcpBuffer = KcpBuffer.CreateFromSpan(rented, rented.Memory.Span.Slice(0, data.Length));
                }
            }

            // In case of aliasing (which shouldn't happen due to window constraints), verify it's empty
            System.Diagnostics.Debug.Assert(itemRef.IsEmpty, "Ring buffer aliasing detected! Overwriting unacknowledged segment. Congestion window bounds exceeded.");
            if (!itemRef.IsEmpty)
            {
                itemRef.Data.Release();
            }

            itemRef = new KcpSendReceiveBufferItem
            {
                Data = kcpBuffer,
                Segment = DuplicateHeader(ref header, 0, 0, 0),
                IsEmpty = false
            };

            mutated = true;

            // move available data from rcv_buf -> rcv_queue
            while (_receiveQueue.GetQueueSize() < _rcv_wnd)
            {
                int nxtIndex = (int)(_rcv_nxt % (uint)_rcvBufArray.Length);
                ref var nxtItemRef = ref _rcvBufArray[nxtIndex];

                if (!nxtItemRef.IsEmpty && nxtItemRef.Segment.SerialNumber == _rcv_nxt)
                {
                    _receiveQueue.Enqueue(nxtItemRef.Data, nxtItemRef.Segment.Fragment);

                    nxtItemRef.Data = default;
                    nxtItemRef.IsEmpty = true;
                    _rcv_nxt++;
                    mutated = true;
                }
                else
                {
                    break;
                }
            }
        }

        return mutated;
    }

    private void AckPush(uint serialNumber, uint timestamp)
    {
        _ackList.Add(serialNumber, timestamp);
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GetTimestamp()
    {
        return (uint)Environment.TickCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TimeDiff(uint later, uint earlier)
    {
        return (int)(later - earlier);
    }

    /// <summary>
    ///     Get the size of the next available message in the receive queue.
    /// </summary>
    /// <param name="result">The transport state and the size of the next available message.</param>
    /// <exception cref="InvalidOperationException">The receive or peek operation is initiated concurrently.</exception>
    /// <returns>
    ///     True if the receive queue contains at least one message. False if the receive queue is empty or the transport
    ///     is closed.
    /// </returns>
    public bool TryPeek(out KcpConversationReceiveResult result)
    {
        return _receiveQueue.TryPeek(out result);
    }

    /// <summary>
    ///     Remove the next available message in the receive queue and copy its content into <paramref name="buffer" />. When
    ///     in stream mode, move as many bytes as possible into <paramref name="buffer" />.
    /// </summary>
    /// <param name="buffer">The buffer to receive message.</param>
    /// <param name="result">The transport state and the count of bytes moved into <paramref name="buffer" />.</param>
    /// <exception cref="ArgumentException">
    ///     The size of the next available message is larger than the size of
    ///     <paramref name="buffer" />. This exception is never thrown in stream mode.
    /// </exception>
    /// <exception cref="InvalidOperationException">The receive or peek operation is initiated concurrently.</exception>
    /// <returns>
    ///     True if the next available message is moved into <paramref name="buffer" />. False if the receive queue is
    ///     empty or the transport is closed.
    /// </returns>
    public bool TryReceive(Span<byte> buffer, out KcpConversationReceiveResult result)
    {
        return _receiveQueue.TryReceive(buffer, out result);
    }

    /// <summary>
    ///     Wait until the receive queue contains at least one full message, or at least one byte in stream mode.
    /// </summary>
    /// <param name="cancellationToken">The token to cancel this operation.</param>
    /// <exception cref="OperationCanceledException">
    ///     The <paramref name="cancellationToken" /> is fired before receive
    ///     operation is completed.
    /// </exception>
    /// <exception cref="InvalidOperationException">The receive or peek operation is initiated concurrently.</exception>
    /// <returns>
    ///     A <see cref="ValueTask{KcpConversationReceiveResult}" /> that completes when the receive queue contains at
    ///     least one full message, or at least one byte in stream mode. Its result contains the transport state and the size
    ///     of the available message.
    /// </returns>
    /// <remarks>WARNING: This method returns a ValueTask. Do not await it multiple times or store the ValueTask directly.</remarks>
    public ValueTask<KcpConversationReceiveResult> WaitToReceiveAsync(CancellationToken cancellationToken = default)
    {
        return _receiveQueue.WaitToReceiveAsync(cancellationToken);
    }

    /// <summary>
    ///     Wait until the receive queue contains at least <paramref name="minimumBytes" /> bytes, and also
    ///     at least <paramref name="minimumSegments" /> segments.
    /// </summary>
    /// <param name="minimumBytes">The minimum bytes in the receive queue.</param>
    /// <param name="minimumSegments">The minimum segments in the receive queue. In stream mode, this counts the number of internal linked list nodes rather than logical segments. It is recommended to use 0 for stream mode.</param>
    /// <param name="cancellationToken">The token to cancel this operation.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     Any of <paramref name="minimumBytes" /> and
    ///     <paramref name="minimumSegments" /> is a negative integer.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    ///     The <paramref name="cancellationToken" /> is fired before receive
    ///     operation is completed.
    /// </exception>
    /// <exception cref="InvalidOperationException">The receive or peek operation is initiated concurrently.</exception>
    /// <returns>
    ///     A <see cref="ValueTask{Boolean}" /> that completes when the receive queue contains at least
    ///     <paramref name="minimumBytes" /> bytes. The result of the task is false when the transport is closed.
    /// </returns>
    /// <remarks>WARNING: This method returns a ValueTask. Do not await it multiple times or store the ValueTask directly.</remarks>
    public ValueTask<bool> WaitForReceiveQueueAvailableDataAsync(int minimumBytes, int minimumSegments = 0,
        CancellationToken cancellationToken = default)
    {
        return _receiveQueue.WaitForAvailableDataAsync(minimumBytes, minimumSegments, cancellationToken);
    }

    /// <summary>
    ///     Gets the UTC time of the last received KCP packet.
    /// </summary>
    internal DateTimeOffset LastReceiveTime
    {
        get
        {
            var elapsedMs = TimeDiff(GetTimestamp(), Volatile.Read(ref _lastReceiveTick));
            if (elapsedMs < 0) elapsedMs = 0;
            return DateTimeOffset.UtcNow.AddMilliseconds(-elapsedMs);
        }
    }

    /// <summary>
    ///     Wait for the next full message to arrive if the receive queue is empty. Remove the next available message in the
    ///     receive queue and copy its content into <paramref name="buffer" />. When in stream mode, move as many bytes as
    ///     possible into <paramref name="buffer" />.
    /// </summary>
    /// <param name="buffer">The buffer to receive message.</param>
    /// <param name="cancellationToken">The token to cancel this operation.</param>
    /// <exception cref="ArgumentException">
    ///     The size of the next available message is larger than the size of
    ///     <paramref name="buffer" />. This exception is never thrown in stream mode.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    ///     The <paramref name="cancellationToken" /> is fired before send operation
    ///     is completed.
    /// </exception>
    /// <exception cref="InvalidOperationException">The receive or peek operation is initiated concurrently.</exception>
    /// <returns>
    ///     A <see cref="ValueTask{KcpConversationReceiveResult}" /> that completes when a full message is moved into
    ///     <paramref name="buffer" /> or the transport is closed. Its result contains the transport state and the count of
    ///     bytes written into <paramref name="buffer" />.
    /// </returns>
    /// <remarks>WARNING: This method returns a ValueTask. Do not await it multiple times or store the ValueTask directly.</remarks>
    public ValueTask<KcpConversationReceiveResult> ReceiveAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        return _receiveQueue.ReceiveAsync(buffer, cancellationToken);
    }

    /// <summary>
    ///     Wait for the next full message to arrive if the receive queue is empty. Remove the next available message in the
    ///     receive queue and write its content into <paramref name="writer" />. When in stream mode, write as many bytes as
    ///     possible.
    /// </summary>
    /// <param name="writer">The buffer writer to receive message.</param>
    /// <param name="cancellationToken">The token to cancel this operation.</param>
    /// <exception cref="OperationCanceledException">
    ///     The <paramref name="cancellationToken" /> is fired before send operation
    ///     is completed.
    /// </exception>
    /// <exception cref="InvalidOperationException">The receive or peek operation is initiated concurrently.</exception>
    /// <returns>
    ///     A <see cref="ValueTask{KcpConversationReceiveResult}" /> that completes when a full message is moved into
    ///     <paramref name="writer" /> or the transport is closed. Its result contains the transport state and the count of
    ///     bytes written.
    /// </returns>
    /// <remarks>WARNING: This method returns a ValueTask. Do not await it multiple times or store the ValueTask directly.</remarks>
    public ValueTask<KcpConversationReceiveResult> ReceiveToWriterAsync(System.Buffers.IBufferWriter<byte> writer,
        CancellationToken cancellationToken = default)
    {
        return _receiveQueue.ReceiveToWriterAsync(writer, cancellationToken);
    }

    internal ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        return _receiveQueue.ReadAsync(buffer, cancellationToken);
    }

    /// <summary>
    ///     Cancel the current receive operation.
    /// </summary>
    /// <returns>True if the current operation is canceled. False if there is no active send operation.</returns>
    public bool CancelPendingReceive()
    {
        return _receiveQueue.CancelPendingOperation(null, default);
    }

    /// <summary>
    ///     Cancel the current receive operation.
    /// </summary>
    /// <param name="innerException">
    ///     The inner exception of the <see cref="OperationCanceledException" /> thrown by the
    ///     <see cref="ReceiveAsync(Memory{byte}, CancellationToken)" /> method or
    ///     <see cref="WaitToReceiveAsync(CancellationToken)" /> method.
    /// </param>
    /// <param name="cancellationToken">
    ///     The <see cref="CancellationToken" /> in the <see cref="OperationCanceledException" />
    ///     thrown by the <see cref="ReceiveAsync(Memory{byte}, CancellationToken)" /> method or
    ///     <see cref="WaitToReceiveAsync(CancellationToken)" /> method.
    /// </param>
    /// <returns>True if the current operation is canceled. False if there is no active send operation.</returns>
    public bool CancelPendingReceive(Exception? innerException, CancellationToken cancellationToken)
    {
        return _receiveQueue.CancelPendingOperation(innerException, cancellationToken);
    }

    /// <inheritdoc />
    public void SetTransportClosed()
    {
        if (Interlocked.Exchange(ref _transportClosedFlag, 1) == 1) return;

        Interlocked.Exchange(ref _updateActivation, null)?.Dispose();
        var updateLoopCts = Interlocked.Exchange(ref _updateLoopCts, null);
        if (updateLoopCts is not null)
        {
            updateLoopCts.Cancel();
            updateLoopCts.Dispose();
        }


        _sendQueue.SetTransportClosed();
        _receiveQueue.SetTransportClosed();

        lock (_sndBufLock)
        {
            for (int i = 0; i < _sndBufArray.Length; i++)
            {
                if (!_sndBufArray[i].IsEmpty)
                {
                    _sndBufArray[i].Data.Release();
                    _sndBufArray[i].IsEmpty = true;
                }
            }
        }

        lock (_rcvBufLock)
        {
            for (int i = 0; i < _rcvBufArray.Length; i++)
            {
                if (!_rcvBufArray[i].IsEmpty)
                {
                    _rcvBufArray[i].Data.Release();
                    _rcvBufArray[i].IsEmpty = true;
                    _rcvBufArray[i].Data = default;
                }
            }
        }

        _ackList.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        SetTransportClosed();

        try { _sendQueue.Dispose(); } catch { }
        try { _receiveQueue.Dispose(); } catch { }

        // Start background tear down for the main tasks to avoid blocking synchronous Dispose.
        // Waiting synchronously in Dispose causes thread-pool starvation.
        _ = DisposeBackgroundTasksAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        SetTransportClosed();

        try { _sendQueue.Dispose(); } catch { }
        try { _receiveQueue.Dispose(); } catch { }

        await DisposeBackgroundTasksAsync().ConfigureAwait(false);
    }

    private async Task DisposeBackgroundTasksAsync()
    {
        try
        {
            if (_updateLoopTask != null)
            {
                // We rely on SetTransportClosed() cancelling `_updateLoopCts`, unblocking network IO.
                // Await completely to guarantee safe disposal of the pre-allocated buffers without use-after-free corruption.
                await _updateLoopTask.ConfigureAwait(false);
            }
        }
        catch { }

        try { _flushSemaphore.Dispose(); } catch { }
        try { _cachedFlushBuffer.Dispose(); } catch { }
        try { _cachedAckFlushBuffer.Dispose(); } catch { }
    }
}