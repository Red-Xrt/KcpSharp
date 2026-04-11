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

            _batchEndpoints[activeSet][slotIndex] = endpoint;
            _batchSizes[activeSet][slotIndex] = requiredSize;

            var sa = endpoint.Serialize();
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
        _ = Task.Run(RunReceiveLoopAsync);
    }

    private async Task RunReceiveLoopAsync()
    {
        if (OperatingSystem.IsLinux() && _socket.Handle != IntPtr.Zero)
        {
            await RunReceiveLoopLinuxAsync().ConfigureAwait(false);
            return;
        }

        var cancellationToken = _cts?.Token ?? new CancellationToken(true);
        IKcpConversation? connection = _connection;
        if (connection is null || cancellationToken.IsCancellationRequested) return;

        var remoteEndpoint = (EndPoint)new IPEndPoint(_socket.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);

        SocketAddress receivedAddress = new SocketAddress(_socket.AddressFamily);

        byte[] localBuffer = ArrayPool<byte>.Shared.Rent(65536);

        // Cache the endpoint to avoid Gen0 GC allocation on every receive when receiving from the same remote IP
        IPEndPoint? cachedEndpoint = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Memory<byte> bufferMemory = localBuffer.AsMemory(0, 65536);

                try
                {
                    var bytesReceived = await _socket.ReceiveFromAsync(bufferMemory, SocketFlags.None, receivedAddress, cancellationToken).ConfigureAwait(false);

                    if (bytesReceived < KcpGlobalVars.HEADER_LENGTH_WITHOUT_CONVID)
                    {
                        continue;
                    }

                    // We must convert the SocketAddress to an IPEndPoint, but cache it if it's the same
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

                        if (_endpointCache.Count >= 512)
                        {
                            _endpointCache.Clear();
                        }

                        var clonedAddress = new SocketAddress(receivedAddress.Family, receivedAddress.Size);
                        for (int i = 0; i < receivedAddress.Size; i++)
                        {
                            clonedAddress[i] = receivedAddress[i];
                        }

                        _endpointCache.TryAdd(clonedAddress, ep);
                    }

                    var packet = bufferMemory.Slice(0, bytesReceived);

                    if (OnRawPacketReceived(packet, endpoint))
                    {
                        continue;
                    }

                    if (connection is not IKcpPacketSink sink)
                    {
                        continue;
                    }

                    var packetOwner = s_sharedPacketOwnerPool.Get();
                    packetOwner.Initialize(s_sharedPacketOwnerPool, bytesReceived);
                    packet.CopyTo(packetOwner.Memory.Slice(0, bytesReceived));

                    var inputTask = sink.InputPacketAsync(packetOwner.Memory.Slice(0, bytesReceived), endpoint, packetOwner, cancellationToken);
                    if (!inputTask.IsCompletedSuccessfully)
                    {
                        await AwaitAndDisposeAsync(inputTask).ConfigureAwait(false);
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                catch (OperationCanceledException)
                {
                    break;
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
            ArrayPool<byte>.Shared.Return(localBuffer);
        }
    }

    private async Task RunReceiveLoopLinuxAsync()
    {
        var cancellationToken = _cts?.Token ?? new CancellationToken(true);
        IKcpConversation? connection = _connection;
        if (connection is null || cancellationToken.IsCancellationRequested) return;

        var remoteEndpoint = (EndPoint)new IPEndPoint(_socket.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
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
        SocketAddress cachedAddress = new SocketAddress(_socket.AddressFamily);

        try
        {
            int sockfd = (int)_socket.Handle;

            while (!cancellationToken.IsCancellationRequested)
            {
                // To avoid completely blocking the threadpool on recv, we can perform a 0-byte read or select.
                // However, recvmmsg doesn't have an async version in .NET. Wait for data using the Socket:
                // We use Socket.ReceiveFromAsync with 0 byte just to wait for the next packet.
                try
                {
                    await _socket.ReceiveFromAsync(Memory<byte>.Empty, SocketFlags.Peek, cachedAddress, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.MessageSize)
                {
                    // Ignore and let recvmmsg clear it or we peek 0 bytes on some platforms
                }
                catch (OperationCanceledException)
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
                ValueTask[] tasks = Array.Empty<ValueTask>();
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

                        ret = KcpSocketTransportNative.recvmmsg(sockfd, pMsgvec, (uint)maxBatchSize, 0, null);
                        if (ret < 0)
                        {
                            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                            if (error == 4 /* EINTR */ || error == 11 /* EAGAIN */) continue;

                            if (error == 104 /* ECONNRESET */) continue;
                            throw new SocketException(error);
                        }

                        tasks = new ValueTask[ret];
                        int taskCount = 0;

                        for (int i = 0; i < ret; i++)
                        {
                            uint bytesReceived = pMsgvec[i].msg_len;
                            if (bytesReceived < KcpGlobalVars.HEADER_LENGTH_WITHOUT_CONVID)
                            {
                                continue;
                            }

                            int addrLen = (int)pMsgvec[i].msg_hdr.msg_namelen;
                            byte* pAddr = pAddrStr + (i * 128);

                            SocketAddress receivedAddress = new SocketAddress(_socket.AddressFamily, addrLen);
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

                                if (_endpointCache.Count >= 512)
                                {
                                    _endpointCache.Clear();
                                }

                                var clonedAddress = new SocketAddress(receivedAddress.Family, receivedAddress.Size);
                                for (int j = 0; j < receivedAddress.Size; j++)
                                {
                                    clonedAddress[j] = receivedAddress[j];
                                }

                                _endpointCache.TryAdd(clonedAddress, ep);
                            }

                            Memory<byte> bufferMemory = buffers[i].AsMemory(0, (int)bytesReceived);
                            var packet = bufferMemory.Slice(0, (int)bytesReceived);

                            if (OnRawPacketReceived(packet, endpoint))
                            {
                                continue;
                            }

                            if (connection is not IKcpPacketSink sink)
                            {
                                continue;
                            }

                            var packetOwner = s_sharedPacketOwnerPool.Get();
                            packetOwner.Initialize(s_sharedPacketOwnerPool, (int)bytesReceived);
                            packet.CopyTo(packetOwner.Memory.Slice(0, (int)bytesReceived));

                            var inputTask = sink.InputPacketAsync(packetOwner.Memory.Slice(0, (int)bytesReceived), endpoint, packetOwner, cancellationToken);
                            if (!inputTask.IsCompletedSuccessfully)
                            {
                                tasks[taskCount++] = inputTask;
                            }
                        }

                        if (taskCount < ret)
                        {
                            Array.Resize(ref tasks, taskCount);
                        }
                    }
                }

                // Await tasks outside of the unsafe block
                for (int i = 0; i < tasks.Length; i++)
                {
                    await AwaitAndDisposeAsync(tasks[i]).ConfigureAwait(false);
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

    private static async ValueTask AwaitAndDisposeAsync(ValueTask task)
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
