namespace KcpSharp.Tests;

public sealed class KcpStreamTests
{
    [Fact]
    public async Task StreamMode_ReadWrite_RoundTrip()
    {
        var options = LoopbackTestHelper.TestOptions(streamMode: true);
        await using var pair = LoopbackTestHelper.CreatePair(11, options);

        await using var localStream = new KcpStream(pair.Local, ownsConversation: false);
        await using var remoteStream = new KcpStream(pair.Remote, ownsConversation: false);

        var payload = new byte[1024];
        Random.Shared.NextBytes(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await localStream.WriteAsync(payload, cts.Token);

        var buffer = new byte[payload.Length];
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await remoteStream.ReadAsync(buffer.AsMemory(offset), cts.Token);
            Assert.True(read > 0);
            offset += read;
        }

        Assert.Equal(payload, buffer);
    }

    [Fact]
    public async Task Dispose_BlocksFurtherReads()
    {
        var options = LoopbackTestHelper.TestOptions(streamMode: true);
        await using var pair = LoopbackTestHelper.CreatePair(12, options);
        var stream = new KcpStream(pair.Local, ownsConversation: false);

        await stream.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            _ = await stream.ReadAsync(new byte[16]));
    }
}
