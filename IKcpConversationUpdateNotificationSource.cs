namespace HyacineCore.Server.Kcp.KcpSharp;

internal interface IKcpConversationUpdateNotificationSource
{
    ReadOnlyMemory<byte> Packet { get; }
    IDisposable? BufferOwner { get; }
    void Release();
}