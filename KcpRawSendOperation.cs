using System.Diagnostics;
using System.Threading.Tasks.Sources;

namespace HyacineCore.Server.Kcp.KcpSharp;

internal sealed class KcpRawSendOperation : IValueTaskSource<bool>, IDisposable
{
    private readonly AsyncAutoResetEvent<int> _notification;

    private bool _activeWait;
    private ReadOnlyMemory<byte> _buffer;
    private CancellationTokenRegistration _cancellationRegistration;
    private CancellationToken _cancellationToken;
    private bool _disposed;
    private ManualResetValueTaskSourceCore<bool> _mrvtsc;
    private bool _signaled;

    private bool _transportClosed;

    private readonly object _syncRoot = new object();

    public KcpRawSendOperation(AsyncAutoResetEvent<int> notification)
    {
        _notification = notification;

        _mrvtsc = new ManualResetValueTaskSourceCore<bool>
        {
            RunContinuationsAsynchronously = true
        };
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            if (_activeWait && !_signaled)
            {
                ClearPreviousOperation();
                _mrvtsc.SetResult(false);
            }

            _disposed = true;
            _transportClosed = true;
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

        _notification.Set(buffer.Length);
        return new ValueTask<bool>(this, token);
    }

    public bool CancelPendingOperation(Exception? innerException, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (_activeWait && !_signaled)
            {
                ClearPreviousOperation();
                _mrvtsc.SetException(
                    ThrowHelper.NewOperationCanceledExceptionForCancelPendingSend(innerException, cancellationToken));
                return true;
            }
        }

        return false;
    }

    private void SetCanceled()
    {
        lock (_syncRoot)
        {
            if (_activeWait && !_signaled)
            {
                var cancellationToken = _cancellationToken;
                ClearPreviousOperation();
                _mrvtsc.SetException(new OperationCanceledException(cancellationToken));
            }
        }
    }

    private void ClearPreviousOperation()
    {
        _signaled = true;
        _buffer = default;
        _cancellationToken = default;
    }

    public bool TryConsume(Memory<byte> buffer, out int bytesWritten)
    {
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed)
            {
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
                _mrvtsc.SetException(ThrowHelper.NewMessageTooLargeForBufferArgument());
                bytesWritten = 0;
                return false;
            }

            source.CopyTo(buffer);
            bytesWritten = source.Length;
            ClearPreviousOperation();
            _mrvtsc.SetResult(true);
            return true;
        }
    }

    /// <summary>
    ///     Mark the underlying transport as closed. Abort all active send or receive operations.
    ///     Note: This method signals a graceful shutdown without freeing underlying resources,
    ///     unlike <see cref="Dispose()" /> which signals the closure and also releases resources.
    /// </summary>
    public void SetTransportClosed()
    {
        lock (_syncRoot)
        {
            if (_transportClosed || _disposed) return;
            if (_activeWait && !_signaled)
            {
                ClearPreviousOperation();
                _mrvtsc.SetResult(false);
            }

            _transportClosed = true;
        }
    }
}