using System;
using System.Buffers;

namespace KcpSharp;

internal readonly struct KcpConversationUpdateNotification : IDisposable
{
    private readonly IKcpConversationUpdateNotificationSource? _source;
    private readonly bool _skipTimerNotification;
    private readonly ReadOnlyMemory<byte> _directPacket;
    private readonly IMemoryOwner<byte>? _directOwner;

    public ReadOnlyMemory<byte> Packet => _source?.Packet ?? _directPacket;
    public IMemoryOwner<byte>? BufferOwner => _source?.BufferOwner ?? _directOwner;
    public bool TimerNotification => !_skipTimerNotification;

    public KcpConversationUpdateNotification(ReadOnlyMemory<byte> packet, IMemoryOwner<byte>? bufferOwner, bool skipTimerNotification)
    {
        _source = null;
        _directPacket = packet;
        _directOwner = bufferOwner;
        _skipTimerNotification = skipTimerNotification;
    }

    public KcpConversationUpdateNotification(IKcpConversationUpdateNotificationSource? source, bool skipTimerNotification)
    {
        _source = source;
        _directPacket = default;
        _directOwner = null;
        _skipTimerNotification = skipTimerNotification;
    }

    public KcpConversationUpdateNotification WithTimerNotification(bool timerNotification)
    {
        if (_source != null)
        {
            return new KcpConversationUpdateNotification(_source, !(!_skipTimerNotification | timerNotification));
        }
        else
        {
            return new KcpConversationUpdateNotification(_directPacket, _directOwner, !(!_skipTimerNotification | timerNotification));
        }
    }

    public void Dispose()
    {
        if (_source is not null)
            _source.Release();
        else
            _directOwner?.Dispose();
    }
}