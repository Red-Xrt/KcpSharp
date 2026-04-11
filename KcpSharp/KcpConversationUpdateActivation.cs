using System.Diagnostics;
using System.Threading.Tasks.Sources;

namespace KcpSharp;

internal sealed class KcpConversationUpdateActivation : IValueTaskSource<KcpConversationUpdateNotification>, IDisposable
{
    private readonly WaitList _waitList;
    private bool _activeWait;
    private CancellationTokenRegistration _cancellationRegistration;
    private CancellationToken _cancellationToken;

    private bool _disposed;
    private ManualResetValueTaskSourceCore<KcpConversationUpdateNotification> _mrvtsc;
    private bool _notificationPending;
    private bool _signaled;

    private readonly System.Threading.Lock _syncRoot = new();

    internal System.Threading.Lock SyncRoot => _syncRoot;

    public KcpConversationUpdateActivation(int interval)
    {
        _mrvtsc = new ManualResetValueTaskSourceCore<KcpConversationUpdateNotification>
            { RunContinuationsAsynchronously = true };
        _waitList = new WaitList(this);

        KcpGlobalTickEngine.Register(this, interval);
    }

    public bool HasPendingPackets
    {
        get
        {
            return _waitList.HasItems;
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
            _mrvtsc.SetResult(default);
        }

        _waitList.Dispose();
    }

    ValueTaskSourceStatus IValueTaskSource<KcpConversationUpdateNotification>.GetStatus(short token)
    {
        return _mrvtsc.GetStatus(token);
    }

    void IValueTaskSource<KcpConversationUpdateNotification>.OnCompleted(Action<object?> continuation, object? state,
        short token, ValueTaskSourceOnCompletedFlags flags)
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
                _signaled = false;
                _activeWait = false;
                _cancellationRegistration = default;
            }
        }
    }

    public void Notify()
    {
        if (_disposed) return;
        bool executeSetResult = false;
        lock (SyncRoot)
        {
            if (_disposed || _notificationPending) return;
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
            _mrvtsc.SetResult(default);
        }
    }

    private void NotifyPacketReceived()
    {
        bool executeSetResult = false;
        KcpConversationUpdateNotification resultToSet = default;
        lock (SyncRoot)
        {
            if (_disposed) return;
            if (_activeWait && !_signaled)
                if (_waitList.Occupy(out var notification))
                {
                    _signaled = true;
                    _cancellationToken = default;
                    var timerNotification = _notificationPending;
                    _notificationPending = false;
                    resultToSet = notification.WithTimerNotification(timerNotification);
                    executeSetResult = true;
                }
        }

        if (executeSetResult)
        {
            _mrvtsc.SetResult(resultToSet);
        }
    }

    public ValueTask<KcpConversationUpdateNotification> WaitAsync(CancellationToken cancellationToken)
    {
        short token;
        lock (SyncRoot)
        {
            if (_disposed) return default;
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<KcpConversationUpdateNotification>(
                    Task.FromCanceled<KcpConversationUpdateNotification>(cancellationToken));
            if (_activeWait) throw new InvalidOperationException();
            if (_waitList.Occupy(out var notification))
            {
                var timerNotification = _notificationPending;
                _notificationPending = false;
                return new ValueTask<KcpConversationUpdateNotification>(
                    notification.WithTimerNotification(timerNotification));
            }

            if (_notificationPending)
            {
                _notificationPending = false;
                return new ValueTask<KcpConversationUpdateNotification>(
                    new KcpConversationUpdateNotification(null, false));
            }

            _activeWait = true;
            Debug.Assert(!_signaled);
            _cancellationToken = cancellationToken;
            token = _mrvtsc.Version;
        }

        _cancellationRegistration =
            cancellationToken.UnsafeRegister(state => ((KcpConversationUpdateActivation?)state)!.CancelWaiting(), this);
        return new ValueTask<KcpConversationUpdateNotification>(this, token);
    }

    private void CancelWaiting()
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

    public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, System.Buffers.IMemoryOwner<byte>? bufferOwner, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            bufferOwner?.Dispose();
            return default;
        }
        return _waitList.InputPacketAsync(packet, bufferOwner, cancellationToken);
    }

    private class WaitList : IValueTaskSource, IKcpConversationUpdateNotificationSource, IDisposable
    {
        private readonly KcpConversationUpdateActivation _parent;

        private bool _available; // activeWait
        private CancellationTokenRegistration _cancellationRegistration;
        private CancellationToken _cancellationToken;
        private bool _disposed;
        private LinkedList<WaitItem>? _list;
        private ManualResetValueTaskSourceCore<bool> _mrvtsc;
        private bool _occupied;

        private ReadOnlyMemory<byte> _packet;
        private System.Buffers.IMemoryOwner<byte>? _bufferOwner;
        private bool _signaled;

        public WaitList(KcpConversationUpdateActivation parent)
        {
            _parent = parent;
            _mrvtsc = new ManualResetValueTaskSourceCore<bool> { RunContinuationsAsynchronously = true };
        }

        internal System.Threading.Lock SyncRoot => _parent.SyncRoot;

        public void Dispose()
        {
            IDisposable? bufferToDispose = null;
            bool executeSetResult = false;
            lock (SyncRoot)
            {
                if (_disposed) return;
                _disposed = true;
                if (_available && !_occupied && !_signaled)
                {
                    _signaled = true;
                    _packet = default;
                    bufferToDispose = _bufferOwner;
                    _bufferOwner = null;
                    _cancellationToken = default;
                    executeSetResult = true;
                }

                var list = _list;
                if (list is not null)
                {
                    _list = null;

                    var node = list.First;
                    var next = node?.Next;
                    while (node is not null)
                    {
                        node.Value.Release();

                        list.Remove(node);
                        node = next;
                        next = node?.Next;
                    }
                }
            }

            if (executeSetResult)
            {
                _mrvtsc.SetResult(false);
            }

            bufferToDispose?.Dispose();
        }

        public ReadOnlyMemory<byte> Packet
        {
            get
            {
                lock (SyncRoot)
                {
                    if (_available && _occupied && !_signaled) return _packet;
                }

                return default;
            }
        }

        public System.Buffers.IMemoryOwner<byte>? BufferOwner
        {
            get
            {
                lock (SyncRoot)
                {
                    if (_available && _occupied && !_signaled) return _bufferOwner;
                }

                return default;
            }
        }

        public void Release()
        {
            IDisposable? bufferToDispose = null;
            bool executeSetResult = false;
            lock (SyncRoot)
            {
                if (_available && _occupied && !_signaled)
                {
                    _signaled = true;
                    _packet = default;
                    bufferToDispose = _bufferOwner;
                    _bufferOwner = null;
                    _cancellationToken = default;
                    executeSetResult = true;
                }
            }

            if (executeSetResult)
            {
                _mrvtsc.SetResult(true);
            }

            bufferToDispose?.Dispose();
        }

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
        {
            return _mrvtsc.GetStatus(token);
        }

        void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            _mrvtsc.OnCompleted(continuation, state, token, flags);
        }

        void IValueTaskSource.GetResult(short token)
        {
            _cancellationRegistration.Dispose();

            try
            {
                _mrvtsc.GetResult(token);
            }
            finally
            {
                _mrvtsc.Reset();

                lock (SyncRoot)
                {
                    _available = false;
                    _occupied = false;
                    _signaled = false;
                    _cancellationRegistration = default;
                }
            }
        }

        public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, System.Buffers.IMemoryOwner<byte>? bufferOwner, CancellationToken cancellationToken)
        {
            try
            {
                WaitItem? waitItem = null;
                short token = 0;
                var ownerAsMemoryOwner = bufferOwner;

                lock (SyncRoot)
                {
                    if (_disposed)
                    {
                        bufferOwner?.Dispose();
                        return default;
                    }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        bufferOwner?.Dispose();
                        return new ValueTask(Task.FromCanceled(cancellationToken));
                    }

                    if (_available)
                    {
                        const int MaxQueuedPackets = 256;
                        if (_list is not null && _list.Count >= MaxQueuedPackets)
                        {
                            // Backpressure WITH dropping:
                            // Drop the newest packet. This is correct UDP semantics.
                            KcpMetrics.WaitListPacketsDropped.Add(1);
                            bufferOwner?.Dispose();
                            return default;
                        }

                        waitItem = WaitItemPool.Rent();
                        waitItem.Initialize(this, packet, ownerAsMemoryOwner, cancellationToken);
                        _list ??= new LinkedList<WaitItem>();
                        _list.AddLast(waitItem.Node);
                    }
                    else
                    {
                        token = _mrvtsc.Version;

                        _available = true;
                        Debug.Assert(!_occupied);
                        Debug.Assert(!_signaled);
                        _packet = packet;
                        _bufferOwner = ownerAsMemoryOwner;
                        _cancellationToken = cancellationToken;
                    }
                }

                ValueTask task;

                if (waitItem is null)
                {
                    _cancellationRegistration =
                        cancellationToken.UnsafeRegister(state => ((WaitList?)state)!.CancelWaiting(), this);
                    task = new ValueTask(this, token);
                }
                else
                {
                    waitItem.RegisterCancellationToken();
                    task = waitItem.Task;
                }

                _parent.NotifyPacketReceived();

                return task;
            }
            catch (Exception)
            {
                bufferOwner?.Dispose();
                throw;
            }
        }

        private void CancelWaiting()
        {
            IDisposable? bufferToDispose = null;
            bool executeSetException = false;
            Exception? exceptionToSet = null;
            lock (SyncRoot)
            {
                if (_available && !_occupied && !_signaled)
                {
                    _signaled = true;
                    var cancellationToken = _cancellationToken;
                    _packet = default;
                    bufferToDispose = _bufferOwner;
                    _bufferOwner = null;
                    _cancellationToken = default;
                    exceptionToSet = new OperationCanceledException(cancellationToken);
                    executeSetException = true;
                }
            }

            if (executeSetException)
            {
                _mrvtsc.SetException(exceptionToSet!);
            }

            bufferToDispose?.Dispose();
        }

        public bool Occupy(out KcpConversationUpdateNotification notification)
        {
            // Caller must hold SyncRoot
            if (_disposed)
            {
                notification = default;
                return false;
            }

            if (_available && !_occupied && !_signaled)
            {
                _occupied = true;
                notification = new KcpConversationUpdateNotification(this, true);
                return true;
            }

            if (_list is null)
            {
                notification = default;
                return false;
            }

            var node = _list.First;
            if (node is not null)
            {
                _list.Remove(node);
                notification = new KcpConversationUpdateNotification(node.Value, true);
                return true;
            }

            notification = default;
            return false;
        }

        internal bool TryRemove(WaitItem item)
        {
            lock (SyncRoot)
            {
                var list = _list;
                if (list is null) return false;
                var node = item.Node;
                if (node.List is null) return false;
                list.Remove(node);
                return true;
            }
        }

        public bool HasItems
        {
            get
            {
                lock (SyncRoot)
                {
                    return (_available && !_occupied && !_signaled) || (_list?.First is not null);
                }
            }
        }
    }

    private class WaitItem : IValueTaskSource, IKcpConversationUpdateNotificationSource
    {
        private WaitList? _parent;
        private CancellationTokenRegistration _cancellationRegistration;
        private CancellationToken _cancellationToken;
        private ReadOnlyMemory<byte> _packet;
        private System.Buffers.IMemoryOwner<byte>? _bufferOwner;
        private bool _released;
        private ManualResetValueTaskSourceCore<bool> _mrvtsc;

        public WaitItem()
        {
            _mrvtsc = new ManualResetValueTaskSourceCore<bool> { RunContinuationsAsynchronously = true };
            Node = new LinkedListNode<WaitItem>(this);
        }

        public ValueTask Task => new ValueTask(this, _mrvtsc.Version);

        public void Initialize(WaitList parent, ReadOnlyMemory<byte> packet, System.Buffers.IMemoryOwner<byte>? bufferOwner, CancellationToken cancellationToken)
        {
            _parent = parent;
            _packet = packet;
            _bufferOwner = bufferOwner;
            _cancellationToken = cancellationToken;
            _released = false;
        }

        internal System.Threading.Lock SyncRoot => _parent!.SyncRoot;

        public LinkedListNode<WaitItem> Node { get; }

        public ReadOnlyMemory<byte> Packet
        {
            get
            {
                lock (SyncRoot)
                {
                    if (!_released) return _packet;
                }

                return default;
            }
        }

        public System.Buffers.IMemoryOwner<byte>? BufferOwner
        {
            get
            {
                lock (SyncRoot)
                {
                    if (!_released) return _bufferOwner;
                }

                return default;
            }
        }

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
        {
            return _mrvtsc.GetStatus(token);
        }

        void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _mrvtsc.OnCompleted(continuation, state, token, flags);
        }

        void IValueTaskSource.GetResult(short token)
        {
            _mrvtsc.GetResult(token);
        }

        public void Release()
        {
            CancellationTokenRegistration cancellationRegistration;
            IDisposable? bufferToDispose = null;
            lock (SyncRoot)
            {
                if (_released) return;
                _released = true;
                cancellationRegistration = _cancellationRegistration;
                _packet = default;
                bufferToDispose = _bufferOwner;
                _bufferOwner = null;
                _cancellationToken = default;
                _cancellationRegistration = default;
            }

            _mrvtsc.SetResult(true);
            cancellationRegistration.Dispose();
            bufferToDispose?.Dispose();
            WaitItemPool.Return(this);
        }

        public void RegisterCancellationToken()
        {
            _cancellationRegistration =
                _cancellationToken.UnsafeRegister(state => ((WaitItem?)state)!.CancelWaiting(), this);
        }

        private void CancelWaiting()
        {
            CancellationTokenRegistration cancellationRegistration = default;
            IDisposable? bufferToDispose = null;
            bool shouldSetCanceled = false;
            CancellationToken cancellationToken = default;

            if (_parent!.TryRemove(this))
            {
                lock (SyncRoot)
                {
                    if (!_released)
                    {
                        _released = true;
                        cancellationToken = _cancellationToken;
                        cancellationRegistration = _cancellationRegistration;
                        _packet = default;
                        bufferToDispose = _bufferOwner;
                        _bufferOwner = null;
                        _cancellationToken = default;
                        _cancellationRegistration = default;
                        shouldSetCanceled = true;
                    }
                }

                if (shouldSetCanceled)
                {
                    _mrvtsc.SetException(new OperationCanceledException(cancellationToken));
                }

                cancellationRegistration.Dispose();
                bufferToDispose?.Dispose();
                WaitItemPool.Return(this);
            }
            else
            {
                _cancellationRegistration.Dispose();
            }
        }

        public void Reset()
        {
            _mrvtsc.Reset();
        }
    }

    private static class WaitItemPool
    {
        private const int MaxPoolSize = 2048;
        private static readonly System.Collections.Concurrent.ConcurrentQueue<WaitItem> s_pool = new();

        public static WaitItem Rent()
        {
            if (s_pool.TryDequeue(out var item))
            {
                return item;
            }
            return new WaitItem();
        }

        public static void Return(WaitItem item)
        {
            if (s_pool.Count >= MaxPoolSize) return;
            item.Reset();
            s_pool.Enqueue(item);
        }
    }
}