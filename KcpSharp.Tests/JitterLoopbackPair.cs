namespace KcpSharp.Tests;

internal sealed class JitterLoopbackPair : IAsyncDisposable
{
    public LoopbackPair Pair { get; }
    public UdpJitterRelay Relay { get; }

    public KcpConversation Client => Pair.Local;
    public KcpConversation Server => Pair.Remote;

    public JitterLoopbackPair(LoopbackPair pair, UdpJitterRelay relay)
    {
        Pair = pair;
        Relay = relay;
    }

    public async ValueTask DisposeAsync()
    {
        await Pair.DisposeAsync().ConfigureAwait(false);
        await Relay.DisposeAsync().ConfigureAwait(false);
    }
}
