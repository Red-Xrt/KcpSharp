using System.Net;
using System.Net.Sockets;

namespace KcpSharp.Tests;

public sealed class KcpBuilderTests
{
    [Fact]
    public void Build_WithoutRemoteEndPoint_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            KcpBuilder.ForConversation()
                .WithUdpSocket(AddressFamily.InterNetwork, out _)
                .Build());
    }

    [Fact]
    public async Task Build_StartsTransport_AndDisposeClosesClient()
    {
        var options = LoopbackTestHelper.TestOptions();

        var client = KcpBuilder.ForConversation()
            .WithUdpSocket(AddressFamily.InterNetwork, out _)
            .WithRemoteEndPoint(new IPEndPoint(IPAddress.Loopback, 9))
            .WithConversationId(88)
            .WithOptions(options)
            .Build();

        Assert.False(client.TransportClosed);
        await client.DisposeAsync();
        Assert.True(client.TransportClosed);
        Assert.False(client.TrySend(new byte[] { 1 }));
    }
}
