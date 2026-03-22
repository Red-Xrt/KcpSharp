using System.Diagnostics;
using System.Threading.Channels;
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
            { RunContinuationsAsynchronously = false };
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
        return _waitList.InputPacketAsync(packet, bufferOwner);
    }

    public bool TryDequeue(out ReadOnlyMemory<byte> packet, out IDisposable? bufferOwner)
    {
        return _waitList.TryDequeue(out packet, out bufferOwner);
    }

    private class WaitList : IDisposable
    {
        private readonly KcpConversationUpdateActivation _parent;
        private readonly Channel<WaitItem> _channel;
        private bool _disposed;

        public WaitList(KcpConversationUpdateActivation parent)
        {
            _parent = parent;
            _channel = Channel.CreateBounded<WaitItem>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        }

        internal object SyncRoot => _parent.SyncRoot;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _channel.Writer.TryComplete();

            while (_channel.Reader.TryRead(out var item))
            {
                item.Release();
            }
        }

        public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, IDisposable? bufferOwner)
        {
            if (_disposed)
            {
                bufferOwner?.Dispose();
                return default;
            }

            var waitItem = new WaitItem(packet, bufferOwner);
            if (!_channel.Writer.TryWrite(waitItem))
            {
                // This shouldn't happen with DropOldest, but handle just in case
                waitItem.Release();
            }

            _parent.NotifyPacketReceived();

            return default;
        }

        public bool Occupy(out KcpConversationUpdateNotification notification)
        {
            if (_disposed)
            {
                notification = default;
                return false;
            }

            if (_channel.Reader.TryRead(out var item))
            {
                notification = new KcpConversationUpdateNotification(item, true);
                return true;
            }

            notification = default;
            return false;
        }

        public bool TryDequeue(out ReadOnlyMemory<byte> packet, out IDisposable? bufferOwner)
        {
            if (!_disposed && _channel.Reader.TryRead(out var item))
            {
                packet = item.Packet;
                bufferOwner = item.BufferOwner;
                return true;
            }

            packet = default;
            bufferOwner = null;
            return false;
        }

        public bool HasItems => !_disposed && _channel.Reader.Count > 0;
    }

    private readonly struct WaitItem : IKcpConversationUpdateNotificationSource
    {
        private readonly ReadOnlyMemory<byte> _packet;
        private readonly IDisposable? _bufferOwner;

        public WaitItem(ReadOnlyMemory<byte> packet, IDisposable? bufferOwner)
        {
            _packet = packet;
            _bufferOwner = bufferOwner;
        }

        public ReadOnlyMemory<byte> Packet => _packet;

        public IDisposable? BufferOwner => _bufferOwner;

        public void Release()
        {
            _bufferOwner?.Dispose();
        }
    }
}