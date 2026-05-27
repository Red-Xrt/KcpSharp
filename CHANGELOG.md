# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-05-27

### Added
- Added a comprehensive `README.md` refresh with updated architecture, API overview, observability, lifecycle guidance, and platform behavior sections.
- Added explicit documentation for `KcpBuilder`-first setup and clarified usage of stream mode, backpressure APIs, and shutdown flows.
- Added an initial `CHANGELOG.md` following Keep a Changelog structure.

### Changed
- Updated documentation to match the current codebase surface and behavior, including internal/public API boundaries and timing-wheel details.
- Improved disposal lifecycle in `KcpConversation` by waiting for the update loop completion in both `Dispose()` and `DisposeAsync()` before releasing cached flush buffers.
- Updated `KcpSocketTransport` disposal flow to prefer async disposal when available (`IAsyncDisposable`) for owned connections.
- Updated update-loop activation handling in `KcpConversation` to re-read activation state each loop iteration and exit cleanly when transport is closed.
- Hardened `KcpSocketTransport` disposal to keep cleanup best-effort and always dispose flush semaphore even if connection disposal throws.

### Fixed
- Fixed a double-dispose path for packet owners in `KcpConversation.SetInput(...)` by removing duplicate owner disposal in exception handling.
- Fixed potential use-after-free during synchronous conversation teardown by ordering update-loop shutdown before cached flush-buffer release.
- Fixed timing-wheel unregister cleanup in `KcpGlobalTickEngine` by removing unregistered activations from their current wheel slot.
- Fixed `KcpPacketOwner` ref-count underflow behavior on redundant dispose calls.
- Fixed potential self-deadlock risk by preventing `KcpConversation` from synchronously/asynchronously waiting on its own update-loop task during disposal.
- Fixed pooled packet-owner lifecycle safety by rejecting `KcpPacketOwner` re-initialization when the previous lease has not been fully released.
