namespace KcpSharp;

using System;

// REQUIRES: caller must ensure thread safety (e.g., holding _syncRoot in KcpSendQueue or KcpReceiveQueue) when accessing this cache.
[Obsolete("No longer used by KcpReceiveQueue.")]
internal sealed class KcpSendReceiveQueueItemCacheUnsafe
{
    private const int MaxCapacity = 4096;
    private readonly LinkedList<(KcpBuffer Data, byte Fragment)> _list = new();
    private int _count;

    public LinkedListNode<(KcpBuffer Data, byte Fragment)> Rent(in KcpBuffer buffer, byte fragment)
    {
        var node = _list.First;
        if (node is null)
        {
            node = new LinkedListNode<(KcpBuffer Data, byte Fragment)>((buffer, fragment));
        }
        else
        {
            node.ValueRef = (buffer, fragment);
            _list.RemoveFirst();
            if (_count > 0) _count--;
        }

        return node;
    }

    public void Return(LinkedListNode<(KcpBuffer Data, byte Fragment)> node)
    {
        node.ValueRef = default;
        if (_count >= MaxCapacity) return;
        _list.AddLast(node);
        _count++;
    }

    public void Clear()
    {
        _list.Clear();
        _count = 0;
    }
}