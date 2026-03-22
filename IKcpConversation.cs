using System.Net;

namespace HyacineCore.Server.Kcp.KcpSharp;

/// <summary>
///     A conversation or a channel over the transport.
/// </summary>
public interface IKcpConversation : IDisposable
{
    /// <summary>
    ///     Mark the underlying transport as closed. Abort all active send or receive operations.
    ///     Note: This method signals a graceful shutdown without freeing underlying resources,
    ///     unlike <see cref="Dispose()" /> which signals the closure and also releases resources.
    /// </summary>
    void SetTransportClosed();
}