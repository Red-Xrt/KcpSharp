# KcpSharp — High-Performance KCP Transport for .NET

[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com)

KcpSharp is a production-oriented C# implementation of the [KCP protocol](https://github.com/skywind3000/kcp) over UDP. It targets real-time and high-throughput workloads: reliable delivery, configurable latency, ref-counted receive buffers, and optional UDP batching on Linux.

---

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [API Overview](#api-overview)
- [Advanced Usage](#advanced-usage)
- [Observability](#observability)
- [Architecture](#architecture)
- [Platform Behavior](#platform-behavior)
- [Lifecycle & Thread Safety](#lifecycle--thread-safety)
- [Acknowledgements](#acknowledgements)

---

## Features

- **Fluent setup** via `KcpBuilder` (UDP socket, remote endpoint, options, exception handler).
- **Message and stream modes** (`StreamMode` + optional `KcpStream` wrapper).
- **ACK fast-path** — pure ACK/probe packets are handled on the receive path without queuing behind PUSH data.
- **Global timing wheel** (`KcpGlobalTickEngine`) — shared 10 ms tick scheduling for all active conversations.
- **Send backpressure** — bounded `KcpSendQueue` with async wait APIs.
- **Optional UDP batching** — `sendmmsg` / `recvmmsg` on Linux; double-buffered pinned slabs for batched sends.
- **Ref-counted datagram buffers** — `KcpPacketOwner` pool + `IRefCountedBuffer` sharing on the receive hot path.
- **OpenTelemetry-style metrics** — `System.Diagnostics.Metrics` meter `KcpSharp`.

---

## Requirements

- **.NET 10** (`net10.0`)
- UDP connectivity between peers
- Matching **conversation ID**, **MTU**, and **KCP options** on both sides (when using a non-zero or multiplexed ID)

---

## Installation

Reference the project or package:

```bash
dotnet add package KcpSharp
```

Or add a project reference:

```bash
dotnet add reference path/to/KcpSharp.csproj
```

---

## Quick Start

### 1. Create a conversation

`KcpBuilder` is the supported way to create a `KcpConversation`. It owns the UDP socket (non-blocking), binds if needed, starts the receive loop, and returns the conversation instance.

```csharp
using System.Net;
using System.Net.Sockets;
using KcpSharp;

var options = KcpConversationOptions.LowLatencyPreset with
{
    Mtu = 1400,
    KeepAliveOptions = new KcpKeepAliveOptions(sendInterval: 5_000, gracePeriod: 30_000),
};

var remote = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 9000);

KcpConversation conversation = KcpBuilder
    .ForConversation()
    .WithUdpSocket(AddressFamily.InterNetwork, out Socket udp)
    .WithLocalEndPoint(new IPEndPoint(IPAddress.Any, 0))   // optional; ephemeral if omitted
    .WithRemoteEndPoint(remote)
    .WithConversationId(0xDEAD_BEEF)                        // optional; default is 0
    .WithOptions(options)
    .WithExceptionHandler(static (ex, conv, _) =>
    {
        Console.WriteLine($"KCP error: {ex.Message}");
        return false; // false → mark transport closed
    })
    .Build();
```

Bind to a fixed local port:

```csharp
KcpConversation conversation = KcpBuilder
    .ForConversation()
    .WithUdpSocket(new IPEndPoint(IPAddress.Any, 9001), AddressFamily.InterNetwork, out _)
    .WithRemoteEndPoint(remote)
    .Build();
```

> **Note:** `KcpCore.CreateConversation` exists but is **[Obsolete]** — use `KcpBuilder` instead.

### 2. Send data

```csharp
byte[] payload = new byte[512];
Random.Shared.NextBytes(payload);

// Non-blocking (no allocation on success path when queue has space)
if (!conversation.TrySend(payload))
    Console.WriteLine("Send queue full or transport closed");

// Async with backpressure
bool sent = await conversation.SendAsync(payload, cancellationToken);

// Optional: force flush of ACKs and pending segments
bool flushed = await conversation.FlushAsync(cancellationToken);

// Bytes accepted by KCP but not yet on the wire
long pending = conversation.UnflushedBytes;
```

Stream mode allows partial enqueue:

```csharp
options.StreamMode = true;
// ...
conversation.TrySend(largeBuffer, allowPartialSend: true, out int written);
```

### 3. Receive data

```csharp
byte[] recvBuffer = new byte[options.Mtu];

// Async — do not await the same ValueTask twice
while (true)
{
    KcpConversationReceiveResult result =
        await conversation.ReceiveAsync(recvBuffer, cancellationToken);

    if (result.TransportClosed)
        break;

    Process(recvBuffer.AsSpan(0, result.BytesReceived));
}

// Non-blocking
while (conversation.TryReceive(recvBuffer, out KcpConversationReceiveResult result))
{
    if (result.TransportClosed) break;
    Process(recvBuffer.AsSpan(0, result.BytesReceived));
}

// Peek size without consuming
if (conversation.TryPeek(out result) && !result.TransportClosed)
    Console.WriteLine($"Next message: {result.BytesReceived} bytes");
```

Pipe-friendly receive:

```csharp
await conversation.ReceiveToWriterAsync(pipeWriter, cancellationToken);
```

### 4. Shutdown

Always tear down conversations explicitly so background tasks finish before flush buffers are freed.

```csharp
// Preferred
await conversation.DisposeAsync();

// Synchronous — also waits for the internal update loop before releasing flush buffers
conversation.Dispose();
```

`SetTransportClosed()` signals closure to pending send/receive operations but does not replace full disposal.

---

## Configuration

### `KcpConversationOptions`

| Property | Default | Description |
|----------|---------|-------------|
| `Mtu` | `1400` | Maximum UDP payload size (minimum **50**). Stay ≤ ~1400 on standard Ethernet. |
| `SendWindow` | `32` | Max in-flight unacknowledged segments (max **65535**). |
| `ReceiveWindow` | `128` | Out-of-order KCP receive buffer (max **65535**). Should be **≥ peer `SendWindow`**. |
| `RemoteReceiveWindow` | `128` | Initial estimate of peer receive window. |
| `UpdateInterval` | `100` ms | KCP update period (values **&lt; 10** fall back to **100**). Drives flush/RTO timing. |
| `NoDelay` | `false` | `true` → min RTO **30** ms; `false` → min RTO **100** ms, default RTO **200** ms. |
| `FastResend` | `0` | Duplicate ACK threshold for fast retransmit (**2** is common for games). |
| `DisableCongestionControl` | `false` | Disables slow-start / AIMD-style `cwnd` limiting. |
| `StreamMode` | `false` | `false` = message mode; `true` = byte stream (use with `KcpStream`). |
| `SendQueueSize` | `32` | App-facing send queue depth (≤ 0 → default). |
| `ReceiveQueueSize` | `32` | App-facing receive queue depth (≤ 0 → default). |
| `EnableBatching` | `true` | UDP send batching via `IKcpBatchTransport`. |
| `MaxBatchSize` | `16` | Batch slots (**0–1024**). Transport memory ≈ **`2 × MaxBatchSize × Mtu`** when batching is enabled. |
| `PreBufferSize` / `PostBufferSize` | `0` | Reserved bytes before/after KCP payload in outbound buffers (tunnel headers). Must leave room for KCP header (+ 4-byte conv ID when used). |
| `InitialSsthresh` | `32` | Slow-start threshold (minimum **2**). |
| `BufferPool` | `null` → shared array pool | Custom `IKcpBufferPool` for segment allocation. |
| `KeepAliveOptions` | `null` | Optional idle detection / probe behavior. |
| `ReceiveWindowNotificationOptions` | `null` | Optional window-size notifications to peer. |

Call `options.Validate()` before use if you build options manually.

### Presets

```csharp
KcpConversationOptions game = KcpConversationOptions.LowLatencyPreset;
// NoDelay, UpdateInterval=10, FastResend=2, DisableCongestionControl=true,
// large windows/queues, EnableBatching=false

KcpConversationOptions bulk = KcpConversationOptions.BulkTransferPreset;
// Conservative latency, larger windows, congestion control enabled
```

Clone mutable presets if you reuse the static instances:

```csharp
var options = KcpConversationOptions.LowLatencyPreset.Clone();
options.Mtu = 1200;
```

### `KcpKeepAliveOptions`

| Parameter | Meaning |
|-----------|---------|
| `sendInterval` | Minimum interval (ms) between keep-alive sends. |
| `gracePeriod` | If no packet is received for this many ms, the transport is closed. |

Keep-alive timing is also tied to `UpdateInterval`.

### Conversation ID

- Set with `KcpBuilder.WithConversationId(uint)`.
- If omitted, the ID is **0**.
- When an ID is used, each KCP segment on the wire is prefixed with a **4-byte little-endian** conversation ID; effective MSS is reduced by 4 bytes compared to a conv-less session.
- Both peers must use the **same ID** and compatible options.

---

## API Overview

### Public surface (typical application code)

| Type | Role |
|------|------|
| `KcpBuilder` | Create and wire a `KcpConversation` over UDP. |
| `KcpConversation` | Send, receive, flush, metrics, lifecycle. |
| `KcpConversationOptions` | Protocol and queue tuning. |
| `KcpConversationReceiveResult` | `BytesReceived` + `TransportClosed`. |
| `KcpStream` | `System.IO.Stream` over a stream-mode conversation or `PipeReader`/`PipeWriter`. |
| `KcpMetrics` | `System.Diagnostics.Metrics` instruments. |
| `IKcpTransport` | Contract for sending outbound datagrams (used internally; implement for custom integrations in-library). |
| `IKcpBatchTransport` | Batch slice allocation + flush (transport implementations). |
| `IKcpBufferPool` / `KcpBufferPoolRentOptions` / `KcpRentedBuffer` | Custom buffer allocation. |
| `IKcpConversation` | `Dispose` / `DisposeAsync` / `SetTransportClosed`. |

Socket transports, multiplex connections, and raw channels are **`internal`** in this repository; they are composed by `KcpBuilder` and library code. Multi-channel UDP routing on one socket is supported inside the library (`KcpMultiplexConnection<T>`) but not exposed as a stable public entry point yet.

### `KcpConversation` — main operations

| Operation | Description |
|-----------|-------------|
| `TrySend` / `SendAsync` | Enqueue application payload. |
| `TryGetSendQueueAvailableSpace` / `WaitForSendQueueAvailableSpaceAsync` | Backpressure helpers. |
| `FlushAsync` | Push ACKs and segments to UDP. |
| `TryReceive` / `ReceiveAsync` / `TryPeek` / `WaitToReceiveAsync` | Consume receive queue. |
| `ReceiveToWriterAsync` | Copy to `IBufferWriter<byte>`. |
| `CancelPendingSend` / `CancelPendingReceive` | Cancel in-flight waiters. |
| `SetExceptionHandler` | Per-conversation flush/receive error policy. |
| `SetTransportClosed` | Signal shutdown without full resource release. |
| `TransportClosed` / `ConversationId` / `StreamMode` / `UnflushedBytes` | State introspection. |

---

## Advanced Usage

### Stream mode + `KcpStream`

```csharp
var options = new KcpConversationOptions { StreamMode = true, /* ... */ };
KcpConversation conv = KcpBuilder.ForConversation() /* ... */ .Build();

await using var stream = new KcpStream(conv, ownsConversation: true);
await stream.WriteAsync(data, cancellationToken);

byte[] buf = new byte[4096];
int read = await stream.ReadAsync(buf, cancellationToken);
```

`KcpStream` requires `StreamMode = true`. Alternatively, construct `KcpStream` from `PipeReader` / `PipeWriter`.

### Custom buffer pool

Implement `IKcpBufferPool` and assign `KcpConversationOptions.BufferPool`. Use pinned or pooled arrays if buffers participate in native batch I/O.

```csharp
public sealed class PinnedBufferPool : IKcpBufferPool
{
    public KcpRentedBuffer Rent(KcpBufferPoolRentOptions options)
    {
        byte[] buffer = GC.AllocateUninitializedArray<byte>(options.Size, pinned: true);
        return KcpRentedBuffer.FromMemory(buffer);
    }
}
```

### Exception handling

- **Builder:** `WithExceptionHandler(Func<Exception, KcpConversation, object?, bool> handler, object? state)`
  - Return **`true`** to ignore and continue; **`false`** closes the transport.
- **Runtime:** `conversation.SetExceptionHandler(...)` for the same contract on flush/update errors.

### Batching

| `EnableBatching` | `MaxBatchSize` | Behavior |
|----------------|----------------|----------|
| `true` | `> 1` | Batch slices committed into a pinned slab; flushed with `sendmmsg` (Linux) or sequential `SendToAsync`. |
| `true` | `1` | Effectively single-packet batching. |
| `false` | ignored (0) | Each send goes directly to `SendToAsync`. |

`LowLatencyPreset` sets `EnableBatching = false` to favor latency over syscall amortization.

---

## Observability

Subscribe to the **`KcpSharp`** meter (`KcpMetrics.Meter`):

| Instrument | Name | Meaning |
|------------|------|---------|
| Counter | `kcp.retransmission.count` | Timeout retransmits |
| Counter | `kcp.fast_retransmission.count` | Fast retransmit |
| Counter | `kcp.packets_dropped.count` | Drops (queues / errors) |
| Counter | `kcp.waitlist_packets_dropped.count` | Receive ring overflow |
| Counter | `kcp.ack_dropped.count` | ACK ring full |
| Counter | `kcp.ack_snapshot_partial.count` | ACK snapshot truncated (queued on next flush) |
| Histogram | `kcp.rtt.ms` | Round-trip time |

Many counters accept a `conversation_id` tag when an ID is configured.

Example with `dotnet-counters`:

```bash
dotnet-counters monitor --counters KcpSharp
```

---

## Architecture

Three cooperating pipelines:

```
┌─────────────────────────────────────────────────────────────────────┐
│  RECEIVE                                                            │
│  UDP ──► KcpPacketOwner (pool) ──► IKcpPacketSink.InputPacketAsync  │
│          ├─ pure ACK / probe ──► ProcessInlineAcksAndProbes (sync)  │
│          └─ PUSH data ──► KcpReceiveRingBuffer ──► update loop       │
│                              ──► SetInput ──► rcv_buf / rcv_queue    │
├─────────────────────────────────────────────────────────────────────┤
│  UPDATE / TIMER                                                     │
│  KcpGlobalTickEngine (10 ms wheel, 1024 slots)                      │
│       ──► KcpConversationUpdateActivation.Notify()                  │
│       ──► RunUpdateOnActivationCore (single task per conversation)  │
│       ──► UpdateCoreAsync / FlushCoreAsync                          │
├─────────────────────────────────────────────────────────────────────┤
│  SEND                                                               │
│  TrySend / SendAsync ──► KcpSendQueue ──► snd_buf ──► FlushCoreAsync│
│       ├─ FlushAcksFastAsync (pre-allocated ACK buffer)              │
│       └─ FlushCore2Async ──► IKcpBatchTransport / SendPacketAsync   │
└─────────────────────────────────────────────────────────────────────┘
```

### Key components

| Component | Role |
|-----------|------|
| `KcpGlobalTickEngine` | Shared 10 ms timing wheel (**1024** slots); registers `KcpConversationUpdateActivation`; removes entries on unregister. |
| `KcpConversationUpdateActivation` | Bridges ticks + ring buffer to `IValueTaskSource` waiters. |
| `KcpReceiveRingBuffer` | SPSC queue for PUSH packets (spin lock). |
| `KcpAcknowledgeList` | Lock-free ACK ring between receive and flush paths. |
| `KcpSendQueue` / `KcpReceiveQueue` | Application-facing queues with `ManualResetValueTaskSourceCore`. |
| `KcpBuffer` | Ref-counted or copied payload views; `Release()` returns memory to pool/owner. |
| `KcpPacketOwner` | Pooled `IMemoryOwner<byte>` with ref-counting for zero-copy receive. |
| `KcpFlushAsyncMethodBuilder` | Custom async builder to reduce allocations on flush hot paths. |

### Packet layout (with conversation ID)

Each KCP segment on the wire:

1. **4 bytes** — conversation ID (when session uses an ID)
2. **20 bytes** — KCP header (`KcpPacketHeader`)
3. **Payload** — `length` bytes from header

Without a conversation ID, only the 20-byte header + payload are sent.

---

## Platform Behavior

| OS | Receive | Send |
|----|---------|------|
| **Linux** | Dedicated long-running thread; `Poll` + `recvmmsg`; copies into pooled `KcpPacketOwner`. | `sendmmsg` when batching and `count > 1`; falls back to `SendToAsync`. |
| **Windows** | `ReceiveFrom` loop with adaptive `Poll` timeout; `SIO_UDP_CONNRESET` disabled when supported. | `SendToAsync` (per datagram or batched loop). |
| **All** | Non-blocking UDP; 4 MiB socket buffers attempted; `DontFragment` set when allowed. | `IKcpBatchTransport` double-buffered native slab (`2 × MaxBatchSize × Mtu`). |

---

## Lifecycle & Thread Safety

1. **One conversation** runs a single **update loop** task (`RunUpdateOnActivationCore`) driven by ring-buffer input and the global tick engine.
2. **ACK fast-path** runs on the thread that calls `InputPacketAsync` (typically the socket receive path).
3. **`Dispose` / `DisposeAsync`** call `SetTransportClosed()`, drain queues, **await the update loop**, then release pre-allocated flush buffers — avoiding use-after-free on `_cachedFlushBuffer`.
4. Do **not** await the same `ValueTask` from `ReceiveAsync` / `SendAsync` multiple times.
5. Only **one concurrent** receive-style operation per conversation (try/receive/peek/wait APIs share one waiter).
6. Configure **`ReceiveWindow` ≥ peer `SendWindow`** to avoid drops and ring-buffer pressure.

---

## Acknowledgements

- [KCP](https://github.com/skywind3000/kcp) by [skywind3000](https://github.com/skywind3000)
- Earlier C# work: [yigolden-oss/KcpSharp](https://github.com/yigolden-oss/KcpSharp)

This tree adds ref-counted receive paths, a global timing wheel, ACK fast-path processing, flush buffer lifecycle fixes, and Linux `mmsg` batching.
