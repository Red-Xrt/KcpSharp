namespace KcpSharp.Tests;

/// <summary>
///     Stress tests with UDP jitter relay, latency percentiles, and KcpSharp meter assertions.
///     Run: dotnet test --filter "Category=Stress&Category=Metrics"
/// </summary>
[Trait("Category", "Stress")]
[Trait("Category", "Metrics")]
public sealed class NetworkStressTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 25)]
    [InlineData(10, 80)]
    public async Task JitterRelay_PingPong_512B_40Samples(int jitterMinMs, int jitterMaxMs)
    {
        const int samples = 40;
        const int size = 512;

        await using var session = await LoopbackTestHelper.CreateJitterPairAsync(0xA100, jitterMinMs, jitterMaxMs);
        using var metrics = new StressMetricsCollector();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var payload = LoopbackTestHelper.CreateGameJson("ping", 0, size);

        for (int i = 0; i < samples; i++)
        {
            metrics.RecordMessageSent(payload.Length);
            long rtt = await LoopbackTestHelper.MeasureAppRttMsAsync(session.Client, session.Server, payload, cts.Token);
            metrics.RecordAppRtt(rtt);
            metrics.RecordMessageReceived(payload.Length);

            var echo = LoopbackTestHelper.CreateGameJson("pong", i, size);
            metrics.RecordMessageSent(echo.Length);
            long echoRtt = await LoopbackTestHelper.MeasureAppRttMsAsync(session.Server, session.Client, echo, cts.Token);
            metrics.RecordAppRtt(echoRtt);
            metrics.RecordMessageReceived(echo.Length);
        }

        var snapshot = metrics.Finish(session.Relay);
        StressMetricsAssertions.AssertHealthyPrivateServerRun(snapshot, "JitterPingPong");
        StressMetricsAssertions.AssertLatencyBudget(snapshot, jitterMaxMs > 0 ? 2500 : 500, "JitterPingPong");
        Assert.True(snapshot.RelayPacketsForwarded > 0);
    }

    [Fact]
    public async Task JitterRelay_LargeStateSync_8KiB_20Ticks()
    {
        const int ticks = 20;
        const int size = 8 * 1024;

        await using var session = await LoopbackTestHelper.CreateJitterPairAsync(0xA200, 8, 40);
        using var metrics = new StressMetricsCollector();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        for (int tick = 0; tick < ticks; tick++)
        {
            var state = LoopbackTestHelper.CreateGameJson("world_state", tick, size);
            metrics.RecordMessageSent(state.Length);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var received = await LoopbackTestHelper.RoundTripMessageAsync(session.Server, session.Client, state, cts.Token);
            sw.Stop();
            metrics.RecordAppRtt(sw.ElapsedMilliseconds);
            metrics.RecordMessageReceived(received.Length);
            Assert.Equal(state, received);

            await Task.Delay(15, cts.Token);
        }

        var snapshot = metrics.Finish(session.Relay);
        StressMetricsAssertions.AssertHealthyPrivateServerRun(snapshot, "LargeStateSync");
        StressMetricsAssertions.AssertLatencyBudget(snapshot, 3000, "LargeStateSync");
    }

    [Fact]
    public async Task CleanLoopback_Metrics_NoPacketDrops_100Rpcs()
    {
        const int count = 100;
        const int size = 384;

        await using var pair = LoopbackTestHelper.CreatePair(0xA300, LoopbackTestHelper.ServerJsonOptions(streamMode: false));
        using var metrics = new StressMetricsCollector();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        for (int i = 0; i < count; i++)
        {
            var rpc = LoopbackTestHelper.CreateGameJson("rpc", i, size);
            metrics.RecordMessageSent(rpc.Length);
            long rtt = await LoopbackTestHelper.MeasureAppRttMsAsync(pair.Local, pair.Remote, rpc, cts.Token);
            metrics.RecordAppRtt(rtt);
            metrics.RecordMessageReceived(rpc.Length);
        }

        var snapshot = metrics.Finish();
        StressMetricsAssertions.AssertHealthyPrivateServerRun(snapshot, "CleanRpcBurst");
        Assert.True(snapshot.AppRttP95Ms < 200, $"P95 RTT too high on loopback: {snapshot.AppRttP95Ms:F1} ms");
        // NoDelay mode uses a 30 ms min-RTO; on a CPU-saturated test host the update loop can be delayed
        // past RTO and emit spurious loopback retransmissions. Guard only against a runaway storm (a broken
        // RTO/ACK path), not the exact count. Packet-drop health is already asserted above.
        Assert.True(snapshot.KcpRetransmissions <= count * 3,
            $"Excessive loopback retransmissions: {snapshot.KcpRetransmissions} (limit {count * 3}).");
    }
}
