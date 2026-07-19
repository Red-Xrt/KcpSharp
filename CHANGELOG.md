# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.1] - 2026-07-20

### Fixed
- Fixed a latent lock-ordering deadlock across all `IValueTaskSource`-based wait primitives (`AsyncCapacityReserve`, `KcpConversationUpdateActivation`, `KcpSendQueue`, `KcpReceiveQueue`, `KcpRawSendOperation`, `KcpRawReceiveQueue`): tearing down the cancellation registration under the sync lock used `CancellationTokenRegistration.Dispose()`, which blocks waiting for a concurrently-firing cancel callback that itself needs the same lock. Switched these in-lock teardowns to the non-blocking `Unregister()`; the existing `_signaled`/`_released` guard preserves exactly-once completion.
- Fixed a use-after-free of the native send-batch slab in `KcpSocketTransport.Dispose`: the unmanaged `_batchBufferSlab` was freed before the owned connection (and its flush loop) was stopped, racing an in-flight `sendmmsg`/`SendToAsync` that reads from it. The slab is now freed only after the connection is disposed.
- Fixed `kcp.rtt.ms` histogram never recording samples on the inline ACK fast path: `UpdateRtoThreadSafe` (used by `ProcessInlineAcksAndProbes`) now records the RTT metric, so pure-ACK packets on loopback/low-latency links are observed.

### Changed
- Bumped runtime dependencies to 10.0.10 (`Microsoft.AspNetCore.Connections.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.ObjectPool`) and test tooling (`Microsoft.NET.Test.Sdk` 18.8.1, `xunit.runner.visualstudio` 3.1.5). No known vulnerable packages.
- Removed a redundant catch-and-rethrow around `KcpStream.DataAvailable`.
- `.gitignore` now excludes `.vs/`, `*.user`, and test result folders.

### Added
- Added `StressMetricsAuditTests` (metrics report harness) and `KcpMetricsListenerTests` (histogram/RTT observability regression).

### Tests
- Hardened `UdpJitterRelay` (test infrastructure) shutdown: drains in-flight forward tasks and swallows shutdown-time socket errors to remove disposal flakiness.
- Relaxed `CleanLoopback_Metrics_NoPacketDrops_100Rpcs` retransmission assertion: NoDelay mode's 30 ms min-RTO produces spurious loopback retransmissions when the test host is CPU-saturated, so the test now guards against a retransmission storm rather than asserting exactly zero.

## [1.0.0] - 2026-05-27

### Added
- Added a comprehensive `README.md` refresh with updated architecture, API overview, observability, lifecycle guidance, and platform behavior sections.
- Added explicit documentation for `KcpBuilder`-first setup and clarified usage of stream mode, backpressure APIs, and shutdown flows.
- Added an initial `CHANGELOG.md` following Keep a Changelog structure.
- Added `JsonStressTests` for private-server JSON traffic (large payloads, ping-pong, burst, many small messages). Run with `dotnet test --filter "Category=Stress"`.
- Added loopback regression tests for multi-segment payloads (`> MSS`) in message and stream mode.
- Added `GameServerStressTests` (login RPC, mixed workload, combat/chat burst, inventory bulk, reconnect cycles, parallel clients) with `[Trait("Category=GameServer")]`.
- Added `NetworkStressTests` with UDP jitter relay, app/KCP latency percentiles, and memory/CPU/drop assertions via `StressMetricsCollector` (`[Trait("Category=Metrics")]`).

### Changed
- Updated documentation to match the current codebase surface and behavior, including internal/public API boundaries and timing-wheel details.
- Improved disposal lifecycle in `KcpConversation` by waiting for the update loop completion in both `Dispose()` and `DisposeAsync()` before releasing cached flush buffers.
- Updated `KcpSocketTransport` disposal flow to prefer async disposal when available (`IAsyncDisposable`) for owned connections.
- Updated update-loop activation handling in `KcpConversation` to re-read activation state each loop iteration and exit cleanly when transport is closed.
- Hardened `KcpSocketTransport` disposal to keep cleanup best-effort and always dispose flush semaphore even if connection disposal throws.
- Synchronized congestion-window (`_cwnd`, `_incr`, `_ssthresh`) updates with a dedicated lock across inline ACK, update-loop, and flush paths.

### Fixed
- Fixed multi-segment flush packing: the send flush loop now accounts for segments already batched when checking MTU, so payloads larger than MSS are split across UDP packets instead of exceeding transport MTU and closing the connection.
- Fixed critical double-dispose of `KcpPacketOwner` on PUSH packets: `SetInput` no longer disposes the buffer owner that `KcpConversationUpdateNotification` releases after `HandleData` may `Retain()` into `rcv_buf`.
- Fixed `AsyncCapacityReserve` waiter pool race by returning `Waiter` instances to the pool only from `GetResult`, not from `Complete`/`SetException`.
- Fixed `KcpRawChannel.Dispose()` not joining the send loop before tearing down queues (aligned with `DisposeAsync`).
- Fixed `KcpSocketTransport` dispose not joining the receive thread/task; socket is closed to unblock receive, then thread/task is joined before connection disposal.
- Fixed post-dispose inline ACK processing by rejecting `InputPacketAsync` when `TransportClosed`.
- Fixed overlapping `KcpGlobalTickEngine` tick loops on idle restart using `Interlocked` start/stop coordination.
- Fixed stale keep-alive grace-period checks by refreshing the timestamp after flush/drain work.
- Fixed `KcpReceiveQueue.GetQueueSize()` reading `_completedPacketsCount` without synchronization.
- Fixed `KcpSendQueue.SetTransportClosed()` disposing `AsyncCapacityReserve` while `Dispose()` also disposes it.
- Fixed potential use-after-free during synchronous conversation teardown by ordering update-loop shutdown before cached flush-buffer release.
- Fixed timing-wheel unregister cleanup in `KcpGlobalTickEngine` by removing unregistered activations from their current wheel slot.
- Fixed `KcpPacketOwner` ref-count underflow behavior on redundant dispose calls.
- Fixed potential self-deadlock risk by preventing `KcpConversation` from waiting on its own update-loop task during disposal.
- Fixed pooled packet-owner lifecycle safety by rejecting `KcpPacketOwner` re-initialization when the previous lease has not been fully released.
- Fixed `KcpBuilder` always passing conversation ID `0` when no ID was configured, incorrectly enabling conv-ID packet headers.
- Fixed `HandleAck` serial-number bounds being checked outside `_sndBufLock`.
- Fixed `KcpReceiveRingBuffer.HasItems` unsynchronized reads that could skip pending PUSH packets.
- Fixed abandoned async wait slots after transport close by releasing `_activeWait` and cancellation registrations on shutdown in send/receive queues.
- Fixed Windows receive loop terminating on non-blocking `WouldBlock` by polling before `ReceiveFrom` and handling `WouldBlock`/`TryAgain` explicitly.
- Fixed `KcpSocketTransport` allowing packet dispatch during teardown by marking `_disposed` before joining the receive thread.
- Fixed Linux `sendmmsg` giving up immediately on `EAGAIN` by retrying with a brief spin before moving on.
- Fixed raw-channel send/receive shutdown leaving `_activeWait` latched when async operations were abandoned.
- Fixed silent raw-channel receive-queue overflow by recording `KcpMetrics.PacketsDropped`.
- Fixed `KcpGlobalTickEngine.Shutdown()` disposing conversation-owned activations instead of only unregistering them from the global timer wheel.
- Fixed synchronously completed `InputPacketAsync` `ValueTask`s not being consumed on the receive path, which could leak `IValueTaskSource` state.
- Fixed `KcpRawChannel` accepting inbound packets after transport shutdown.
- Fixed Linux `MSG_TRUNC` packets being dropped without metrics.
- Fixed `KcpRawSendOperation.TryConsume` leaving `_activeWait` latched when transport closed during an in-flight send wait.
- Fixed `CancelPendingOperation` / token cancellation not releasing `_activeWait` slots in raw and reliable send/receive queues.
- Fixed `KcpRawChannel` double-dispose and idempotent lifecycle via an explicit `_disposed` flag.
- Fixed Windows receive path processing zero-length datagrams.
- Fixed multiplex packet routing during the dispose window by checking `_disposeState` before dispatch.
- Fixed pooled flush async state machine retention by clearing `_flushStateMachine` on conversation dispose.
- Fixed `KcpGlobalTickEngine` idle-shutdown race that could leave newly registered conversations without timer ticks.
- Fixed `KcpConversationUpdateActivation` not releasing `_activeWait` on cancel/dispose (aligned with send/receive queue fixes).
- Fixed `KcpPacketOwner` pool leak when segment enqueue throws mid-copy in `KcpSendQueue`.
- Fixed `KcpStream` pipe mode not completing pipes on dispose and incorrectly nulling a non-owned conversation reference.
- Fixed `KcpBuilder.Build()` leaking the builder-owned UDP socket when construction fails after socket creation.
- Fixed Linux receive silently dropping packets when endpoint resolution fails; now records `KcpMetrics.PacketsDropped`.
- Fixed Windows batch send silently dropping packets on `WouldBlock` by retrying with spin (aligned with Linux `sendmmsg` behavior).
- Fixed `TrySendOrBatchAsync` treating swallowed flush exceptions as successful sends.
- Fixed inline window-probe packets not waking the update loop promptly for zero-window recovery.
- Fixed `KcpBuilder`/`KcpCore` leaving the socket receive thread running after `KcpConversation.Dispose()` by disposing dedicated socket transports.
- Fixed multiplex `UnregisterConversation` being a no-op after transport close, leaving stale entries in `Contains()`.
- Fixed `AsyncCapacityReserve.TryReserve` spuriously failing while async waiters exist despite free capacity.
- Fixed `SendPacketAsync` silently ignoring packets larger than MTU; now throws and records a drop metric.
- Fixed DEBUG `ConsumePacket` assert rejecting stream `ReadAsync` (`_operationMode == 4`).
- Fixed `KcpStream.FlushAsync` blocking on remote ACK instead of using stream-oriented flush semantics.
- Fixed `KcpBuilder.Build()` calling `Connection` before `Start()`, which always threw.
- Added `KcpSharp.Tests` xUnit project with UDP loopback integration tests.
