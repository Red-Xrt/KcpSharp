namespace HyacineCore.Server.Kcp.KcpSharp;

/// <summary>
///     Options used to control the behaviors of <see cref="KcpConversation" />.
/// </summary>
public class KcpConversationOptions
{
    internal const int MtuDefaultValue = 1400;
    internal const uint SendWindowDefaultValue = 32;
    internal const uint ReceiveWindowDefaultValue = 128;
    internal const uint RemoteReceiveWindowDefaultValue = 128;
    internal const uint UpdateIntervalDefaultValue = 100;

    internal const int SendQueueSizeDefaultValue = 32;
    internal const int ReceiveQueueSizeDefaultValue = 32;

    /// <summary>
    ///     The buffer pool to rent buffer from.
    /// </summary>
    public IKcpBufferPool? BufferPool { get; set; }

    /// <summary>
    ///     The maximum packet size that can be transmitted over the underlying transport.
    /// </summary>
    public int Mtu { get; set; } = 1400;

    /// <summary>
    ///     The number of packets in the send window.
    ///     Values less than or equal to 0 will use the default value of 32.
    /// </summary>
    public int SendWindow { get; set; } = 32;

    /// <summary>
    ///     The number of packets in the receive window.
    ///     Values less than or equal to 0 will use the default value of 128.
    /// </summary>
    public int ReceiveWindow { get; set; } = 128;

    /// <summary>
    ///     The number of packets in the receive window of the remote host.
    ///     Values less than or equal to 0 will use the default value of 128.
    /// </summary>
    public int RemoteReceiveWindow { get; set; } = 128;

    /// <summary>
    ///     The interval in milliseconds to update the internal state of <see cref="KcpConversation" />.
    ///     Values less than 10 will use the default value of 100.
    /// </summary>
    public int UpdateInterval { get; set; } = 100;

    /// <summary>
    ///     Whether no-delay mode is enabled.
    /// </summary>
    public bool NoDelay { get; set; }

    /// <summary>
    ///     The number of ACK packet skipped before a resend is triggered.
    /// </summary>
    public int FastResend { get; set; }

    /// <summary>
    ///     Whether congestion control is disabled.
    /// </summary>
    public bool DisableCongestionControl { get; set; }

    /// <summary>
    ///     Whether stream mode is enabled.
    /// </summary>
    public bool StreamMode { get; set; }

    /// <summary>
    ///     The number of packets in the send queue.
    ///     Values less than or equal to 0 will use the default value of 32.
    /// </summary>
    public int SendQueueSize { get; set; }

    /// <summary>
    ///     The number of packets in the receive queue.
    ///     Values less than or equal to 0 will use the default value of 32.
    /// </summary>
    public int ReceiveQueueSize { get; set; }

    /// <summary>
    ///     The number of bytes to reserve at the start of buffer passed into the underlying transport. The transport should
    ///     fill this reserved space.
    /// </summary>
    public int PreBufferSize { get; set; }

    /// <summary>
    ///     The number of bytes to reserve at the end of buffer passed into the underlying transport. The transport should fill
    ///     this reserved space.
    /// </summary>
    public int PostBufferSize { get; set; }

    /// <summary>
    ///     Options for customized keep-alive functionality.
    /// </summary>
    public KcpKeepAliveOptions? KeepAliveOptions { get; set; }

    /// <summary>
    ///     Options for receive window size notification functionality.
    /// </summary>
    public KcpReceiveWindowNotificationOptions? ReceiveWindowNotificationOptions { get; set; }
}