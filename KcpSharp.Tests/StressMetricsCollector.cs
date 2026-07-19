using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace KcpSharp.Tests;

internal readonly record struct StressMetricsSnapshot(
    long ManagedMemoryDeltaBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    TimeSpan CpuTime,
    long BytesSent,
    long BytesReceived,
    long RelayBytesForwarded,
    long RelayPacketsForwarded,
    long KcpRetransmissions,
    long KcpFastRetransmissions,
    long KcpPacketsDropped,
    long KcpWaitListDropped,
    long KcpAckDropped,
    double KcpRttMeanMs,
    int KcpRttSampleCount,
    double AppRttMeanMs,
    double AppRttP50Ms,
    double AppRttP95Ms,
    double AppRttP99Ms,
    int AppRttSampleCount,
    int MessagesSent,
    int MessagesReceived);

/// <summary>
///     Collects memory, CPU, application RTT, relay traffic, and KcpSharp <see cref="Meter"/> counters during a stress run.
/// </summary>
internal sealed class StressMetricsCollector : IDisposable
{
    private readonly MeterListener _listener;
    private readonly long _memoryStart;
    private readonly int _gen0Start;
    private readonly int _gen1Start;
    private readonly int _gen2Start;
    private readonly TimeSpan _cpuStart;
    private readonly object _appRttLock = new();
    private readonly List<long> _appRttSamples = new();
    private readonly object _kcpRttLock = new();
    private readonly List<double> _kcpRttSamples = new();

    private long _kcpRetransmissions;
    private long _kcpFastRetransmissions;
    private long _kcpPacketsDropped;
    private long _kcpWaitListDropped;
    private long _kcpAckDropped;
    private long _bytesSent;
    private long _bytesReceived;
    private long _messagesSent;
    private long _messagesReceived;

    public StressMetricsCollector()
    {
        _memoryStart = GC.GetTotalMemory(forceFullCollection: true);
        _gen0Start = GC.CollectionCount(0);
        _gen1Start = GC.CollectionCount(1);
        _gen2Start = GC.CollectionCount(2);
        _cpuStart = Process.GetCurrentProcess().TotalProcessorTime;

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == KcpMetrics.Meter.Name)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurementLong);
        _listener.SetMeasurementEventCallback<double>(OnMeasurementDouble);
        _listener.Start();
    }

    public void RecordMessageSent(int byteCount)
    {
        Interlocked.Increment(ref _messagesSent);
        Interlocked.Add(ref _bytesSent, byteCount);
    }

    public void RecordMessageReceived(int byteCount)
    {
        Interlocked.Increment(ref _messagesReceived);
        Interlocked.Add(ref _bytesReceived, byteCount);
    }

    public void RecordAppRtt(long milliseconds)
    {
        lock (_appRttLock) { _appRttSamples.Add(milliseconds); }
    }

    public StressMetricsSnapshot Finish(UdpJitterRelay? relay = null)
    {
        _listener.Dispose();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        var cpuEnd = Process.GetCurrentProcess().TotalProcessorTime;
        var memoryEnd = GC.GetTotalMemory(forceFullCollection: false);

        var appRtt = GetAppRttSnapshot();
        var kcpRtt = GetKcpRttSnapshot();

        return new StressMetricsSnapshot(
            ManagedMemoryDeltaBytes: memoryEnd - _memoryStart,
            Gen0Collections: GC.CollectionCount(0) - _gen0Start,
            Gen1Collections: GC.CollectionCount(1) - _gen1Start,
            Gen2Collections: GC.CollectionCount(2) - _gen2Start,
            CpuTime: cpuEnd - _cpuStart,
            BytesSent: Interlocked.Read(ref _bytesSent),
            BytesReceived: Interlocked.Read(ref _bytesReceived),
            RelayBytesForwarded: relay?.BytesForwarded ?? 0,
            RelayPacketsForwarded: relay?.PacketsForwarded ?? 0,
            KcpRetransmissions: Interlocked.Read(ref _kcpRetransmissions),
            KcpFastRetransmissions: Interlocked.Read(ref _kcpFastRetransmissions),
            KcpPacketsDropped: Interlocked.Read(ref _kcpPacketsDropped),
            KcpWaitListDropped: Interlocked.Read(ref _kcpWaitListDropped),
            KcpAckDropped: Interlocked.Read(ref _kcpAckDropped),
            KcpRttMeanMs: Mean(kcpRtt),
            KcpRttSampleCount: kcpRtt.Count,
            AppRttMeanMs: Mean(appRtt.Select(x => (double)x).ToList()),
            AppRttP50Ms: Percentile(appRtt, 0.50),
            AppRttP95Ms: Percentile(appRtt, 0.95),
            AppRttP99Ms: Percentile(appRtt, 0.99),
            AppRttSampleCount: appRtt.Count,
            MessagesSent: (int)Interlocked.Read(ref _messagesSent),
            MessagesReceived: (int)Interlocked.Read(ref _messagesReceived));
    }

    public void Dispose() => _listener.Dispose();

    private void OnMeasurementLong(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (instrument.Name == KcpMetrics.RetransmissionCount.Name)
            Interlocked.Add(ref _kcpRetransmissions, measurement);
        else if (instrument.Name == KcpMetrics.FastRetransmissionCount.Name)
            Interlocked.Add(ref _kcpFastRetransmissions, measurement);
        else if (instrument.Name == KcpMetrics.PacketsDropped.Name)
            Interlocked.Add(ref _kcpPacketsDropped, measurement);
        else if (instrument.Name == KcpMetrics.WaitListPacketsDropped.Name)
            Interlocked.Add(ref _kcpWaitListDropped, measurement);
        else if (instrument.Name == KcpMetrics.AckDropped.Name)
            Interlocked.Add(ref _kcpAckDropped, measurement);
    }

    private void OnMeasurementDouble(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (instrument.Meter.Name != KcpMetrics.Meter.Name)
            return;

        if (instrument.Name != KcpMetrics.RoundTripTime.Name)
            return;

        lock (_kcpRttLock) { _kcpRttSamples.Add(measurement); }
    }

    internal static double Percentile(IReadOnlyList<long> samples, double percentile)
    {
        if (samples.Count == 0) return 0;
        var sorted = samples.OrderBy(x => x).ToArray();
        double index = percentile * (sorted.Length - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        if (lower == upper) return sorted[lower];
        double weight = index - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    private List<long> GetAppRttSnapshot()
    {
        lock (_appRttLock) { return _appRttSamples.ToList(); }
    }

    private List<double> GetKcpRttSnapshot()
    {
        lock (_kcpRttLock) { return _kcpRttSamples.ToList(); }
    }

    private static double Mean(IReadOnlyList<double> values)
        => values.Count == 0 ? 0 : values.Sum() / values.Count;
}

internal static class StressMetricsAssertions
{
    public static void AssertHealthyPrivateServerRun(StressMetricsSnapshot m, string scenario)
    {
        Assert.False(m.MessagesSent == 0, $"{scenario}: no messages sent.");
        Assert.Equal(m.MessagesSent, m.MessagesReceived);
        Assert.Equal(0, m.KcpPacketsDropped);
        Assert.Equal(0, m.KcpWaitListDropped);
        Assert.Equal(0, m.KcpAckDropped);
        Assert.True(m.ManagedMemoryDeltaBytes < 80 * 1024 * 1024,
            $"{scenario}: managed memory grew {m.ManagedMemoryDeltaBytes / 1024 / 1024} MB (limit 80 MB).");
    }

    public static void AssertLatencyBudget(StressMetricsSnapshot m, double p99MsLimit, string scenario)
    {
        if (m.AppRttSampleCount == 0) return;
        Assert.True(m.AppRttP99Ms <= p99MsLimit,
            $"{scenario}: app RTT P99 {m.AppRttP99Ms:F1} ms exceeds {p99MsLimit} ms.");
    }
}
