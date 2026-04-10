using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace KcpSharp;

internal sealed class AsyncCapacityReserve : IValueTaskSource<bool>
{
    private volatile int _currentCount;
    private readonly int _maxCapacity;
    private readonly System.Threading.Lock _syncRoot = new();

    private ManualResetValueTaskSourceCore<bool> _mrvtsc;
    private bool _isWaiting;
    private int _waitForCount;
    private CancellationTokenRegistration _cancellationRegistration;
    private CancellationToken _cancellationToken;
    private bool _disposed;

    public AsyncCapacityReserve(int capacity)
    {
        _maxCapacity = capacity;
        _currentCount = capacity;
        _mrvtsc = new ManualResetValueTaskSourceCore<bool> { RunContinuationsAsynchronously = true };
    }

    public int CurrentCount => _currentCount;

    public bool TryReserve(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return true;

        while (true)
        {
            int current = _currentCount;
            if (current < count) return false;

            if (Interlocked.CompareExchange(ref _currentCount, current - count, current) == current)
                return true;
        }
    }

    public void Release(int count = 1)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;

        int newCount;
        while (true)
        {
            int current = _currentCount;
            newCount = current + count;
            if (newCount > _maxCapacity) ThrowSemaphoreFullException();

            if (Interlocked.CompareExchange(ref _currentCount, newCount, current) == current)
                break;
        }

        CheckWaiters();
    }

    private void CheckWaiters()
    {
        lock (_syncRoot)
        {
            if (_isWaiting && _currentCount >= _waitForCount && !_disposed)
            {
                if (TryReserve(_waitForCount))
                {
                    _isWaiting = false;
                    _cancellationRegistration.Dispose();
                    // Call SetResult inside the lock so that if another thread calls WaitAsync,
                    // it doesn't call _mrvtsc.Reset() concurrently with SetResult.
                    _mrvtsc.SetResult(true);
                }
            }
        }
    }

    public ValueTask<bool> WaitAsync(int count, CancellationToken cancellationToken)
    {
        if (TryReserve(count))
            return new ValueTask<bool>(true);

        lock (_syncRoot)
        {
            if (_disposed)
                return new ValueTask<bool>(Task.FromException<bool>(new ObjectDisposedException(nameof(AsyncCapacityReserve))));

            if (TryReserve(count))
                return new ValueTask<bool>(true);

            if (_isWaiting)
                throw new InvalidOperationException("Concurrent waits are not supported.");

            _isWaiting = true;
            _waitForCount = count;
            _mrvtsc.Reset();
            _cancellationToken = cancellationToken;

            if (cancellationToken.CanBeCanceled)
            {
                _cancellationRegistration = cancellationToken.UnsafeRegister(state => ((AsyncCapacityReserve)state!).CancelWait(), this);
            }

            return new ValueTask<bool>(this, _mrvtsc.Version);
        }
    }

    private void CancelWait()
    {
        lock (_syncRoot)
        {
            if (!_isWaiting) return;
            _isWaiting = false;
            _cancellationRegistration.Dispose();
        }
        _mrvtsc.SetException(new OperationCanceledException(_cancellationToken));
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            if (_isWaiting)
            {
                _isWaiting = false;
                _cancellationRegistration.Dispose();
                _mrvtsc.SetException(new ObjectDisposedException(nameof(AsyncCapacityReserve)));
            }
        }
    }

    private static void ThrowSemaphoreFullException() => throw new SemaphoreFullException();

    bool IValueTaskSource<bool>.GetResult(short token) => _mrvtsc.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _mrvtsc.GetStatus(token);
    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _mrvtsc.OnCompleted(continuation, state, token, flags);
}
