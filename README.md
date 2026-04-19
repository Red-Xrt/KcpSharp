# KcpSharp — High-Performance KCP Transport for .NET

[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

KcpSharp is a production-grade, zero-allocation C# implementation of the [KCP protocol](https://github.com/skywind3000/kcp) built on top of raw UDP sockets. It is designed for real-time applications that demand reliable delivery, sub-millisecond latency, and sustained high throughput without GC pressure.

---

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Installation](#installation)
- [Quick Start — Full Packet Lifecycle](#quick-start--full-packet-lifecycle)
- [Configuration Reference](#configuration-reference)
- [Advanced Patterns](#advanced-patterns)
- [Internal Architecture](#internal-architecture)
- [Platform Specifics](#platform-specifics)
- [Acknowledgements](#acknowledgements)

---

## Architecture Overview

KcpSharp separates concerns across three independent pipelines:

```
┌─────────────────────────────────────────────────────────────────────┐
│  RECEIVE PIPELINE                                                   │
│                                                                     │
│  OS Socket ──► recvmmsg/ReceiveFrom ──► KcpPacketOwner (pool) ──►  │
│  InputPacketAsync ──► [ACK fast-path inline] or                    │
│                        KcpReceiveRingBuffer ──► UpdateLoop         │
│                        ──► SetInput ──► KcpReceiveQueue ──► User   │
├─────────────────────────────────────────────────────────────────────┤
│  UPDATE / TIMER PIPELINE                                            │
│                                                                     │
│  KcpGlobalTickEngine (10ms timing wheel, 256 slots)                │
│  ──► Notify() ──► RunUpdateOnActivationCore (single async loop)    │
│  ──► UpdateCoreAsync (RTO / ts_flush) ──► FlushCoreAsync          │
├─────────────────────────────────────────────────────────────────────┤
│  SEND PIPELINE                                                      │
│                                                                     │
│  User SendAsync ──► AsyncCapacityReserve ──► KcpSendQueue         │
│  ──► FlushCoreAsync ──► FlushAcksFastAsync (no semaphore)         │
│                     ──► FlushCore2Async (semaphore-protected)     │
│                         ──► TryGetBatchSliceAndCommit (pinned slab)│
│                         ──► sendmmsg (Linux) / SendToAsync (Win)  │
└─────────────────────────────────────────────────────────────────────┘
```

**Key design decisions:**
- **ACK fast-path**: Pure ACKs bypass the ring buffer and are processed inline on the receive thread, guaranteeing that data flushing never delays time-critical acknowledgements.
- **Lock-free capacity reservation**: `AsyncCapacityReserve` eliminates monitor contention on the hot send path.
- **State-machine box pooling**: Custom async method builders eliminate `Task` allocation per KCP update tick.
- **Double-buffered batch slab**: Batches use pre-allocated, GC-pinned slabs that swap atomically, preventing flush thread contention.

---

## Installation

```bash
dotnet add package KcpSharp
```

---

## Quick Start — Full Packet Lifecycle

### Step 1 — Build a Conversation

```csharp
using System.Net;
using System.Net.Sockets;
using KcpSharp;

var options = new KcpConversationOptions
{
    Mtu = 1400,
    SendWindow = 256,
    ReceiveWindow = 256,
    RemoteReceiveWindow = 256,
    UpdateInterval = 10,
    NoDelay = true,
    FastResend = 2,
    DisableCongestionControl = true,
    SendQueueSize = 256,
    ReceiveQueueSize = 256,
    EnableBatching = true,
    MaxBatchSize = 64,
    StreamMode = false,
    KeepAliveOptions = new KcpKeepAliveOptions(5_000, 30_000)
};

var remoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 9000);

KcpConversation conversation = KcpBuilder
    .ForConversation()
    .WithUdpSocket(AddressFamily.InterNetwork, out Socket _)
    .WithRemoteEndPoint(remoteEndPoint)
    .WithConversationId(0xDEAD_BEEF)
    .WithOptions(options)
    .Build();
```

### Step 2 — Send Packets

```csharp
byte[] payload = new byte[512];
Random.Shared.NextBytes(payload);

// OPTION A — Non-blocking try send (zero allocation)
bool queued = conversation.TrySend(payload);

// OPTION B — Async send with backpressure
bool sent = await conversation.SendAsync(payload, cancellationToken);

// OPTION C — Flush
bool flushed = await conversation.FlushAsync(cancellationToken);
```

### Step 3 — Receive Packets

```csharp
byte[] recvBuffer = new byte[options.Mtu];

// OPTION A — Async receive
while (true)
{
    KcpConversationReceiveResult result = await conversation.ReceiveAsync(recvBuffer, cancellationToken);
    if (result.TransportClosed) break;
    ProcessMessage(recvBuffer.AsSpan(0, result.BytesReceived));
}

// OPTION B — Sync non-blocking try-receive
while (conversation.TryReceive(recvBuffer, out var result))
{
    if (result.TransportClosed) break;
    ProcessMessage(recvBuffer.AsSpan(0, result.BytesReceived));
}
```

### Step 4 — Graceful Teardown

```csharp
// Wait for background tasks to finalize buffers, eliminating use-after-free
await conversation.DisposeAsync();
```

---

## Configuration Reference

### KcpConversationOptions

| Property | Default | Description |
|---|---|---|
| `Mtu` | 1400 | Max UDP payload size (Keep below 1400 for standard Ethernet). |
| `SendWindow` | 32 | Max in-flight unacknowledged segments. |
| `ReceiveWindow` | 128 | Out-of-order receive buffer depth (must be ≥ peer's `SendWindow`). |
| `RemoteReceiveWindow`| 128 | Initial peer receive window estimate. |
| `UpdateInterval` | 100 ms | KCP tick rate (≥ 10ms). Controls RTO and flush frequency. |
| `NoDelay` | false | Enables minimum RTO of 30ms (instead of 200ms). |
| `FastResend` | 0 | Skip count to trigger immediate fast retransmission (2 for games). |
| `DisableCongestionControl`| false | Disables TCP-like slow-start/AIMD window backoff. |
| `Send/ReceiveQueueSize`| 32 | Depth of app-facing send/receive queues. |
| `EnableBatching` | true | Controls usage of batched UDP transmission. |
| `MaxBatchSize` | 16 | Slots per batch. Memory: `2 × MaxBatchSize × Mtu`. |
| `StreamMode` | false | Toggles byte-stream vs message-oriented behavior. |

**Presets:** Use `KcpConversationOptions.LowLatencyPreset` for gaming, and `KcpConversationOptions.BulkTransferPreset` for reliable background transfers.

---

## Advanced Patterns

### Stream Mode
Toggle `StreamMode = true` and wrap the conversation with `new KcpStream(conversation)` for a `System.IO.Stream` interface.

### Multiplexing
```csharp
var muxTransport = KcpSocketTransport.CreateMultiplexConnection(socket, mtu: 1400);
var mux = muxTransport.Connection;

// Route individual channels transparently by ConversationId prefix
KcpConversation chat = mux.CreateConversation(id: 1, remoteEndpoint, options);
KcpConversation game = mux.CreateConversation(id: 2, remoteEndpoint, options);
```

### Custom Buffer Pools
Inject an `IKcpBufferPool` to provide `GC.AllocateUninitializedArray<byte>(..., pinned: true)` memory directly into the socket pipeline to avoid GC movement.

---

## Internal Architecture

- **`KcpGlobalTickEngine`**: 10 ms resolution timing wheel (256 slots) tracking active conversations globally.
- **`AsyncCapacityReserve`**: Lock-free CAS semaphore enabling backpressure for `SendQueueSize`.
- **`KcpAcknowledgeList`**: MPSC lock-free ring buffer for ACKs routed from the receive loop.
- **`KcpReceiveRingBuffer`**: SPSC ring buffer synchronizing inbound UDP packets to the async KCP update loop.

---

## Platform Specifics

- **Linux**: Aggressive `recvmmsg(2)` and `sendmmsg(2)` batching with stack-allocated headers mapped to an asynchronous `LongRunning` thread pool task.
- **Windows**: High-priority synchronous `ReceiveFrom` + `Poll` thread, with concurrent non-batched `Socket.SendToAsync` equivalents. Automatically manages `SIO_UDP_CONNRESET`.

---

## Acknowledgements

Built upon the original KCP protocol design by [skywind3000](https://github.com/skywind3000/kcp) and the initial C# port by [yigolden](https://github.com/yigolden-oss/KcpSharp). This fork introduces zero-allocation fast-paths, true background UDP multiplexing, and robust concurrency primitives.