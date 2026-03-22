using System.Buffers;
using System.Net;
using System.Net.Sockets;
using HyacineCore.Server.Util;

namespace HyacineCore.Server.Kcp.KcpSharp;

/// <summary>
///     A Socket transport for upper-level connections.
/// </summary>
/// <typeparam name="T"></typeparam>
internal abstract class KcpSocketTransport<T> : IKcpTransport, IKcpBatchTransport, IDisposable where T : class, IKcpConversation
{
    private readonly int _mtu;
    private readonly int _receiveBufferPoolSize;
    private readonly Socket _socket;
    private static readonly Logger Logger = new("KcpServer");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> UdpDebugCounters =
        new(System.StringComparer.Ordinal);
    private T? _connection;
    private CancellationTokenSource? _cts;
    private volatile bool _disposed;
    private volatile bool _stopped;
    private Thread? _receiveThread;
    private KcpScheduler? _scheduler;

    private readonly byte[][] _batchBuffers;
    private readonly IPEndPoint?[] _batchEndpoints;
    private readonly int[] _batchSizes;
    private int _batchCount;
    private const int MaxBatchSize = 32;

    private int _packetsSentLastSecond;
    private long _lastBatchTick;
    private int _effectiveBatchSize = 16;

    /// <summary>
    ///     Construct a socket transport with the specified socket and remote endpoint.
    /// </summary>
    /// <param name="listener">The socket instance.</param>
    /// <param name="mtu">The maximum packet size that can be transmitted.</param>
    /// <param name="receiveBufferPoolSize">The size of the pool to allocate receive buffers.</param>
    protected KcpSocketTransport(UdpClient listener, int mtu, int receiveBufferPoolSize = 8)
    {
        if (listener == null) throw new ArgumentNullException(nameof(listener));
        _socket = listener.Client;
        _mtu = mtu;
        _receiveBufferPoolSize = receiveBufferPoolSize;
        if (mtu < 50) throw new ArgumentOutOfRangeException(nameof(mtu));
        if (receiveBufferPoolSize <= 0) throw new ArgumentOutOfRangeException(nameof(receiveBufferPoolSize));

        _batchBuffers = new byte[MaxBatchSize][];
        for (int i = 0; i < MaxBatchSize; i++)
            _batchBuffers[i] = GC.AllocateUninitializedArray<byte>(_mtu, pinned: true);
        
        _batchEndpoints = new IPEndPoint[MaxBatchSize];
        _batchSizes = new int[MaxBatchSize];
        _batchCount = 0;
    }

    /// <summary>
    ///     Get the upper-level connection instace. If Start is not called or the transport is closed,
    ///     <see cref="InvalidOperationException" /> will be thrown.
    /// </summary>
    /// <exception cref="InvalidOperationException">Start is not called or the transport is closed.</exception>
    internal T Connection => _connection ?? throw new InvalidOperationException();

    /// <inheritdoc />
    void IDisposable.Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    ValueTask IKcpTransport.SendPacketAsync(Memory<byte> packet, IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        if (_disposed) return default;
        if (packet.Length > _mtu) return default;

        return SendCoreAsync(packet, endpoint, cancellationToken);
    }

    private ValueTask SendCoreAsync(Memory<byte> packet, IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _packetsSentLastSecond);
        try
        {
            var task = _socket.SendToAsync(packet, SocketFlags.None, endpoint, cancellationToken);
            if (task.IsCompletedSuccessfully)
            {
                return default;
            }
            return new ValueTask(task.AsTask());
        }
        catch (SocketException)
        {
            return default;
        }
    }

    int IKcpBatchTransport.BatchCapacity => _effectiveBatchSize - _batchCount;

    bool IKcpBatchTransport.TryGetBatchSlice(int requiredSize, out Memory<byte> slice, out int slotIndex)
    {
        long currentTick = Environment.TickCount64;
        if (currentTick - _lastBatchTick >= 1000)
        {
            _lastBatchTick = currentTick;
            int sent = Interlocked.Exchange(ref _packetsSentLastSecond, 0);
            if (sent < 50) _effectiveBatchSize = 8;
            else if (sent > 500) _effectiveBatchSize = 32;
            else _effectiveBatchSize = 16;
        }

#if NET8_0_OR_GREATER && HAS_SENDMESSAGESASYNC
        if (_batchCount >= _effectiveBatchSize || requiredSize > _mtu)
        {
            slice = default;
            slotIndex = -1;
            return false;
        }
        slotIndex = _batchCount;
        slice = _batchBuffers[slotIndex].AsMemory(0, _mtu);
        return true;
#else
        // Fallback: Nếu không có API gửi nhiều gói tin cùng lúc của OS (SendMessagesAsync),
        // việc copy vào _batchBuffers rồi gửi từng gói bằng SendToAsync (vòng lặp)
        // sẽ làm CHẬM đi do tốn chi phí copy bộ nhớ dư thừa.
        // Trả về false ở đây sẽ ép luồng chạy thẳng xuống SendPacketAsync (zero-copy).
        slice = default;
        slotIndex = -1;
        return false;
#endif
    }

    void IKcpBatchTransport.CommitBatchSlot(int slotIndex, int actualSize, IPEndPoint endpoint)
    {
        // Lưu size và endpoint trước khi tăng _batchCount để đảm bảo thread safety (mặc dù code gọi single-threaded)
        _batchSizes[slotIndex] = actualSize;
        _batchEndpoints[slotIndex] = endpoint;
        _batchCount++;
    }

    ValueTask IKcpBatchTransport.FlushBatchAsync(CancellationToken cancellationToken)
    {
        if (_batchCount == 0) return default;

#if NET8_0_OR_GREATER && HAS_SENDMESSAGESASYNC
        // Giả sử API này tồn tại trong tương lai hoặc extension method custom
        var datagrams = new SocketDatagram[_batchCount];
        for (int i = 0; i < _batchCount; i++)
        {
            datagrams[i] = new SocketDatagram(
                _batchBuffers[i].AsMemory(0, _batchSizes[i]),
                _batchEndpoints[i]!);
        }
        
        _batchCount = 0;
        Interlocked.Increment(ref _packetsSentLastSecond);
        try
        {
            var task = _socket.SendMessagesAsync(datagrams, SocketFlags.None, cancellationToken);
            if (task.IsCompletedSuccessfully)
            {
                return default;
            }
            return new ValueTask(task.AsTask());
        }
        catch (SocketException)
        {
            return default;
        }
#else
        int count = _batchCount;
        _batchCount = 0;

        try
        {
            for (int i = 0; i < count; i++)
            {
                var task = _socket.SendToAsync(_batchBuffers[i].AsMemory(0, _batchSizes[i]),
                             SocketFlags.None,
                             _batchEndpoints[i]!,
                             cancellationToken);

                if (!task.IsCompletedSuccessfully)
                {
                    // Fall back to awaiting the rest of the batch sequentially.
                    return FinishFlushBatchAsync(task.AsTask(), i + 1, count, cancellationToken);
                }
            }
        }
        catch (SocketException) { }

        return default;
#endif
    }

#if !(NET8_0_OR_GREATER && HAS_SENDMESSAGESASYNC)
    private async ValueTask FinishFlushBatchAsync(Task firstTask, int startIndex, int count, CancellationToken cancellationToken)
    {
        try
        {
            await firstTask.ConfigureAwait(false);
            for (int i = startIndex; i < count; i++)
            {
                await _socket.SendToAsync(_batchBuffers[i].AsMemory(0, _batchSizes[i]),
                             SocketFlags.None,
                             _batchEndpoints[i]!,
                             cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SocketException) { }
    }
#endif

    /// <summary>
    ///     Create the upper-level connection instance.
    /// </summary>
    /// <returns>The upper-level connection instance.</returns>
    protected abstract T Activate();


    /// <summary>
    ///     Called before a received packet is forwarded to the KCP conversation.
    ///     Return true to consume the packet (skip KCP processing).
    ///     Return false to let KCP handle it normally.
    /// </summary>
    protected virtual bool OnRawPacketReceived(ReadOnlyMemory<byte> packet, IPEndPoint remoteEndPoint)
    {
        return false;
    }

    /// <summary>
    ///     Handle exception thrown when receiving from remote endpoint.
    /// </summary>
    /// <param name="ex">The exception thrown.</param>
    /// <returns>Whether error should be ignored.</returns>
    protected virtual bool HandleException(Exception ex)
    {
        return false;
    }

    /// <summary>
    ///     Create the upper-level connection and start pumping packets from the socket to the upper-level connection.
    /// </summary>
    internal void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(KcpSocketTransport));
        if (_connection is not null) throw new InvalidOperationException();

        _connection = Activate();
        if (_connection is null) throw new InvalidOperationException();

        TuneSocket(_socket);

        _scheduler = new KcpScheduler(workerThreadCount: 2); // default 2 workers
        if (_connection is KcpConversation kcpConv)
        {
            _scheduler.RegisterConversation(kcpConv);
            kcpConv.OnWorkAvailable = () => _scheduler.EnqueueWork(kcpConv);
            _scheduler.ScheduleTimer(kcpConv, 100);
        }

        _cts = new CancellationTokenSource();
        _stopped = false;

        var port = ((IPEndPoint?)_socket.LocalEndPoint)?.Port ?? 0;
        _receiveThread = new Thread(RunReceiveLoopSync)
        {
            IsBackground = true,
            Name = $"KcpRecv-{port}",
            Priority = ThreadPriority.AboveNormal
        };
        _receiveThread.Start();
    }

    internal void ScheduleConversation(KcpConversation conversation)
    {
        if (_scheduler != null && !_disposed)
        {
            _scheduler.RegisterConversation(conversation);
            conversation.OnWorkAvailable = () => _scheduler.EnqueueWork(conversation);
            _scheduler.ScheduleTimer(conversation, 100);
        }
    }

    internal void UnscheduleConversation(KcpConversation conversation)
    {
        if (_scheduler != null && !_disposed)
        {
            _scheduler.UnscheduleConversation(conversation);
        }
    }

    private void TuneSocket(Socket socket)
    {
        try
        {
            socket.SendBufferSize = 4 * 1024 * 1024;
            socket.ReceiveBufferSize = 4 * 1024 * 1024;
        }
        catch { }

        try
        {
            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        }
        catch { }

    }

    private void RunReceiveLoopSync()
    {
        IKcpConversation? connection = _connection;
        if (connection is null || _stopped) return;

        var pool = new PacketBufferPool(_receiveBufferPoolSize, _mtu);
        var remoteEndpoint = new IPEndPoint(_socket.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        var reusableSocketAddress = remoteEndpoint.Serialize();

        var cancellationToken = _cts?.Token ?? CancellationToken.None;

        try
        {
            while (!_stopped && !cancellationToken.IsCancellationRequested)
            {
                var bufferOwnerTask = pool.RentAsync(cancellationToken);
                var bufferOwner = bufferOwnerTask.IsCompletedSuccessfully
                    ? bufferOwnerTask.Result
                    : bufferOwnerTask.AsTask().GetAwaiter().GetResult();

                var bytesReceived = 0;
                IPEndPoint? endpoint = null;
                try
                {
                    var receiveTask = _socket.ReceiveFromAsync(bufferOwner.Memory, System.Net.Sockets.SocketFlags.None, reusableSocketAddress);
                    if (receiveTask.IsCompletedSuccessfully)
                    {
                        bytesReceived = receiveTask.Result;
                    }
                    else
                    {
                        bytesReceived = receiveTask.AsTask().GetAwaiter().GetResult();
                    }

                    if (reusableSocketAddress.Family == AddressFamily.InterNetwork)
                    {
                        long ip = (long)reusableSocketAddress[4] | ((long)reusableSocketAddress[5] << 8) | ((long)reusableSocketAddress[6] << 16) | ((long)reusableSocketAddress[7] << 24);
                        int port = (reusableSocketAddress[2] << 8) | reusableSocketAddress[3];
                        endpoint = new IPEndPoint(new IPAddress(ip), port);
                    }
                    else
                    {
                        endpoint = (IPEndPoint)remoteEndpoint.Create(reusableSocketAddress);
                    }
                }
                catch (OperationCanceledException)
                {
                    bufferOwner.Dispose();
                    break;
                }
                catch (ObjectDisposedException)
                {
                    bufferOwner.Dispose();
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Windows-specific behavior: UDP sockets may throw WSAECONNRESET when receiving ICMP "port unreachable".
                    // This should not terminate the server receive loop.
                    bufferOwner.Dispose();
                    continue;
                }
                catch (Exception ex)
                {
                    Logger.Error("UDP receive loop error", ex);
                    HandleExceptionWrapper(ex);
                    bufferOwner.Dispose();
                    break;
                }

                if (bytesReceived != 0 && bytesReceived <= _mtu)
                {
                    var packet = bufferOwner.Memory.Slice(0, bytesReceived);

                    // Visibility: log first few small UDP packets per remote endpoint.
                    try
                    {
                        if (ShouldShowHandshakeLog() && bytesReceived <= 96)
                        {
                            var key = endpoint?.ToString() ?? "unknown";
                            var count = UdpDebugCounters.AddOrUpdate(key, 1, static (_, c) => c + 1);
                            if (count <= 10)
                            {
                                var headLen = Math.Min(16, bytesReceived);
                                var headHex = Convert.ToHexString(packet.Span.Slice(0, headLen));
                                Logger.Debug($"UDP recv: remote={endpoint} bytes={bytesReceived} head16={headHex}");
                            }
                        }
                    }
                    catch
                    {
                        // ignore logging errors
                    }

                    if (endpoint is null || OnRawPacketReceived(packet, endpoint) || connection is not IKcpPacketSink sink)
                    {
                        bufferOwner.Dispose();
                        continue;
                    }

                    var inputTask = sink.InputPacketAsync(packet, endpoint, bufferOwner, CancellationToken.None);
                    if (!inputTask.IsCompletedSuccessfully)
                    {
                        inputTask.AsTask().GetAwaiter().GetResult();
                    }

                }
                else
                {
                    bufferOwner.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Do nothing
        }
        catch (Exception ex)
        {
            HandleExceptionWrapper(ex);
        }
    }

    private bool HandleExceptionWrapper(Exception ex)
    {
        bool result;
        try
        {
            new Logger("KcpServer").Error("KCP Error:", ex);
            result = HandleException(ex);
        }
        catch
        {
            result = false;
        }

        _connection?.SetTransportClosed();
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        return result;
    }

    /// <summary>
    ///     Dispose all the managed and the unmanaged resources used by this instance.
    /// </summary>
    /// <param name="disposing">If managed resources should be disposed.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _stopped = true;
            _disposed = true;

            if (disposing)
            {
                var cts = Interlocked.Exchange(ref _cts, null);
                if (cts is not null)
                {
                    try { cts.Cancel(); } catch { }
                    cts.Dispose();
                }

                try
                {
                    // Forcefully unblock any active socket operations
                    _socket.Dispose();
                }
                catch { }

                _connection?.Dispose();

                var thread = _receiveThread;
                if (thread != null && thread.IsAlive && Thread.CurrentThread != thread)
                {
                    thread.Join(100);
                }

                _scheduler?.Dispose();
            }

            _connection = null;
            _cts = null;
            _receiveThread = null;
            _scheduler = null;
        }
    }

    private static bool ShouldShowHandshakeLog()
    {
        try
        {
            return ConfigManager.Config.ServerOption.LogOption.ShowKcpHandShake;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    ///     Dispose the unmanaged resources used by this instance.
    /// </summary>
    ~KcpSocketTransport()
    {
        Dispose(false);
    }
}
