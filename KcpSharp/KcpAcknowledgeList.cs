using System.Runtime.CompilerServices;

namespace KcpSharp;

internal sealed class KcpAcknowledgeList
{
    private readonly KcpSendQueue _sendQueue;
    private readonly int _initialCapacity;
    private readonly int _maxCapacity;
    private (uint SerialNumber, uint Timestamp)[] _array;
    private int _head;
    private int _tail;
    private int _count;
    private readonly System.Threading.Lock _lock;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public KcpAcknowledgeList(KcpSendQueue sendQueue, int windowSize)
    {
        _initialCapacity = windowSize;
        _maxCapacity = windowSize * 2;
        _array = new (uint SerialNumber, uint Timestamp)[windowSize];
        _head = 0;
        _tail = 0;
        _count = 0;
        _lock = new System.Threading.Lock();
        _sendQueue = sendQueue;
    }

    public int SnapshotAndClear(Span<(uint SerialNumber, uint Timestamp)> destination)
    {
        bool notEmpty;
        int count;
        lock (_lock)
        {
            count = Math.Min(_count, destination.Length);

            // The skipped metric was misnamed and misused as an overflow indicator.
            // ACKs aren't lost, they're just left for the next snapshot.
            // Removed misleading AckQueueOverflow.Add(skipped);

            if (count > 0)
            {
                if (_head < _tail)
                {
                    _array.AsSpan(_head, count).CopyTo(destination);
                }
                else
                {
                    int rightLen = _array.Length - _head;
                    if (count <= rightLen)
                    {
                        _array.AsSpan(_head, count).CopyTo(destination);
                    }
                    else
                    {
                        _array.AsSpan(_head, rightLen).CopyTo(destination.Slice(0, rightLen));
                        _array.AsSpan(0, count - rightLen).CopyTo(destination.Slice(rightLen));
                    }
                }
            }

            _count -= count;
            _head = (_head + count) % _array.Length;

            if (_count == 0 && _array.Length > _initialCapacity * 4)
            {
                _array = new (uint SerialNumber, uint Timestamp)[_initialCapacity];
                _head = 0;
                _tail = 0;
            }
            else if (_count == 0)
            {
                _head = 0;
                _tail = 0;
            }

            notEmpty = _count > 0;
        }

        _sendQueue.NotifyAckListChanged(notEmpty);
        return count;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _count = 0;
            _head = 0;
            _tail = 0;
            if (_array.Length > _initialCapacity * 4)
            {
                _array = new (uint SerialNumber, uint Timestamp)[_initialCapacity];
            }
        }
        _sendQueue.NotifyAckListChanged(false);
    }

    public void Add(uint serialNumber, uint timestamp)
    {
        lock (_lock)
        {
            if (_count >= _maxCapacity) return;
            EnsureCapacity();
            _array[_tail] = (serialNumber, timestamp);
            _tail = (_tail + 1) % _array.Length;
            _count++;
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

        if (_count > 0)
        {
            if (_head < _tail)
            {
                _array.AsSpan(_head, _count).CopyTo(newArray);
            }
            else
            {
                int rightLen = _array.Length - _head;
                _array.AsSpan(_head, rightLen).CopyTo(newArray.AsSpan(0, rightLen));
                _array.AsSpan(0, _tail).CopyTo(newArray.AsSpan(rightLen, _tail));
            }
        }

        _array = newArray;
        _head = 0;
        _tail = _count;
    }
}