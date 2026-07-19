using System.Net;
using System.Net.Sockets;

namespace KcpSharp.Tests;

public sealed class LoopbackConversationTests
{
    [Fact]
    public async Task MessageMode_TrySend_TryReceive_RoundTrip()
    {
        await using var pair = LoopbackTestHelper.CreatePair(0xBEEF);
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        Assert.True(pair.Local.TrySend(payload));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await LoopbackTestHelper.ReceiveExactAsync(pair.Remote, payload.Length, cts.Token);

        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task MessageMode_SendAsync_ReceiveAsync_RoundTrip()
    {
        await using var pair = LoopbackTestHelper.CreatePair(42);
        var payload = Enumerable.Range(0, 512).Select(i => (byte)(i & 0xFF)).ToArray();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await pair.Local.SendAsync(payload, cts.Token));

        var received = await LoopbackTestHelper.ReceiveExactAsync(pair.Remote, payload.Length, cts.Token);
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task MessageMode_TwoSegments_FitSingleMtu_WhenMtuRaised()
    {
        var options = LoopbackTestHelper.TestOptions();
        options.Mtu = 4096;
        await using var pair = LoopbackTestHelper.CreatePair(77, options);
        var payload = new byte[2048];
        Random.Shared.NextBytes(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await LoopbackTestHelper.RoundTripMessageAsync(pair.Local, pair.Remote, payload, cts.Token);
        Assert.Equal(payload, received);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1376)]
    [InlineData(1377)]
    public async Task StreamMode_PayloadSize_RoundTrip(int size)
    {
        var options = LoopbackTestHelper.TestOptions(streamMode: true);
        await using var pair = LoopbackTestHelper.CreatePair((uint)(9100 + size), options);
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.True(await pair.Local.SendAsync(payload, cts.Token));
        var received = await LoopbackTestHelper.ReceiveStreamExactAsync(pair.Remote, size, cts.Token);
        Assert.Equal(payload, received);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1376)]
    [InlineData(1377)]
    [InlineData(2048)]
    public async Task MessageMode_PayloadSize_RoundTrip(int size)
    {
        await using var pair = LoopbackTestHelper.CreatePair((uint)(9000 + size));
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiveTask = LoopbackTestHelper.ReceiveExactAsync(pair.Remote, size, cts.Token);
        Assert.True(await pair.Local.SendAsync(payload, cts.Token));
        var received = await receiveTask;
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task MessageMode_OneKilobyte_RoundTrip()
    {
        await using var pair = LoopbackTestHelper.CreatePair(7);
        var payload = new byte[1024];
        Random.Shared.NextBytes(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await pair.Local.SendAsync(payload, cts.Token));

        var received = await LoopbackTestHelper.ReceiveExactAsync(pair.Remote, payload.Length, cts.Token);
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task StreamMode_OneKilobyte_RoundTrip()
    {
        var options = LoopbackTestHelper.TestOptions(streamMode: true);
        await using var pair = LoopbackTestHelper.CreatePair(99, options);
        var payload = new byte[1024];
        Random.Shared.NextBytes(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await pair.Local.SendAsync(payload, cts.Token));

        var buffer = new byte[payload.Length];
        int offset = 0;
        while (offset < payload.Length)
        {
            var result = await pair.Remote.ReceiveAsync(buffer.AsMemory(offset), cts.Token);
            Assert.True(result.BytesReceived > 0);
            offset += result.BytesReceived;
        }

        Assert.Equal(payload, buffer);
    }

    [Fact]
    public async Task NoConversationId_RoundTrip()
    {
        await using var pair = LoopbackTestHelper.CreatePairWithoutConversationId();
        var payload = new byte[] { 10, 20, 30 };

        Assert.True(pair.Local.TrySend(payload));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await LoopbackTestHelper.ReceiveExactAsync(pair.Remote, payload.Length, cts.Token);
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task Bidirectional_RoundTrip()
    {
        await using var pair = LoopbackTestHelper.CreatePair(1);
        var aToB = new byte[] { 0xAA };
        var bToA = new byte[] { 0xBB };

        Assert.True(pair.Local.TrySend(aToB));
        Assert.True(pair.Remote.TrySend(bToA));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receivedOnB = await LoopbackTestHelper.ReceiveExactAsync(pair.Remote, 1, cts.Token);
        var receivedOnA = await LoopbackTestHelper.ReceiveExactAsync(pair.Local, 1, cts.Token);

        Assert.Equal(aToB, receivedOnB);
        Assert.Equal(bToA, receivedOnA);
    }

    [Fact]
    public async Task Dispose_ClosesTransport()
    {
        var pair = LoopbackTestHelper.CreatePair(5);

        try
        {
            Assert.True(pair.Local.TrySend(new byte[] { 1 }));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await LoopbackTestHelper.ReceiveExactAsync(pair.Remote, 1, cts.Token);

            await pair.Local.DisposeAsync();

            Assert.True(pair.Local.TransportClosed);
            Assert.False(pair.Local.TrySend(new byte[] { 2 }));
            Assert.False(pair.Remote.TransportClosed);
        }
        finally
        {
            await pair.Remote.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancelPendingReceive_CompletesWithCancellation()
    {
        await using var pair = LoopbackTestHelper.CreatePair(3);
        using var cts = new CancellationTokenSource();

        var receiveTask = pair.Local.WaitToReceiveAsync(cts.Token).AsTask();
        await Task.Delay(50);
        Assert.True(pair.Local.CancelPendingReceive());

        await Assert.ThrowsAsync<OperationCanceledException>(() => receiveTask);
    }
}
