using System.Net;

namespace KcpSharp;

internal interface IKcpPacketSink
{
    ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, IPEndPoint remoteEndPoint, System.Buffers.IMemoryOwner<byte>? bufferOwner, CancellationToken cancellationToken = default);
}
