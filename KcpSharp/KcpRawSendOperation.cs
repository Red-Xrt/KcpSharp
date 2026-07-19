using System.Diagnostics;
using System.Threading.Tasks.Sources;

namespace KcpSharp;

internal sealed class KcpRawSendOperation : IValueTaskSource<bool>, IDisposable
{
    private readonly System.Threading.Channels.Channel<int> _notification;

    private bool _activeWait;
    private ReadOnlyMemory<byte> _buffer;
    private CancellationTokenRegistration _cancellationRegistration;
    private CancellationToken _cancellationToken;
    private bool _disposed;
    private ManualResetValueTaskSourceCore<bool> _mrvtsc;
    private bool _signaled;

    private bool _transportClosed;

    private readonly System.Threading.Lock _syncRoot = new();

    public KcpRawSendOperation(System.Threading.Channels.Channel<int> notification)
    {
        _notification = notification;

        _mrvtsc = new ManualResetValueTaskSourceCore<bool>
        {
            RunContinuationsAsynchronously = true
        };
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
                ReleaseActiveWaitSlot();
                executeSetResult = true;
            }

            _disposed = true;
            _transportClosed = true;
        }

        if (executeSetResult)
        {
            _mrvtsc.SetResult(false);
        }
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

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
    {
        return _mrvtsc.GetStatus(token);
    }

    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        _mrvtsc.OnCompleted(continuation, state, token, flags);
    }

    public ValueTask<bool> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
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
            _buffer = buffer;
            _cancellationToken = cancellationToken;
            token = _mrvtsc.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpRawSendOperation?)state)!.SetCanceled(), this);

        _notification.Writer.TryWrite(buffer.Length);
        return new ValueTask<bool>(this, token);
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
                ReleaseActiveWaitSlot();
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
                ReleaseActiveWaitSlot();
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
        _buffer = default;
        _cancellationToken = default;
    }

    private void ReleaseActiveWaitSlot()
    {
        _activeWait = false;
        // Unregister (non-blocking) instead of Dispose: this runs under _syncRoot, and Dispose would
        // block waiting for a concurrently-firing SetCanceled callback that also needs _syncRoot -> deadlock.
        // The _signaled flag already guarantees exactly-once completion.
        _cancellationRegistration.Unregister();
        _cancellationRegistration = default;
    }

    public bool TryConsume(Memory<byte> buffer, out int bytesWritten)
    {
        bool executeSetException = false;
        bool executeSetResult = false;
        bool executeSetResultOnClose = false;
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed)
            {
                if (_activeWait && !_signaled)
                {
                    ClearPreviousOperation();
                    ReleaseActiveWaitSlot();
                    executeSetResultOnClose = true;
                }

                bytesWritten = 0;
                return false;
            }

            if (!_activeWait)
            {
                bytesWritten = 0;
                return false;
            }

            var source = _buffer;
            if (source.Length > buffer.Length)
            {
                ClearPreviousOperation();
                executeSetException = true;
                bytesWritten = 0;
            }
            else
            {
                source.CopyTo(buffer);
                bytesWritten = source.Length;
                ClearPreviousOperation();
                executeSetResult = true;
            }
        }

        if (executeSetException)
        {
            _mrvtsc.SetException(ThrowHelper.NewMessageTooLargeForBufferArgument());
            return false;
        }

        if (executeSetResultOnClose)
        {
            _mrvtsc.SetResult(false);
            return false;
        }

        if (executeSetResult)
        {
            _mrvtsc.SetResult(true);
            return true;
        }

        return false;
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
                ClearPreviousOperation();
                ReleaseActiveWaitSlot();
                executeSetResult = true;
            }

            _transportClosed = true;
        }

        if (executeSetResult)
        {
            _mrvtsc.SetResult(false);
        }
    }
}