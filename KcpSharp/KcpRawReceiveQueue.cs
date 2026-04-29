using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace KcpSharp;

internal sealed class KcpRawReceiveQueue : IValueTaskSource<KcpConversationReceiveResult>, IDisposable
{
    private readonly IKcpBufferPool _bufferPool;
    private readonly int _capacity;

    private readonly KcpBuffer[] _queue;
    private int _head;
    private int _tail;
    private int _count;

    private readonly System.Threading.Lock _syncRoot = new();
    private ManualResetValueTaskSourceCore<KcpConversationReceiveResult> _mrvtsc;

    private bool _activeWait;
    private bool _signaled;
    private bool _bufferProvided;
    private Memory<byte> _buffer;
    private CancellationToken _cancellationToken;
    private CancellationTokenRegistration _cancellationRegistration;

    private bool _transportClosed;
    private bool _disposed;

    public KcpRawReceiveQueue(IKcpBufferPool bufferPool, int capacity)
    {
        _bufferPool = bufferPool;
        _capacity = capacity;

        int pow2Capacity = 16;
        while (pow2Capacity < capacity) pow2Capacity *= 2;
        _queue = new KcpBuffer[pow2Capacity];

        _mrvtsc = new ManualResetValueTaskSourceCore<KcpConversationReceiveResult>();
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
            if (_count == 0)
            {
                result = new KcpConversationReceiveResult(0);
                return false;
            }

            result = new KcpConversationReceiveResult(_queue[_head].Length);
            return true;
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

            if (_count > 0)
                return new ValueTask<KcpConversationReceiveResult>(
                    new KcpConversationReceiveResult(_queue[_head].Length));

            _activeWait = true;
            Debug.Assert(!_signaled);
            _bufferProvided = false;
            _buffer = default;
            _cancellationToken = cancellationToken;

            token = _mrvtsc.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpRawReceiveQueue?)state)!.SetCanceled(), this);

        return new ValueTask<KcpConversationReceiveResult>(this, token);
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
            if (_count == 0)
            {
                result = new KcpConversationReceiveResult(0);
                return false;
            }

            ref var source = ref _queue[_head];
            if (buffer.Length < source.Length) ThrowHelper.ThrowBufferTooSmall();

            source.DataRegion.Span.CopyTo(buffer);
            result = new KcpConversationReceiveResult(source.Length);

            source.Release();
            source = default;
            _head = (_head + 1) & (_queue.Length - 1);
            _count--;

            return true;
        }
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

            if (_count > 0)
            {
                ref var source = ref _queue[_head];
                var length = source.Length;
                if (buffer.Length < source.Length)
                    return new ValueTask<KcpConversationReceiveResult>(
                        Task.FromException<KcpConversationReceiveResult>(
                            ThrowHelper.NewBufferTooSmallForBufferArgument()));

                source.DataRegion.CopyTo(buffer);
                source.Release();
                source = default;
                _head = (_head + 1) & (_queue.Length - 1);
                _count--;

                return new ValueTask<KcpConversationReceiveResult>(new KcpConversationReceiveResult(length));
            }

            _activeWait = true;
            Debug.Assert(!_signaled);
            _bufferProvided = true;
            _buffer = buffer;
            _cancellationToken = cancellationToken;

            token = _mrvtsc.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpRawReceiveQueue?)state)!.SetCanceled(), this);

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
                ClearPreviousOperation();
                exceptionToSet = ThrowHelper.NewOperationCanceledExceptionForCancelPendingReceive(innerException, cancellationToken);
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
        _bufferProvided = false;
        _buffer = default;
        _cancellationToken = default;
    }

    internal void Enqueue(ReadOnlySpan<byte> buffer)
    {
        bool executeSetException = false;
        bool executeSetResult = false;
        KcpConversationReceiveResult resultToSet = default;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return;

            if (_count > 0 || !_activeWait)
            {
                if (_count >= _capacity) return;

                var owner = _bufferPool.Rent(new KcpBufferPoolRentOptions(buffer.Length, false));
                _queue[_tail] = KcpBuffer.CreateFromSpan(owner, buffer);
                _tail = (_tail + 1) & (_queue.Length - 1);
                _count++;
                return;
            }

            if (!_bufferProvided)
            {
                var owner = _bufferPool.Rent(new KcpBufferPoolRentOptions(buffer.Length, false));
                _queue[_tail] = KcpBuffer.CreateFromSpan(owner, buffer);
                _tail = (_tail + 1) & (_queue.Length - 1);
                _count++;

                ClearPreviousOperation();
                resultToSet = new KcpConversationReceiveResult(buffer.Length);
                executeSetResult = true;
            }
            else if (buffer.Length > _buffer.Length)
            {
                var owner = _bufferPool.Rent(new KcpBufferPoolRentOptions(buffer.Length, false));
                _queue[_tail] = KcpBuffer.CreateFromSpan(owner, buffer);
                _tail = (_tail + 1) & (_queue.Length - 1);
                _count++;

                ClearPreviousOperation();
                executeSetException = true;
            }
            else
            {
                buffer.CopyTo(_buffer.Span);
                ClearPreviousOperation();
                resultToSet = new KcpConversationReceiveResult(buffer.Length);
                executeSetResult = true;
            }
        }

        if (executeSetException)
        {
            _mrvtsc.SetException(ThrowHelper.NewBufferTooSmallForBufferArgument());
        }
        else if (executeSetResult)
        {
            _mrvtsc.SetResult(resultToSet);
        }
    }

    /// <summary>
    ///     Mark the underlying transport as closed. Abort all active send or receive operations.
    ///     Note: This method signals a graceful shutdown without freeing underlying resources,
    ///     unlike <see cref="Dispose()" /> which signals the closure and also releases resources.
    /// </summary>
    internal void SetTransportClosed()
    {
        bool executeSetResult = false;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return;
            if (_activeWait && !_signaled)
            {
                ClearPreviousOperation();
                executeSetResult = true;
            }

            while (_count > 0)
            {
                _queue[_head].Release();
                _queue[_head] = default;
                _head = (_head + 1) & (_queue.Length - 1);
                _count--;
            }
            _transportClosed = true;
        }

        if (executeSetResult)
        {
            _mrvtsc.SetResult(default);
        }
    }
}
