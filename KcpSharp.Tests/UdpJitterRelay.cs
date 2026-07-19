using System.Net;
using System.Net.Sockets;

namespace KcpSharp.Tests;

/// <summary>
///     Forwards UDP between two endpoints with random per-packet delay (jitter simulation).
/// </summary>
internal sealed class UdpJitterRelay : IAsyncDisposable
{
    private readonly Socket _relay;
    private readonly int _minDelayMs;
    private readonly int _maxDelayMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _sync = new();

    private IPEndPoint? _endpointA;
    private IPEndPoint? _endpointB;

    private long _packetsForwarded;
    private long _bytesForwarded;
    private int _pendingForwards;

    public UdpJitterRelay(int minDelayMs, int maxDelayMs)
    {
        if (minDelayMs < 0) throw new ArgumentOutOfRangeException(nameof(minDelayMs));
        if (maxDelayMs < minDelayMs) throw new ArgumentOutOfRangeException(nameof(maxDelayMs));

        _minDelayMs = minDelayMs;
        _maxDelayMs = maxDelayMs;
        _relay = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _relay.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _loop = Task.Run(RunAsync);
    }

    public IPEndPoint RelayEndpoint => (IPEndPoint)_relay.LocalEndPoint!;

    public long PacketsForwarded => Interlocked.Read(ref _packetsForwarded);
    public long BytesForwarded => Interlocked.Read(ref _bytesForwarded);

    public void RegisterEndpoints(IPEndPoint endpointA, IPEndPoint endpointB)
    {
        lock (_sync)
        {
            _endpointA = endpointA;
            _endpointB = endpointB;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (SocketException) { }

        await WaitForPendingForwardsAsync().ConfigureAwait(false);

        try { _relay.Dispose(); } catch { }
        _cts.Dispose();
    }

    private async Task WaitForPendingForwardsAsync()
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (Volatile.Read(ref _pendingForwards) > 0 && Environment.TickCount64 < deadline)
            await Task.Delay(5).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        var buffer = new byte[65536];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        var ct = _cts.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_relay.Poll(50_000, SelectMode.SelectRead))
                    continue;

                int length = _relay.ReceiveFrom(buffer, SocketFlags.None, ref remote);
                if (length <= 0) continue;

                var destination = ResolveDestination((IPEndPoint)remote);
                if (destination is null) continue;

                var packet = buffer.AsSpan(0, length).ToArray();
                int delayMs = _maxDelayMs == _minDelayMs
                    ? _minDelayMs
                    : Random.Shared.Next(_minDelayMs, _maxDelayMs + 1);

                _ = ForwardWithDelayAsync(packet, destination, delayMs, ct);
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (SocketException) { break; }
        }
    }

    private async Task ForwardWithDelayAsync(byte[] packet, IPEndPoint destination, int delayMs, CancellationToken ct)
    {
        Interlocked.Increment(ref _pendingForwards);
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
                return;

            await _relay.SendToAsync(packet, SocketFlags.None, destination, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _packetsForwarded);
            Interlocked.Add(ref _bytesForwarded, packet.Length);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
        finally
        {
            Interlocked.Decrement(ref _pendingForwards);
        }
    }

    private IPEndPoint? ResolveDestination(IPEndPoint source)
    {
        lock (_sync)
        {
            if (_endpointA is not null && EndpointsEqual(_endpointA, source))
                return _endpointB;
            if (_endpointB is not null && EndpointsEqual(_endpointB, source))
                return _endpointA;
            return null;
        }
    }

    private static bool EndpointsEqual(IPEndPoint a, IPEndPoint b)
        => a.Port == b.Port && a.Address.Equals(b.Address);
}
