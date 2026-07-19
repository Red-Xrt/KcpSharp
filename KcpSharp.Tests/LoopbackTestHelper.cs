using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KcpSharp.Tests;

internal static class LoopbackTestHelper
{
    /// <summary>
    ///     Low-latency defaults used by most integration tests.
    /// </summary>
    public static KcpConversationOptions TestOptions(bool streamMode = false)
    {
        var options = KcpConversationOptions.LowLatencyPreset.Clone();
        options.StreamMode = streamMode;
        options.EnableBatching = false;
        options.UpdateInterval = 10;
        return options;
    }

    /// <summary>
    ///     Options tuned for private-server JSON traffic (larger windows/queues, stream-friendly).
    /// </summary>
    public static KcpConversationOptions ServerJsonOptions(bool streamMode = true)
    {
        var options = KcpConversationOptions.LowLatencyPreset.Clone();
        options.StreamMode = streamMode;
        options.EnableBatching = false;
        options.UpdateInterval = 10;
        options.SendWindow = 512;
        options.ReceiveWindow = 512;
        options.RemoteReceiveWindow = 512;
        options.SendQueueSize = 512;
        options.ReceiveQueueSize = 512;
        options.DisableCongestionControl = true;
        return options;
    }

    /// <summary>
    ///     Builds UTF-8 JSON-like payload of an exact byte length (for realistic server messages).
    /// </summary>
    public static byte[] CreateSyntheticJson(int size, int id = 0)
    {
        if (size < 32) throw new ArgumentOutOfRangeException(nameof(size), "JSON payload too small.");

        var header = Encoding.UTF8.GetBytes($"{{\"id\":{id},\"type\":\"state\",\"data\":\"");
        const string suffix = "\"}";
        var suffixBytes = Encoding.UTF8.GetBytes(suffix);
        int dataLen = size - header.Length - suffixBytes.Length;
        if (dataLen < 0)
            throw new ArgumentOutOfRangeException(nameof(size), "JSON envelope does not fit in requested size.");

        var data = new byte[dataLen];
        for (int i = 0; i < dataLen; i++)
            data[i] = (byte)('a' + (i % 26));

        var result = new byte[size];
        header.CopyTo(result, 0);
        data.CopyTo(result, header.Length);
        suffixBytes.CopyTo(result, header.Length + dataLen);
        return result;
    }

    public static TimeSpan TimeoutForPayload(int payloadBytes, int multiplierSeconds = 1)
        => TimeSpan.FromSeconds(Math.Clamp(payloadBytes / 4096 + 15, 15, 120) * multiplierSeconds);

    /// <summary>
    ///     Game-server style JSON envelope with explicit message type (login, state, chat, action, ...).
    /// </summary>
    public static byte[] CreateGameJson(string type, int id, int size)
    {
        if (size < 48) throw new ArgumentOutOfRangeException(nameof(size), "Game JSON payload too small.");

        var header = Encoding.UTF8.GetBytes($"{{\"id\":{id},\"type\":\"{type}\",\"ts\":0,\"data\":\"");
        const string suffix = "\"}";
        var suffixBytes = Encoding.UTF8.GetBytes(suffix);
        int dataLen = size - header.Length - suffixBytes.Length;
        if (dataLen < 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Game JSON envelope does not fit in requested size.");

        var data = new byte[dataLen];
        for (int i = 0; i < dataLen; i++)
            data[i] = (byte)('a' + (i % 26));

        var result = new byte[size];
        header.CopyTo(result, 0);
        data.CopyTo(result, header.Length);
        suffixBytes.CopyTo(result, header.Length + dataLen);
        return result;
    }

    public static async Task<long> MeasureAppRttMsAsync(
        KcpConversation sender,
        KcpConversation receiver,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await RoundTripMessageAsync(sender, receiver, payload, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        return (long)sw.Elapsed.TotalMilliseconds;
    }

    public static async Task<JitterLoopbackPair> CreateJitterPairAsync(
        uint conversationId,
        int jitterMinMs,
        int jitterMaxMs,
        KcpConversationOptions? options = null)
    {
        options ??= ServerJsonOptions(streamMode: false);
        var relay = new UdpJitterRelay(jitterMinMs, jitterMaxMs);

        var socketClient = CreateBoundUdpSocket();
        var socketServer = CreateBoundUdpSocket();
        var epClient = (IPEndPoint)socketClient.LocalEndPoint!;
        var epServer = (IPEndPoint)socketServer.LocalEndPoint!;
        relay.RegisterEndpoints(epClient, epServer);

        var transportClient = KcpSocketTransport.CreateConversation(socketClient, relay.RelayEndpoint, conversationId, options);
        var transportServer = KcpSocketTransport.CreateConversation(socketServer, relay.RelayEndpoint, conversationId, options);
        ((IKcpTransport<KcpConversation>)transportClient).Start();
        ((IKcpTransport<KcpConversation>)transportServer).Start();

        var pair = new LoopbackPair(transportClient.Connection, transportServer.Connection);
        return new JitterLoopbackPair(pair, relay);
    }

    public static LoopbackPair CreatePair(uint conversationId, KcpConversationOptions? options = null)
    {
        options ??= TestOptions();

        var socketA = CreateBoundUdpSocket();
        var socketB = CreateBoundUdpSocket();
        var endpointA = (IPEndPoint)socketA.LocalEndPoint!;
        var endpointB = (IPEndPoint)socketB.LocalEndPoint!;

        var transportA = KcpSocketTransport.CreateConversation(socketA, endpointB, conversationId, options);
        var transportB = KcpSocketTransport.CreateConversation(socketB, endpointA, conversationId, options);
        ((IKcpTransport<KcpConversation>)transportA).Start();
        ((IKcpTransport<KcpConversation>)transportB).Start();

        return new LoopbackPair(transportA.Connection, transportB.Connection);
    }

    public static LoopbackPair CreatePairWithoutConversationId(KcpConversationOptions? options = null)
    {
        options ??= TestOptions();

        var socketA = CreateBoundUdpSocket();
        var socketB = CreateBoundUdpSocket();
        var endpointA = (IPEndPoint)socketA.LocalEndPoint!;
        var endpointB = (IPEndPoint)socketB.LocalEndPoint!;

        var transportA = KcpSocketTransport.CreateConversation(socketA, endpointB, options);
        var transportB = KcpSocketTransport.CreateConversation(socketB, endpointA, options);
        ((IKcpTransport<KcpConversation>)transportA).Start();
        ((IKcpTransport<KcpConversation>)transportB).Start();

        return new LoopbackPair(transportA.Connection, transportB.Connection);
    }

    public static async Task<byte[]> ReceiveExactAsync(
        KcpConversation conversation,
        int expectedLength,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[expectedLength + 4096];
        var result = await conversation.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result.BytesReceived != expectedLength)
            throw new InvalidOperationException($"Expected {expectedLength} bytes, got {result.BytesReceived}.");
        return buffer.AsSpan(0, result.BytesReceived).ToArray();
    }

    public static async Task<byte[]> ReceiveStreamExactAsync(
        KcpConversation conversation,
        int expectedLength,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[expectedLength];
        int offset = 0;
        while (offset < expectedLength)
        {
            var result = await conversation.ReceiveAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (result.BytesReceived <= 0)
                throw new InvalidOperationException("Stream ended before all bytes were received.");
            offset += result.BytesReceived;
        }

        return buffer;
    }

    /// <summary>
    ///     Simulates real client/server JSON exchange: receiver is already waiting while sender transmits.
    /// </summary>
    public static async Task<byte[]> RoundTripMessageAsync(
        KcpConversation sender,
        KcpConversation receiver,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var receiveTask = ReceiveExactAsync(receiver, payload.Length, cancellationToken);
        if (!await sender.SendAsync(payload, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("SendAsync returned false.");
        return await receiveTask.ConfigureAwait(false);
    }

    /// <summary>
    ///     Stream-mode round trip with concurrent receive (typical for large JSON blobs over KcpStream).
    /// </summary>
    public static async Task<byte[]> RoundTripStreamAsync(
        KcpConversation sender,
        KcpConversation receiver,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var receiveTask = ReceiveStreamExactAsync(receiver, payload.Length, cancellationToken);
        if (!await sender.SendAsync(payload, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("SendAsync returned false.");
        return await receiveTask.ConfigureAwait(false);
    }

    public static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        while (!predicate())
        {
            await Task.Delay(10, cts.Token).ConfigureAwait(false);
        }
    }

    private static Socket CreateBoundUdpSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }
}

internal sealed class LoopbackPair : IAsyncDisposable
{
    public KcpConversation Local { get; }
    public KcpConversation Remote { get; }

    public LoopbackPair(KcpConversation local, KcpConversation remote)
    {
        Local = local;
        Remote = remote;
    }

    public async ValueTask DisposeAsync()
    {
        await Local.DisposeAsync().ConfigureAwait(false);
        await Remote.DisposeAsync().ConfigureAwait(false);
    }
}
