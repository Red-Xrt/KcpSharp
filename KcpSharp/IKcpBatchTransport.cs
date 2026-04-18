using System.Net;

namespace KcpSharp;

/// <summary>
///     Defines an internal interface for batched KCP transport operations.
///     Implementations must be thread-safe as batch operations can be called concurrently.
/// </summary>
public interface IKcpBatchTransport
{
    /// <summary>
    ///     Tries to get a memory slice in the batch buffer for the required size, commits it with the target endpoint,
    ///     and invokes the provided action to write the packet data into the slice.
    ///     This is an atomic operation to ensure thread safety.
    /// </summary>
    /// <param name="requiredSize">The exact number of bytes required.</param>
    /// <param name="endpoint">The destination endpoint for this packet.</param>
    /// <param name="dataWriter">Action invoked to copy packet data into the reserved memory slice.</param>
    /// <returns>True if there was enough capacity in the batch and the data was written; false otherwise.</returns>
    bool TryGetBatchSliceAndCommit(int requiredSize, IPEndPoint endpoint, Action<Memory<byte>> dataWriter);
    
    /// <summary>
    ///     Flushes all committed packets in the current batch down to the underlying OS socket in a single system call.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation requests.</param>
    /// <returns>A ValueTask representing the async operation.</returns>
    ValueTask FlushBatchAsync(CancellationToken cancellationToken);
    
    /// <summary>
    ///     Gets the number of remaining free slots in the active batch.
    /// </summary>
    int BatchCapacity { get; }
}

public interface IKcpBatchTransport2 : IKcpBatchTransport
{
    bool AnyPacketCommitted { get; }
}
