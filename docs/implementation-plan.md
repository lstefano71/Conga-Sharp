# Conga-Sharp — Detailed Implementation Plan

## 1. Prerequisites & Tooling

| Tool | Version | Purpose |
|------|---------|---------|
| .NET SDK | 10.0 | Build, NativeAOT compilation |
| C# | 14 | Language features (primary constructors, etc.) |
| Visual Studio 2022 / Rider | Latest | IDE |
| Dyalog APL | 19.0+ | Testing the ⎕NA integration |
| xUnit | 2.x | Unit and integration tests |

### NuGet Dependencies

| Package | Purpose |
|---------|---------|
| `System.IO.Hashing` | CRC-32C (Castagnoli) — hardware-accelerated |
| `K4os.Compression.LZ4` | LZ4 compression |
| `ZstdSharp.Port` | Zstandard compression (managed, AOT-safe) |
| `System.Text.Json` | JSON serialization (built-in, AOT source generators) |

## 2. Project Structure

```
Conga-Sharp/
├── docs/
│   ├── PRD.md                          # Product Requirements Document
│   ├── implementation-plan.md          # This file
│   └── reference/
│       └── Conga_User_Guide.pdf.md     # Original Conga reference
├── src/
│   └── CongaSharp/
│       ├── CongaSharp.csproj           # NativeAOT library project
│       ├── Properties/
│       │   └── PublishProfiles/
│       │       └── win-x64.pubxml      # AOT publish profile
│       │
│       ├── NativeExports.cs            # [UnmanagedCallersOnly] C API functions
│       │
│       ├── Core/
│       │   ├── Root.cs                 # Root container, owns all state
│       │   ├── ObjectRegistry.cs       # Thread-safe object name→instance map
│       │   ├── CongaObject.cs          # Base class for all managed objects
│       │   ├── ServerObject.cs         # TCP server (listener)
│       │   ├── ClientObject.cs         # TCP client (outbound connection)
│       │   ├── ConnectionObject.cs     # Accepted inbound connection
│       │   ├── CommandObject.cs        # Named command (Command mode)
│       │   └── HandleTable.cs          # GCHandle-based handle ↔ Root mapping
│       │
│       ├── Protocol/
│       │   ├── FrameHeader.cs          # 52-byte wire frame header struct
│       │   ├── FrameReader.cs          # Reads frames from a stream
│       │   ├── FrameWriter.cs          # Writes frames to a stream
│       │   ├── MsgType.cs              # Enum: Data, Respond, Progress, Control
│       │   ├── FrameFlags.cs           # Flags bitfield helpers
│       │   ├── UserHeaders.cs          # Encode/decode user header key-value pairs
│       │   ├── Crc32C.cs              # CRC-32C wrapper (System.IO.Hashing)
│       │   └── Compression.cs          # Compress/decompress dispatcher
│       │
│       ├── Modes/
│       │   ├── IConnectionMode.cs      # Interface for mode-specific behavior
│       │   ├── RawMode.cs              # Raw: pass-through
│       │   ├── TextMode.cs             # Text: EOM-delimited
│       │   ├── BlkRawMode.cs           # BlkRaw: length-prefixed binary
│       │   ├── BlkTextMode.cs          # BlkText: length-prefixed text
│       │   └── CommandMode.cs          # Command: named cmds, Respond, Progress
│       │
│       ├── Events/
│       │   ├── EventType.cs            # Enum with 9 event types
│       │   ├── CongaEvent.cs           # Event data structure
│       │   └── EventQueue.cs           # ConcurrentQueue + ManualResetEventSlim
│       │
│       ├── Properties/
│       │   ├── PropertyStore.cs        # Per-object property storage
│       │   ├── PropertyDefinitions.cs  # Registry of known properties, types, defaults
│       │   └── PropertySerializer.cs   # JSON ↔ property value conversion
│       │
│       ├── Networking/
│       │   ├── TcpListener.cs          # Async accept loop
│       │   ├── TcpConnector.cs         # Async connect with timeout
│       │   ├── SocketPipeline.cs       # Read/write pipeline per connection
│       │   └── DnsResolver.cs          # TCPLookup implementation
│       │
│       ├── Diagnostics/
│       │   ├── TraceLevel.cs           # Enum: Off, Errors, Connections, Messages, Wire
│       │   └── TraceLogger.cs          # File-based trace logger
│       │
│       ├── Errors/
│       │   └── ErrorCodes.cs           # All error code constants
│       │
│       └── Marshalling/
│           ├── StringMarshaller.cs     # wchar_t* ↔ string conversion
│           ├── BufferMarshaller.cs     # byte* + len ↔ ReadOnlySpan<byte>
│           └── HandleMarshaller.cs     # uintptr_t ↔ Root lookup
│
├── tests/
│   └── CongaSharp.Tests/
│       ├── CongaSharp.Tests.csproj
│       ├── Protocol/
│       │   ├── FrameHeaderTests.cs
│       │   ├── FrameRoundtripTests.cs
│       │   ├── Crc32CTests.cs
│       │   ├── CompressionTests.cs
│       │   └── UserHeadersTests.cs
│       ├── Core/
│       │   ├── ObjectRegistryTests.cs
│       │   ├── HandleTableTests.cs
│       │   └── PropertyStoreTests.cs
│       ├── Modes/
│       │   ├── RawModeTests.cs
│       │   ├── TextModeTests.cs
│       │   └── CommandModeTests.cs
│       ├── Events/
│       │   └── EventQueueTests.cs
│       ├── Integration/
│       │   ├── EchoServerTests.cs      # Server + Client round-trip
│       │   ├── CommandModeTests.cs     # Full command protocol test
│       │   ├── CompressionE2ETests.cs  # Compressed message round-trip
│       │   └── ConcurrencyTests.cs     # Multi-threaded stress test
│       └── NativeExports/
│           └── MarshallingTests.cs     # C ABI marshalling tests
│
├── apl/
│   └── CongaSharp.apln                # Reference APL namespace with ⎕NA wrappers
│
├── CongaSharp.sln                      # Solution file
└── README.md
```

## 3. Implementation Phases

### Phase 0: Project Scaffolding
**Goal:** Working .NET 10 NativeAOT project that compiles to a DLL with a single exported function.

**Tasks:**
1. Create solution and project files (`dotnet new sln`, `dotnet new classlib`)
2. Configure `.csproj` for NativeAOT:
   - `<PublishAot>true</PublishAot>`
   - `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`
   - Target framework `net10.0`
3. Add NuGet dependencies
4. Create a minimal `conga_version` export with `[UnmanagedCallersOnly]`
5. Publish AOT and verify the DLL exports with `dumpbin /exports`
6. Create xUnit test project
7. Verify from Dyalog APL: `⎕NA 'I4 congasharp|conga_version >0T[] I4'`

**Deliverable:** A `congasharp.dll` that exports `conga_version` and is callable from APL.

---

### Phase 1: Core Infrastructure
**Goal:** Object model, handle management, property system, error codes, and tracing.

**Tasks:**
1. **Error codes** (`ErrorCodes.cs`) — all constants defined
2. **Handle table** (`HandleTable.cs`) — `uintptr_t` ↔ `Root` mapping using `GCHandle` or `nint` dictionary. Thread-safe.
3. **Object registry** (`ObjectRegistry.cs`) — `ConcurrentDictionary<string, CongaObject>`. Name generation (auto-incrementing S1, S2, C1, C2, CON0001, etc.)
4. **CongaObject base** (`CongaObject.cs`) — name, type enum, parent reference, state enum (Created, Started, Closed), property store
5. **Root** (`Root.cs`) — owns the object registry, event queue, shutdown flag
6. **Property system**:
   - `PropertyDefinitions.cs` — static registry: property name → type, default value, read-only flag, applicable object types
   - `PropertyStore.cs` — per-object dictionary of overridden values
   - `PropertySerializer.cs` — JSON string ↔ typed value using `System.Text.Json` source generators
7. **Trace logger** (`TraceLogger.cs`) — file-based, configured per root. Levels 0–4.
8. **Marshalling helpers** — `StringMarshaller`, `BufferMarshaller`, `HandleMarshaller`
9. **Native exports** for: `conga_init`, `conga_shutdown`, `conga_setprop`, `conga_getprop`, `conga_tree`, `conga_describe`, `conga_names`, `conga_close`

**Tests:**
- HandleTable: alloc/free/lookup, concurrent access
- ObjectRegistry: create, find, name generation, concurrent mutations
- PropertyStore: set/get, defaults, read-only enforcement, JSON round-trip
- Marshalling: string encoding, buffer overflow behavior

**Deliverable:** Core object model that passes unit tests. `conga_init`/`conga_shutdown` work from APL. Properties can be set and retrieved.

---

### Phase 2: Wire Protocol
**Goal:** Frame serialization/deserialization with CRC and compression.

**Tasks:**
1. **FrameHeader struct** (`FrameHeader.cs`) — 52-byte `[StructLayout(LayoutKind.Sequential)]`, pack=1. Fields: Version, MsgType, Flags, Magic, CmdName (32-byte fixed), HeadersLen, PayloadLen, HeaderCRC.
2. **CRC-32C** (`Crc32C.cs`) — thin wrapper over `System.IO.Hashing.Crc32C`. Methods: `ComputeHeaderCrc(ReadOnlySpan<byte> header48)`, `ComputePayloadCrc(ReadOnlySpan<byte> data)`.
3. **FrameFlags** (`FrameFlags.cs`) — helpers to pack/unpack the 16-bit flags field (compression algo, has-headers, has-CRC).
4. **Compression** (`Compression.cs`) — dispatcher: `Compress(algo, data) → byte[]`, `Decompress(algo, data, maxSize) → byte[]`. Implementations for None, Deflate, LZ4, Zstd.
5. **UserHeaders** (`UserHeaders.cs`) — encode/decode length-prefixed key-value pairs to/from `Dictionary<string, byte[]>`.
6. **FrameWriter** (`FrameWriter.cs`) — takes MsgType, cmdName, payload, headers, compression algo → writes complete frame to `Stream`. Computes both CRCs.
7. **FrameReader** (`FrameReader.cs`) — reads from `Stream`. First reads 52 bytes, validates header CRC, then reads variable section, validates payload CRC, decompresses.

**Tests:**
- FrameHeader: serialize/deserialize roundtrip, field offsets
- CRC: known test vectors, hardware vs software equivalence
- Compression: roundtrip for all 4 algorithms, empty payload, large payload
- UserHeaders: roundtrip, empty, multiple keys, unicode keys
- Frame roundtrip: write → read for every MsgType, with/without headers, with/without compression, with/without CRC

**Deliverable:** Wire protocol fully implemented and tested in isolation (no networking yet). All 4 compression algorithms working.

---

### Phase 3: Networking Layer
**Goal:** TCP server and client with async I/O, connection lifecycle, ephemeral ports.

**Tasks:**
1. **ServerObject** (`ServerObject.cs`):
   - Three-phase: Created → Started → Closed
   - On Start: bind `Socket`, listen, begin async accept loop
   - Accept loop: create `ConnectionObject` for each accepted client, enqueue Connect event
   - Ephemeral port: if port=0, query actual port after bind, expose via `LocalPort` property
2. **ClientObject** (`ClientObject.cs`):
   - Three-phase: Created → Connected → Closed
   - On Connect: async connect with `timeout_ms`, enqueue Connect or Error event on result
3. **ConnectionObject** (`ConnectionObject.cs`):
   - Created by server accept or wraps client socket
   - Owns a `SocketPipeline` for read/write
4. **SocketPipeline** (`SocketPipeline.cs`):
   - Async read loop: reads frames using `FrameReader`, hands to mode processor
   - Write: sends frames using `FrameWriter`
   - Handles disconnection: enqueues Closed event
5. **DnsResolver** (`DnsResolver.cs`) — resolves hostnames for `conga_clt_connect` and `TCPLookup` property behavior
6. **Native exports** for: `conga_srv_create`, `conga_srv_start`, `conga_clt_create`, `conga_clt_connect`

**Tests:**
- Server bind + client connect (loopback)
- Ephemeral port allocation and query
- Connection close from both sides
- Connect timeout
- Multiple concurrent connections to one server

**Deliverable:** TCP connections work end-to-end. Server accepts clients, data can flow (raw bytes through SocketPipeline).

---

### Phase 4: Connection Modes
**Goal:** Implement all 5 connection modes on top of the networking layer.

**Tasks:**
1. **IConnectionMode interface** — `OnDataReceived(...)`, `OnSend(data, headers, closeFlag)`, determines when to enqueue events and whether the wire protocol is used
2. **RawMode** — **unframed**: sends/receives raw TCP bytes with no wire protocol header. Each TCP read → Receive event. For interop with arbitrary TCP servers (FTP, SMTP, etc.)
3. **TextMode** — **unframed**: sends raw TCP bytes. Accumulates received bytes until EOM pattern found, then Receive event. Handle partial EOM matches across TCP reads. For interop with line-oriented protocols.
4. **BlkRawMode** — **framed**: uses full Conga-Sharp wire protocol (header, CRC, compression, user headers). Each frame → Block/BlockLast event.
5. **BlkTextMode** — **framed**: same as BlkRaw but payload treated as text. May apply EOM within framed payload.
6. **CommandMode**:
   - Track active commands per connection (`ConcurrentDictionary<string, CommandObject>`)
   - On `conga_send` with a new command name: create `CommandObject`, send Data frame with CmdName
   - On received Data frame with CmdName: create `CommandObject`, enqueue Receive event
   - `conga_respond`: send Respond frame, close `CommandObject`
   - `conga_progress`: send Progress frame, enqueue Progress event on receiver
   - Parallel commands: multiple `CommandObject`s per connection, independent lifecycles
7. **Native exports** for: `conga_send`, `conga_respond`, `conga_progress`

**Tests:**
- Raw mode: send and receive bytes
- Text mode: EOM detection, multiple terminators, partial match across packets
- Command mode: single command lifecycle, parallel commands, Progress + Respond
- Send with close_flag: all 4 values
- Mode mismatch error handling

**Deliverable:** All 5 modes working end-to-end through the C API.

---

### Phase 5: Event System & Wait
**Goal:** Complete event queue with filtered Wait, timeout, shutdown semantics.

**Tasks:**
1. **EventQueue** (`EventQueue.cs`):
   - `ConcurrentQueue<CongaEvent>` + `SemaphoreSlim` for blocking
   - `Enqueue(event)` — add event, signal semaphore
   - `Wait(objectFilter, timeout)` — block until matching event or timeout
   - Object filtering: if name is specified, skip non-matching events (re-enqueue them)
   - Shutdown: signal all waiters with error 2002
2. **CongaEvent** (`CongaEvent.cs`) — struct: ObjectName, EventType (enum + string), Payload, UserHeaders, Timestamp
3. **Wait integration**: wire up all event producers (accept, receive, close, timeout, error) to enqueue events
4. **Native export** for `conga_wait` — marshals event data to output buffers

**Tests:**
- Wait returns immediately when event already queued
- Wait blocks and returns on new event
- Wait times out correctly
- Wait with object filter
- Concurrent Wait from multiple threads
- Shutdown unblocks all waits
- Buffer too small returns 2001 + required size

**Deliverable:** Full event-driven communication loop works: Wait → Send → Wait → Respond.

---

### Phase 6: Polish & Integration
**Goal:** JSON query functions, APL wrapper, end-to-end validation.

**Tasks:**
1. **Tree** — walk object hierarchy, produce JSON:
   ```json
   {"name": ".", "type": "Root", "children": [
     {"name": "S1", "type": "Server", "children": [
       {"name": "S1.CON0001", "type": "Connection", "children": []}
     ]}
   ]}
   ```
2. **Describe** — dump all properties of an object as JSON object
3. **Names** — list child names as JSON array
4. **APL reference wrapper** (`apl/CongaSharp.apln`):
   - `⎕NA` declarations for all 17 functions
   - `CS.Init`, `CS.Srv`, `CS.Clt`, `CS.Wait`, `CS.Send`, etc.
   - Three-phase lifecycle helper: `CS.Srv` calls create + setprop loop + start
   - Error handling: check rc, ⎕SIGNAL on failure
5. **End-to-end tests**:
   - Echo server (server sends back what it receives)
   - Command-mode RPC (client sends command, server responds)
   - Compressed message round-trip
   - Multi-client concurrent test
6. **README.md** — usage guide, build instructions, APL examples

**Deliverable:** Complete, tested, documented library ready for use from Dyalog APL.

## 4. Phase Dependencies

```
Phase 0 (Scaffolding)
   │
   ▼
Phase 1 (Core Infrastructure)
   │
   ├──────────────────┐
   ▼                  ▼
Phase 2 (Wire)    Phase 5* (Event queue structure)
   │                  │
   ▼                  │
Phase 3 (Networking)  │
   │                  │
   ▼                  │
Phase 4 (Modes) ◄─────┘
   │
   ▼
Phase 5 (Event System — full integration)
   │
   ▼
Phase 6 (Polish & Integration)
```

*Note: The EventQueue data structure (Phase 5) can be implemented early as it has no networking dependencies. The full Wait integration happens after modes are complete.

## 5. Testing Strategy

### Unit Tests
- Every module has isolated unit tests
- Wire protocol tests use in-memory streams (no sockets)
- Property tests verify JSON round-trip for all property types
- CRC tests use known test vectors

### Integration Tests
- Use loopback connections (`127.0.0.1`)
- Create server + client pairs in the same process
- Test every mode with real TCP connections
- Concurrency tests: multiple threads calling Wait/Send simultaneously

### APL Integration Tests
- Manual testing from Dyalog APL session
- Verify all `⎕NA` signatures work
- Test the reference APL namespace
- Round-trip: APL → C# → wire → C# → APL

### NativeAOT Validation
- Publish with `dotnet publish -c Release` and verify the DLL
- `dumpbin /exports congasharp.dll` to confirm all exports
- Size check: DLL should be reasonable (< 20 MB with tree shaking)
- Verify no `MissingMethodException` at runtime (AOT trim issues)

## 6. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| NativeAOT trim removes needed code | Build breaks at publish | Use `[DynamicDependency]`, test early, source generators for JSON |
| `System.Text.Json` AOT limitations | JSON serialization fails | Use source generators (`JsonSerializerContext`) from Phase 1 |
| LZ4/Zstd packages not AOT-safe | Compression fails | Both K4os.LZ4 and ZstdSharp.Port are managed-only, AOT-compatible. Verified. |
| ⎕NA marshalling bugs | APL crashes | Test each signature independently in Phase 0. Use `dumpbin` to verify calling conventions. |
| Performance under concurrent load | Slow event delivery | Use lock-free data structures. Profile with BenchmarkDotNet if needed. |
| Wire protocol design flaws | Incompatibility issues | Protocol version field allows breaking changes. CRC catches corruption early. |
| Large payload memory pressure | OOM on corrupt PayloadLen | HeaderCRC validation before allocation. BufferSize limit enforcement. |

## 7. NativeAOT Specific Considerations

### C Export Conventions
```csharp
[UnmanagedCallersOnly(EntryPoint = "conga_version")]
public static int CongaVersion(char* outVersion, int versionCap)
{
    // char = wchar_t on Windows (2 bytes)
    // All exceptions must be caught — unhandled exceptions crash the process
}
```

### AOT Source Generators
```csharp
[JsonSerializable(typeof(PropertyValue))]
[JsonSerializable(typeof(TreeNode))]
[JsonSerializable(typeof(string[]))]
internal partial class CongaJsonContext : JsonSerializerContext { }
```

### Critical AOT Rules
1. **No reflection-based serialization** — use source generators
2. **No `dynamic`** — everything statically typed
3. **All exceptions caught at the export boundary** — unhandled exceptions abort the process
4. **No `Assembly.Load`** — everything compiled in
5. **Test the published DLL**, not the debug build — trimming only happens on publish

## 8. Estimated Complexity

| Phase | Files | Complexity | Notes |
|-------|-------|------------|-------|
| 0 - Scaffolding | ~5 | Low | Boilerplate, but AOT validation is critical |
| 1 - Core | ~12 | Medium | Property system and handle table need care |
| 2 - Wire Protocol | ~8 | Medium-High | CRC + compression + framing correctness |
| 3 - Networking | ~6 | High | Async I/O, connection lifecycle, error handling |
| 4 - Modes | ~6 | Medium-High | Command mode parallel commands are tricky |
| 5 - Events | ~3 | Medium | Concurrent queue + filtered wait |
| 6 - Polish | ~5 | Medium | APL wrapper, JSON queries, documentation |
| **Total** | **~45** | | |
