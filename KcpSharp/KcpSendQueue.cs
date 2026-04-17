using System.Diagnostics;
using System.Threading.Tasks.Sources;

namespace KcpSharp;

internal sealed class KcpSendQueue : IValueTaskSource<bool>, IValueTaskSource, IDisposable
{
    private readonly System.Threading.Lock _syncRoot = new();
    private readonly AsyncCapacityReserve _spaceSemaphore;

    private readonly IKcpBufferPool _bufferPool;

    private readonly int _capacity;
    private readonly int _mss;

    private readonly (KcpBuffer Data, byte Fragment)[] _queueArray;
    private int _queueHead;
    private int _queueTail;
    private int _queueCount;


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
        int capacity, int mss)
    {
        _bufferPool = bufferPool;
        _updateActivation = updateActivation;
        _stream = stream;
        _capacity = capacity;
        _mss = mss;

        _mrvtsc = new ManualResetValueTaskSourceCore<bool>
        {
            RunContinuationsAsynchronously = true
        };

        _queueArray = new (KcpBuffer Data, byte Fragment)[capacity];
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

            while (_queueCount > 0)
            {
                _queueArray[_queueHead].Data.Release();
                _queueArray[_queueHead] = default;
                _queueHead = (_queueHead + 1) % _queueArray.Length;
                _queueCount--;
            }

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
            while (_queueCount > 0 && count < maxCount && count < results.Length)
            {
                var item = _queueArray[_queueHead];
                results[count] = (item.Data, item.Fragment);
                _queueArray[_queueHead] = default;
                _queueHead = (_queueHead + 1) % _queueArray.Length;
                _queueCount--;
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
        var availableFragments = _capacity - _queueCount;
        if (availableFragments < 0)
        {
            byteCount = 0;
            segmentCount = 0;
            return;
        }

        var availableBytes = availableFragments * mss;
        if (_stream && _queueCount > 0)
        {
            var last = _queueArray[(_queueTail - 1 + _queueArray.Length) % _queueArray.Length];
            availableBytes += _mss - last.Data.Length;
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
                if (_stream && _queueCount > 0)
                {
                    int lastIndex = (_queueTail - 1 + _queueArray.Length) % _queueArray.Length;
                    var last = _queueArray[lastIndex];
                    expand = mss - last.Data.Length;
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
            if (_stream && _queueCount > 0)
            {
                int lastIndex = (_queueTail - 1 + _queueArray.Length) % _queueArray.Length;
                ref var data = ref _queueArray[lastIndex].Data;
                var expand = mss - data.Length;
                expand = Math.Min(expand, buffer.Length);
                if (expand > 0)
                {
                    data = data.AppendData(buffer.Slice(0, expand));
                    buffer = buffer.Slice(expand);
                    Interlocked.Add(ref _unflushedBytes, expand);
                    bytesWritten = expand;
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

                    _queueArray[_queueTail] = (kcpBuffer, _stream ? (byte)0 : (byte)fragment);
                    _queueTail = (_queueTail + 1) % _queueArray.Length;
                    _queueCount++;
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
        int originalBufferLength = buffer.Length;
        int reservedSlots = originalBufferLength <= mss ? 1 : (originalBufferLength + mss - 1) / mss;

        if (!_stream && reservedSlots > 256)
            throw new ArgumentException("Message is too large (requires > 256 fragments).", nameof(buffer));

        try
        {
            await _spaceSemaphore.WaitAsync(reservedSlots, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        if (_transportClosed || _disposed)
        {
            try { _spaceSemaphore.Release(reservedSlots); } catch (ObjectDisposedException) { }
            return false;
        }

        int usedSlots = 0;
        bool anySegmentAdded = false;

        try
        {
            lock (_syncRoot)
            {
                if (_transportClosed || _disposed)
                {
                    // The finally block handles semaphore release
                    return false;
                }

                if (_stream && _queueCount > 0)
                {
                    int lastIndex = (_queueTail - 1 + _queueArray.Length) % _queueArray.Length;
                    ref var dataRef = ref _queueArray[lastIndex].Data;
                    var expand = mss - dataRef.Length;
                    expand = Math.Min(expand, buffer.Length);
                    if (expand > 0)
                    {
                        dataRef = dataRef.AppendData(buffer.Span.Slice(0, expand));
                        buffer = buffer.Slice(expand);
                        Interlocked.Add(ref _unflushedBytes, expand);
                        anySegmentAdded = true;
                    }
                }

                int fragmentsNeeded = buffer.Length <= mss ? 1 : (buffer.Length + mss - 1) / mss;
                int remainingFragments = fragmentsNeeded;
                int currentFragmentIndex = fragmentsNeeded - 1; // Count down for fragments

                while (remainingFragments > 0)
                {
                    int size = buffer.Length > mss ? mss : buffer.Length;
                    var owner = _bufferPool.Rent(new KcpBufferPoolRentOptions(mss, false));
                    var kcpBuffer = KcpBuffer.CreateFromSpan(owner, buffer.Span.Slice(0, size));
                    buffer = buffer.Slice(size);

                    _queueArray[_queueTail] = (kcpBuffer, _stream ? (byte)0 : (byte)currentFragmentIndex);
                    _queueTail = (_queueTail + 1) % _queueArray.Length;
                    _queueCount++;
                    Interlocked.Add(ref _unflushedBytes, size);
                    usedSlots++;
                    currentFragmentIndex--;
                    remainingFragments--;
                    anySegmentAdded = true;
                }
            }
        }
        finally
        {
            int unusedSlots = reservedSlots - usedSlots;
            if (unusedSlots > 0)
            {
                try { _spaceSemaphore.Release(unusedSlots); } catch (ObjectDisposedException) { }
            }
        }

        if (anySegmentAdded) _updateActivation.Notify();
        return true;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_transportClosed || _disposed)
            throw new InvalidOperationException("Transport closed.");
        if (cancellationToken.IsCancellationRequested)
            cancellationToken.ThrowIfCancellationRequested();

        var mss = _mss;
        int originalBufferLength = buffer.Length;
        int reservedSlots = originalBufferLength <= mss ? 1 : (originalBufferLength + mss - 1) / mss;

        if (!_stream && reservedSlots > 256)
            throw new ArgumentException("Message is too large (requires > 256 fragments).", nameof(buffer));

        try
        {
            await _spaceSemaphore.WaitAsync(reservedSlots, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new InvalidOperationException("Transport closed.");
        }

        if (_transportClosed || _disposed)
        {
            try { _spaceSemaphore.Release(reservedSlots); } catch (ObjectDisposedException) { }
            throw new InvalidOperationException("Transport closed.");
        }

        int usedSlots = 0;
        bool anySegmentAdded = false;

        try
        {
            lock (_syncRoot)
            {
                if (_transportClosed || _disposed)
                {
                    // The finally block handles semaphore release
                    throw new InvalidOperationException("Transport closed.");
                }

                if (_stream && _queueCount > 0)
                {
                    int lastIndex = (_queueTail - 1 + _queueArray.Length) % _queueArray.Length;
                    ref var dataRef = ref _queueArray[lastIndex].Data;
                    var expand = mss - dataRef.Length;
                    expand = Math.Min(expand, buffer.Length);
                    if (expand > 0)
                    {
                        dataRef = dataRef.AppendData(buffer.Span.Slice(0, expand));
                        buffer = buffer.Slice(expand);
                        Interlocked.Add(ref _unflushedBytes, expand);
                        anySegmentAdded = true;
                    }
                }

                int fragmentsNeeded = buffer.Length <= mss ? 1 : (buffer.Length + mss - 1) / mss;
                int remainingFragments = fragmentsNeeded;
                int currentFragmentIndex = fragmentsNeeded - 1; // Count down for fragments

                while (remainingFragments > 0)
                {
                    int size = buffer.Length > mss ? mss : buffer.Length;
                    var owner = _bufferPool.Rent(new KcpBufferPoolRentOptions(mss, false));
                    var kcpBuffer = KcpBuffer.CreateFromSpan(owner, buffer.Span.Slice(0, size));
                    buffer = buffer.Slice(size);

                    _queueArray[_queueTail] = (kcpBuffer, _stream ? (byte)0 : (byte)currentFragmentIndex);
                    _queueTail = (_queueTail + 1) % _queueArray.Length;
                    _queueCount++;
                    Interlocked.Add(ref _unflushedBytes, size);
                    usedSlots++;
                    currentFragmentIndex--;
                    remainingFragments--;
                    anySegmentAdded = true;
                }
            }
        }
        finally
        {
            int unusedSlots = reservedSlots - usedSlots;
            if (unusedSlots > 0)
            {
                try { _spaceSemaphore.Release(unusedSlots); } catch (ObjectDisposedException) { }
            }
        }

        if (anySegmentAdded) _updateActivation.Notify();
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
            if (_queueCount == 0 && unflushedBytes == 0 && !_ackListNotEmpty)
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

            while (_queueCount > 0)
            {
                _queueArray[_queueHead].Data.Release();
                _queueArray[_queueHead] = default;
                _queueHead = (_queueHead + 1) % _queueArray.Length;
                _queueCount--;
            }


            _transportClosed = true;
            Interlocked.Exchange(ref _unflushedBytes, 0);

            // Wake up waiters
            int currentCount = _spaceSemaphore.CurrentCount;
            int toRelease = _capacity - currentCount;
            if (toRelease > 0)
            {
                try { _spaceSemaphore.Release(toRelease); } catch (ObjectDisposedException) { } catch (System.Threading.SemaphoreFullException) { }
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