#if NEED_LINKEDLIST_SHIM
using LinkedListOfBufferItem = KcpSharp.NetstandardShim.LinkedList<KcpSharp.KcpSendReceiveBufferItem>;
using LinkedListNodeOfBufferItem = KcpSharp.NetstandardShim.LinkedListNode<KcpSharp.KcpSendReceiveBufferItem>;
#else
using LinkedListNodeOfBufferItem =
    System.Collections.Generic.LinkedListNode<HyacineCore.Server.Kcp.KcpSharp.KcpSendReceiveBufferItem>;
using LinkedListOfBufferItem =
    System.Collections.Generic.LinkedList<HyacineCore.Server.Kcp.KcpSharp.KcpSendReceiveBufferItem>;
#endif

namespace HyacineCore.Server.Kcp.KcpSharp;

internal struct KcpSendReceiveBufferItemCacheUnsafe
{
    private LinkedListOfBufferItem _items;

    public static KcpSendReceiveBufferItemCacheUnsafe Create()
    {
        return new KcpSendReceiveBufferItemCacheUnsafe
        {
            _items = new LinkedListOfBufferItem()
        };
    }

    public LinkedListNodeOfBufferItem Allocate(in KcpSendReceiveBufferItem item)
    {
        var node = _items.First;
        if (node is null)
        {
            node = new LinkedListNodeOfBufferItem(item);
        }
        else
        {
            _items.Remove(node);
            node.ValueRef = item;
        }

        return node;
    }

    public void Return(LinkedListNodeOfBufferItem node)
    {
        node.ValueRef = default;
        _items.AddLast(node);
    }
}
