using System.Text;

namespace KcpSharp.Tests;

/// <summary>
///     Emits a human-readable metrics report for manual/CI inspection.
///     Run: dotnet test --filter "FullyQualifiedName~EmitFullReport" --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Stress")]
[Trait("Category", "Metrics")]
public sealed class StressMetricsAuditTests
{
    [Fact]
    public async Task EmitFullReport_AllScenarios()
    {
        var report = new StringBuilder();
        report.AppendLine("=== KcpSharp Stress Metrics Audit ===");

        await RunScenario(report, "CleanLoopback_100x384B", async (m, ct) =>
        {
            await using var pair = LoopbackTestHelper.CreatePair(0xD001, LoopbackTestHelper.ServerJsonOptions(streamMode: false));
            for (int i = 0; i < 100; i++)
            {
                var rpc = LoopbackTestHelper.CreateGameJson("rpc", i, 384);
                m.RecordMessageSent(rpc.Length);
                long rtt = await LoopbackTestHelper.MeasureAppRttMsAsync(pair.Local, pair.Remote, rpc, ct);
                m.RecordAppRtt(rtt);
                m.RecordMessageReceived(rpc.Length);
            }
            return (UdpJitterRelay?)null;
        });

        await RunScenario(report, "JitterPing_40x512B_5-25ms", async (m, ct) =>
        {
            await using var session = await LoopbackTestHelper.CreateJitterPairAsync(0xD002, 5, 25);
            var payload = LoopbackTestHelper.CreateGameJson("ping", 0, 512);
            for (int i = 0; i < 40; i++)
            {
                m.RecordMessageSent(payload.Length);
                long rtt = await LoopbackTestHelper.MeasureAppRttMsAsync(session.Client, session.Server, payload, ct);
                m.RecordAppRtt(rtt);
                m.RecordMessageReceived(payload.Length);
            }
            return session.Relay;
        });

        await RunScenario(report, "MixedWorkload_80rounds", async (m, ct) =>
        {
            await using var session = await LoopbackTestHelper.CreateJitterPairAsync(0xD003, 3, 30);
            for (int i = 0; i < 80; i++)
            {
                bool large = i % 10 >= 7;
                int size = large ? 8 * 1024 : 320;
                var payload = LoopbackTestHelper.CreateGameJson(large ? "world_state" : "rpc", i, size);
                m.RecordMessageSent(payload.Length);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await LoopbackTestHelper.RoundTripMessageAsync(session.Client, session.Server, payload, ct);
                sw.Stop();
                m.RecordAppRtt(sw.ElapsedMilliseconds);
                m.RecordMessageReceived(payload.Length);
            }
            return session.Relay;
        });

        await RunScenario(report, "Reconnect_12cycles", async (m, ct) =>
        {
            for (int c = 0; c < 12; c++)
            {
                await using var session = await LoopbackTestHelper.CreateJitterPairAsync((uint)(0xD004 + c), 5, 25);
                var payload = LoopbackTestHelper.CreateGameJson("keepalive", c, 1024);
                m.RecordMessageSent(payload.Length);
                var received = await LoopbackTestHelper.RoundTripMessageAsync(session.Client, session.Server, payload, ct);
                m.RecordMessageReceived(received.Length);
            }
            return null;
        });

        await RunScenario(report, "LargePayload_64KiB", async (m, ct) =>
        {
            await using var pair = LoopbackTestHelper.CreatePair(0xD005, LoopbackTestHelper.ServerJsonOptions(streamMode: false));
            var payload = LoopbackTestHelper.CreateGameJson("inventory", 1, 64 * 1024);
            m.RecordMessageSent(payload.Length);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var received = await LoopbackTestHelper.RoundTripMessageAsync(pair.Local, pair.Remote, payload, ct);
            sw.Stop();
            m.RecordAppRtt(sw.ElapsedMilliseconds);
            m.RecordMessageReceived(received.Length);
            return (UdpJitterRelay?)null;
        });

        report.AppendLine("=== End Report ===");
        Console.WriteLine(report.ToString());
    }

    private static async Task RunScenario(
        StringBuilder report,
        string name,
        Func<StressMetricsCollector, CancellationToken, Task<UdpJitterRelay?>> action)
    {
        using var metrics = new StressMetricsCollector();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var relay = await action(metrics, cts.Token);
        var s = metrics.Finish(relay);
        report.AppendLine(FormatSnapshot(name, s));
    }

    internal static string FormatSnapshot(string name, StressMetricsSnapshot s) =>
        $"""
        [{name}]
          messages: {s.MessagesSent} sent / {s.MessagesReceived} recv
          bytes: {s.BytesSent:N0} sent / {s.BytesReceived:N0} recv
          relay: {s.RelayPacketsForwarded:N0} pkts / {s.RelayBytesForwarded:N0} bytes
          app RTT ms: mean={s.AppRttMeanMs:F1} p50={s.AppRttP50Ms:F1} p95={s.AppRttP95Ms:F1} p99={s.AppRttP99Ms:F1} (n={s.AppRttSampleCount})
          kcp RTT ms: mean={s.KcpRttMeanMs:F1} (n={s.KcpRttSampleCount})
          kcp drops: pkts={s.KcpPacketsDropped} waitlist={s.KcpWaitListDropped} ack={s.KcpAckDropped}
          kcp retransmit: normal={s.KcpRetransmissions} fast={s.KcpFastRetransmissions}
          memory: delta={s.ManagedMemoryDeltaBytes / 1024.0:F1} KB | gen0={s.Gen0Collections} gen1={s.Gen1Collections} gen2={s.Gen2Collections}
          cpu: {s.CpuTime.TotalMilliseconds:F0} ms
        """;
}
