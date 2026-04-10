using System.Diagnostics;
using System.Threading.Tasks.Sources;

namespace KcpSharp;

internal sealed class KcpRawReceiveQueue : IValueTaskSource<KcpConversationReceiveResult>, IDisposable
{
    private readonly System.Threading.Lock _syncRoot = new();

    private readonly IKcpBufferPool _bufferPool;
    private readonly int _capacity;
    private readonly LinkedList<KcpBuffer> _queue;
    private readonly LinkedList<KcpBuffer> _recycled;

    private bool _activeWait;
    private Memory<byte> _buffer;
    private bool _bufferProvided;
    private CancellationTokenRegistration _cancellationRegistration;
    private CancellationToken _cancellationToken;
    private bool _disposed;
    private ManualResetValueTaskSourceCore<KcpConversationReceiveResult> _mrvtsc;
    private bool _signaled;

    private bool _transportClosed;

    internal KcpRawReceiveQueue(IKcpBufferPool bufferPool, int capacity)
    {
        _bufferPool = bufferPool;
        _capacity = capacity;
        _queue = new LinkedList<KcpBuffer>();
        _recycled = new LinkedList<KcpBuffer>();
    }

    public void Dispose()
    {
        bool executeSetResult = false;
        lock (_syncRoot)
        {
            if (_disposed) return;
            if (_activeWait && !_signaled)
            {
                ClearPreviousOperation();
                executeSetResult = true;
            }

            var node = _queue.First;
            while (node is not null)
            {
                node.ValueRef.Release();
                node = node.Next;
            }

            _queue.Clear();
            _recycled.Clear();
            _disposed = true;
            _transportClosed = true;
        }

        if (executeSetResult)
        {
            _mrvtsc.SetResult(default);
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
            _mrvtsc.Reset();
            lock (_syncRoot)
            {
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
            var first = _queue.First;
            if (first is null)
            {
                result = new KcpConversationReceiveResult(0);
                return false;
            }

            result = new KcpConversationReceiveResult(first.ValueRef.Length);
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

            var first = _queue.First;
            if (first is not null)
                return new ValueTask<KcpConversationReceiveResult>(
                    new KcpConversationReceiveResult(first.ValueRef.Length));

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
            var first = _queue.First;
            if (first is null)
            {
                result = new KcpConversationReceiveResult(0);
                return false;
            }

            ref var source = ref first.ValueRef;
            if (buffer.Length < source.Length) ThrowHelper.ThrowBufferTooSmall();

            source.DataRegion.Span.CopyTo(buffer);
            result = new KcpConversationReceiveResult(source.Length);

            _queue.RemoveFirst();
            source.Release();
            source = default;
            _recycled.AddLast(first);

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

            var first = _queue.First;
            if (first is not null)
            {
                ref var source = ref first.ValueRef;
                var length = source.Length;
                if (buffer.Length < source.Length)
                    return new ValueTask<KcpConversationReceiveResult>(
                        Task.FromException<KcpConversationReceiveResult>(
                            ThrowHelper.NewBufferTooSmallForBufferArgument()));
                _queue.Remove(first);

                source.DataRegion.CopyTo(buffer);
                source.Release();
                source = default;
                _recycled.AddLast(first);

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

            var queueSize = _queue.Count;
            if (queueSize > 0 || !_activeWait)
            {
                if (queueSize >= _capacity) return;

                var owner = _bufferPool.Rent(new KcpBufferPoolRentOptions(buffer.Length, false));
                _queue.AddLast(AllocateNode(KcpBuffer.CreateFromSpan(owner, buffer)));
                return;
            }

            if (!_bufferProvided)
            {
                var owner = _bufferPool.Rent(new KcpBufferPoolRentOptions(buffer.Length, false));
                _queue.AddLast(AllocateNode(KcpBuffer.CreateFromSpan(owner, buffer)));

                ClearPreviousOperation();
                resultToSet = new KcpConversationReceiveResult(buffer.Length);
                executeSetResult = true;
            }
            else if (buffer.Length > _buffer.Length)
            {
                var owner = _bufferPool.Rent(new KcpBufferPoolRentOptions(buffer.Length, false));
                _queue.AddLast(AllocateNode(KcpBuffer.CreateFromSpan(owner, buffer)));

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

    private LinkedListNode<KcpBuffer> AllocateNode(KcpBuffer buffer)
    {
        var node = _recycled.First;
        if (node is null)
        {
            node = new LinkedListNode<KcpBuffer>(buffer);
        }
        else
        {
            node.ValueRef = buffer;
            _recycled.Remove(node);
        }

        return node;
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

            _recycled.Clear();
            _transportClosed = true;
        }

        if (executeSetResult)
        {
            _mrvtsc.SetResult(default);
        }
    }
}