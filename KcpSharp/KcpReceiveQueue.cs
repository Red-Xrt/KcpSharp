using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace KcpSharp;

internal struct ReceiveQueueSlot
{
    public KcpBuffer Data;
    public byte Fragment;
}

internal sealed class KcpReceiveQueue : IValueTaskSource<KcpConversationReceiveResult>, IValueTaskSource<int>, IValueTaskSource<bool>,
    IValueTaskSource, IDisposable
{
    /// <summary>
    ///     A marker used in StreamMode to indicate that a fragment was only partially consumed
    ///     into the caller's buffer. In Non-Stream (Datagram) Mode, it is mathematically
    ///     unreachable because datagram consumes ensure sufficient buffer size prior to copy.
    /// </summary>
    private const byte PartiallyConsumedFragment = 255;

    private readonly ReceiveQueueSlot[] _slots;
    private int _head;
    private int _tail;

    private readonly bool _stream;
    private readonly System.Threading.Lock _syncRoot = new();
    private ManualResetValueTaskSourceCore<KcpConversationReceiveResult> _mrvtsc;
    private ManualResetValueTaskSourceCore<int> _mrvtscInt;
    private ManualResetValueTaskSourceCore<bool> _mrvtscBool;
    private ManualResetValueTaskSourceCore<bool> _mrvtscVoid; // Using bool underneath for Void because of standard MRVTSC

    private int _completedPacketsCount;
    private int _totalBytesInQueue;
    private int _totalSegmentsInQueue;

    private bool _activeWait;
    private bool _signaled;
    private int _operationMode; // 0: receive, 1: peek, 2: wait for data, 3: receive to writer
    private Memory<byte> _buffer;
    private System.Buffers.IBufferWriter<byte>? _writer;
    private int _minimumBytes;
    private int _minimumSegments;
    private CancellationToken _cancellationToken;
    private CancellationTokenRegistration _cancellationRegistration;

    private bool _transportClosed;
    private bool _disposed;

    public KcpReceiveQueue(bool stream, int capacity)
    {
        _stream = stream;

        int pow2Capacity = 16;
        while (pow2Capacity <= capacity) pow2Capacity *= 2;
        _slots = new ReceiveQueueSlot[pow2Capacity];

        _mrvtsc = new ManualResetValueTaskSourceCore<KcpConversationReceiveResult>();
        _mrvtscInt = new ManualResetValueTaskSourceCore<int>();
        _mrvtscBool = new ManualResetValueTaskSourceCore<bool>();
        _mrvtscVoid = new ManualResetValueTaskSourceCore<bool>();
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            SetTransportClosed();
            _disposed = true;
        }
    }

    KcpConversationReceiveResult IValueTaskSource<KcpConversationReceiveResult>.GetResult(short token)
    {
        _cancellationRegistration.Dispose();

        try
        {
            return _mrvtsc.GetResult(token);
        }
        finally
        {
            lock (_syncRoot)
            {
                _mrvtsc.Reset();
                _activeWait = false;
                _signaled = false;
                _cancellationRegistration = default;
            }
        }
    }

    ValueTaskSourceStatus IValueTaskSource<KcpConversationReceiveResult>.GetStatus(short token)
    {
        return _mrvtsc.GetStatus(token);
    }

    void IValueTaskSource<KcpConversationReceiveResult>.OnCompleted(Action<object?> continuation, object? state,
        short token, ValueTaskSourceOnCompletedFlags flags)
    {
        _mrvtsc.OnCompleted(continuation, state, token, flags);
    }

    int IValueTaskSource<int>.GetResult(short token)
    {
        _cancellationRegistration.Dispose();

        try
        {
            return _mrvtscInt.GetResult(token);
        }
        finally
        {
            lock (_syncRoot)
            {
                _mrvtscInt.Reset();
                _activeWait = false;
                _signaled = false;
                _cancellationRegistration = default;
            }
        }
    }

    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token)
    {
        return _mrvtscInt.GetStatus(token);
    }

    void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        _mrvtscInt.OnCompleted(continuation, state, token, flags);
    }

    bool IValueTaskSource<bool>.GetResult(short token)
    {
        _cancellationRegistration.Dispose();

        try
        {
            return _mrvtscBool.GetResult(token);
        }
        finally
        {
            lock (_syncRoot)
            {
                _mrvtscBool.Reset();
                _activeWait = false;
                _signaled = false;
                _cancellationRegistration = default;
            }
        }
    }

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
    {
        return _mrvtscBool.GetStatus(token);
    }

    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        _mrvtscBool.OnCompleted(continuation, state, token, flags);
    }

    void IValueTaskSource.GetResult(short token)
    {
        _cancellationRegistration.Dispose();

        try
        {
            _mrvtscVoid.GetResult(token);
        }
        finally
        {
            lock (_syncRoot)
            {
                _mrvtscVoid.Reset();
                _activeWait = false;
                _signaled = false;
                _cancellationRegistration = default;
            }
        }
    }

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
    {
        return _mrvtscVoid.GetStatus(token);
    }

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        _mrvtscVoid.OnCompleted(continuation, state, token, flags);
    }

    internal bool TryPeek(out KcpConversationReceiveResult result)
    {
        lock (_syncRoot)
        {
            if (_disposed || _transportClosed)
            {
                result = default;
                return false;
            }

            if (_activeWait) ThrowHelper.ThrowConcurrentReceiveException();

            if ((!_stream && _completedPacketsCount > 0) || (_stream && _totalBytesInQueue > 0))
            {
                if (CalculatePacketSize(out var bytesRecevied))
                {
                    result = new KcpConversationReceiveResult(bytesRecevied);
                    return true;
                }
            }

            result = new KcpConversationReceiveResult(0);
            return false;
        }
    }

    internal bool TryReceive(Span<byte> buffer, out KcpConversationReceiveResult result)
    {
        lock (_syncRoot)
        {
            if (_disposed || _transportClosed)
            {
                result = default;
                return false;
            }

            if (_activeWait) ThrowHelper.ThrowConcurrentReceiveException();

            if ((!_stream && _completedPacketsCount > 0) || (_stream && _totalBytesInQueue > 0))
            {
                ConsumePacket(buffer, out result, out var bufferTooSmall);
                if (bufferTooSmall) ThrowHelper.ThrowBufferTooSmall();
                return true;
            }

            result = new KcpConversationReceiveResult(0);
            return false;
        }
    }

    internal ValueTask<KcpConversationReceiveResult> WaitToReceiveAsync(CancellationToken cancellationToken)
    {
        short token;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return default;
            if (_activeWait)
                return new ValueTask<KcpConversationReceiveResult>(
                    Task.FromException<KcpConversationReceiveResult>(ThrowHelper.NewConcurrentReceiveException()));
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<KcpConversationReceiveResult>(
                    Task.FromCanceled<KcpConversationReceiveResult>(cancellationToken));

            if ((!_stream && _completedPacketsCount > 0) || (_stream && _totalBytesInQueue > 0))
            {
                if (CalculatePacketSize(out var bytesRecevied))
                    return new ValueTask<KcpConversationReceiveResult>(new KcpConversationReceiveResult(bytesRecevied));
            }

            _activeWait = true;
            Debug.Assert(!_signaled);
            _operationMode = 1;
            _cancellationToken = cancellationToken;

            token = _mrvtsc.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpReceiveQueue?)state)!.SetCanceled(), this);

        return new ValueTask<KcpConversationReceiveResult>(this, token);
    }

    internal ValueTask<bool> WaitForAvailableDataAsync(int minimumBytes, int minimumSegments,
        CancellationToken cancellationToken)
    {
        if (minimumBytes < 0) ThrowHelper.ThrowArgumentOutOfRangeException(nameof(minimumBytes));
        if (minimumSegments < 0) ThrowHelper.ThrowArgumentOutOfRangeException(nameof(minimumSegments));

        short token;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return new ValueTask<bool>(false);
            if (_activeWait)
                return new ValueTask<bool>(Task.FromException<bool>(ThrowHelper.NewConcurrentReceiveException()));
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken));

            if (CheckQueueSize(minimumBytes, minimumSegments)) return new ValueTask<bool>(true);

            _activeWait = true;
            Debug.Assert(!_signaled);
            _operationMode = 2;
            _minimumBytes = minimumBytes;
            _minimumSegments = minimumSegments;
            _cancellationToken = cancellationToken;

            token = _mrvtscBool.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpReceiveQueue?)state)!.SetCanceled(), this);

        return new ValueTask<bool>(this, token);
    }

    internal ValueTask<KcpConversationReceiveResult> ReceiveAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        short token;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return default;
            if (_activeWait)
                return new ValueTask<KcpConversationReceiveResult>(
                    Task.FromException<KcpConversationReceiveResult>(ThrowHelper.NewConcurrentReceiveException()));
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<KcpConversationReceiveResult>(
                    Task.FromCanceled<KcpConversationReceiveResult>(cancellationToken));

            if ((!_stream && _completedPacketsCount > 0) || (_stream && _totalBytesInQueue > 0))
            {
                ConsumePacket(buffer.Span, out var result, out var bufferTooSmall);
                if (bufferTooSmall)
                    return new ValueTask<KcpConversationReceiveResult>(
                        Task.FromException<KcpConversationReceiveResult>(
                            ThrowHelper.NewBufferTooSmallForBufferArgument()));
                return new ValueTask<KcpConversationReceiveResult>(result);
            }

            _activeWait = true;
            Debug.Assert(!_signaled);
            _operationMode = 0;
            _buffer = buffer;
            _cancellationToken = cancellationToken;

            token = _mrvtsc.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpReceiveQueue?)state)!.SetCanceled(), this);

        return new ValueTask<KcpConversationReceiveResult>(this, token);
    }

    internal ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        short token;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return default;
            if (_activeWait)
                return new ValueTask<int>(Task.FromException<int>(ThrowHelper.NewConcurrentReceiveException()));
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<int>(Task.FromCanceled<int>(cancellationToken));

            if ((!_stream && _completedPacketsCount > 0) || (_stream && _totalBytesInQueue > 0))
            {
                ConsumePacket(buffer.Span, out var result, out var bufferTooSmall);
                if (bufferTooSmall)
                    return new ValueTask<int>(
                        Task.FromException<int>(ThrowHelper.NewBufferTooSmallForBufferArgument()));
                return new ValueTask<int>(result.BytesReceived);
            }

            _activeWait = true;
            Debug.Assert(!_signaled);
            _operationMode = 4; // ReadAsync mode
            _buffer = buffer;
            _cancellationToken = cancellationToken;

            token = _mrvtscInt.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpReceiveQueue?)state)!.SetCanceled(), this);

        return new ValueTask<int>(this, token);
    }

    internal ValueTask<KcpConversationReceiveResult> ReceiveToWriterAsync(System.Buffers.IBufferWriter<byte> writer,
        CancellationToken cancellationToken = default)
    {
        short token;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return default;
            if (_activeWait)
                return new ValueTask<KcpConversationReceiveResult>(
                    Task.FromException<KcpConversationReceiveResult>(ThrowHelper.NewConcurrentReceiveException()));
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<KcpConversationReceiveResult>(
                    Task.FromCanceled<KcpConversationReceiveResult>(cancellationToken));

            if ((!_stream && _completedPacketsCount > 0) || (_stream && _totalBytesInQueue > 0))
            {
                ConsumePacketToWriter(writer, out var result);
                return new ValueTask<KcpConversationReceiveResult>(result);
            }

            _activeWait = true;
            Debug.Assert(!_signaled);
            _operationMode = 3;
            _writer = writer;
            _cancellationToken = cancellationToken;

            token = _mrvtsc.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpReceiveQueue?)state)!.SetCanceled(), this);

        return new ValueTask<KcpConversationReceiveResult>(this, token);
    }

    internal bool CancelPendingOperation(Exception? innerException, CancellationToken cancellationToken)
    {
        bool executeSetException = false;
        Exception? exceptionToSet = null;
        lock (_syncRoot)
        {
            if (_activeWait && !_signaled)
            {
                ClearPreviousOperation(true);
                exceptionToSet = ThrowHelper.NewOperationCanceledExceptionForCancelPendingReceive(innerException, cancellationToken);
                executeSetException = true;
            }
        }

        if (executeSetException)
        {
            if (_operationMode == 0 || _operationMode == 1 || _operationMode == 3)
                _mrvtsc.SetException(exceptionToSet!);
            else if (_operationMode == 2)
                _mrvtscBool.SetException(exceptionToSet!);
            else if (_operationMode == 4)
                _mrvtscInt.SetException(exceptionToSet!);
            return true;
        }

        return false;
    }

    private void SetCanceled()
    {
        bool executeSetException = false;
        Exception? exceptionToSet = null;
        int operationMode = 0;
        lock (_syncRoot)
        {
            if (_activeWait && !_signaled)
            {
                var cancellationToken = _cancellationToken;
                operationMode = _operationMode;
                ClearPreviousOperation(true);
                exceptionToSet = new OperationCanceledException(cancellationToken);
                executeSetException = true;
            }
        }

        if (executeSetException)
        {
            if (operationMode == 0 || operationMode == 1 || operationMode == 3)
                _mrvtsc.SetException(exceptionToSet!);
            else if (operationMode == 2)
                _mrvtscBool.SetException(exceptionToSet!);
            else if (operationMode == 4)
                _mrvtscInt.SetException(exceptionToSet!);
        }
    }

    private void ClearPreviousOperation(bool signaled)
    {
        _signaled = signaled;
        _buffer = default;
        _writer = null;
        _cancellationToken = default;
    }

    internal void Enqueue(in KcpBuffer buffer, byte fragment)
    {
        bool executeSetException = false;
        Exception? exceptionToSet = null;
        bool executeSetResult = false;
        KcpConversationReceiveResult resultToSet = default;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return;

            int nextTail = (_tail + 1) & (_slots.Length - 1);
            if (nextTail == _head)
            {
                // Queue full
                KcpMetrics.PacketsDropped.Add(1);
                buffer.Release();
                return;
            }

            _slots[_tail].Data = buffer;
            _slots[_tail].Fragment = fragment;
            _tail = nextTail;

            _totalBytesInQueue += buffer.Length;
            if (fragment == 0 || _stream)
                _totalSegmentsInQueue++;

            if (fragment == 0)
            {
                _completedPacketsCount++;
            }

            if (_activeWait && !_signaled)
            {
                if ((!_stream && _completedPacketsCount > 0) || (_stream && _totalBytesInQueue > 0))
                {
                    TryCompleteReceive(ref executeSetException, ref exceptionToSet, ref executeSetResult, ref resultToSet);
                }
                TryCompleteWaitForData(ref executeSetResult, ref resultToSet);
            }
        }

        if (executeSetException)
        {
            if (_operationMode == 0 || _operationMode == 1 || _operationMode == 3)
                _mrvtsc.SetException(exceptionToSet!);
            else if (_operationMode == 4)
                _mrvtscInt.SetException(exceptionToSet!);
        }
        else if (executeSetResult)
        {
            if (_operationMode == 0 || _operationMode == 1 || _operationMode == 3)
                _mrvtsc.SetResult(resultToSet);
            else if (_operationMode == 2)
                _mrvtscBool.SetResult(true);
            else if (_operationMode == 4)
                _mrvtscInt.SetResult(resultToSet.BytesReceived);
        }
    }

    private void TryCompleteReceive(ref bool executeSetException, ref Exception? exceptionToSet, ref bool executeSetResult, ref KcpConversationReceiveResult resultToSet)
    {
        Debug.Assert(_activeWait && !_signaled);

        if (_operationMode <= 1 || _operationMode == 4)
        {
            Debug.Assert(_operationMode == 0 || _operationMode == 1 || _operationMode == 4);
            ConsumePacket(_buffer.Span, out var result, out var bufferTooSmall);
            ClearPreviousOperation(true);
            if (bufferTooSmall)
            {
                exceptionToSet = ThrowHelper.NewBufferTooSmallForBufferArgument();
                executeSetException = true;
            }
            else
            {
                resultToSet = result;
                executeSetResult = true;
            }
        }
        else if (_operationMode == 3) // Receive to writer
        {
            var writer = _writer!;
            ConsumePacketToWriter(writer, out var result);
            ClearPreviousOperation(true);
            resultToSet = result;
            executeSetResult = true;
        }
    }

    private void TryCompleteWaitForData(ref bool executeSetResult, ref KcpConversationReceiveResult resultToSet)
    {
        if (_operationMode == 2)
            if (CheckQueueSize(_minimumBytes, _minimumSegments))
            {
                ClearPreviousOperation(true);
                resultToSet = new KcpConversationReceiveResult(0);
                executeSetResult = true;
            }
    }

    private void ConsumePacketToWriter(System.Buffers.IBufferWriter<byte> writer, out KcpConversationReceiveResult result)
    {
        if (_head == _tail)
        {
            result = default;
            return;
        }

        var bytesInPacket = 0;

        while (_head != _tail)
        {
            var fragment = _slots[_head].Fragment;
            ref var data = ref _slots[_head].Data;

            if (data.Length > 0)
            {
                var span = writer.GetSpan(data.Length);
                data.DataRegion.Span.CopyTo(span);
                writer.Advance(data.Length);
                bytesInPacket += data.Length;
            }

            // full fragment is consumed
            _totalBytesInQueue -= data.Length;
            if (fragment == 0 || _stream) _totalSegmentsInQueue--;
            data.Release();

            _slots[_head] = default; // clear
            _head = (_head + 1) & (_slots.Length - 1);

            if (fragment == 0 || fragment == PartiallyConsumedFragment) _completedPacketsCount--;

            if (!_stream && fragment == 0) break;
        }

        result = new KcpConversationReceiveResult(bytesInPacket);
    }

    private void ConsumePacket(Span<byte> buffer, out KcpConversationReceiveResult result, out bool bufferTooSmall)
    {
        if (_head == _tail)
        {
            result = default;
            bufferTooSmall = false;
            return;
        }

        // peek
        if (_operationMode == 1)
        {
            if (CalculatePacketSize(out var bytesRecevied))
                result = new KcpConversationReceiveResult(bytesRecevied);
            else
                result = default;
            bufferTooSmall = false;
            return;
        }

        Debug.Assert(_operationMode == 0);

        // ensure buffer is big enough
        var bytesInPacket = 0;
        if (!_stream)
        {
            int current = _head;
            while (current != _tail)
            {
                bytesInPacket += _slots[current].Data.Length;
                if (_slots[current].Fragment == 0) break;
                current = (current + 1) & (_slots.Length - 1);
            }

            if (current == _tail)
            {
                // incomplete packet
                result = default;
                bufferTooSmall = false;
                return;
            }

            if (bytesInPacket > buffer.Length)
            {
                result = default;
                bufferTooSmall = true;
                return;
            }
        }

        var anyDataReceived = false;
        bytesInPacket = 0;

        while (_head != _tail)
        {
            var fragment = _slots[_head].Fragment;
            var originalFragment = fragment; // Cache original fragment to prevent breaking message boundary after marking as 255
            ref var data = ref _slots[_head].Data;

            var sizeToCopy = Math.Min(data.Length, buffer.Length);
            data.DataRegion.Span.Slice(0, sizeToCopy).CopyTo(buffer);
            buffer = buffer.Slice(sizeToCopy);
            bytesInPacket += sizeToCopy;
            anyDataReceived = true;

            if (sizeToCopy != data.Length)
            {
                // partial data is received.
                _slots[_head].Data = data.Consume(sizeToCopy);
                _totalBytesInQueue -= sizeToCopy;

                // Even though the data is only partially consumed, if this is the last fragment of a packet
                // (or if we are in stream mode where boundaries don't matter), the packet itself is considered
                // completed from the queue's boundary tracking perspective because we've started reading it.
                if (fragment == 0 && sizeToCopy > 0)
                {
                    // By setting the fragment to a non-zero value, we prevent it from being counted again later.
                    _slots[_head].Fragment = PartiallyConsumedFragment;
                    // Do not decrement in stream mode, because the partial read means more data is still available
                    // and we shouldn't zero out completedPacketsCount and prevent future reads of the remaining chunk.
                    if (!_stream)
                    {
                        _completedPacketsCount--;
                    }
                }
            }
            else
            {
                // full fragment is consumed
                _totalBytesInQueue -= data.Length;
                if (fragment == 0 || _stream) _totalSegmentsInQueue--;
                data.Release();

                _slots[_head] = default;
                _head = (_head + 1) & (_slots.Length - 1);

                if (fragment == 0 || fragment == PartiallyConsumedFragment) _completedPacketsCount--;
            }

            if (!_stream && originalFragment == 0) break;

            if (sizeToCopy == 0) break;
        }

        if (!anyDataReceived)
        {
            result = new KcpConversationReceiveResult(0);
            bufferTooSmall = false;
        }
        else
        {
            result = new KcpConversationReceiveResult(bytesInPacket);
            bufferTooSmall = false;
        }
    }

    private bool CalculatePacketSize(out int packetSize)
    {
        int current = _head;
        if (current == _tail)
        {
            packetSize = 0;
            return false;
        }

        var bytesRecevied = _slots[current].Data.Length;
        if (_slots[current].Fragment == 0)
        {
            packetSize = bytesRecevied;
            return true;
        }

        current = (current + 1) & (_slots.Length - 1);
        while (current != _tail)
        {
            bytesRecevied += _slots[current].Data.Length;
            if (_slots[current].Fragment == 0)
            {
                packetSize = bytesRecevied;
                return true;
            }

            current = (current + 1) & (_slots.Length - 1);
        }

        // deadlink
        packetSize = 0;
        return false;
    }

    private bool CheckQueueSize(int minimumBytes, int minimumSegments)
    {
        return _totalBytesInQueue >= minimumBytes && _totalSegmentsInQueue >= minimumSegments;
    }

    /// <summary>
    ///     Mark the underlying transport as closed. Abort all active send or receive operations.
    ///     Note: This method signals a graceful shutdown without freeing underlying resources,
    ///     unlike <see cref="Dispose()" /> which signals the closure and also releases resources.
    /// </summary>
    public void SetTransportClosed()
    {
        bool executeSetResult = false;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return;
            if (_activeWait && !_signaled)
            {
                ClearPreviousOperation(true);
                executeSetResult = true;
            }

            while (_head != _tail)
            {
                _slots[_head].Data.Release();
                _slots[_head] = default;
                _head = (_head + 1) & (_slots.Length - 1);
            }
            _head = _tail = 0;
            _totalBytesInQueue = 0;
            _totalSegmentsInQueue = 0;
            _completedPacketsCount = 0;

            _transportClosed = true;
        }

        if (executeSetResult)
        {
            if (_operationMode == 0 || _operationMode == 1 || _operationMode == 3)
                _mrvtsc.SetResult(default);
            else if (_operationMode == 2)
                _mrvtscBool.SetResult(false);
            else if (_operationMode == 4)
                _mrvtscInt.SetResult(0);
        }
    }

    /// <summary>
    ///     Gets the number of complete packets in the receive queue.
    ///     Used to approximate memory usage against the receive window (_rcv_wnd) limit,
    ///     although fragments also consume slots.
    /// </summary>
    public int GetQueueSize()
    {
        return _completedPacketsCount;
    }
}
