using System.Diagnostics.Metrics;

namespace KcpSharp;

/// <summary>
///     Provides metrics for KCP connections.
/// </summary>
/// <remarks>
///     To consume these metrics, use the .NET `MeterListener` or `OpenTelemetry.Instrumentation.Runtime`
///     and subscribe to the "KcpSharp" meter.
/// </remarks>
public static class KcpMetrics
{
    /// <summary>
    ///     The meter used for KCP metrics.
    /// </summary>
    public static readonly Meter Meter = new Meter("KcpSharp");

    /// <summary>
    ///     The counter for the number of KCP segments retransmitted.
    /// </summary>
    public static readonly Counter<long> RetransmissionCount = Meter.CreateCounter<long>(
        "kcp.retransmission.count",
        description: "Number of KCP segments retransmitted.");

    /// <summary>
    ///     The counter for the number of KCP segments fast retransmitted.
    /// </summary>
    public static readonly Counter<long> FastRetransmissionCount = Meter.CreateCounter<long>(
        "kcp.fast_retransmission.count",
        description: "Number of KCP segments fast retransmitted.");

    /// <summary>
    ///     The counter for the number of KCP packets dropped due to full queues or errors.
    /// </summary>
    public static readonly Counter<long> PacketsDropped = Meter.CreateCounter<long>(
        "kcp.packets_dropped.count",
        description: "Number of KCP packets dropped due to full queues or errors.");

    /// <summary>
    ///     The histogram for the round trip time in milliseconds.
    /// </summary>
    public static readonly Histogram<double> RoundTripTime = Meter.CreateHistogram<double>(
        "kcp.rtt.ms",
        unit: "ms",
        description: "Round trip time in milliseconds.");

    /// <summary>
    ///     The counter for the number of ACKs skipped due to full buffer.
    /// </summary>
    public static readonly Counter<long> AckSnapshotPartial = Meter.CreateCounter<long>(
        "kcp.ack_snapshot_partial.count",
        description: "Number of times ACK snapshot was limited by destination buffer size - ACKs are queued for next flush, not lost.");

    /// <summary>
    ///     The counter for the number of packets dropped due to WaitList overflow.
    /// </summary>
    public static readonly Counter<long> WaitListPacketsDropped = Meter.CreateCounter<long>(
        "kcp.waitlist_packets_dropped.count",
        description: "Number of KCP packets dropped due to WaitList overflow.");

    /// <summary>
    ///     The counter for the number of ACK packets dropped due to ring buffer overflow.
    /// </summary>
    public static readonly Counter<long> AckDropped = Meter.CreateCounter<long>(
        "kcp.ack_dropped.count",
        description: "Number of KCP ACK packets dropped due to ring buffer overflow.");
}
