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
                _cancellationRegistration.Dispose();
                executeSetResult = true;
            }
        }

        if (executeSetResult)
        {
            _mrvtsc.SetResult(new KcpConversationUpdateNotification(null, true));
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
                _cancellationRegistration.Dispose();
                executeSetResult = true;
            }
            else
            {
                _notificationPending = true;
            }
        }

        if (executeSetResult)
        {
            _mrvtsc.SetResult(new KcpConversationUpdateNotification(null, true));
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

            if (_notificationPending)
            {
                _notificationPending = false;
                return new ValueTask<KcpConversationUpdateNotification>(new KcpConversationUpdateNotification(null, true));
            }

            if (_ringBuffer.TryDequeue(out var packet, out var bufferOwner))
            {
                return new ValueTask<KcpConversationUpdateNotification>(new KcpConversationUpdateNotification(packet, bufferOwner, true));
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
        lock (SyncRoot)
        {
            _activeWait = false;
            _signaled = false;
            _mrvtsc.Reset();
        }

        return _mrvtsc.GetResult(token);
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
    private int _head;
    private int _tail;

    public KcpReceiveRingBuffer(int capacity)
    {
        _slots = new Slot[capacity];
        _mask = capacity - 1;
    }

    public bool TryEnqueue(ReadOnlyMemory<byte> packet, System.Buffers.IMemoryOwner<byte>? owner)
    {
        int tail = Volatile.Read(ref _tail);
        int head = Volatile.Read(ref _head);

        if (tail - head >= _slots.Length) return false;

        _slots[tail & _mask] = new Slot(packet, owner);
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    public bool TryDequeue(out ReadOnlyMemory<byte> packet, out System.Buffers.IMemoryOwner<byte>? owner)
    {
        int head = Volatile.Read(ref _head);
        int tail = Volatile.Read(ref _tail);

        if (head == tail)
        {
            packet = default;
            owner = null;
            return false;
        }

        int index = head & _mask;
        var slot = _slots[index];
        packet = slot.Packet;
        owner = slot.Owner;

        _slots[index] = default; // clear
        Volatile.Write(ref _head, head + 1);
        return true;
    }

    public bool HasItems => Volatile.Read(ref _head) != Volatile.Read(ref _tail);

    public void Dispose()
    {
        int head = Volatile.Read(ref _head);
        int tail = Volatile.Read(ref _tail);

        while (head != tail)
        {
            var slot = _slots[head & _mask];
            slot.Owner?.Dispose();
            _slots[head & _mask] = default;
            head++;
        }
        Volatile.Write(ref _head, tail);
    }
}