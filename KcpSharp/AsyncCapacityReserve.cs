using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace KcpSharp;

internal sealed class AsyncCapacityReserve : IDisposable
{
    private volatile int _currentCount;
    private readonly int _maxCapacity;
    private readonly System.Threading.Lock _syncRoot = new();

    private readonly LinkedList<Waiter> _waiters = new();
    private bool _disposed;

    public AsyncCapacityReserve(int capacity)
    {
        _maxCapacity = capacity;
        _currentCount = capacity;
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
            if (_disposed) return;

            while (_waiters.First is not null)
            {
                var waiterNode = _waiters.First;
                var waiter = waiterNode.Value;
                if (_currentCount >= waiter.Count)
                {
                    if (TryReserve(waiter.Count))
                    {
                        _waiters.RemoveFirst();
                        waiter.Node = null;
                        waiter.Complete();
                    }
                    else
                    {
                        // Someone else took the capacity lock-free. We wait again.
                        break;
                    }
                }
                else
                {
                    // Not enough capacity for the front waiter. Stop checking.
                    break;
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

            // Double-check inside lock
            if (TryReserve(count))
                return new ValueTask<bool>(true);

            var waiter = WaiterPool.Rent();
            waiter.Initialize(this, count, cancellationToken);
            waiter.Node = _waiters.AddLast(waiter);

            return waiter.Task;
        }
    }

    internal bool TryRemoveWaiter(Waiter waiter)
    {
        lock (_syncRoot)
        {
            if (_disposed || waiter.Node == null) return false;

            if (waiter.Node.List == _waiters)
            {
                _waiters.Remove(waiter.Node);
                waiter.Node = null;
                return true;
            }

            return false;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;

            while (_waiters.First is not null)
            {
                var waiter = _waiters.First.Value;
                _waiters.RemoveFirst();
                waiter.Node = null;
                waiter.DisposeWithException(new ObjectDisposedException(nameof(AsyncCapacityReserve)));
            }
        }
    }

    private static void ThrowSemaphoreFullException() => throw new SemaphoreFullException();

    internal sealed class Waiter : IValueTaskSource<bool>
    {
        private ManualResetValueTaskSourceCore<bool> _mrvtsc;
        private AsyncCapacityReserve? _parent;
        private int _count;
        private CancellationTokenRegistration _cancellationRegistration;
        private CancellationToken _cancellationToken;
        private bool _released;
        private readonly System.Threading.Lock _syncRoot = new();
        internal LinkedListNode<Waiter>? Node;

        public Waiter()
        {
            _mrvtsc = new ManualResetValueTaskSourceCore<bool> { RunContinuationsAsynchronously = true };
        }

        public int Count => _count;
        public ValueTask<bool> Task => new ValueTask<bool>(this, _mrvtsc.Version);

        public void Initialize(AsyncCapacityReserve parent, int count, CancellationToken cancellationToken)
        {
            _parent = parent;
            _count = count;
            _cancellationToken = cancellationToken;
            _released = false;
            _mrvtsc.Reset();

            if (cancellationToken.CanBeCanceled)
            {
                _cancellationRegistration = cancellationToken.UnsafeRegister(state => ((Waiter)state!).CancelWait(), this);
            }
        }

        public void Complete()
        {
            lock (_syncRoot)
            {
                if (_released) return;
                _released = true;
                _cancellationRegistration.Dispose();
                _mrvtsc.SetResult(true);
            }
            ReturnToPool();
        }

        public void DisposeWithException(Exception ex)
        {
            lock (_syncRoot)
            {
                if (_released) return;
                _released = true;
                _cancellationRegistration.Dispose();
                _mrvtsc.SetException(ex);
            }
            ReturnToPool();
        }

        private void CancelWait()
        {
            bool removed = false;
            var parent = _parent;
            if (parent != null)
            {
                removed = parent.TryRemoveWaiter(this);
            }

            if (removed)
            {
                lock (_syncRoot)
                {
                    if (!_released)
                    {
                        _released = true;
                        _cancellationRegistration.Dispose();
                        _mrvtsc.SetException(new OperationCanceledException(_cancellationToken));
                    }
                }
                ReturnToPool();
            }
        }

        private void ReturnToPool()
        {
            _parent = null;
            _cancellationToken = default;
            WaiterPool.Return(this);
        }

        bool IValueTaskSource<bool>.GetResult(short token) => _mrvtsc.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _mrvtsc.GetStatus(token);
        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _mrvtsc.OnCompleted(continuation, state, token, flags);
    }

    private static class WaiterPool
    {
        private const int MaxPoolSize = 2048;
        private static readonly System.Collections.Concurrent.ConcurrentQueue<Waiter> s_pool = new();
        private static int s_poolCount;

        public static Waiter Rent()
        {
            if (s_pool.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref s_poolCount);
                return item;
            }
            return new Waiter();
        }

        public static void Return(Waiter item)
        {
            while (true)
            {
                int currentCount = s_poolCount;
                if (currentCount >= MaxPoolSize) return;
                if (Interlocked.CompareExchange(ref s_poolCount, currentCount + 1, currentCount) == currentCount)
                {
                    s_pool.Enqueue(item);
                    return;
                }
            }
        }
    }
}
