using System.Net;
using System.Net.Sockets;

namespace KcpSharp.Tests;

public sealed class MultiplexConnectionTests
{
    [Fact]
    public async Task MultiplexConversation_RoundTrip()
    {
        var options = LoopbackTestHelper.TestOptions();
        var socketA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var socketB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socketA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socketB.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpointA = (IPEndPoint)socketA.LocalEndPoint!;
        var endpointB = (IPEndPoint)socketB.LocalEndPoint!;

        var transportA = KcpSocketTransport.CreateMultiplexConnection<object>(socketA, endpointB, options.Mtu);
        var transportB = KcpSocketTransport.CreateMultiplexConnection<object>(socketB, endpointA, options.Mtu);
        ((IKcpTransport<IKcpMultiplexConnection<object>>)transportA).Start();
        ((IKcpTransport<IKcpMultiplexConnection<object>>)transportB).Start();

        var muxA = (KcpMultiplexConnection<object>)transportA.Connection;
        var muxB = (KcpMultiplexConnection<object>)transportB.Connection;

        var convA = muxA.CreateConversation(100, endpointB, options);
        var convB = muxB.CreateConversation(100, endpointA, options);

        try
        {
            var payload = new byte[] { 9, 8, 7, 6, 5 };
            Assert.True(convA.TrySend(payload));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var received = await LoopbackTestHelper.ReceiveExactAsync(convB, payload.Length, cts.Token);
            Assert.Equal(payload, received);
        }
        finally
        {
            await convA.DisposeAsync();
            await convB.DisposeAsync();
            await muxA.DisposeAsync();
            await muxB.DisposeAsync();
            ((IDisposable)transportA).Dispose();
            ((IDisposable)transportB).Dispose();
        }
    }

    [Fact]
    public async Task UnregisterConversation_WorksAfterTransportClosed()
    {
        var options = LoopbackTestHelper.TestOptions();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var localEndpoint = (IPEndPoint)socket.LocalEndPoint!;

        var transport = KcpSocketTransport.CreateMultiplexConnection<object>(socket, localEndpoint, options.Mtu);
        ((IKcpTransport<IKcpMultiplexConnection<object>>)transport).Start();
        var mux = (KcpMultiplexConnection<object>)transport.Connection;

        var conv = mux.CreateConversation(200, localEndpoint, options);
        Assert.True(mux.Contains(200));

        mux.SetTransportClosed();
        Assert.True(conv.TransportClosed);

        var unregistered = mux.UnregisterConversation(200);
        Assert.NotNull(unregistered);
        Assert.False(mux.Contains(200));

        await conv.DisposeAsync();
        await mux.DisposeAsync();
        ((IDisposable)transport).Dispose();
    }
}
