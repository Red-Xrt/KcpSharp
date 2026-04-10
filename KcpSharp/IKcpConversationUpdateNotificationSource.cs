using System.Buffers;

namespace KcpSharp;

internal interface IKcpConversationUpdateNotificationSource
{
    ReadOnlyMemory<byte> Packet { get; }
    IMemoryOwner<byte>? BufferOwner { get; }
    void Release();
}