using System.Diagnostics.Metrics;

namespace HyacineCore.Server.Kcp.KcpSharp;

/// <summary>
///     Provides metrics for KCP connections.
/// </summary>
public static class KcpMetrics
{
    /// <summary>
    ///     The meter used for KCP metrics.
    /// </summary>
    public static readonly Meter Meter = new Meter("HyacineCore.Server.Kcp");

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
}
