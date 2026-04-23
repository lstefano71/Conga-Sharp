# Changelog

## Unreleased

### Fix: Use-after-return during decompression in `DecompressPooled`

**Issue:** `Compression.DecompressPooled` called `sourceOwner?.Dispose()` before `Decompress(algo, data)`. Since `data` is a `ReadOnlySpan<byte>` over the pooled buffer owned by `sourceOwner`, the array was returned to `ArrayPool<byte>.Shared` while decompression was still reading from it — causing nondeterministic payload corruption.

**Resolution:** Moved `sourceOwner?.Dispose()` to after `Decompress()` completes.

**Affected files:**
- `src/CongaSharp/Protocol/Compression.cs`

### Fix: Dangling pooled user-header memory in framed read path

**Issue:** `FrameData` had no `RawUserHeadersOwner`, so `SocketPipeline.FramedReadLoopAsync` could not transfer header buffer ownership from `FrameReadResult` to downstream consumers. When `result.Dispose()` was called, the pooled header buffer was returned while modes (CommandMode, BlkRawMode, BlkTextMode) still referenced that memory in events — causing nondeterministic corruption of user headers.

**Resolution:** Added `RawUserHeadersOwner` + `TakeRawUserHeadersOwner()` to `FrameData` and wired the ownership chain: `FrameReadResult` → `FrameData` → `CongaEvent.UserHeadersOwner`.

**Affected files:**
- `src/CongaSharp/Modes/IConnectionMode.cs` — added `RawUserHeadersOwner` field to `FrameData`
- `src/CongaSharp/Networking/SocketPipeline.cs` — takes header owner from result, passes to `FrameData`
- `src/CongaSharp/Modes/CommandMode.cs` — transfers header owner to `CongaEvent`
- `src/CongaSharp/Modes/BlkRawMode.cs` — transfers header owner to `CongaEvent`
- `src/CongaSharp/Modes/BlkTextMode.cs` — transfers header owner to `CongaEvent`

### Fix: Tracked mailbox re-enqueue no longer loses events after terminal response

**Issue:** In tracked Command mode, if a terminal response had already completed the mailbox writer and `conga_wait` then had to re-enqueue an earlier event (for example, object/event output buffer too small), `ReEnqueue` attempted `ChannelWriter.TryWrite` on a completed channel. The write failed and the event was silently dropped.

**Resolution:** `CommandMailbox` now has a dedicated requeue path that remains valid even after channel completion. `EventQueue.ReEnqueue` uses this mailbox requeue path, and `TryReceive` prioritizes requeued events so delivery semantics remain correct for retry scenarios.

### Breaking: `conga_send` ABI change — new `track` parameter for race-safe Command mode

**Issue:** "Fast Server / Slow Client" race condition in Command mode. When `conga_send` returns a handle to APL and APL spawns a specific `conga_wait` on that handle, the server's response can arrive during the gap between the two calls. A broad catch-all waiter (e.g. `conga_wait("C1", ...)`) steals the response, leaving the specific waiter blocked forever.

**Resolution:** Added a per-command **mailbox pre-registration** mechanism with a new `track` parameter on `conga_send`:

- `track=1`: Creates a `CommandMailbox` (backed by `System.Threading.Channels.Channel<CongaEvent>`) keyed by the resolved handle *before* bytes are sent. The background socket reader routes responses to the mailbox. `conga_wait` with the exact handle reads from the mailbox. Broad waiters cannot see mailboxed events.
- `track=0` (default): Events go to the global queue as before. Backward-compatible for single-threaded event loops.

**Routing rules:**
| Scenario | track=0 | track=1 |
|----------|---------|---------|
| Broad `Wait("C1")` | Sees all events | Cannot see tracked command events |
| Specific `Wait("C1.Auto00000001")` | Scans global queue (race-vulnerable) | Reads from mailbox (race-safe) |

**Lifecycle/cleanup:**
- Terminal event (Respond) marks mailbox complete; lazy cleanup after last read
- Connection disconnect: `UnregisterMailboxesByPrefix` posts Closed events to all matching mailboxes
- Shutdown: Error events delivered to all mailboxes
- Send failure: mailbox rolled back (unregistered) before returning error

**Affected files:**
- `src/CongaSharp/Events/CommandMailbox.cs` — **new** per-command event buffer
- `src/CongaSharp/Events/EventQueue.cs` — mailbox registry, routing in Enqueue/Wait/ReEnqueue/shutdown
- `src/CongaSharp/Events/CongaEvent.cs` — added `IsTerminal` property
- `src/CongaSharp/Modes/CommandMode.cs` — sets `IsTerminal=true` on Respond events
- `src/CongaSharp/NativeExportsNetworking.cs` — new `track` param, mailbox pre-registration + rollback
- `src/CongaSharp/NativeExports.cs` — mailbox cleanup in `conga_close`
- `tests/CongaSharp.Tests/Events/CommandMailboxTests.cs` — **new** 8 unit tests
- `tests/CongaSharp.Tests/Events/EventQueueTests.cs` — 13 new mailbox routing tests
- `docs/PRD.md` — updated §7 conga_send signature, §9.2 event delivery
- `apl/CongaSharp.apln` — updated ⎕NA declaration and Send/SendEx/SendTracked
- `tests/external/python/conga_api.py` — updated argtypes and send()

### Fix: Receiver-side command names now valid APL variable names

**Issue:** Server-side auto-generated command names used the first 8 hex chars of the CorrelationId Guid (e.g. `c306c4f7`), which can start with a digit — invalid as an APL variable name.

**Resolution:** Receiver-side command names now use `Cmd00000000` format (sequential counter per connection, always starting with a letter). Uses a different prefix than sender-side `Auto00000000` to prevent namespace collisions on the same connection.

### Fix: Double respond to same command now rejected

**Issue:** `conga_respond` could be called twice on the same command handle. The second call would silently succeed with `Guid.Empty` on the wire, and the correlation table entry had already been removed — meaning we sent a garbage frame.

**Resolution:** `TryPrepareRespond` uses atomic `TryRemove` from the correlation map. Only the first caller succeeds; subsequent calls return `InvalidName` (1002) from the native export. Same protection added to `TryPrepareProgress`.

### Breaking: Wire protocol header — CmdName replaced with CorrelationId

**Issue:** The 32-byte UTF-8 `CmdName` field in the frame header imposed a 31-byte limit on command names, coupled wire-level correlation with user-visible naming, and was fragile due to truncation risk.

**Resolution:** Replaced with a 16-byte binary `CorrelationId` (Guid, little-endian). This shrinks the fixed header from 56 to 40 bytes and decouples command correlation from naming.

**Key changes:**
- `FrameHeader.Size` reduced from 56 to 40 bytes. All field offsets after byte 8 shifted.
- `CmdName` field (offset 8, 32 bytes) → `CorrelationId` (offset 8, 16 bytes, `System.Guid`).
- Wire encoding: `Guid.TryWriteBytes(span, bigEndian: false)` / `new Guid(span, bigEndian: false)` for consistent little-endian format.
- `CommandMode` maintains a per-connection bidirectional map (`Guid↔string`) to resolve CorrelationIds to local command names.
- Non-Command framed modes (BlkRaw, BlkText) use `Guid.Empty` (16 zero bytes).
- No version bump — backward compatibility is not required. Old readers will fail on CRC mismatch.

**Affected files:**
- `src/CongaSharp/Protocol/FrameHeader.cs` — header layout, WriteTo/ReadFrom
- `src/CongaSharp/Protocol/FrameWriter.cs` — `string cmdName` → `Guid correlationId`
- `src/CongaSharp/Protocol/FrameReader.cs` — updated offset comments
- `src/CongaSharp/Networking/AsyncFrameIO.cs` — `string cmdName` → `Guid correlationId`
- `src/CongaSharp/Networking/SocketPipeline.cs` — passes CorrelationId on send/receive
- `src/CongaSharp/Modes/IConnectionMode.cs` — `FrameData`/`OutboundMessage` carry both CmdName and CorrelationId
- `src/CongaSharp/Modes/CommandMode.cs` — full rewrite with bidirectional correlation maps
- All test files updated for new header layout and Guid-based correlation

### Breaking: `conga_send` ABI change — returns resolved handle name

**Issue:** `conga_send` did not return the auto-generated or resolved command/message handle, violating `DRC.Send` parity (Section A.26 of the Conga User Guide). Callers had no way to obtain the handle needed to `Wait` on a specific command.

**Resolution:** Extended `conga_send` with three new parameters:

| Parameter | Type | Description |
|-----------|------|-------------|
| `out_name` | `wchar_t*` | Caller-allocated buffer receiving the resolved handle name |
| `out_name_cap` | `int32_t` | Capacity of `out_name` in `wchar_t` units |
| `out_name_len` | `int32_t*` | Output: actual length written (excluding null terminator) |

**Behavior changes:**
- Sending with a base client/connection name (e.g. `"C1"`) now auto-generates a unique handle (`"C1.Auto00000000"`, `"C1.Auto00000001"`, ...) and returns it via `out_name`.
- Sending with an explicit dotted name (e.g. `"C1.MyCmd"`) returns the name as-is.
- Command-mode server-side `Send` is now rejected with `InvalidMode` (1010) — servers must use `conga_respond`/`conga_progress`.
- If `out_name` is `NULL`, the send proceeds without writing the name (backward-compatible for callers that don't need it).
- If `out_name_cap` is too small, returns `BufferTooSmall` (2001) *before* sending (send is not undoable).

**Affected files:**
- `src/CongaSharp/NativeExportsNetworking.cs` — rewritten `CongaSend`
- `src/CongaSharp/Core/ObjectRegistry.cs` — added `GenerateAutoName()`
- `tests/CongaSharp.Tests/NativeExports/NativeApi.cs` — updated test binding
- `apl/CongaSharp.apln` — updated `⎕NA` declaration and `Send`/`SendEx` bodies
- `tests/external/python/conga_api.py` — updated ctypes argtypes and `send()` method
- `docs/PRD.md` — updated `conga_send` C signature and semantics
