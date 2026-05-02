using System.Runtime.CompilerServices;

namespace KcpSharp;

internal sealed class KcpAcknowledgeList
{
    private readonly KcpSendQueue _sendQueue;
    private readonly int _maxCapacity;

    // We pad the struct to avoid false sharing, and track sequence to ensure safe lock-free writes
    private struct Node
    {
        public uint SN;
        public uint TS;
#pragma warning disable CS0420 // Volatile reference bypassing
        public volatile int Sequence;
#pragma warning restore CS0420
    }

    private readonly Node[] _ring;
    private readonly int _mask;

    private int _enqueuePos;
    private int _dequeuePos;

    // Approximate count
    public int Count => Math.Max(0, Volatile.Read(ref _enqueuePos) - Volatile.Read(ref _dequeuePos));

    public KcpAcknowledgeList(KcpSendQueue sendQueue, int windowSize)
    {
        _maxCapacity = windowSize * 2;
        int capacity = 16;
        while (capacity < _maxCapacity) capacity *= 2; // Power of 2

        _ring = new Node[capacity];
        _mask = capacity - 1;

        for (int i = 0; i < capacity; i++)
        {
            _ring[i].Sequence = i;
        }

        _enqueuePos = 0;
        _dequeuePos = 0;
        _sendQueue = sendQueue;
    }

    public int SnapshotAndClear(Span<(uint SerialNumber, uint Timestamp)> destination)
    {
        int readCount = 0;
        int maxToRead = destination.Length;

        while (readCount < maxToRead)
        {
            int currentDequeuePos = Volatile.Read(ref _dequeuePos);
            int index = currentDequeuePos & _mask;
#pragma warning disable CS0420
            int sequence = Volatile.Read(ref _ring[index].Sequence);
#pragma warning restore CS0420

            // Check if the slot is populated with a new item
            int diff = sequence - (currentDequeuePos + 1);

            if (diff == 0)
            {
                // Slot is ready to be consumed
                if (Interlocked.CompareExchange(ref _dequeuePos, currentDequeuePos + 1, currentDequeuePos) == currentDequeuePos)
                {
                    // Volatile CAS provides sufficient barrier
                    destination[readCount] = (_ring[index].SN, _ring[index].TS);
                    readCount++;

                    // Mark slot as free for the next wrap-around cycle
#pragma warning disable CS0420
                    Volatile.Write(ref _ring[index].Sequence, currentDequeuePos + _mask + 1);
#pragma warning restore CS0420
                }
            }
            else if (diff < 0)
            {
                // Queue is empty
                break;
            }
        }

        bool notEmpty = Volatile.Read(ref _enqueuePos) - Volatile.Read(ref _dequeuePos) > 0;
        _sendQueue.NotifyAckListChanged(notEmpty);

        return readCount;
    }

    public void Clear()
    {
        // Thread-safe clear is hard in a pure lock-free queue without blocking,
        // but this is only called during transport close or reset.
        // It must be guaranteed by caller (SetTransportClosed) that the update loop is already terminated,
        // so no concurrent Add calls can happen, and the send queue is closed.

        int currentDequeuePos = Volatile.Read(ref _dequeuePos);
        int currentEnqueuePos = Volatile.Read(ref _enqueuePos);

        while (currentDequeuePos < currentEnqueuePos)
        {
            if (Interlocked.CompareExchange(ref _dequeuePos, currentEnqueuePos, currentDequeuePos) == currentDequeuePos)
            {
                // Fix the sequences for skipped slots so writers don't hang
                for (int pos = currentDequeuePos; pos < currentEnqueuePos; pos++)
                {
#pragma warning disable CS0420
                    Volatile.Write(ref _ring[pos & _mask].Sequence, pos + _mask + 1);
#pragma warning restore CS0420
                }
                break;
            }
            currentDequeuePos = Volatile.Read(ref _dequeuePos);
            currentEnqueuePos = Volatile.Read(ref _enqueuePos);
        }

        _sendQueue.NotifyAckListChanged(false);
    }

    public void Add(uint serialNumber, uint timestamp)
    {
        while (true)
        {
            int currentEnqueuePos = Volatile.Read(ref _enqueuePos);

            // Capacity check (approximate)
            int currentDequeuePos = Volatile.Read(ref _dequeuePos);
            if (currentEnqueuePos - currentDequeuePos >= _maxCapacity)
            {
                // Drop packet if full
                KcpMetrics.AckDropped.Add(1);
                return;
            }

            int index = currentEnqueuePos & _mask;
#pragma warning disable CS0420
            int sequence = Volatile.Read(ref _ring[index].Sequence);
#pragma warning restore CS0420

            int diff = sequence - currentEnqueuePos;

            if (diff == 0)
            {
                // Slot is available to write
                if (Interlocked.CompareExchange(ref _enqueuePos, currentEnqueuePos + 1, currentEnqueuePos) == currentEnqueuePos)
                {
                    _ring[index].SN = serialNumber;
                    _ring[index].TS = timestamp;

                    // Publish the write to the consumer
#pragma warning disable CS0420
                    Volatile.Write(ref _ring[index].Sequence, currentEnqueuePos + 1);
#pragma warning restore CS0420

                    _sendQueue.NotifyAckListChanged(true);
                    return;
                }
            }
            else if (diff < 0)
            {
                // Another thread hasn't finished writing or consumer hasn't freed it.
                // We just loop.
            }
            else
            {
                // diff > 0 means currentEnqueuePos has already been advanced by another thread.
            }
        }
    }
}