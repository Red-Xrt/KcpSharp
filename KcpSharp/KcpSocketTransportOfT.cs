using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;


namespace KcpSharp;

internal static class KcpSocketTransportNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct iovec
    {
        public void* iov_base;
        public nuint iov_len;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct msghdr
    {
        public void* msg_name;
        public uint msg_namelen;
        public iovec* msg_iov;
        public nuint msg_iovlen;
        public void* msg_control;
        public nuint msg_controllen;
        public int msg_flags;
    }

    // Stack Buffer Overflow Fix: Separate msghdr and msg_len and handle alignment properly for 64-bit systems.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mmsghdr
    {
        public msghdr msg_hdr;
        public uint msg_len;
        // The CLR will automatically pad this to the largest member alignment, but Linux ABI demands 8-byte alignment
        // if msghdr is 8-byte aligned. On 64-bit systems, msghdr will be aligned to 8 bytes, so mmsghdr will be as well.
    }

    [DllImport("libc", EntryPoint = "sendmmsg", SetLastError = true)]
    internal static extern unsafe int sendmmsg(int sockfd, mmsghdr* msgvec, uint vlen, int flags);

    [DllImport("libc", EntryPoint = "recvmmsg", SetLastError = true)]
    internal static extern unsafe int recvmmsg(int sockfd, mmsghdr* msgvec, uint vlen, int flags, void* timeout);
}

/// <summary>
///     A Socket transport for upper-level connections.
/// </summary>
/// <typeparam name="T"></typeparam>
internal abstract class KcpSocketTransport<T> : IKcpTransport, IKcpBatchTransport, IKcpBatchTransport2, IDisposable where T : class, IKcpConversation
{
    private readonly int _mtu;
    private readonly int _receiveBufferPoolSize;
    protected readonly Socket _socket;

    private T? _connection;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private readonly byte[][][] _batchBuffers;
    private readonly IPEndPoint?[][] _batchEndpoints;
    private readonly int[][] _batchSizes;
    private readonly byte[][][] _batchAddresses;
    private readonly int[][] _batchAddressLengths;
    private int _batchCount;
    private int _activeSet;
    private volatile bool _anyPacketCommitted;
    private readonly int _maxBatchSize;
    private readonly System.Threading.Lock _batchLock = new();
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);

    private static readonly ObjectPool<KcpPacketOwner> s_sharedPacketOwnerPool = new DefaultObjectPool<KcpPacketOwner>(
            new DefaultPooledObjectPolicy<KcpPacketOwner>(),
            maximumRetained: Math.Max(4096, Environment.ProcessorCount * 128));

    /// <summary>
    ///     Construct a socket transport with the specified socket and remote endpoint.
    /// </summary>
    /// <param name="socket">The socket instance.</param>
    /// <param name="mtu">The maximum packet size that can be transmitted.</param>
    /// <param name="maxBatchSize">The maximum number of packets to batch.</param>
    /// <param name="receiveBufferPoolSize">The size of the pool to allocate receive buffers.</param>
    protected KcpSocketTransport(Socket socket, int mtu, int maxBatchSize, int receiveBufferPoolSize = 8)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _mtu = mtu;
        _receiveBufferPoolSize = receiveBufferPoolSize;
        if (mtu < 50) throw new ArgumentOutOfRangeException(nameof(mtu));
        if (receiveBufferPoolSize <= 0) throw new ArgumentOutOfRangeException(nameof(receiveBufferPoolSize));
        if (maxBatchSize < 0) throw new ArgumentOutOfRangeException(nameof(maxBatchSize), "MaxBatchSize must be non-negative.");

        _maxBatchSize = maxBatchSize;

        _batchBuffers = new byte[2][][];
        _batchEndpoints = new IPEndPoint?[2][];
        _batchSizes = new int[2][];
        _batchAddresses = new byte[2][][];
        _batchAddressLengths = new int[2][];

        if (maxBatchSize > 0)
        {
            for (int s = 0; s < 2; s++)
            {
                _batchBuffers[s] = new byte[maxBatchSize][];
                _batchAddresses[s] = new byte[maxBatchSize][];
                for (int i = 0; i < maxBatchSize; i++)
                {
                    _batchBuffers[s][i] = GC.AllocateUninitializedArray<byte>(_mtu, pinned: true);
                    _batchAddresses[s][i] = new byte[128]; // Max SocketAddress size
                }

                _batchEndpoints[s] = new IPEndPoint[maxBatchSize];
                _batchSizes[s] = new int[maxBatchSize];
                _batchAddressLengths[s] = new int[maxBatchSize];
            }
        }

        _batchCount = 0;
        _activeSet = 0;
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

    private async ValueTask SendCoreAsync(Memory<byte> packet, IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            await _socket
                .SendToAsync(packet, SocketFlags.None, endpoint, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            HandleExceptionWrapper(ex);
            throw;
        }
    }

    int IKcpBatchTransport.BatchCapacity => _maxBatchSize - Volatile.Read(ref _batchCount);

    bool IKcpBatchTransport.TryGetBatchSliceAndCommit(int requiredSize, IPEndPoint endpoint, Action<Memory<byte>> dataWriter)
    {
        if (_maxBatchSize <= 1) return false;

        int slotIndex;
        int activeSet;
        lock (_batchLock)
        {
            if (_batchCount >= _maxBatchSize || requiredSize > _mtu)
            {
                return false;
            }
            slotIndex = _batchCount;
            activeSet = _activeSet;

            var sa = endpoint.Serialize();
            if (sa.Size > 128)
            {
                return false;
            }

            _batchEndpoints[activeSet][slotIndex] = endpoint;
            _batchSizes[activeSet][slotIndex] = requiredSize;

            sa.Buffer.Span.Slice(0, sa.Size).CopyTo(_batchAddresses[activeSet][slotIndex]);
            _batchAddressLengths[activeSet][slotIndex] = sa.Size;

            var slice = _batchBuffers[activeSet][slotIndex].AsMemory(0, requiredSize);
            dataWriter(slice);

            _batchCount++;
            _anyPacketCommitted = true;
        }
        return true;
    }

    bool IKcpBatchTransport2.AnyPacketCommitted => _anyPacketCommitted;


    async ValueTask IKcpBatchTransport.FlushBatchAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _batchCount) == 0) return;

        int countToFlush;
        int activeSet;

        await _flushSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_batchLock)
            {
                if (_batchCount == 0) return;

                countToFlush = _batchCount;
                activeSet = _activeSet;

                _activeSet = 1 - _activeSet;
                _batchCount = 0;
                _anyPacketCommitted = false;
            }

            if (OperatingSystem.IsLinux() && countToFlush > 1)
            {
                unsafe
                {
                    KcpSocketTransportNative.mmsghdr[]? msgvecPool = null;
                    KcpSocketTransportNative.iovec[]? iovecsPool = null;
                    Span<KcpSocketTransportNative.mmsghdr> msgvec;
                    Span<KcpSocketTransportNative.iovec> iovecs;

                    if (countToFlush <= 32)
                    {
                        msgvec = stackalloc KcpSocketTransportNative.mmsghdr[countToFlush];
                        iovecs = stackalloc KcpSocketTransportNative.iovec[countToFlush];
                    }
                    else
                    {
                        msgvecPool = ArrayPool<KcpSocketTransportNative.mmsghdr>.Shared.Rent(countToFlush);
                        iovecsPool = ArrayPool<KcpSocketTransportNative.iovec>.Shared.Rent(countToFlush);
                        msgvec = msgvecPool.AsSpan(0, countToFlush);
                        iovecs = iovecsPool.AsSpan(0, countToFlush);
                    }

                    byte[] socketAddresses = ArrayPool<byte>.Shared.Rent(countToFlush * 128);

                    try
                    {
                        fixed (byte* pAddrStr = socketAddresses)
                        {
                            fixed (KcpSocketTransportNative.iovec* pIovecs = iovecs)
                            fixed (KcpSocketTransportNative.mmsghdr* msgvecPtr = msgvec)
                            {
                                for (int i = 0; i < countToFlush; i++)
                                {
                                    // _batchBuffers is allocated with pinned: true
                                    ref byte firstByte = ref _batchBuffers[activeSet][i][0];
                                    pIovecs[i].iov_base = System.Runtime.CompilerServices.Unsafe.AsPointer(ref firstByte);
                                    pIovecs[i].iov_len = (nuint)_batchSizes[activeSet][i];

                                    int addrLen = _batchAddressLengths[activeSet][i];

                                    byte* pAddr = pAddrStr + (i * 128);
                                    fixed (byte* srcAddr = _batchAddresses[activeSet][i])
                                    {
                                        for (int j = 0; j < addrLen; j++)
                                        {
                                            pAddr[j] = srcAddr[j];
                                        }
                                    }

                                    msgvecPtr[i].msg_hdr.msg_name = pAddr;
                                    msgvecPtr[i].msg_hdr.msg_namelen = (uint)addrLen;
                                    msgvecPtr[i].msg_hdr.msg_iov = &pIovecs[i];
                                    msgvecPtr[i].msg_hdr.msg_iovlen = 1;
                                    msgvecPtr[i].msg_hdr.msg_control = null;
                                    msgvecPtr[i].msg_hdr.msg_controllen = 0;
                                    msgvecPtr[i].msg_hdr.msg_flags = 0;
                                    msgvecPtr[i].msg_len = 0;
                                }

                                int sockfd = _socket.Handle.ToInt32();
                                int sent = 0;
                                while (sent < countToFlush)
                                {
                                    int ret = KcpSocketTransportNative.sendmmsg(sockfd, msgvecPtr + sent, (uint)(countToFlush - sent), 0);
                                    if (ret < 0)
                                    {
                                        int error = Marshal.GetLastWin32Error();
                                        // Handle EINTR or EAGAIN/EWOULDBLOCK if needed, here we just throw or fallback
                                        if (error == 4 /* EINTR */) continue;
                                        throw new SocketException(error);
                                    }
                                    sent += ret;
                                }
                            }
                        }
                    }
                    catch (SocketException ex)
                    {
                        HandleExceptionWrapper(ex);
                        throw;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(socketAddresses);
                        if (msgvecPool != null) ArrayPool<KcpSocketTransportNative.mmsghdr>.Shared.Return(msgvecPool);
                        if (iovecsPool != null) ArrayPool<KcpSocketTransportNative.iovec>.Shared.Return(iovecsPool);
                    }
                }
            }
            else
            {
                for (int i = 0; i < countToFlush; i++)
                {
                    try
                    {
                        await _socket
                            .SendToAsync(_batchBuffers[activeSet][i].AsMemory(0, _batchSizes[activeSet][i]),
                                         SocketFlags.None,
                                         _batchEndpoints[activeSet][i]!,
                                         cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (SocketException ex) when (
                        ex.SocketErrorCode == SocketError.WouldBlock ||
                        ex.SocketErrorCode == SocketError.TryAgain ||
                        ex.SocketErrorCode == SocketError.Interrupted)
                    {
                        continue; // Retry/skip packet
                    }
                    catch (SocketException ex)
                    {
                        HandleExceptionWrapper(ex);
                        break;
                    }
                }
            }
        }
        finally
        {
            _flushSemaphore.Release();
        }
    }

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

        _cts = new CancellationTokenSource();
        RunReceiveLoop();
    }

    private void TuneSocket(Socket socket)
    {
        if (socket.AddressFamily == AddressFamily.InterNetworkV6)
        {
            try { socket.DualMode = true; } catch { }
        }
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DontFragment, true);
        }
        catch { }

        try
        {
            socket.SendBufferSize = 4 * 1024 * 1024;
            socket.ReceiveBufferSize = 4 * 1024 * 1024;
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (PlatformNotSupportedException) { }
        }
    }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<SocketAddress, IPEndPoint> _endpointCache = new(new SocketAddressEqualityComparer());
    private readonly SocketAddress[] _endpointEvictionQueue = new SocketAddress[512];
    private int _endpointEvictionIndex;


    private sealed class SocketAddressEqualityComparer : IEqualityComparer<SocketAddress>
    {
        public bool Equals(SocketAddress? x, SocketAddress? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            if (x.Family != y.Family || x.Size != y.Size) return false;

            for (int i = 0; i < x.Size; i++)
            {
                if (x[i] != y[i]) return false;
            }

            return true;
        }

        public int GetHashCode(SocketAddress obj)
        {
            if (obj is null) return 0;

            // FNV-1a hash algorithm
            uint hash = 2166136261;
            for (int i = 0; i < obj.Size; i++)
            {
                hash ^= obj[i];
                hash *= 16777619;
            }

            return (int)hash;
        }
    }

    private void RunReceiveLoop()
    {
        // Use a single receive loop. Multiple concurrent receive loops on the same socket
        // cause out-of-order packet delivery, increasing latency and memory pressure.
        RunReceiveLoopAsync();
    }


    private void RunReceiveLoopAsync()
    {
        if (OperatingSystem.IsLinux() && _socket.Handle != IntPtr.Zero)
        {
            _ = Task.Run(RunReceiveLoopLinuxAsync);
            return;
        }

        var thread = new System.Threading.Thread(RunReceiveLoopWindowsSync)
        {
            IsBackground = true,
            Priority = System.Threading.ThreadPriority.AboveNormal,
            Name = "KcpReceiveThread"
        };
        thread.Start();
    }

    private void RunReceiveLoopWindowsSync()
    {
        var cancellationToken = _cts?.Token ?? new CancellationToken(true);
        IKcpConversation? connection = _connection;
        if (connection is null || cancellationToken.IsCancellationRequested) return;

        var remoteEndpoint = (EndPoint)new IPEndPoint(_socket.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        byte[] localBuffer = ArrayPool<byte>.Shared.Rent(65536);
        IPEndPoint? cachedEndpoint = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesReceived = 0;

                try
                {
                    // Blocking receive first to wait for any data (avoids CPU spin)
                    bytesReceived = _socket.ReceiveFrom(localBuffer, 0, 65536, SocketFlags.None, ref remoteEndpoint);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.Interrupted)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break; // Expected on exit/transport close
                }
                catch (Exception ex)
                {
                    HandleExceptionWrapper(ex);
                    break;
                }

                if (cancellationToken.IsCancellationRequested) break;

                IPEndPoint? ep = ResolveEndpoint(remoteEndpoint, ref cachedEndpoint);
                if (ep != null)
                {
                    // Process the first packet
                    ProcessReceivedPacket(localBuffer, bytesReceived, ep);
                }

                // Drain the socket buffer completely
                while (!cancellationToken.IsCancellationRequested && _socket.Poll(0, SelectMode.SelectRead))
                {
                    try
                    {
                        bytesReceived = _socket.ReceiveFrom(localBuffer, 0, 65536, SocketFlags.None, ref remoteEndpoint);
                        ep = ResolveEndpoint(remoteEndpoint, ref cachedEndpoint);
                        if (ep != null)
                        {
                            ProcessReceivedPacket(localBuffer, bytesReceived, ep);
                        }
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.Interrupted)
                    {
                        break; // Buffer is empty or reset, go back to blocking wait
                    }
                    catch (ObjectDisposedException)
                    {
                        break; // Expected on exit/transport close
                    }
                    catch (Exception ex)
                    {
                        HandleExceptionWrapper(ex);
                        break;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(localBuffer);
        }
    }

    private IPEndPoint? ResolveEndpoint(EndPoint remoteEndpoint, ref IPEndPoint? cachedEndpoint)
    {
        var rawEp = remoteEndpoint as IPEndPoint;
        if (rawEp is null) return null;

        SocketAddress receivedAddress = remoteEndpoint.Serialize();

        if (cachedEndpoint != null && KcpNetworkUtils.EndPointEquals(cachedEndpoint, receivedAddress))
        {
            return cachedEndpoint;
        }
        else if (_endpointCache.TryGetValue(receivedAddress, out var epFromCache))
        {
            cachedEndpoint = epFromCache;
            return epFromCache;
        }
        else
        {
            var clonedAddress = new SocketAddress(receivedAddress.Family, receivedAddress.Size);
            for (int j = 0; j < receivedAddress.Size; j++)
            {
                clonedAddress[j] = receivedAddress[j];
            }

            if (_endpointCache.TryAdd(clonedAddress, rawEp))
            {
                int index = (int)((uint)System.Threading.Interlocked.Increment(ref _endpointEvictionIndex) % 512);
                var oldAddress = System.Threading.Interlocked.Exchange(ref _endpointEvictionQueue[index], clonedAddress);
                if (oldAddress != null)
                {
                    _endpointCache.TryRemove(oldAddress, out _);
                }
            }
            cachedEndpoint = rawEp;
            return rawEp;
        }
    }

    private void ProcessReceivedPacket(byte[] buffer, int bytesReceived, IPEndPoint ep)
    {
        if (bytesReceived < KcpGlobalVars.HEADER_LENGTH_WITHOUT_CONVID)
            return;

        var packetMemory = new ReadOnlyMemory<byte>(buffer, 0, bytesReceived);

        if (OnRawPacketReceived(packetMemory, ep))
        {
            return;
        }

        var connection = _connection;
        if (connection != null && connection is IKcpPacketSink sink)
        {
            var owner = s_sharedPacketOwnerPool.Get();
            owner.Initialize(s_sharedPacketOwnerPool, bytesReceived);
            packetMemory.Span.CopyTo(owner.Memory.Span);
            _ = FireAndForgetInput(sink.InputPacketAsync(owner.Memory.Slice(0, bytesReceived), ep, owner, default));
        }
    }

private async Task RunReceiveLoopLinuxAsync()
    {
        // Hop off the threadpool to run a blocking recvmmsg loop.
        // This is safe because RunReceiveLoopLinuxAsync is called in a fire-and-forget Task.Run
        // from Start(), but since we are doing Socket.Poll which blocks, we should ensure
        // we yield control first, or explicitly request a LongRunning thread if desired.
        // Task.Yield() ensures we hop onto a clean thread-pool thread to become a dedicated receiver.
        await Task.Yield();

        var cancellationToken = _cts?.Token ?? new CancellationToken(true);
        IKcpConversation? connection = _connection;
        if (connection is null || cancellationToken.IsCancellationRequested) return;

        var remoteEndpoint = (EndPoint)new IPEndPoint(_socket.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        // Even if send batching is disabled (_maxBatchSize == 0), we want to aggressively batch receive packets
        // from the OS kernel buffer into managed memory to reduce recvmmsg syscalls and context switches.
        int maxBatchSize = _maxBatchSize > 0 ? _maxBatchSize : 32;

        KcpSocketTransportNative.mmsghdr[] msgvecPool = ArrayPool<KcpSocketTransportNative.mmsghdr>.Shared.Rent(maxBatchSize);
        KcpSocketTransportNative.iovec[] iovecsPool = ArrayPool<KcpSocketTransportNative.iovec>.Shared.Rent(maxBatchSize);
        byte[] addressBuffer = ArrayPool<byte>.Shared.Rent(maxBatchSize * 128);
        byte[][] buffers = new byte[maxBatchSize][];

        for (int i = 0; i < maxBatchSize; i++)
        {
            buffers[i] = GC.AllocateUninitializedArray<byte>(65536, pinned: true);
        }

        IPEndPoint? cachedEndpoint = null;
        SocketAddress[] socketAddressesPool = new SocketAddress[maxBatchSize];
        for (int i = 0; i < maxBatchSize; i++)
        {
            socketAddressesPool[i] = new SocketAddress(_socket.AddressFamily, 128); // Max typical size
        }

        try
        {
            int sockfd = _socket.Handle.ToInt32();
            int emptyPollCount = 0;
            int currentPollTimeout = 1000; // 1ms active

            while (!cancellationToken.IsCancellationRequested)
            {
                // We use Socket.Poll to wait synchronously without busy-spinning CPU,
                // waiting until at least one packet is fully available in kernel buffer.
                // 10,000 microseconds = 10 ms. If no data, the loop continues and checks cancellationToken.
                try
                {
                    bool dataAvailable = _socket.Poll(currentPollTimeout, SelectMode.SelectRead);
                    if (!dataAvailable)
                    {
                        emptyPollCount++;
                        if (emptyPollCount >= 10)
                        {
                            currentPollTimeout = 5000; // 5ms idle
                        }
                        continue;
                    }

                    // Reset back to active 1ms tier
                    emptyPollCount = 0;
                    currentPollTimeout = 1000;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Ignore connection resets from ICMP unreachable messages
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    HandleExceptionWrapper(ex);
                    break;
                }

                if (cancellationToken.IsCancellationRequested) break;

                int ret = 0;


                unsafe
                {
                    fixed (byte* pAddrStr = addressBuffer)
                    fixed (KcpSocketTransportNative.iovec* pIovecs = iovecsPool)
                    fixed (KcpSocketTransportNative.mmsghdr* pMsgvec = msgvecPool)
                    {
                        for (int i = 0; i < maxBatchSize; i++)
                        {
                            ref byte firstByte = ref buffers[i][0];
                            pIovecs[i].iov_base = System.Runtime.CompilerServices.Unsafe.AsPointer(ref firstByte);
                            pIovecs[i].iov_len = 65536;

                            byte* pAddr = pAddrStr + (i * 128);
                            pMsgvec[i].msg_hdr.msg_name = pAddr;
                            pMsgvec[i].msg_hdr.msg_namelen = 128;
                            pMsgvec[i].msg_hdr.msg_iov = pIovecs + i;
                            pMsgvec[i].msg_hdr.msg_iovlen = 1;
                            pMsgvec[i].msg_hdr.msg_control = null;
                            pMsgvec[i].msg_hdr.msg_controllen = 0;
                            pMsgvec[i].msg_hdr.msg_flags = 0;
                        }

                        // MSG_WAITFORONE = 0x10000 -> Block until at least 1 packet is ready
                        ret = KcpSocketTransportNative.recvmmsg(sockfd, pMsgvec, (uint)maxBatchSize, 0x10000, null);
                        if (ret < 0)
                        {
                            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                            if (error == 4 /* EINTR */ || error == 11 /* EAGAIN */ || error == 14 /* EWOULDBLOCK */) continue;

                            if (error == 104 /* ECONNRESET */) continue;
                            throw new SocketException(error);
                        }

                        for (int i = 0; i < ret; i++)
                        {
                            uint bytesReceived = pMsgvec[i].msg_len;
                            if (bytesReceived < KcpGlobalVars.HEADER_LENGTH_WITHOUT_CONVID)
                            {
                                continue;
                            }

                            int addrLen = (int)pMsgvec[i].msg_hdr.msg_namelen;
                            byte* pAddr = pAddrStr + (i * 128);

                            SocketAddress receivedAddress = socketAddressesPool[i];
                            // Update size if it changed (though SocketAddress Size is internal/initonly in some frameworks,
                            // we just overwrite the existing bytes up to addrLen and use it)
                            for (int j = 0; j < addrLen; j++)
                            {
                                receivedAddress[j] = pAddr[j];
                            }

                            IPEndPoint endpoint;
                            if (cachedEndpoint != null && KcpNetworkUtils.EndPointEquals(cachedEndpoint, receivedAddress))
                            {
                                endpoint = cachedEndpoint;
                            }
                            else if (_endpointCache.TryGetValue(receivedAddress, out var epFromCache))
                            {
                                endpoint = epFromCache;
                                cachedEndpoint = epFromCache;
                            }
                            else
                            {
                                var ep = remoteEndpoint.Create(receivedAddress) as IPEndPoint;
                                if (ep is null) continue;
                                endpoint = ep;
                                cachedEndpoint = ep;

                                var clonedAddress = new SocketAddress(receivedAddress.Family, receivedAddress.Size);
                                for (int j = 0; j < receivedAddress.Size; j++)
                                {
                                    clonedAddress[j] = receivedAddress[j];
                                }

                                if (_endpointCache.TryAdd(clonedAddress, ep))
                                {
                                    int index = (int)((uint)System.Threading.Interlocked.Increment(ref _endpointEvictionIndex) % 512);
                                    var oldAddress = System.Threading.Interlocked.Exchange(ref _endpointEvictionQueue[index], clonedAddress);
                                    if (oldAddress != null)
                                    {
                                        _endpointCache.TryRemove(oldAddress, out _);
                                    }
                                }
                            }

                            Memory<byte> bufferMemory = buffers[i].AsMemory(0, (int)bytesReceived);
                            var packet = bufferMemory.Slice(0, (int)bytesReceived);

                            if (OnRawPacketReceived(packet, endpoint))
                            {
                                continue;
                            }

                            if (connection is IKcpPacketSink sink)
                            {
                                var packetOwner = s_sharedPacketOwnerPool.Get();
                                packetOwner.Initialize(s_sharedPacketOwnerPool, (int)bytesReceived);
                                packet.CopyTo(packetOwner.Memory.Slice(0, (int)bytesReceived));

                                var inputTask = sink.InputPacketAsync(packetOwner.Memory.Slice(0, (int)bytesReceived), endpoint, packetOwner, cancellationToken);
                                if (!inputTask.IsCompletedSuccessfully)
                                {
                                    _ = FireAndForgetInput(inputTask);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HandleExceptionWrapper(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(addressBuffer);
            ArrayPool<KcpSocketTransportNative.mmsghdr>.Shared.Return(msgvecPool);
            ArrayPool<KcpSocketTransportNative.iovec>.Shared.Return(iovecsPool);
        }
    }

    private async ValueTask FireAndForgetInput(ValueTask task)
    {
        // No need to dispose the owner here: ownership was already transferred to InputPacketAsync
        await task.ConfigureAwait(false);
    }

    private bool HandleExceptionWrapper(Exception ex)
    {
        bool result;
        try
        {
            result = HandleException(ex);
        }
        catch
        {
            result = false;
        }

        if (!result)
        {
            _connection?.SetTransportClosed();
            var cts = Interlocked.Exchange(ref _cts, null);
            if (cts is not null)
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
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
            if (disposing)
            {
                var cts = Interlocked.Exchange(ref _cts, null);
                if (cts is not null)
                {
                    cts.Cancel();
                    cts.Dispose();
                }

                _connection?.Dispose();
                _flushSemaphore.Dispose();
            }

            _connection = null;
            _cts = null;
            _disposed = true;
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
