using System.Diagnostics;
using System.Threading.Tasks.Sources;

namespace KcpSharp;

internal sealed class KcpSendQueue : IValueTaskSource<bool>, IValueTaskSource, IDisposable
{
    private readonly System.Threading.Lock _syncRoot = new();
    private readonly AsyncCapacityReserve _spaceSemaphore;

    private readonly IKcpBufferPool _bufferPool;
    private readonly KcpSendReceiveQueueItemCacheUnsafe _cache;
    private readonly int _capacity;
    private readonly int _mss;

    private readonly LinkedList<(KcpBuffer Data, byte Fragment)> _queue;
    private readonly bool _stream;
    private readonly KcpConversationUpdateActivation _updateActivation;

    private bool _ackListNotEmpty;

    private bool _activeWait;

    private CancellationTokenRegistration _cancellationRegistration;
    private CancellationToken _cancellationToken;
    private bool _disposed;
    private bool _forStream;
    private ManualResetValueTaskSourceCore<bool> _mrvtsc;
    private byte _operationMode; // 0-send 1-flush 2-wait for space
    private bool _signaled;

    private volatile bool _transportClosed;
    private long _unflushedBytes;
    private int _waitForByteCount;
    private int _waitForSegmentCount;

    public KcpSendQueue(IKcpBufferPool bufferPool, KcpConversationUpdateActivation updateActivation, bool stream,
        int capacity, int mss, KcpSendReceiveQueueItemCacheUnsafe cache)
    {
        _bufferPool = bufferPool;
        _updateActivation = updateActivation;
        _stream = stream;
        _capacity = capacity;
        _mss = mss;
        _cache = cache;
        _mrvtsc = new ManualResetValueTaskSourceCore<bool>
        {
            RunContinuationsAsynchronously = true
        };

        _queue = new LinkedList<(KcpBuffer Data, byte Fragment)>();
        _spaceSemaphore = new AsyncCapacityReserve(capacity);
    }

    public void Dispose()
    {
        bool executeSetException = false;
        bool executeSetResult = false;
        Exception? exceptionToSet = null;

        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;

            if (_activeWait && !_signaled)
            {
                if (_forStream)
                {
                    ClearPreviousOperation();
                    exceptionToSet = ThrowHelper.NewTransportClosedForStreamException();
                    executeSetException = true;
                }
                else
                {
                    ClearPreviousOperation();
                    executeSetResult = true;
                }
            }

            var node = _queue.First;
            while (node is not null)
            {
                node.ValueRef.Data.Release();
                node = node.Next;
            }

            _queue.Clear();
            _cache.Clear();
            _transportClosed = true;
        }

        _spaceSemaphore.Dispose();

        if (executeSetException)
        {
            _mrvtsc.SetException(exceptionToSet!);
        }
        else if (executeSetResult)
        {
            _mrvtsc.SetResult(false);
        }
    }

    void IValueTaskSource.GetResult(short token)
    {
        try
        {
            _mrvtsc.GetResult(token);
        }
        finally
        {
            _mrvtsc.Reset();
            lock (_syncRoot)
            {
                _activeWait = false;
                _signaled = false;
                _cancellationRegistration = default;
            }
        }
    }

    public ValueTaskSourceStatus GetStatus(short token)
    {
        return _mrvtsc.GetStatus(token);
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        _mrvtsc.OnCompleted(continuation, state, token, flags);
    }

    bool IValueTaskSource<bool>.GetResult(short token)
    {
        _cancellationRegistration.Dispose();
        try
        {
            return _mrvtsc.GetResult(token);
        }
        finally
        {
            _mrvtsc.Reset();
            lock (_syncRoot)
            {
                _activeWait = false;
                _signaled = false;
                _cancellationRegistration = default;
            }
        }
    }

    public bool TryGetAvailableSpace(out int byteCount, out int segmentCount)
    {
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed)
            {
                byteCount = 0;
                segmentCount = 0;
                return false;
            }



            GetAvailableSpaceCore(out byteCount, out segmentCount);
            return true;
        }
    }

    /// <summary>
    ///     Try to dequeue multiple message fragments from the send queue in a single batch.
    /// </summary>
    /// <param name="results">The buffer to store the dequeued fragments.</param>
    /// <param name="maxCount">The maximum number of fragments to dequeue.</param>
    /// <returns>The number of fragments actually dequeued.</returns>
    public int TryDequeueBatch(Span<(KcpBuffer Data, byte Fragment)> results, int maxCount)
    {
        if (_transportClosed)
            return 0;

        int count = 0;
        bool needSignal = false;
        lock (_syncRoot)
        {
            while (count < maxCount && count < results.Length)
            {
                var node = _queue.First;
                if (node is null) break;

                results[count] = (node.ValueRef.Data, node.ValueRef.Fragment);
                _queue.RemoveFirst();
                node.ValueRef = default;
                _cache.Return(node);
                count++;
            }

            if (count > 0)
            {
                CheckForAvailableSpace(ref needSignal);
            }
        }

        if (count > 0)
        {
            try
            {
                _spaceSemaphore.Release(count);
            }
            catch (ObjectDisposedException)
            {
                // Ignore: transport is shutting down
            }
        }

        if (needSignal)
            _mrvtsc.SetResult(true);

        return count;
    }

    private void GetAvailableSpaceCore(out int byteCount, out int segmentCount)
    {
        var mss = _mss;
        var availableFragments = _capacity - _queue.Count;
        if (availableFragments < 0)
        {
            byteCount = 0;
            segmentCount = 0;
            return;
        }

        var availableBytes = availableFragments * mss;
        if (_stream)
        {
            var last = _queue.Last;
            if (last is not null) availableBytes += _mss - last.ValueRef.Data.Length;
        }

        byteCount = availableBytes;
        segmentCount = availableFragments;
    }

    public ValueTask<bool> WaitForAvailableSpaceAsync(int minimumBytes, int minimumSegments,
        CancellationToken cancellationToken)
    {
        short token;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed)
            {
                minimumBytes = 0;
                minimumSegments = 0;
                return default;
            }

            if ((uint)minimumBytes > (uint)(_mss * _capacity))
                return new ValueTask<bool>(
                    Task.FromException<bool>(ThrowHelper.NewArgumentOutOfRangeException(nameof(minimumBytes))));
            if ((uint)minimumSegments > (uint)_capacity)
                return new ValueTask<bool>(
                    Task.FromException<bool>(ThrowHelper.NewArgumentOutOfRangeException(nameof(minimumSegments))));
            if (_activeWait)
                return new ValueTask<bool>(Task.FromException<bool>(ThrowHelper.NewConcurrentSendException()));
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken));
            GetAvailableSpaceCore(out var currentByteCount, out var currentSegmentCount);
            if (currentByteCount >= minimumBytes && currentSegmentCount >= minimumSegments)
                return new ValueTask<bool>(true);

            _activeWait = true;
            Debug.Assert(!_signaled);
            _forStream = false;
            _operationMode = 2;
            _waitForByteCount = minimumBytes;
            _waitForSegmentCount = minimumSegments;
            _cancellationToken = cancellationToken;
            token = _mrvtsc.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpSendQueue?)state)!.SetCanceled(), this);

        return new ValueTask<bool>(this, token);
    }

    public bool TrySend(ReadOnlySpan<byte> buffer, bool allowPartialSend, out int bytesWritten)
    {
        lock (_syncRoot)
        {
            if (allowPartialSend && !_stream) ThrowHelper.ThrowAllowPartialSendArgumentException();
            if (_transportClosed || _disposed)
            {
                bytesWritten = 0;
                return false;
            }

            var mss = _mss;
            // Make sure there is enough space.
            int requiredSlots = 0;
            if (!allowPartialSend)
            {
                int expand = 0;
                if (_stream)
                {
                    var last = _queue.Last;
                    if (last is not null) expand = mss - last.ValueRef.Data.Length;
                }

                if (buffer.Length > expand)
                {
                    int remaining = buffer.Length - expand;
                    requiredSlots = remaining <= mss ? 1 : (remaining + mss - 1) / mss;
                }

                if (_spaceSemaphore.CurrentCount < requiredSlots)
                {
                    bytesWritten = 0;
                    return false;
                }

                if (!_spaceSemaphore.TryReserve(requiredSlots))
                {
                    bytesWritten = 0;
                    return false;
                }
            }

            // Copy buffer content.
            bytesWritten = 0;
            if (_stream)
            {
                var node = _queue.Last;
                if (node is not null)
                {
                    ref var data = ref node.ValueRef.Data;
                    var expand = mss - data.Length;
                    expand = Math.Min(expand, buffer.Length);
                    if (expand > 0)
                    {
                        data = data.AppendData(buffer.Slice(0, expand));
                        buffer = buffer.Slice(expand);
                        Interlocked.Add(ref _unflushedBytes, expand);
                        bytesWritten = expand;
                    }
                }

                if (buffer.IsEmpty)
                {
                    _updateActivation.Notify();
                    return true;
                }
            }

            var anySegmentAdded = false;
            var count = buffer.Length <= mss ? 1 : (buffer.Length + mss - 1) / mss;
            Debug.Assert(count >= 1);

            if (!_stream && count > 256)
            {
                if (!allowPartialSend && requiredSlots > 0)
                {
                    _spaceSemaphore.Release(requiredSlots);
                }
                throw new ArgumentException("Message is too large (requires > 256 fragments).", nameof(buffer));
            }

            int acquiredSlots = allowPartialSend ? 0 : requiredSlots;
            int addedToQueue = 0;

            try
            {
                if (allowPartialSend)
                {
                    for (int i = count; i > 0; i--)
                    {
                        if (_spaceSemaphore.TryReserve(i))
                        {
                            acquiredSlots = i;
                            count = i;
                            break;
                        }
                    }
                    if (acquiredSlots == 0) count = 0;
                }

                while (count > 0)
                {
                    var fragment = --count;

                    var size = buffer.Length > mss ? mss : buffer.Length;

                    var owner = _bufferPool.Rent(new KcpBufferPoolRentOptions(mss, false));
                    var kcpBuffer = KcpBuffer.CreateFromSpan(owner, buffer.Slice(0, size));
                    buffer = buffer.Slice(size);

                    _queue.AddLast(_cache.Rent(kcpBuffer, _stream ? (byte)0 : (byte)fragment));
                    Interlocked.Add(ref _unflushedBytes, size);
                    bytesWritten += size;
                    addedToQueue++;
                    anySegmentAdded = true;
                }
            }
            catch
            {
                int unusedSlots = acquiredSlots - addedToQueue;
                if (unusedSlots > 0)
                {
                    try { _spaceSemaphore.Release(unusedSlots); } catch (ObjectDisposedException) { }
                }
                throw;
            }

            if (anySegmentAdded) _updateActivation.Notify();
            return anySegmentAdded;
        }
    }


    public async ValueTask<bool> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_transportClosed || _disposed) return false;
        if (cancellationToken.IsCancellationRequested) return false;

        var mss = _mss;
        int streamExpandBytes = 0;
        int originalBufferLength = buffer.Length;

        if (_stream)
        {
            lock (_syncRoot)
            {
                var node = _queue.Last;
                if (node is not null)
                {
                    var data = node.ValueRef.Data;
                    var expand = mss - data.Length;
                    expand = Math.Min(expand, originalBufferLength);
                    if (expand > 0)
                    {
                        streamExpandBytes = expand;
                    }
                }

                if (streamExpandBytes > 0)
                {
                    if (originalBufferLength == streamExpandBytes)
                    {
                        // Completely fits into the last node, no need to wait for a semaphore
                        ref var dataRef = ref node!.ValueRef.Data;
                        dataRef = dataRef.AppendData(buffer.Span.Slice(0, streamExpandBytes));
                        Interlocked.Add(ref _unflushedBytes, streamExpandBytes);
                        _updateActivation.Notify();
                        return true;
                    }
                }
            }
        }

        var remainingLength = originalBufferLength - streamExpandBytes;
        var count = remainingLength <= mss ? 1 : (remainingLength + mss - 1) / mss;

        if (!_stream && count > 256)
            throw new ArgumentException("Message is too large (requires > 256 fragments).", nameof(buffer));

        while (count > 0)
        {
            try
            {
                await _spaceSemaphore.WaitAsync(1, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            if (_transportClosed || _disposed)
            {
                try { _spaceSemaphore.Release(1); } catch (ObjectDisposedException) { }
                return false;
            }

            try
            {
                lock (_syncRoot)
                {
                    if (_transportClosed || _disposed)
                    {
                        try { _spaceSemaphore.Release(1); } catch (ObjectDisposedException) { }
                        return false;
                    }

                    if (streamExpandBytes > 0)
                    {
                        var node = _queue.Last;
                        if (node is not null)
                        {
                            ref var dataRef = ref node.ValueRef.Data;
                            dataRef = dataRef.AppendData(buffer.Span.Slice(0, streamExpandBytes));
                            buffer = buffer.Slice(streamExpandBytes);
                            Interlocked.Add(ref _unflushedBytes, streamExpandBytes);
                        }
                        streamExpandBytes = 0;
                    }

                    var fragment = --count;
                    var size = buffer.Length > mss ? mss : buffer.Length;
                    var owner = _bufferPool.Rent(new KcpBufferPoolRentOptions(mss, false));
                    var kcpBuffer = KcpBuffer.CreateFromSpan(owner, buffer.Span.Slice(0, size));
                    buffer = buffer.Slice(size);

                    _queue.AddLast(_cache.Rent(kcpBuffer, _stream ? (byte)0 : (byte)fragment));
                    Interlocked.Add(ref _unflushedBytes, size);
                }
            }
            catch
            {
                try { _spaceSemaphore.Release(1); } catch (ObjectDisposedException) { }
                throw;
            }
        }

        _updateActivation.Notify();
        return true;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_transportClosed || _disposed)
            throw new InvalidOperationException("Transport closed.");
        if (cancellationToken.IsCancellationRequested)
            cancellationToken.ThrowIfCancellationRequested();

        var mss = _mss;
        int streamExpandBytes = 0;
        int originalBufferLength = buffer.Length;

        if (_stream)
        {
            lock (_syncRoot)
            {
                var node = _queue.Last;
                if (node is not null)
                {
                    var data = node.ValueRef.Data;
                    var expand = mss - data.Length;
                    expand = Math.Min(expand, originalBufferLength);
                    if (expand > 0)
                    {
                        streamExpandBytes = expand;
                    }
                }

                if (streamExpandBytes > 0)
                {
                    if (originalBufferLength == streamExpandBytes)
                    {
                        ref var dataRef = ref node!.ValueRef.Data;
                        dataRef = dataRef.AppendData(buffer.Span.Slice(0, streamExpandBytes));
                        Interlocked.Add(ref _unflushedBytes, streamExpandBytes);
                        _updateActivation.Notify();
                        return;
                    }
                }
            }
        }

        var remainingLength = originalBufferLength - streamExpandBytes;
        var count = remainingLength <= mss ? 1 : (remainingLength + mss - 1) / mss;

        while (count > 0)
        {
            try
            {
                await _spaceSemaphore.WaitAsync(1, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                throw new InvalidOperationException("Transport closed.");
            }

            if (_transportClosed || _disposed)
            {
                try { _spaceSemaphore.Release(1); } catch (ObjectDisposedException) { }
                throw new InvalidOperationException("Transport closed.");
            }

            try
            {
                lock (_syncRoot)
                {
                    if (_transportClosed || _disposed)
                    {
                        try { _spaceSemaphore.Release(1); } catch (ObjectDisposedException) { }
                        throw new InvalidOperationException("Transport closed.");
                    }

                    if (streamExpandBytes > 0)
                    {
                        var node = _queue.Last;
                        if (node is not null)
                        {
                            ref var dataRef = ref node.ValueRef.Data;
                            dataRef = dataRef.AppendData(buffer.Span.Slice(0, streamExpandBytes));
                            buffer = buffer.Slice(streamExpandBytes);
                            Interlocked.Add(ref _unflushedBytes, streamExpandBytes);
                        }
                        streamExpandBytes = 0;
                    }

                    var size = buffer.Length > mss ? mss : buffer.Length;
                    var owner = _bufferPool.Rent(new KcpBufferPoolRentOptions(mss, false));
                    var kcpBuffer = KcpBuffer.CreateFromSpan(owner, buffer.Span.Slice(0, size));
                    buffer = buffer.Slice(size);

                    _queue.AddLast(_cache.Rent(kcpBuffer, 0));
                    Interlocked.Add(ref _unflushedBytes, size);
                    count--;
                }
            }
            catch
            {
                try { _spaceSemaphore.Release(1); } catch (ObjectDisposedException) { }
                throw;
            }
        }

        _updateActivation.Notify();
    }
public ValueTask<bool> FlushAsync(CancellationToken cancellationToken)
    {
        short token;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return new ValueTask<bool>(false);
            if (_activeWait)
                return new ValueTask<bool>(Task.FromException<bool>(ThrowHelper.NewConcurrentSendException()));
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken));

            _activeWait = true;
            Debug.Assert(!_signaled);
            _forStream = false;
            _operationMode = 1;
            _cancellationToken = cancellationToken;
            token = _mrvtsc.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpSendQueue?)state)!.SetCanceled(), this);

        return new ValueTask<bool>(this, token);
    }

    /// <summary>
    ///     Flushes the stream-oriented data in the send queue.
    ///     Unlike <see cref="FlushAsync"/> which flushes individual packets, this method is
    ///     designed for stream operations to potentially combine data or handle partial
    ///     segments efficiently before transmission.
    /// </summary>
    public ValueTask FlushForStreamAsync(CancellationToken cancellationToken)
    {
        short token;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed)
                return new ValueTask(Task.FromException(ThrowHelper.NewTransportClosedForStreamException()));
            if (_activeWait) return new ValueTask(Task.FromException(ThrowHelper.NewConcurrentSendException()));
            if (cancellationToken.IsCancellationRequested) return new ValueTask(Task.FromCanceled(cancellationToken));

            _activeWait = true;
            Debug.Assert(!_signaled);
            _forStream = true;
            _operationMode = 1;
            _cancellationToken = cancellationToken;
            token = _mrvtsc.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpSendQueue?)state)!.SetCanceled(), this);

        return new ValueTask(this, token);
    }

    public bool CancelPendingOperation(Exception? innerException, CancellationToken cancellationToken)
    {
        bool executeSetException = false;
        Exception? exceptionToSet = null;
        lock (_syncRoot)
        {
            if (_activeWait && !_signaled)
            {
                ClearPreviousOperation();
                exceptionToSet = ThrowHelper.NewOperationCanceledExceptionForCancelPendingSend(innerException, cancellationToken);
                executeSetException = true;
            }
        }

        if (executeSetException)
        {
            _mrvtsc.SetException(exceptionToSet!);
            return true;
        }

        return false;
    }

    private void SetCanceled()
    {
        bool executeSetException = false;
        Exception? exceptionToSet = null;
        lock (_syncRoot)
        {
            if (_activeWait && !_signaled)
            {
                var cancellationToken = _cancellationToken;
                ClearPreviousOperation();
                exceptionToSet = new OperationCanceledException(cancellationToken);
                executeSetException = true;
            }
        }

        if (executeSetException)
        {
            _mrvtsc.SetException(exceptionToSet!);
        }
    }

    private void ClearPreviousOperation()
    {
        _signaled = true;
        _forStream = false;
        _operationMode = 0;
        _waitForByteCount = default;
        _waitForSegmentCount = default;
        _cancellationToken = default;
    }

    public void NotifyAckListChanged(bool itemsListNotEmpty)
    {
        bool executeSetResult = false;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return;

            _ackListNotEmpty = itemsListNotEmpty;
            TryCompleteFlush(ref executeSetResult);
        }

        if (executeSetResult)
        {
            _mrvtsc.SetResult(true);
        }
    }

    private void CheckForAvailableSpace(ref bool executeSetResult)
    {
        if (_activeWait && !_signaled && _operationMode == 2)
        {
            GetAvailableSpaceCore(out var byteCount, out var segmentCount);
            if (byteCount >= _waitForByteCount && segmentCount >= _waitForSegmentCount)
            {
                ClearPreviousOperation();
                executeSetResult = true;
            }
        }
    }

    private void TryCompleteFlush(ref bool executeSetResult)
    {
        if (_activeWait && !_signaled && _operationMode == 1)
        {
            var unflushedBytes = Interlocked.Read(ref _unflushedBytes);
            if (_queue.Last is null && unflushedBytes == 0 && !_ackListNotEmpty)
            {
                ClearPreviousOperation();
                executeSetResult = true;
            }
        }
    }

    /// <summary>
    ///     Subtract unflushed bytes in batch.
    /// </summary>
    /// <param name="bytes">The total bytes sent or acknowledged.</param>
    public void SubtractUnflushedBytes(long bytes)
    {
        var unflushedBytes = Interlocked.Add(ref _unflushedBytes, -bytes);
        if (unflushedBytes <= 0)
        {
            if (unflushedBytes < 0)
            {
                long current;
                do
                {
                    current = Interlocked.Read(ref _unflushedBytes);
                    if (current >= 0) break;
                } while (Interlocked.CompareExchange(ref _unflushedBytes, 0, current) != current);
            }

            bool executeSetResult = false;
            lock (_syncRoot)
            {
                TryCompleteFlush(ref executeSetResult);
            }
            if (executeSetResult)
            {
                _mrvtsc.SetResult(true);
            }
        }
    }

    public long GetUnflushedBytes()
    {
        if (_transportClosed || _disposed) return 0;
        return Interlocked.Read(ref _unflushedBytes);
    }

    /// <summary>
    ///     Mark the underlying transport as closed. Abort all active send or receive operations.
    ///     Note: This method signals a graceful shutdown without freeing underlying resources,
    ///     unlike <see cref="Dispose()" /> which signals the closure and also releases resources.
    /// </summary>
    public void SetTransportClosed()
    {
        bool executeSetException = false;
        bool executeSetResult = false;
        Exception? exceptionToSet = null;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return;
            if (_activeWait && !_signaled)
            {
                if (_forStream)
                {
                    ClearPreviousOperation();
                    exceptionToSet = ThrowHelper.NewTransportClosedForStreamException();
                    executeSetException = true;
                }
                else
                {
                    ClearPreviousOperation();
                    executeSetResult = true;
                }
            }

            var node = _queue.First;
            while (node is not null)
            {
                node.ValueRef.Data.Release();
                node = node.Next;
            }
            _queue.Clear();
            _cache.Clear();

            _transportClosed = true;
            Interlocked.Exchange(ref _unflushedBytes, 0);

            // Wake up waiters
            int currentCount = _spaceSemaphore.CurrentCount;
            int toRelease = _capacity - currentCount;
            if (toRelease > 0)
            {
                try { _spaceSemaphore.Release(toRelease); } catch (ObjectDisposedException) { }
            }
        }

        if (executeSetException)
        {
            _mrvtsc.SetException(exceptionToSet!);
        }
        else if (executeSetResult)
        {
            _mrvtsc.SetResult(false);
        }
    }
}