using System.Diagnostics;
using System.Threading.Tasks.Sources;

namespace HyacineCore.Server.Kcp.KcpSharp;

internal sealed class KcpConversationUpdateActivation : IValueTaskSource<KcpConversationUpdateNotification>, IDisposable
{
    private readonly Timer _timer;

    private readonly WaitList _waitList;
    private bool _activeWait;
    private CancellationTokenRegistration _cancellationRegistration;
    private CancellationToken _cancellationToken;

    private bool _disposed;
    private ManualResetValueTaskSourceCore<KcpConversationUpdateNotification> _mrvtsc;
    private bool _notificationPending;
    private bool _signaled;

    private readonly object _syncRoot = new object();

    internal object SyncRoot => _syncRoot;

    public KcpConversationUpdateActivation(int interval)
    {
        _timer = new Timer(state =>
        {
            var target = (KcpConversationUpdateActivation)state!;
            target.Notify();
        }, this, interval, interval);
        _mrvtsc = new ManualResetValueTaskSourceCore<KcpConversationUpdateNotification>
            { RunContinuationsAsynchronously = true };
        _waitList = new WaitList(this);
    }

    public bool HasPendingPackets
    {
        get
        {
            lock (_waitList.SyncRoot)
            {
                return _waitList.HasItems;
            }
        }
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            if (_activeWait && !_signaled)
            {
                _signaled = true;
                _cancellationToken = default;
                _mrvtsc.SetResult(default);
            }
        }

        _timer.Dispose();
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
        lock (SyncRoot)
        {
            if (_disposed || _notificationPending) return;
            if (_activeWait && !_signaled)
            {
                _signaled = true;
                _cancellationToken = default;
                _mrvtsc.SetResult(default);
            }
            else
            {
                _notificationPending = true;
            }
        }
    }

    private void NotifyPacketReceived()
    {
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
                    _mrvtsc.SetResult(notification.WithTimerNotification(timerNotification));
                }
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
                return default;
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
        lock (SyncRoot)
        {
            if (_activeWait && !_signaled)
            {
                var cancellationToken = _cancellationToken;
                _signaled = true;
                _cancellationToken = default;
                _mrvtsc.SetException(new OperationCanceledException(cancellationToken));
            }
        }
    }

    public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, IDisposable? bufferOwner, CancellationToken cancellationToken)
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
        private IDisposable? _bufferOwner;
        private bool _signaled;

        public WaitList(KcpConversationUpdateActivation parent)
        {
            _parent = parent;
            _mrvtsc = new ManualResetValueTaskSourceCore<bool> { RunContinuationsAsynchronously = true };
        }

        internal object SyncRoot => _parent.SyncRoot;

        public void Dispose()
        {
            if (_disposed) return;
            IDisposable? bufferToDispose = null;
            lock (SyncRoot)
            {
                _disposed = true;
                if (_available && !_occupied && !_signaled)
                {
                    _signaled = true;
                    _packet = default;
                    bufferToDispose = _bufferOwner;
                    _bufferOwner = null;
                    _cancellationToken = default;
                    _mrvtsc.SetResult(false);
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

        public IDisposable? BufferOwner
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
            lock (SyncRoot)
            {
                if (_available && _occupied && !_signaled)
                {
                    _signaled = true;
                    _packet = default;
                    bufferToDispose = _bufferOwner;
                    _bufferOwner = null;
                    _cancellationToken = default;
                    _mrvtsc.SetResult(true);
                }
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

        public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, IDisposable? bufferOwner, CancellationToken cancellationToken)
        {
            WaitItem? waitItem = null;
            short token = 0;
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
                    waitItem = new WaitItem(this, packet, bufferOwner, cancellationToken);
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
                    _bufferOwner = bufferOwner;
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
                task = new ValueTask(waitItem.Task);
            }

            _parent.NotifyPacketReceived();

            return task;
        }

        private void CancelWaiting()
        {
            IDisposable? bufferToDispose = null;
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
                    _mrvtsc.SetException(new OperationCanceledException(cancellationToken));
                }
            }
            bufferToDispose?.Dispose();
        }

        public bool Occupy(out KcpConversationUpdateNotification notification)
        {
            lock (SyncRoot)
            {
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
                if (node.Previous is null && node.Next is null) return false;
                list.Remove(node);
                return true;
            }
        }

        public bool HasItems
        {
            get
            {
                return (_available && !_occupied && !_signaled) || (_list?.First is not null);
            }
        }
    }

    private class WaitItem : TaskCompletionSource, IKcpConversationUpdateNotificationSource
    {
        private readonly WaitList _parent;
        private CancellationTokenRegistration _cancellationRegistration;
        private CancellationToken _cancellationToken;
        private ReadOnlyMemory<byte> _packet;
        private IDisposable? _bufferOwner;
        private bool _released;

        public WaitItem(WaitList parent, ReadOnlyMemory<byte> packet, IDisposable? bufferOwner, CancellationToken cancellationToken)
        {
            _parent = parent;
            _packet = packet;
            _bufferOwner = bufferOwner;
            _cancellationToken = cancellationToken;

            Node = new LinkedListNode<WaitItem>(this);
        }

        internal object SyncRoot => _parent.SyncRoot;

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

        public IDisposable? BufferOwner
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

            TrySetResult();
            cancellationRegistration.Dispose();
            bufferToDispose?.Dispose();
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
            if (_parent.TryRemove(this))
            {
                CancellationToken cancellationToken = default;
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
                    }
                }

                if (cancellationRegistration != default)
                {
                    TrySetCanceled(cancellationToken);
                }
            }

            if (cancellationRegistration != default)
            {
                cancellationRegistration.Dispose();
            }
            else
            {
                _cancellationRegistration.Dispose();
            }
            bufferToDispose?.Dispose();
        }
    }
}