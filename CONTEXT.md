# Conga-Sharp — Domain Context

## What This Is

Conga-Sharp is a C# 14 / .NET 10 NativeAOT reimplementation of a **subset** of Dyalog's Conga TCP/IP framework. It compiles to a native DLL consumed by Dyalog APL via `⎕NA`.

## Ubiquitous Language

| Term | Definition |
|------|-----------|
| **Root** | Top-level Conga instance. Owns the object registry and event queue. Named `.` in the API. |
| **Server** | A listening TCP socket. Auto-named `SRVnnnnnnnnn` (zero-padded monotonic counter). |
| **Client** | An outbound TCP connection. Auto-named `CLTnnnnnnnnn`. |
| **Connection** | An accepted inbound connection on a server. Auto-named `CONnnnnnnnnn`. Child of a server. |
| **Command** | A named request/response exchange on a connection (Command mode only). Auto-named `Autonnnnnnnnn`. |
| **Object name** | Dot-separated path: `SRV00000000.CON00000000.Auto00000000`. Case-insensitive. |
| **Three-phase lifecycle** | Create → configure (setprop) → start/connect. No I/O until explicitly started. |
| **Framed mode** | BlkRaw, BlkText, Command — uses Conga-Sharp wire protocol. Both endpoints must be Conga-Sharp. |
| **Unframed mode** | Raw, Text — raw TCP bytes, no wire protocol. For interop with arbitrary TCP peers. |
| **EventMode** | Property on root controlling how errors/timeouts surface in `conga_wait`. 0 = error codes returned as rc; 1 = everything is an event with rc=0. |

## Resolved Decisions

### D1: Auto-name format matches real Conga
- **Decision**: Use `SRV00000000`, `CLT00000000`, `CON00000000` format (prefix + 8 zero-padded digits).
- **Rationale**: Minimizes migration friction for APL code that pattern-matches on Conga object name prefixes. The counter is monotonic per category — only the string format matters.
- **Impact**: ObjectRegistry.cs name generators, PRD §6.2, APL cover namespace, all tests.

### D2: Close immediately frees the name (deliberate deviation)
- **Decision**: `conga_close` removes the object from the registry immediately. The name becomes reusable at once.
- **Rationale**: Real Conga's deferred cleanup is an implementation artifact, not a semantic contract. No well-written APL code depends on a name being *unavailable* after close. The important invariant — you can't create a name that's actively in use — is already enforced.
- **Deviation from real Conga**: Real Conga transitions to `SocketClosed` and the name lingers until GC. Conga-Sharp removes instantly. Document this in PRD.

### D3: EventMode is always 1; use real Conga numeric codes
- **Decision**: Conga-Sharp always behaves as EventMode 1. `conga_wait` returns rc=0 for all events, with the event tuple carrying the real code. No EventMode switch — attempting to set EventMode to 0 returns an error or is silently ignored.
- **Numeric codes match real Conga**: Timeout → `0 obj "Timeout" 100`. Closed → `0 obj "Closed" 1119`. Receive → `0 obj "Receive" data`.
- **Impact**: Drop error code 1 (WaitTimeout). Refactor `conga_wait` to always return rc=0 for dequeued events. Update PRD §11.1 (remove code 1), §10.1 (EventMode fixed at 1). Update APL cover.
- **Deviation from real Conga**: Real Conga supports EventMode 0 (rc carries the error) and EventMode 1 (rc=0, event tuple carries the code). Conga-Sharp only supports mode 1.

### D4: Enforce command name uniqueness while pending
- **Decision**: `conga_send` in Command mode with a command name that is already pending (awaiting response) returns error 1008 (`NameInUse` / `ERR_INVALID_OBJECT`). Once the response is consumed, the name is freed and reusable.
- **Rationale**: Two concurrent commands sharing a name makes response routing ambiguous. The mailbox system already assumes unique active command names — this makes the invariant explicit at the API boundary.
- **Impact**: Add error code 1008 to ErrorCodes.cs. Add collision check in `conga_send` Command mode path. Update PRD §11.1.

### D5: Add `conga_exists` API
- **Decision**: Add `conga_exists(handle, name)` → returns 0 if object exists, 1002 if not found.
- **Rationale**: Trivial to implement (one registry lookup), useful for guard-before-send and double-create prevention. Avoids the overhead of `conga_names` + search.
- **Impact**: New export in NativeExports.cs, update PRD §7, update APL cover namespace.

### D6: Four-state object model (Created, Started, Error, Closed)
- **Decision**: Keep the simplified state model but add `Error` as a 4th state. Objects transition: Created → Started → Error or Closed. Error captures peer disconnect / socket failure before explicit close.
- **Rationale**: Most APL code reacts to events, not state queries. The 3 lifecycle states + Error cover all observable behavior. Internal pipeline states (Sending, Receiving, Processing) are not exposed.
- **Impact**: Add `Error` to ObjectState enum. Update state transitions in ConnectionObject/ServerObject/ClientObject. `conga_getprop` for `State` returns one of `"Created"`, `"Started"`, `"Error"`, `"Closed"`. Update PRD §6.

### D7: Connection counter is per-server (deliberate deviation)
- **Decision**: Each server maintains its own monotonic connection counter. `SRV00000000.CON00000000` and `SRV00000001.CON00000000` are both valid — the full dotted path is always unique.
- **Rationale**: A global counter adds shared mutable state across servers for no functional benefit. The full qualified name is the identity, not the leaf name.
- **Deviation from real Conga**: Real Conga uses a single global counter across all servers on the same root.

### D8: Peer disconnect produces Closed event, not Error
- **Decision**: When the remote side disconnects (clean TCP close or EOF), deliver a `Closed` event (type 7) with code 1119. Reserve `Error` event (type 9) for genuine failures (CRC mismatch, protocol violation, unexpected exceptions).
- **Rationale**: Matches real Conga EventMode 1 behavior. APL code distinguishes "other side is done" (Closed) from "something broke" (Error) — conflating them forces defensive coding.
- **Impact**: SocketPipeline EOF/disconnect handling, event emission logic. Audit existing Error event usage to split Closed vs Error correctly.

### D9: Remove error code 1 (WaitTimeout)
- **Decision**: Delete error code 1 from ErrorCodes.cs and PRD §11.1. With EventMode always 1, `conga_wait` returns rc=0 for all events including timeout. Code 100 appears inside the event tuple, not as rc.
- **Impact**: ErrorCodes.cs, PRD §11.1, any code returning WaitTimeout, APL cover.

### D10: Transition to DWA Kit calling convention
- **Decision**: Replace the current C-ABI exports (`[UnmanagedCallersOnly]` with `int` return, `char*`/`byte*` buffers) with DWA exports (`[DwaExport]` with `Localp` parameters). Complex results (e.g., `conga_wait`) return nested APL arrays directly via `>PP`.
- **Root handle stays as `nint`**: Passed as an integer type in `⎕NA` (like DuckDB/SQLite bridges). It's an opaque token, not an APL value.
- **Send/Respond data**: For now, byte data comes as type 83 vectors (from `220⌶`). Future: transition to `<Z` wire format.
- **Rationale**: Eliminates the buffer-capacity-length dance, avoids copy overhead from `⎕NA` marshalling, and allows `conga_wait` to return the 4-element event tuple `(rc)(objName)(eventType)(data_or_code)` natively — matching real Conga's EventMode 1 result shape.
- **Impact**: Every NativeExports function, StringMarshaller (removed), CongaSharp.csproj (add DWA Kit references), APL cover (simplified), PRD §7 (new signatures).

### D11: Keep three-phase lifecycle, hide in APL cover
- **Decision**: Keep `create` → `setprop` → `start/connect` as separate DLL exports. The APL cover namespace presents familiar single-call `Srv` and `Clt` functions that internally do create → setprop → start.
- **Rationale**: Three-phase prevents the "socket goes live before all options are set" race. It's a deliberate improvement over real Conga's single-call model where everything is crammed in. DWA doesn't change this calculus.
- **Impact**: DLL exports stay separate (srv_create, srv_start, clt_create, clt_connect). APL cover combines them into convenience functions.

### D12: Errors as I4 return codes, not APL exceptions
- **Decision**: All DWA exports use `'I4 dll|pp_fn ...'` pattern. rc=0 = success (result `>PP` is valid), rc≠0 = error code. No `DwaException` for expected errors (1002, 1009, 100, etc.).
- **Exception**: `conga_wait` always returns rc=0 (per D3); even timeout/closed are events in the `>PP` result tuple.
- **Rationale**: Conga errors are normal control flow, not exceptional. Throwing APL errors would force `⎕TRAP`/`:Trap` everywhere. The `rc←fn args ⋄ :If rc≠0` idiom is standard Conga APL practice.
- **Impact**: All DWA exports return `int`, APL cover checks rc before using result pocket.

### D13: APL cover matches real Conga argument/result shapes
- **Decision**: The APL cover namespace (`CongaSharp.apln`) must match the argument shapes and result shapes of real Conga (`DRC.Srv`, `DRC.Clt`, `DRC.Wait`, `DRC.Send`, `DRC.Close`, etc.) as closely as possible. Internally it uses the three-phase lifecycle and DWA exports.
- **Rationale**: Minimizes migration friction — APL code written for real Conga should work with Conga-Sharp by changing a single `Init` call. Deviations from real Conga shapes are only allowed where Conga-Sharp's documented subset differs.
- **Testing**: Comprehensive APL test scripts using the RIDE skill, verified against real Conga behavior in live sessions when docs are ambiguous. Scope is limited to what Conga-Sharp supports (no HTTP, no SSL, no WebSocket).
- **Impact**: Complete rewrite of `apl/CongaSharp.apln`, new test scripts in `apl/`, reference tests against real Conga.
