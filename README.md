# KcpSharp - High-Performance KCP Transport Layer 🚀

Welcome to **KcpSharp**! 👋

This library provides a highly optimized, zero-allocation C# implementation of the KCP protocol (targeting .NET 6+). Originally designed for the HyacineCore game server, KcpSharp is built to be a robust, lightning-fast **Transport Layer**.

If you're building multiplayer games, real-time applications, or any system requiring reliable UDP communication with ultra-low latency and virtually zero garbage collection (GC) overhead, KcpSharp is the tool for you! 🛠️

## 🌟 Acknowledgements

A huge thank you to the original creators and maintainers of the KCP protocol ([skywind3000](https://github.com/skywind3000/kcp)) and the fantastic initial C# port ([yigolden](https://github.com/yigolden-oss/KcpSharp)). We've proudly built upon their solid foundation, aggressively optimizing the architecture for modern .NET environments with a heavy focus on zero-allocation and concurrent data processing.

## 🚀 What Makes KcpSharp Special?

KcpSharp has been extensively refactored to deliver extreme performance characteristics:

1. **Decoupled Dual-Loop Pipeline:** The CPU-bound packet ingestion loop is completely decoupled from the I/O-bound socket flush loop via a bounded `Channel<bool>`. This eliminates Head-of-Line blocking and prevents network jitter from stalling your application's receive rate.
2. **Zero-Allocation Backpressure:** We natively backoff the socket via `Task.Yield()` when the receive queue is full, forcing the OS to absorb traffic spikes without allocating GC-heavy Timers or silently dropping application packets.
3. **Lock-Free Capacity Reservations:** `SemaphoreSlim` bottlenecks have been replaced with a custom lock-free `AsyncCapacityReserve` primitive, completely eliminating thread contention when concurrently requesting memory slots for large stream payloads.
4. **Task/ValueTask Optimization:** We use `IValueTaskSource` and `ManualResetValueTaskSourceCore` heavily to eliminate `Task` allocations during high-frequency network loops. ⚡

## ⚡ Quick Start

> 📝 **A Note on `*Unsafe.cs` Files in the Codebase:**
> If you explore the source code, you might notice files ending in `Unsafe.cs` (e.g., `KcpSendReceiveBufferItemCacheUnsafe.cs`). In this context, "Unsafe" doesn't mean unsafe memory or pointers; it simply means **thread-unsafe**. These collections are intentionally designed without internal locking mechanisms to maximize performance. They are exclusively used in highly controlled environments where the caller already holds the appropriate lock (like `_sndBufLock`), completely avoiding redundant synchronization overhead.

```csharp
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using KcpSharp;

public class PureKcpExample
{
    public static async Task RunAsync()
    {
        var remoteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9999);
        
        // ==============================================================
        // 1. CONFIGURE KCP OPTIONS
        // ==============================================================
        var options = new KcpConversationOptions
        {
            Mtu = 1400,                // Maximum Transmission Unit. The max size of a UDP payload. Keep under 1400 to avoid IP fragmentation.
            UpdateInterval = 10,       // The internal tick rate in milliseconds. Lower = faster reaction to packet loss, but higher CPU usage.
            StreamMode = false,        // If true, KCP merges small packets together into a continuous stream (like TCP). If false, it preserves your message boundaries (like UDP).
            SendWindow = 256,          // Maximum number of unacknowledged packets allowed in flight. Increase for high-bandwidth/high-ping scenarios.
            ReceiveWindow = 256,       // Maximum number of packets the receiver can hold in its buffer. Must be >= SendWindow of the peer.
            NoDelay = true,            // If true, disables pacing and sends packets immediately. Crucial for low-latency games.
            FastResend = 2,            // Skip count needed to trigger an immediate retransmission (Recommend: 2).
            DisableCongestionControl = true // If true, turns off TCP-like backoff, keeping throughput high during packet loss.
        };

        // ==============================================================
        // 2. USE THE FLUENT BUILDER (Auto-creates optimized Socket)
        // ==============================================================
        await using IKcpConversation conversation = KcpBuilder.ForConversation()
            .WithRemoteEndPoint(remoteEndPoint)
            .WithUdpSocket(AddressFamily.InterNetwork, out Socket udpSocket)
            .WithOptions(options)
            .Build();

        // Bind the auto-generated UDP socket
        udpSocket.Bind(new IPEndPoint(IPAddress.Any, 0));

        Console.WriteLine("KCP ready!");

        // ==============================================================
        // 3. I/O: NETWORK -> KCP CORE
        // ==============================================================
        _ = Task.Run(async () =>
        {
            byte[] udpBuffer = new byte[2048];
            while (true)
            {
                try
                {
                    var result = await udpSocket.ReceiveFromAsync(udpBuffer, SocketFlags.None, remoteEndPoint);
                    await conversation.InputPacketAsync(udpBuffer.AsMemory(0, result.ReceivedBytes));
                }
                catch (Exception) { break; }
            }
        });

        // ==============================================================
        // 4. I/O: APP LOGIC <-> KCP CORE
        // ==============================================================
        byte[] dataToSend = Encoding.UTF8.GetBytes("Hello from Pure KcpSharp!");
        await conversation.SendAsync(dataToSend); 

        // [B] RECEIVE
        byte[] receiveBuffer = new byte[2048];
        while (true)
        {
            KcpConversationReceiveResult result = await conversation.ReceiveAsync(receiveBuffer);
            
            if (result.TransportClosed)
            {
                Console.WriteLine("Connection closed!");
                break;
            }

            string text = Encoding.UTF8.GetString(receiveBuffer, 0, result.BytesReceived);
            Console.WriteLine($"[KCP received]: {text}");
        }
    }
}
```

## 🤝 Contributing

We love contributions! ✨ Whether you're fixing bugs, adding features, or improving docs, your help is incredibly valuable.

To contribute:
1. **Fork the repository** and create a branch from `main`.
2. **Write clear commit messages.**
3. **Maintain the zero-allocation philosophy** in hot paths.
4. **Submit a Pull Request (PR)** explaining your brilliant changes!

Got ideas or found a bug? Open an issue in the tracker.💡
