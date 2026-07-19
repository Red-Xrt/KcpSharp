namespace KcpSharp.Tests;

/// <summary>
///     Stress tests simulating private-server JSON traffic between client and server.
///     Run all: dotnet test
///     Run only stress: dotnet test --filter "Category=Stress"
/// </summary>
[Trait("Category", "Stress")]
public sealed class JsonStressTests
{
    public static TheoryData<int> JsonPayloadSizes => new()
    {
        2 * 1024,
        8 * 1024,
        16 * 1024,
        32 * 1024,
        64 * 1024,
    };

    [Theory]
    [MemberData(nameof(JsonPayloadSizes))]
    public async Task MessageMode_LargeJson_RoundTrip(int size)
    {
        var options = LoopbackTestHelper.ServerJsonOptions(streamMode: false);
        await using var pair = LoopbackTestHelper.CreatePair((uint)(0x5000 + size), options);
        var payload = LoopbackTestHelper.CreateSyntheticJson(size, id: 1);

        using var cts = new CancellationTokenSource(LoopbackTestHelper.TimeoutForPayload(size));
        var received = await LoopbackTestHelper.RoundTripMessageAsync(pair.Local, pair.Remote, payload, cts.Token);
        Assert.Equal(payload, received);
    }

    [Theory]
    [MemberData(nameof(JsonPayloadSizes))]
    public async Task StreamMode_LargeJson_RoundTrip(int size)
    {
        var options = LoopbackTestHelper.ServerJsonOptions(streamMode: true);
        await using var pair = LoopbackTestHelper.CreatePair((uint)(0x6000 + size), options);
        var payload = LoopbackTestHelper.CreateSyntheticJson(size, id: 2);

        using var cts = new CancellationTokenSource(LoopbackTestHelper.TimeoutForPayload(size));
        var received = await LoopbackTestHelper.RoundTripStreamAsync(pair.Local, pair.Remote, payload, cts.Token);
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task MessageMode_RapidPingPong_4KiB_50Rounds()
    {
        const int rounds = 50;
        const int size = 4 * 1024;
        var options = LoopbackTestHelper.ServerJsonOptions(streamMode: false);
        await using var pair = LoopbackTestHelper.CreatePair(0x7100, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        for (int i = 0; i < rounds; i++)
        {
            var clientToServer = LoopbackTestHelper.CreateSyntheticJson(size, id: i * 2);
            var onServer = await LoopbackTestHelper.RoundTripMessageAsync(pair.Local, pair.Remote, clientToServer, cts.Token);
            Assert.Equal(clientToServer, onServer);

            var serverToClient = LoopbackTestHelper.CreateSyntheticJson(size, id: i * 2 + 1);
            var onClient = await LoopbackTestHelper.RoundTripMessageAsync(pair.Remote, pair.Local, serverToClient, cts.Token);
            Assert.Equal(serverToClient, onClient);
        }
    }

    [Fact]
    public async Task MessageMode_BurstTen_16KiB_Messages()
    {
        const int messageCount = 10;
        const int size = 16 * 1024;
        var options = LoopbackTestHelper.ServerJsonOptions(streamMode: false);
        await using var pair = LoopbackTestHelper.CreatePair(0x7200, options);
        var sent = new byte[messageCount][];

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var receiveTask = Task.Run(async () =>
        {
            var received = new byte[messageCount][];
            for (int i = 0; i < messageCount; i++)
                received[i] = await LoopbackTestHelper.ReceiveExactAsync(pair.Remote, size, cts.Token);
            return received;
        }, cts.Token);

        for (int i = 0; i < messageCount; i++)
        {
            sent[i] = LoopbackTestHelper.CreateSyntheticJson(size, id: 1000 + i);
            Assert.True(await pair.Local.SendAsync(sent[i], cts.Token));
        }

        var received = await receiveTask;
        for (int i = 0; i < messageCount; i++)
            Assert.Equal(sent[i], received[i]);
    }

    [Fact]
    public async Task MessageMode_ConcurrentBidirectional_8KiB()
    {
        const int size = 8 * 1024;
        var options = LoopbackTestHelper.ServerJsonOptions(streamMode: false);
        await using var pair = LoopbackTestHelper.CreatePair(0x7300, options);

        var clientPayload = LoopbackTestHelper.CreateSyntheticJson(size, id: 1);
        var serverPayload = LoopbackTestHelper.CreateSyntheticJson(size, id: 2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var clientRecv = LoopbackTestHelper.ReceiveExactAsync(pair.Local, size, cts.Token);
        var serverRecv = LoopbackTestHelper.ReceiveExactAsync(pair.Remote, size, cts.Token);
        var clientSend = pair.Local.SendAsync(clientPayload, cts.Token);
        var serverSend = pair.Remote.SendAsync(serverPayload, cts.Token);

        Assert.True(await clientSend);
        Assert.True(await serverSend);

        var clientGot = await clientRecv;
        var serverGot = await serverRecv;

        Assert.Equal(serverPayload, clientGot);
        Assert.Equal(clientPayload, serverGot);
    }

    [Fact]
    public async Task MessageMode_ManySmallJson_256Messages()
    {
        const int messageCount = 256;
        const int size = 512;
        var options = LoopbackTestHelper.ServerJsonOptions(streamMode: false);
        await using var pair = LoopbackTestHelper.CreatePair(0x7400, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        for (int i = 0; i < messageCount; i++)
        {
            var payload = LoopbackTestHelper.CreateSyntheticJson(size, id: i);
            var received = await LoopbackTestHelper.RoundTripMessageAsync(pair.Local, pair.Remote, payload, cts.Token);
            Assert.Equal(payload, received);
        }
    }

    [Fact]
    public async Task StreamMode_BackToBackLargeJson_Three32KiB()
    {
        const int size = 32 * 1024;
        var options = LoopbackTestHelper.ServerJsonOptions(streamMode: true);
        await using var pair = LoopbackTestHelper.CreatePair(0x7500, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        for (int i = 0; i < 3; i++)
        {
            var payload = LoopbackTestHelper.CreateSyntheticJson(size, id: 5000 + i);
            var received = await LoopbackTestHelper.RoundTripStreamAsync(pair.Local, pair.Remote, payload, cts.Token);
            Assert.Equal(payload, received);
        }
    }
}
