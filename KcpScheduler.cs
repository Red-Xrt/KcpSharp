using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;

namespace HyacineCore.Server.Kcp.KcpSharp;

internal sealed class KcpScheduler : IDisposable
{
    private volatile bool _stopped;
    private readonly Channel<KcpConversation> _workQueue;
    private readonly Thread[] _workerThreads;
    private readonly Thread _timerThread;

    // Hashed Timing Wheel variables
    private readonly ConcurrentQueue<IKcpConversation>[] _timingWheel;
    private readonly int _wheelSize = 512;
    private volatile uint _currentTick;

    // Track active conversations to prevent ghost entries and ensure clean un-scheduling
    private readonly ConcurrentDictionary<IKcpConversation, byte> _activeConversations;

    public KcpScheduler(int workerThreadCount)
    {
        _timingWheel = new ConcurrentQueue<IKcpConversation>[_wheelSize];
        for (int i = 0; i < _wheelSize; i++)
        {
            _timingWheel[i] = new ConcurrentQueue<IKcpConversation>();
        }

        _activeConversations = new ConcurrentDictionary<IKcpConversation, byte>();

        _workQueue = Channel.CreateUnbounded<KcpConversation>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        _workerThreads = new Thread[workerThreadCount];
        for (int i = 0; i < workerThreadCount; i++)
        {
            _workerThreads[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"KcpScheduler-Worker-{i}"
            };
            _workerThreads[i].Start();
        }

        _timerThread = new Thread(TimerLoop)
        {
            IsBackground = true,
            Name = "KcpScheduler-Timer"
        };
        _timerThread.Start();
    }

    public void EnqueueWork(KcpConversation work)
    {
        if (_stopped) return;
        _workQueue.Writer.TryWrite(work);
    }

    public void ScheduleTimer(IKcpConversation conversation, int delayMs)
    {
        if (_stopped || !_activeConversations.ContainsKey(conversation)) return;
        int targetTick = (int)((_currentTick + (uint)delayMs) % _wheelSize);
        _timingWheel[targetTick].Enqueue(conversation);
    }

    public void RegisterConversation(IKcpConversation conversation)
    {
        if (_stopped) return;
        _activeConversations.TryAdd(conversation, 0);
    }

    public void UnscheduleConversation(IKcpConversation conversation)
    {
        if (_stopped) return;
        _activeConversations.TryRemove(conversation, out _);
    }

    private void WorkerLoop()
    {
        while (!_stopped)
        {
            try
            {
                KcpConversation conv;
                if (_workQueue.Reader.TryRead(out conv))
                {
                    conv.ProcessUpdate();
                }
                else
                {
                    // Block until item is available
                    var workTask = _workQueue.Reader.ReadAsync(CancellationToken.None);
                    if (workTask.IsCompletedSuccessfully)
                    {
                        workTask.Result.ProcessUpdate();
                    }
                    else
                    {
                        workTask.AsTask().GetAwaiter().GetResult().ProcessUpdate();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ignoring
            }
            catch (ChannelClosedException)
            {
                // Ignoring
            }
            catch (Exception ex)
            {
                new Util.Logger("KcpServer").Error("KcpScheduler worker error", ex);
            }
        }
    }

    private void TimerLoop()
    {
        long lastTick = Environment.TickCount64;

        while (!_stopped)
        {
            long currentTick64 = Environment.TickCount64;
            int elapsedMs = (int)(currentTick64 - lastTick);

            if (elapsedMs <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            // Catch up on any missed ticks due to Thread.Sleep drift
            for (int i = 0; i < elapsedMs; i++)
            {
                int slot = (int)(_currentTick % _wheelSize);
                var queue = _timingWheel[slot];

                if (!queue.IsEmpty)
                {
                    int itemsToProcess = queue.Count;
                    while (itemsToProcess > 0 && queue.TryDequeue(out var conversation))
                    {
                    if (_activeConversations.ContainsKey(conversation) && conversation is KcpConversation kcpConv && !kcpConv.TransportClosed)
                        {
                            EnqueueWork(kcpConv);
                            ScheduleTimer(conversation, 100);
                        }
                        itemsToProcess--;
                    }
                }

                _currentTick++;
            }

            lastTick = currentTick64;
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        _workQueue.Writer.TryComplete();

        foreach (var thread in _workerThreads)
        {
            if (thread.IsAlive && Thread.CurrentThread != thread)
            {
                thread.Join(100);
            }
        }

        if (_timerThread.IsAlive && Thread.CurrentThread != _timerThread)
        {
            _timerThread.Join(100);
        }
    }
}
