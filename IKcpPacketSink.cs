using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace HyacineCore.Server.Kcp.KcpSharp;

internal interface IKcpPacketSink
{
    ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, IPEndPoint remoteEndPoint, IDisposable? bufferOwner, CancellationToken cancellationToken = default);
}
