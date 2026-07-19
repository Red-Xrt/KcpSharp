namespace KcpSharp.Tests;

/// <summary>
///     Realistic private game-server workloads: login RPC, world sync, mixed traffic, combat bursts, reconnect.
///     Run: dotnet test --filter "Category=Stress&Category=GameServer"
/// </summary>
[Trait("Category", "Stress")]
[Trait("Category", "GameServer")]
public sealed class GameServerStressTests
{
    [Fact]
    public async Task LoginRpc_UnderModerateJitter()
    {
        const int logins = 25;
        const int size = 256;

        await using var session = await LoopbackTestHelper.CreateJitterPairAsync(0xB100, 5, 35);
        using var metrics = new StressMetricsCollector();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        for (int i = 0; i < logins; i++)
        {
            var login = LoopbackTestHelper.CreateGameJson("login", i, size);
            metrics.RecordMessageSent(login.Length);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var creds = await LoopbackTestHelper.RoundTripMessageAsync(session.Client, session.Server, login, cts.Token);
            sw.Stop();
            metrics.RecordAppRtt(sw.ElapsedMilliseconds);
            metrics.RecordMessageReceived(creds.Length);

            var token = LoopbackTestHelper.CreateGameJson("login_ok", i, size);
            metrics.RecordMessageSent(token.Length);
            sw.Restart();
            var onClient = await LoopbackTestHelper.RoundTripMessageAsync(session.Server, session.Client, token, cts.Token);
            sw.Stop();
            metrics.RecordAppRtt(sw.ElapsedMilliseconds);
            metrics.RecordMessageReceived(onClient.Length);
            Assert.Equal(token, onClient);
        }

        var snapshot = metrics.Finish(session.Relay);
        StressMetricsAssertions.AssertHealthyPrivateServerRun(snapshot, "LoginRpc");
        StressMetricsAssertions.AssertLatencyBudget(snapshot, 2000, "LoginRpc");
    }

    [Fact]
    public async Task MixedWorkload_SessionSimulation()
    {
        const int rounds = 80;
        const int smallSize = 320;
        const int largeSize = 8 * 1024;

        await using var session = await LoopbackTestHelper.CreateJitterPairAsync(0xB200, 3, 30);
        using var metrics = new StressMetricsCollector();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        for (int i = 0; i < rounds; i++)
        {
            bool large = i % 10 >= 7;
            int size = large ? largeSize : smallSize;
            string type = large ? "world_state" : "rpc";
            var payload = LoopbackTestHelper.CreateGameJson(type, i, size);

            metrics.RecordMessageSent(payload.Length);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var received = await LoopbackTestHelper.RoundTripMessageAsync(session.Client, session.Server, payload, cts.Token);
            sw.Stop();
            metrics.RecordAppRtt(sw.ElapsedMilliseconds);
            metrics.RecordMessageReceived(received.Length);
            Assert.Equal(payload, received);

            if (!large)
            {
                var ack = LoopbackTestHelper.CreateGameJson("rpc_ack", i, smallSize);
                metrics.RecordMessageSent(ack.Length);
                sw.Restart();
                var onClient = await LoopbackTestHelper.RoundTripMessageAsync(session.Server, session.Client, ack, cts.Token);
                sw.Stop();
                metrics.RecordAppRtt(sw.ElapsedMilliseconds);
                metrics.RecordMessageReceived(onClient.Length);
            }

            if (i % 5 == 0)
                await Task.Delay(Random.Shared.Next(5, 20), cts.Token);
        }

        var snapshot = metrics.Finish(session.Relay);
        StressMetricsAssertions.AssertHealthyPrivateServerRun(snapshot, "MixedWorkload");
        StressMetricsAssertions.AssertLatencyBudget(snapshot, 4000, "MixedWorkload");
    }

    [Fact]
    public async Task CombatChatBurst_100SmallMessages()
    {
        const int messages = 100;
        const int size = 192;

        await using var session = await LoopbackTestHelper.CreateJitterPairAsync(0xB300, 2, 20);
        using var metrics = new StressMetricsCollector();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var receiveTask = Task.Run(async () =>
        {
            int received = 0;
            while (received < messages)
            {
                var buffer = new byte[size + 256];
                var result = await session.Server.ReceiveAsync(buffer, cts.Token);
                Assert.True(result.BytesReceived > 0);
                metrics.RecordMessageReceived(result.BytesReceived);
                received++;
            }
        }, cts.Token);

        for (int i = 0; i < messages; i++)
        {
            string type = i % 3 == 0 ? "chat" : "action";
            var msg = LoopbackTestHelper.CreateGameJson(type, i, size);
            Assert.True(await session.Client.SendAsync(msg, cts.Token));
            metrics.RecordMessageSent(msg.Length);
            if (i % 7 == 0)
                await Task.Delay(Random.Shared.Next(0, 12), cts.Token);
        }

        await receiveTask;
        var snapshot = metrics.Finish(session.Relay);
        StressMetricsAssertions.AssertHealthyPrivateServerRun(snapshot, "CombatChatBurst");
    }

    [Fact]
    public async Task InventoryBulkLoad_64KiB_UnderJitter()
    {
        const int size = 64 * 1024;

        await using var session = await LoopbackTestHelper.CreateJitterPairAsync(0xB400, 10, 50);
        using var metrics = new StressMetricsCollector();
        using var cts = new CancellationTokenSource(LoopbackTestHelper.TimeoutForPayload(size, multiplierSeconds: 2));

        var inventory = LoopbackTestHelper.CreateGameJson("inventory", 1, size);
        metrics.RecordMessageSent(inventory.Length);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var received = await LoopbackTestHelper.RoundTripMessageAsync(session.Server, session.Client, inventory, cts.Token);
        sw.Stop();
        metrics.RecordAppRtt(sw.ElapsedMilliseconds);
        metrics.RecordMessageReceived(received.Length);
        Assert.Equal(inventory, received);

        var snapshot = metrics.Finish(session.Relay);
        StressMetricsAssertions.AssertHealthyPrivateServerRun(snapshot, "InventoryBulk");
        StressMetricsAssertions.AssertLatencyBudget(snapshot, 8000, "InventoryBulk");
    }

    [Fact]
    public async Task ReconnectCycles_MemoryStable()
    {
        const int cycles = 8;
        const int size = 1024;
        using var metrics = new StressMetricsCollector();

        for (int c = 0; c < cycles; c++)
        {
            await using var session = await LoopbackTestHelper.CreateJitterPairAsync((uint)(0xB500 + c), 5, 25);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            for (int i = 0; i < 5; i++)
            {
                var payload = LoopbackTestHelper.CreateGameJson("keepalive", i, size);
                metrics.RecordMessageSent(payload.Length);
                var received = await LoopbackTestHelper.RoundTripMessageAsync(session.Client, session.Server, payload, cts.Token);
                metrics.RecordMessageReceived(received.Length);
            }
        }

        var snapshot = metrics.Finish();
        StressMetricsAssertions.AssertHealthyPrivateServerRun(snapshot, "ReconnectCycles");
        Assert.True(snapshot.Gen2Collections < 20, $"Gen2 collections high: {snapshot.Gen2Collections}");
    }

    [Fact]
    public async Task ParallelClients_FourSessions()
    {
        const int messagesPerClient = 15;
        const int size = 400;
        using var metrics = new StressMetricsCollector();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var tasks = Enumerable.Range(0, 4).Select(async clientId =>
        {
            await using var session = await LoopbackTestHelper.CreateJitterPairAsync((uint)(0xB600 + clientId), 4, 28);
            for (int i = 0; i < messagesPerClient; i++)
            {
                var msg = LoopbackTestHelper.CreateGameJson("player_update", clientId * 1000 + i, size);
                metrics.RecordMessageSent(msg.Length);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var received = await LoopbackTestHelper.RoundTripMessageAsync(session.Client, session.Server, msg, cts.Token);
                sw.Stop();
                metrics.RecordAppRtt(sw.ElapsedMilliseconds);
                metrics.RecordMessageReceived(received.Length);
                Assert.Equal(msg, received);
            }
        });

        await Task.WhenAll(tasks);

        var snapshot = metrics.Finish();
        StressMetricsAssertions.AssertHealthyPrivateServerRun(snapshot, "ParallelClients");
        StressMetricsAssertions.AssertLatencyBudget(snapshot, 3000, "ParallelClients");
    }
}
