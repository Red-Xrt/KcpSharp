
namespace KcpSharp;

// REQUIRES: caller must hold the appropriate lock (_sndBufLock or _rcvBufLock) when accessing this cache.
internal struct KcpSendReceiveBufferItemCacheUnsafe
{
    private const int MaxCapacity = 4096;
    private LinkedList<KcpSendReceiveBufferItem> _items;
    private int _count;

    public static KcpSendReceiveBufferItemCacheUnsafe Create()
    {
        return new KcpSendReceiveBufferItemCacheUnsafe
        {
            _items = new LinkedList<KcpSendReceiveBufferItem>()
        };
    }

    public LinkedListNode<KcpSendReceiveBufferItem> Allocate(in KcpSendReceiveBufferItem item)
    {
        var node = _items.First;
        if (node is null)
        {
            node = new LinkedListNode<KcpSendReceiveBufferItem>(item);
        }
        else
        {
            _items.RemoveFirst();
            node.ValueRef = item;
            if (_count > 0) _count--;
        }

        return node;
    }

    public void Return(LinkedListNode<KcpSendReceiveBufferItem> node)
    {
        node.ValueRef = default;
        if (_count >= MaxCapacity) return;
        _items.AddLast(node);
        _count++;
    }
}
