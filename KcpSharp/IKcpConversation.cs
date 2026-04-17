namespace KcpSharp;

/// <summary>
///     Represents a reliable conversation or channel over an underlying, potentially unreliable transport.
/// </summary>
public interface IKcpConversation : IDisposable, IAsyncDisposable
{
    /// <summary>
    ///     Marks the underlying transport as closed and aborts all active send or receive operations.
    ///     Note: This method signals a graceful shutdown of the connection but does not immediately free underlying memory resources.
    ///     This is different from <see cref="Dispose()" />, which both signals closure and aggressively releases all associated resources.
    /// </summary>
    void SetTransportClosed();
}