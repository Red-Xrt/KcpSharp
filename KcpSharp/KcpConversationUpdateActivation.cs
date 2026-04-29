using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace KcpSharp;

internal sealed class KcpConversationUpdateActivation : IValueTaskSource<KcpConversationUpdateNotification>, IDisposable
{
    private bool _activeWait;
    private CancellationTokenRegistration _cancellationRegistration;
    private CancellationToken _cancellationToken;

    private bool _disposed;
    private ManualResetValueTaskSourceCore<KcpConversationUpdateNotification> _mrvtsc;
    private bool _notificationPending;
    private bool _signaled;

    private readonly System.Threading.Lock _syncRoot = new();

    // SPSC Ring Buffer
    private readonly KcpReceiveRingBuffer _ringBuffer;

    internal System.Threading.Lock SyncRoot => _syncRoot;

    public KcpConversationUpdateActivation(int interval, int maxWaitListSize)
    {
        _mrvtsc = new ManualResetValueTaskSourceCore<KcpConversationUpdateNotification>
            { RunContinuationsAsynchronously = true };

        // Must be power of two
        int size = 1;
        while (size < maxWaitListSize && size < 16384) size <<= 1;
        _ringBuffer = new KcpReceiveRingBuffer(size);

        KcpGlobalTickEngine.Register(this, interval);
    }

    public bool HasPendingPackets => _ringBuffer.HasItems;

    public bool HasTimerPending()
    {
        lock (SyncRoot)
        {
            return _notificationPending;
        }
    }

    public void Dispose()
    {
        KcpGlobalTickEngine.Unregister(this);

        bool executeSetResult = false;
        lock (SyncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            if (_activeWait && !_signaled)
            {
                _signaled = true;
                _cancellationToken = default;
                executeSetResult = true;
            }
        }

        if (executeSetResult)
        {
            _mrvtsc.SetResult(new KcpConversationUpdateNotification(null, false));
        }

        _ringBuffer.Dispose();
    }

    public void Notify()
    {
        bool executeSetResult = false;
        lock (SyncRoot)
        {
            if (_disposed) return;
            if (_activeWait && !_signaled)
            {
                _signaled = true;
                _cancellationToken = default;
                executeSetResult = true;
            }
            else
            {
                _notificationPending = true;
            }
        }

        if (executeSetResult)
        {
            _mrvtsc.SetResult(new KcpConversationUpdateNotification(null, false));
        }
    }

    public ValueTask<KcpConversationUpdateNotification> WaitAsync(CancellationToken cancellationToken)
    {
        short token;
        lock (SyncRoot)
        {
            if (_disposed)
                return new ValueTask<KcpConversationUpdateNotification>(Task.FromException<KcpConversationUpdateNotification>(new ObjectDisposedException(nameof(KcpConversation))));
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<KcpConversationUpdateNotification>(Task.FromCanceled<KcpConversationUpdateNotification>(cancellationToken));
            if (_activeWait)
                return new ValueTask<KcpConversationUpdateNotification>(Task.FromException<KcpConversationUpdateNotification>(ThrowHelper.NewConcurrentReceiveException()));

            if (_ringBuffer.TryDequeue(out var packet, out var bufferOwner))
            {
                // We return the packet, but if a timer notification is pending, we leave it pending
                // so that the update loop will process it when draining packets finishes.
                return new ValueTask<KcpConversationUpdateNotification>(new KcpConversationUpdateNotification(packet, bufferOwner, true));
            }

            if (_notificationPending)
            {
                _notificationPending = false;
                return new ValueTask<KcpConversationUpdateNotification>(new KcpConversationUpdateNotification(null, false));
            }

            _activeWait = true;
            Debug.Assert(!_signaled);
            _cancellationToken = cancellationToken;
            token = _mrvtsc.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpConversationUpdateActivation?)state)!.SetCanceled(), this);

        return new ValueTask<KcpConversationUpdateNotification>(this, token);
    }

    private void SetCanceled()
    {
        bool executeSetException = false;
        Exception? exceptionToSet = null;
        lock (SyncRoot)
        {
            if (_activeWait && !_signaled)
            {
                var cancellationToken = _cancellationToken;
                _signaled = true;
                _cancellationToken = default;
                exceptionToSet = new OperationCanceledException(cancellationToken);
                executeSetException = true;
            }
        }

        if (executeSetException)
        {
            _mrvtsc.SetException(exceptionToSet!);
        }
    }

    ValueTaskSourceStatus IValueTaskSource<KcpConversationUpdateNotification>.GetStatus(short token)
    {
        return _mrvtsc.GetStatus(token);
    }

    void IValueTaskSource<KcpConversationUpdateNotification>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        _mrvtsc.OnCompleted(continuation, state, token, flags);
    }

    KcpConversationUpdateNotification IValueTaskSource<KcpConversationUpdateNotification>.GetResult(short token)
    {
        _cancellationRegistration.Dispose();
        try
        {
            return _mrvtsc.GetResult(token);
        }
        finally
        {
            _mrvtsc.Reset();
            lock (SyncRoot)
            {
                _activeWait = false;
                _signaled = false;
                _cancellationRegistration = default;
            }
        }
    }

    public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, System.Buffers.IMemoryOwner<byte>? bufferOwner, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            bufferOwner?.Dispose();
            return new ValueTask(Task.FromException(new ObjectDisposedException(nameof(KcpConversation))));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            bufferOwner?.Dispose();
            return new ValueTask(Task.FromCanceled(cancellationToken));
        }

        bool enqueued = _ringBuffer.TryEnqueue(packet, bufferOwner);
        if (!enqueued)
        {
            KcpMetrics.WaitListPacketsDropped.Add(1);
            bufferOwner?.Dispose();
            return default;
        }

        Notify();
        return default;
    }
}

internal sealed class KcpReceiveRingBuffer : IDisposable
{
    private readonly struct Slot
    {
        public readonly ReadOnlyMemory<byte> Packet;
        public readonly System.Buffers.IMemoryOwner<byte>? Owner;
        public readonly bool HasValue;

        public Slot(ReadOnlyMemory<byte> packet, System.Buffers.IMemoryOwner<byte>? owner)
        {
            Packet = packet;
            Owner = owner;
            HasValue = true;
        }
    }

    private readonly Slot[] _slots;
    private readonly int _mask;
    private volatile uint _head;
    private volatile uint _tail;
    private SpinLock _spinLock = new SpinLock(false);

    public KcpReceiveRingBuffer(int capacity)
    {
        _slots = new Slot[capacity];
        _mask = capacity - 1;
    }

    private bool _disposed;

    public bool TryEnqueue(ReadOnlyMemory<byte> packet, System.Buffers.IMemoryOwner<byte>? owner)
    {
        bool lockTaken = false;
        try
        {
            _spinLock.Enter(ref lockTaken);
            if (_disposed) return false;
            if (_tail - _head >= (uint)_slots.Length) return false;

            _slots[_tail & _mask] = new Slot(packet, owner);
            _tail++;
            return true;
        }
        finally
        {
            if (lockTaken) _spinLock.Exit(false);
        }
    }

    public bool TryDequeue(out ReadOnlyMemory<byte> packet, out System.Buffers.IMemoryOwner<byte>? owner)
    {
        bool lockTaken = false;
        try
        {
            _spinLock.Enter(ref lockTaken);
            if (_head == _tail)
            {
                packet = default;
                owner = null;
                return false;
            }

            uint index = _head & (uint)_mask;
            var slot = _slots[index];
            packet = slot.Packet;
            owner = slot.Owner;

            _slots[index] = default;
            _head++;
            return true;
        }
        finally
        {
            if (lockTaken) _spinLock.Exit(false);
        }
    }

    public bool HasItems => _head != _tail;

    public void Dispose()
    {
        bool lockTaken = false;
        try
        {
            _spinLock.Enter(ref lockTaken);
            _disposed = true;
            uint head = _head;
            uint tail = _tail;

            while (head != tail)
            {
                var slot = _slots[head & _mask];
                slot.Owner?.Dispose();
                _slots[head & _mask] = default;
                head++;
            }
            _head = tail;
        }
        finally
        {
            if (lockTaken) _spinLock.Exit(false);
        }
    }
}