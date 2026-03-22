using System.Runtime.CompilerServices;

namespace HyacineCore.Server.Kcp.KcpSharp;

internal sealed class KcpAcknowledgeList
{
    private readonly KcpSendQueue _sendQueue;
    private readonly int _initialCapacity;
    private (uint SerialNumber, uint Timestamp)[] _array;
    private int _count;
    private SpinLock _lock;

    public int Count
    {
        get
        {
            var lockTaken = false;
            try
            {
                _lock.Enter(ref lockTaken);
                return _count;
            }
            finally
            {
                if (lockTaken) _lock.Exit();
            }
        }
    }

    public KcpAcknowledgeList(KcpSendQueue sendQueue, int windowSize)
    {
        _initialCapacity = windowSize;
        _array = new (uint SerialNumber, uint Timestamp)[windowSize];
        _count = 0;
        _lock = new SpinLock();
        _sendQueue = sendQueue;
    }

    public bool TryGetAt(int index, out uint serialNumber, out uint timestamp)
    {
        var lockTaken = false;
        try
        {
            _lock.Enter(ref lockTaken);

            if ((uint)index >= (uint)_count)
            {
                serialNumber = default;
                timestamp = default;
                return false;
            }

            (serialNumber, timestamp) = _array[index];
            return true;
        }
        finally
        {
            if (lockTaken) _lock.Exit();
        }
    }

    public int Snapshot(Span<(uint SerialNumber, uint Timestamp)> destination)
    {
        var lockTaken = false;
        try
        {
            _lock.Enter(ref lockTaken);
            var count = Math.Min(_count, destination.Length);
            _array.AsSpan(0, count).CopyTo(destination);
            return count;
        }
        finally
        {
            if (lockTaken) _lock.Exit();
        }
    }

    public void Clear()
    {
        var lockTaken = false;
        try
        {
            _lock.Enter(ref lockTaken);

            _count = 0;
            if (_array.Length > _initialCapacity * 4)
            {
                _array = new (uint SerialNumber, uint Timestamp)[_initialCapacity];
            }
        }
        finally
        {
            if (lockTaken) _lock.Exit();
        }

        _sendQueue.NotifyAckListChanged(false);
    }

    public void Add(uint serialNumber, uint timestamp)
    {
        var lockTaken = false;
        try
        {
            _lock.Enter(ref lockTaken);

            EnsureCapacity();
            _array[_count++] = (serialNumber, timestamp);
        }
        finally
        {
            if (lockTaken) _lock.Exit();
        }

        _sendQueue.NotifyAckListChanged(true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity()
    {
        if (_count == _array.Length) Expand();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Expand()
    {
        var capacity = _count + 1;
        capacity = Math.Max(capacity + capacity / 2, 16);
        var newArray = new (uint SerialNumber, uint Timestamp)[capacity];
        _array.AsSpan(0, _count).CopyTo(newArray);
        _array = newArray;
    }
}