using System.Collections.Concurrent;

namespace KcpSharp;

internal static class KcpGlobalTickEngine
{
    private sealed class Entry
    {
        public KcpConversationUpdateActivation Activation;
        public int Interval;
        public uint NextTick;
        public volatile int CurrentWheelSlot;
        public volatile int _unregisteredRef;
        public bool Unregistered => _unregisteredRef != 0;

        public Entry(KcpConversationUpdateActivation activation, int interval, uint currentTick)
        {
            Activation = activation;
            Interval = interval;
            NextTick = currentTick + (uint)interval;
        }
    }

    private static readonly ConcurrentDictionary<KcpConversationUpdateActivation, Entry> s_activations = new();

    // Timing wheel based on 10ms slots
    private const int SlotMs = 10;
    private const int WheelSlots = 256; // Must be power of 2 for fast modulo
    private const int WheelMask = WheelSlots - 1;

    // Each slot holds a HashSet of entries
    private static readonly HashSet<KcpConversationUpdateActivation>[] s_wheel = new HashSet<KcpConversationUpdateActivation>[WheelSlots];
    private static readonly System.Threading.Lock[] s_wheelLocks = new System.Threading.Lock[WheelSlots];

    // Stack-based object pool for sets to avoid Gen0 allocations
    private static readonly Stack<HashSet<KcpConversationUpdateActivation>> s_setPool = new();
    private static readonly System.Threading.Lock s_setPoolLock = new();

    private static int s_isTimerRunning = 0;
    private static CancellationTokenSource? s_cts;
    private static Task? s_tickTask;
    private static readonly System.Threading.Lock s_engineLock = new();
    private static uint s_lastTickMs = 0;

    static KcpGlobalTickEngine()
    {
        for (int i = 0; i < WheelSlots; i++)
        {
            s_wheel[i] = new HashSet<KcpConversationUpdateActivation>();
            s_wheelLocks[i] = new System.Threading.Lock();
        }
    }

    private static HashSet<KcpConversationUpdateActivation> RentSet()
    {
        lock (s_setPoolLock)
        {
            if (s_setPool.TryPop(out var set))
            {
                return set;
            }
        }
        return new HashSet<KcpConversationUpdateActivation>();
    }

    private static void ReturnSet(HashSet<KcpConversationUpdateActivation> set)
    {
        set.Clear();
        lock (s_setPoolLock)
        {
            s_setPool.Push(set);
        }
    }

    private static int GetSlot(uint tick)
    {
        return (int)((tick / SlotMs) & WheelMask);
    }

    public static void Register(KcpConversationUpdateActivation activation, int interval)
    {
        var currentTick = (uint)Environment.TickCount;
        // Jitter: random offset to spread out concurrent registrations
        var jitter = (uint)(Random.Shared.Next(0, interval));
        var entry = new Entry(activation, interval, currentTick - (uint)interval + jitter);
        entry.NextTick = currentTick + jitter;

        if (s_activations.TryAdd(activation, entry))
        {
            int slot = GetSlot(entry.NextTick);
            entry.CurrentWheelSlot = slot;
            lock (s_wheelLocks[slot])
            {
                s_wheel[slot].Add(activation);
            }

            lock (s_engineLock)
            {
                if (s_isTimerRunning == 0)
                {
                    s_isTimerRunning = 1;
                    s_cts = new CancellationTokenSource();
                    s_lastTickMs = (uint)Environment.TickCount;
                    s_tickTask = Task.Run(() => TickLoopAsync(s_cts.Token));
                }
            }
        }
    }

    public static void Unregister(KcpConversationUpdateActivation activation)
    {
        if (s_activations.TryRemove(activation, out var entry))
        {
#pragma warning disable CS0420
            Volatile.Write(ref entry._unregisteredRef, 1);
#pragma warning restore CS0420
            // Note: `entry.NextTick` might have been updated by the tick loop since `entry` was captured.
            // This `Remove` attempt on `s_wheel` is best-effort.
            // If we miss the actual slot because it was processed and moved, the activation will simply
            // be safely skipped by the tick loop later because it will no longer be found in `s_activations`.
            // Crucially, we do NOT dispose `activation` here; it's handled by its own lifecycle (e.g. `KcpConversationUpdateActivation.Dispose`).
        }
    }

    public static void Shutdown()
    {
        Task? tickTaskToWait = null;
        lock (s_engineLock)
        {
            if (s_cts != null)
            {
                s_cts.Cancel();
                tickTaskToWait = s_tickTask;

                var acts = s_activations.ToArray();
                s_activations.Clear();
                for (int i = 0; i < WheelSlots; i++)
                {
                    lock (s_wheelLocks[i])
                    {
                        s_wheel[i].Clear();
                    }
                }

                foreach (var kvp in acts)
                {
                    kvp.Key.Dispose();
                }

                s_cts.Dispose();
                s_cts = null;
                s_isTimerRunning = 0;
            }
        }

        if (tickTaskToWait != null)
        {
            try
            {
                tickTaskToWait.Wait();
            }
            catch
            {
                // Ignored
            }
        }
    }

    private static async Task TickLoopAsync(CancellationToken ct)
    {
        using var periodicTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(SlotMs));
        try
        {
            while (await periodicTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (s_activations.IsEmpty)
                {
                    lock (s_engineLock)
                    {
                        if (s_activations.IsEmpty)
                        {
                            s_cts?.Cancel();
                            s_cts?.Dispose();
                            s_cts = null;
                            s_isTimerRunning = 0;
                            return;
                        }
                    }
                }

                uint currentTickMs = (uint)Environment.TickCount;
                int diffMs = unchecked((int)(currentTickMs - s_lastTickMs));

                // Process missed slots if we slept too long
                // To prevent O(N) lag loops (e.g., millions of iterations if clock skips),
                // clamp the max loop count to the size of the wheel. All slots will be visited.
                int loops = Math.Min(diffMs / SlotMs, WheelSlots);

                while (loops > 0)
                {
                    int slotIndex = (int)((s_lastTickMs / SlotMs) & WheelMask);

                    HashSet<KcpConversationUpdateActivation>? toExecute = null;
                    lock (s_wheelLocks[slotIndex])
                    {
                        var slotSet = s_wheel[slotIndex];
                        if (slotSet.Count > 0)
                        {
                            toExecute = slotSet;
                            s_wheel[slotIndex] = RentSet();
                        }
                    }

                    if (toExecute != null)
                    {
                        foreach (var activation in toExecute)
                        {
                            if (s_activations.TryGetValue(activation, out var entry))
                            {
                                if (entry.Unregistered)
                                {
                                    // If we somehow pulled an unregistered activation from the execution list
                                    // but it's still in the dictionary, it shouldn't be added to the wheel.
                                    s_activations.TryRemove(activation, out _);
                                    continue;
                                }

                                if (TimeDiff(currentTickMs, entry.NextTick) >= 0)
                                {
                                    entry.NextTick = (uint)(currentTickMs + entry.Interval + Random.Shared.Next(-1, 2));
                                    activation.Notify();
                                }

                                // Re-insert into the wheel for the next tick
                                int nextSlot = GetSlot(entry.NextTick);
                                entry.CurrentWheelSlot = nextSlot;
                                lock (s_wheelLocks[nextSlot])
                                {
                                    s_wheel[nextSlot].Add(activation);
                                }
                            }
                        }
                        ReturnSet(toExecute);
                    }

                    s_lastTickMs += SlotMs;
                    loops--;
                }

                // If we skipped more than the wheel size, fast-forward the last tick
                // to prevent accumulating lag on the next loop iteration.
                if (diffMs / SlotMs > WheelSlots)
                {
                    // Clear all slots first to prevent duplicates
                    for (int i = 0; i < WheelSlots; i++)
                    {
                        lock (s_wheelLocks[i])
                        {
                            s_wheel[i].Clear();
                        }
                    }

                    // Emergency: stagger notifications to prevent O(N) storm
                    int staggerIndex = 0;
                    foreach (var kvp in s_activations)
                    {
                        var entry = kvp.Value;
                        if (!entry.Unregistered)
                        {
                            uint staggeredTick = currentTickMs + (uint)(staggerIndex % WheelSlots) * SlotMs;
                            entry.NextTick = staggeredTick;
                            int nextSlot = GetSlot(staggeredTick);
                            entry.CurrentWheelSlot = nextSlot;
                            lock (s_wheelLocks[nextSlot])
                            {
                                s_wheel[nextSlot].Add(kvp.Key);
                            }
                            staggerIndex++;
                        }
                    }
                    s_lastTickMs = currentTickMs - (currentTickMs % SlotMs);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    private static int TimeDiff(uint later, uint earlier)
    {
        return unchecked((int)(later - earlier));
    }
}
