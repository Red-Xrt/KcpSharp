namespace KcpSharp;

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
    public int Mtu
    {
        get => _mtu;
        set
        {
            if (value < 50) throw new ArgumentOutOfRangeException(nameof(value), "MTU must be at least 50 bytes.");
            _mtu = value;
        }
    }
    private int _mtu = 1400;

    /// <summary>
    ///     The number of packets in the send window.
    ///     Values less than or equal to 0 will use the default value of 32.
    /// </summary>
    public int SendWindow
    {
        get => _sendWindow;
        set
        {
            if (value > 65535) throw new ArgumentOutOfRangeException(nameof(value), "SendWindow cannot exceed 65535.");
            _sendWindow = value;
        }
    }
    private int _sendWindow = 32;

    /// <summary>
    ///     The number of packets in the receive window.
    ///     Values less than or equal to 0 will use the default value of 128.
    /// </summary>
    public int ReceiveWindow
    {
        get => _receiveWindow;
        set
        {
            if (value > 65535) throw new ArgumentOutOfRangeException(nameof(value), "ReceiveWindow cannot exceed 65535.");
            _receiveWindow = value;
        }
    }
    private int _receiveWindow = 128;

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
    ///     Whether to enable packet batching before sending to the underlying transport.
    ///     Batching can significantly improve throughput at the cost of slight latency.
    /// </summary>
    public bool EnableBatching { get; set; } = true;

    /// <summary>
    ///     The maximum number of packets to batch before flushing to the underlying transport.
    ///     Only applicable if <see cref="EnableBatching"/> is true.
    /// </summary>
    public int MaxBatchSize
    {
        get => _maxBatchSize;
        set
        {
            if (value < 1 || value > 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "MaxBatchSize must be between 1 and 1024.");
            }
            _maxBatchSize = value;
        }
    }
    private int _maxBatchSize = 16;

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
    ///     The initial value of the slow start threshold (ssthresh).
    ///     A higher value speeds up initial transmission but may increase the risk of initial packet loss.
    /// </summary>
    public int InitialSsthresh { get; set; } = 32;

    /// <summary>
    ///     Options for customized keep-alive functionality.
    ///     Note: Keep-alive sends are subject to <see cref="UpdateInterval" /> and will not fire exactly
    ///     at the specified time if the update interval is longer.
    /// </summary>
    public KcpKeepAliveOptions? KeepAliveOptions { get; set; }

    /// <summary>
    ///     Options for receive window size notification functionality.
    /// </summary>
    public KcpReceiveWindowNotificationOptions? ReceiveWindowNotificationOptions { get; set; }

    /// <summary>
    ///     Creates a deep copy of this options object.
    /// </summary>
    public KcpConversationOptions Clone()
    {
        var clone = (KcpConversationOptions)MemberwiseClone();
        if (KeepAliveOptions != null)
        {
            clone.KeepAliveOptions = new KcpKeepAliveOptions(KeepAliveOptions.SendInterval, KeepAliveOptions.GracePeriod);
        }
        if (ReceiveWindowNotificationOptions != null)
        {
            clone.ReceiveWindowNotificationOptions = new KcpReceiveWindowNotificationOptions(
                ReceiveWindowNotificationOptions.InitialInterval,
                ReceiveWindowNotificationOptions.MaximumInterval);
        }
        return clone;
    }

    /// <summary>
    ///     Returns a preset optimized for low-latency game traffic.
    ///     NoDelay=true, UpdateInterval=10ms, FastResend=2, DisableCongestionControl=true.
    ///     <para>Note: Actual interval depends on OS Timer Resolution (e.g. 15.6ms jitter on default Windows).</para>
    /// </summary>
    public static KcpConversationOptions LowLatencyPreset => new()
    {
        NoDelay = true,
        UpdateInterval = 10,
        FastResend = 2,
        DisableCongestionControl = true,
        SendWindow = 256,
        ReceiveWindow = 256,
        RemoteReceiveWindow = 256,
        SendQueueSize = 256,
        ReceiveQueueSize = 256,
        EnableBatching = false,
    };

    /// <summary>
    ///     Returns a preset balanced for reliable bulk data transfer.
    /// </summary>
    public static KcpConversationOptions BulkTransferPreset => new()
    {
        NoDelay = false,
        UpdateInterval = 100,
        FastResend = 0,
        DisableCongestionControl = false,
        SendWindow = 512,
        ReceiveWindow = 512,
        SendQueueSize = 128,
        ReceiveQueueSize = 128,
    };
    /// <summary>
    ///     Validates the options, throwing an ArgumentException if any option is invalid.
    /// </summary>
    public void Validate()
    {
        if (Mtu < 50)
            throw new ArgumentException("MTU must be at least 50 bytes.", nameof(Mtu));
        if (UpdateInterval < 0)
            throw new ArgumentException("UpdateInterval must be a positive integer.", nameof(UpdateInterval));
        if (FastResend < 0)
            throw new ArgumentException("FastResend must be a positive integer.", nameof(FastResend));
        if (SendWindow <= 0)
            throw new ArgumentException("SendWindow must be greater than zero.", nameof(SendWindow));
        if (ReceiveWindow <= 0)
            throw new ArgumentException("ReceiveWindow must be greater than zero.", nameof(ReceiveWindow));

        if (SendQueueSize < 1 && SendQueueSize != 0) // Allows 0 to fallback to default later, but <0 is invalid
            throw new ArgumentException("SendQueueSize must be greater than zero.", nameof(SendQueueSize));
        if (ReceiveQueueSize < 1 && ReceiveQueueSize != 0) // Allows 0 to fallback to default later, but <0 is invalid
            throw new ArgumentException("ReceiveQueueSize must be greater than zero.", nameof(ReceiveQueueSize));

        _ = MaxBatchSize; // trigger validation in property getter/setter

        if (InitialSsthresh < 2)
            throw new ArgumentException("InitialSsthresh must be at least 2.", nameof(InitialSsthresh));
        if (RemoteReceiveWindow < 1)
            throw new ArgumentException("RemoteReceiveWindow must be at least 1.", nameof(RemoteReceiveWindow));
    }
}
