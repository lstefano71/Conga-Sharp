# Conga-Sharp — Product Requirements Document

## 1. Executive Summary

Conga-Sharp is a reimplementation of Dyalog's Conga TCP/IP communication framework, written in C# 14 / .NET 10, compiled ahead-of-time (NativeAOT) to a native Windows DLL. It exposes a C-compatible API consumed by Dyalog APL via the `⎕NA` foreign function interface.

Unlike original Conga (written in C++), Conga-Sharp:
- Uses modern .NET for safety, async I/O, and maintainability
- Defines a **new wire protocol** (not compatible with Conga)
- Restricts payloads to **flat byte vectors** (no nested APL arrays)
- Returns structured data as **JSON strings** (parsed with `⎕JSON` on APL side)
- Includes **CRC validation**, **per-message compression**, and **user-defined headers** in the wire protocol from day one

## 2. Goals

| # | Goal |
|---|------|
| G1 | Provide a drop-in TCP communication library for Dyalog APL applications |
| G2 | Support Command, Text, Raw, BlkText, and BlkRaw connection modes |
| G3 | Expose a C ABI callable from Dyalog APL `⎕NA` with zero managed interop burden on the APL side |
| G4 | Compile to a single native DLL via NativeAOT — no .NET runtime installation required |
| G5 | Be thread-safe for concurrent APL threads (via `&` or `⎕NA&`) |
| G6 | Provide a modern, extensible wire protocol with integrity checking and compression |

## 3. Non-Goals (MVP)

| # | Non-Goal |
|---|----------|
| NG1 | HTTP mode (REST, WebSocket) |
| NG2 | TLS/SSL (secure sockets) |
| NG3 | Nested APL array payloads |
| NG4 | Wire compatibility with original Conga |
| NG5 | Cross-platform support (Linux, macOS) — Windows x64 only for MVP |
| NG6 | AllowEndPoints / DenyEndPoints IP filtering |
| NG7 | Multiple named roots (single root per handle for MVP) |

## 4. Target Users

- Dyalog APL developers building TCP client/server applications
- Existing Conga users who want a modernized, extensible transport layer
- Applications that need Command mode (RPC-style named commands with Progress/Respond)

## 5. Architecture Overview

```
┌──────────────────────────────────────────────────────────────┐
│                      Dyalog APL                              │
│                                                              │
│   ⎕NA declarations ──► congasharp.dll (C exports)            │
│   CS.Init / CS.Srv / CS.Clt / CS.Wait / CS.Send / ...       │
│   Payloads: ⎕DR 83 (serialize to byte vector)               │
│   Structured results: ⎕JSON (parse JSON strings)            │
└──────────────────────────┬───────────────────────────────────┘
                           │ C ABI (wchar_t*, uint8_t*, int32_t)
┌──────────────────────────▼───────────────────────────────────┐
│                    congasharp.dll                             │
│                  (NativeAOT, C# 14)                          │
│                                                              │
│  ┌─────────────┐  ┌──────────────┐  ┌─────────────────────┐ │
│  │ Native      │  │ Object Model │  │ Property System     │ │
│  │ Exports     │──│ Root/Srv/Clt │──│ JSON Get/Set        │ │
│  │ (C ABI)     │  │ Conn/Cmd     │  │ Tree/Describe/Names │ │
│  └─────────────┘  └──────┬───────┘  └─────────────────────┘ │
│                          │                                   │
│  ┌───────────────────────▼───────────────────────────────┐   │
│  │                   Event System                         │   │
│  │  ConcurrentQueue per root, Wait blocks with timeout    │   │
│  └───────────────────────┬───────────────────────────────┘   │
│                          │                                   │
│  ┌───────────────────────▼───────────────────────────────┐   │
│  │               Connection Modes                         │   │
│  │  Command │ Text │ Raw │ BlkText │ BlkRaw               │   │
│  └───────────────────────┬───────────────────────────────┘   │
│                          │                                   │
│  ┌───────────────────────▼───────────────────────────────┐   │
│  │                Wire Protocol                           │   │
│  │  52-byte header │ CRC-32C │ Compression │ User Headers │   │
│  └───────────────────────┬───────────────────────────────┘   │
│                          │                                   │
│  ┌───────────────────────▼───────────────────────────────┐   │
│  │              TCP Networking (.NET Socket)               │   │
│  │  Async Accept/Connect/Read/Write, Ephemeral Ports      │   │
│  └───────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────┘
```

## 6. Object Model

### 6.1 Object Types

| Type | Description | Parent | Created By |
|------|-------------|--------|------------|
| **Root** | Top-level container, holds all state | — | `conga_init` |
| **Server** | TCP listener | Root | `conga_srv_create` + `conga_srv_start` |
| **Client** | Outbound TCP connection | Root | `conga_clt_create` + `conga_clt_connect` |
| **Connection** | Accepted inbound connection | Server | Automatic on accept |
| **Command** | Named command (Command mode only) | Connection | `conga_send` with command name |

### 6.2 Naming Convention

Objects form a hierarchy:
- Server: `S1`
- Connection on S1: `S1.CON0001`
- Command on connection: `S1.CON0001.MyCmdName`
- Client: `C1`
- Command on client: `C1.MyCmdName`

### 6.3 Object Lifecycle (Three-Phase Pattern)

All servers and clients follow a three-phase lifecycle to eliminate race conditions:

```
Phase 1: CREATE    →  Allocates object in registry, no I/O
Phase 2: CONFIGURE →  SetProp calls to set EOM, KeepAlive, etc.
Phase 3: START     →  Opens socket, begins I/O
```

This ensures all configuration is applied before any network activity occurs.

## 7. API Specification

### 7.1 Conventions

- **Return value:** All functions return `int32_t` (0 = success, non-zero = error code)
- **Strings:** `wchar_t*` (UTF-16LE on Windows) — `<0T` input, `>0T[]` output in `⎕NA`
- **Byte arrays:** `uint8_t*` with explicit length — `<U1[]` input, `>U1[]` output in `⎕NA`
- **Handle:** `uintptr_t` (`P` type in `⎕NA`) — opaque pointer to root state
- **Output buffers:** Caller provides buffer + capacity. If too small, function returns error `2001` and writes required size to an `int32_t*` out-parameter
- **Structured output:** JSON strings for Tree, Describe, Names, GetProp

### 7.2 Function Reference

#### `conga_version`
Returns the library version string.
```c
int32_t conga_version(wchar_t* out_version, int32_t version_cap);
```
```apl
'I4 congasharp|conga_version >0T[] I4'
```

#### `conga_init`
Creates a new root and returns an opaque handle.
```c
int32_t conga_init(uintptr_t* out_handle);
```
```apl
'I4 congasharp|conga_init >P'
```
Returns: `rc handle` — rc in APL return value, handle as second element.

#### `conga_shutdown`
Destroys a root: closes all connections, unblocks pending waits (which return error 2002), frees all resources.
```c
int32_t conga_shutdown(uintptr_t handle);
```
```apl
'I4 congasharp|conga_shutdown P'
```

#### `conga_srv_create`
Allocates a server object without opening a socket. Returns the assigned name.
```c
int32_t conga_srv_create(
    uintptr_t  handle,
    wchar_t*   name,          // requested name (empty = auto-assign)
    wchar_t*   addr,          // bind address (empty = all interfaces)
    int32_t    port,          // port (0 = ephemeral)
    wchar_t*   mode,          // "Command", "Text", "Raw", "BlkText", "BlkRaw"
    int32_t    buffer_size,   // receive buffer size
    wchar_t*   out_name,      // assigned name
    int32_t    out_name_cap,
    int32_t*   out_name_len
);
```
```apl
'I4 congasharp|conga_srv_create P <0T <0T I4 <0T I4 >0T[] I4 >I4'
```

#### `conga_srv_start`
Starts a previously created server (binds and listens).
```c
int32_t conga_srv_start(uintptr_t handle, wchar_t* name);
```
```apl
'I4 congasharp|conga_srv_start P <0T'
```

#### `conga_clt_create`
Allocates a client object without connecting.
```c
int32_t conga_clt_create(
    uintptr_t  handle,
    wchar_t*   name,          // requested name
    wchar_t*   addr,          // remote address
    int32_t    port,          // remote port
    wchar_t*   mode,          // connection mode
    int32_t    buffer_size,
    wchar_t*   out_name,
    int32_t    out_name_cap,
    int32_t*   out_name_len
);
```
```apl
'I4 congasharp|conga_clt_create P <0T <0T I4 <0T I4 >0T[] I4 >I4'
```

#### `conga_clt_connect`
Connects a previously created client to the remote endpoint.
```c
int32_t conga_clt_connect(uintptr_t handle, wchar_t* name, int32_t timeout_ms);
```
```apl
'I4 congasharp|conga_clt_connect P <0T I4'
```

#### `conga_wait`
Blocks until an event occurs or timeout expires. Returns event details.
```c
int32_t conga_wait(
    uintptr_t  handle,
    wchar_t*   name,           // object to wait on (empty or "." = root)
    int32_t    timeout_ms,
    wchar_t*   out_obj,        // object that raised event
    int32_t    out_obj_cap,
    wchar_t*   out_event,      // event name string
    int32_t    out_event_cap,
    int32_t*   out_event_code, // numeric event code
    uint8_t*   out_data,       // payload bytes
    int32_t    out_data_cap,
    int32_t*   out_data_len,
    uint8_t*   out_headers,    // user headers bytes
    int32_t    out_headers_cap,
    int32_t*   out_headers_len
);
```
```apl
'I4 congasharp|conga_wait P <0T I4 >0T[] I4 >0T[] I4 >I4 >U1[] I4 >I4 >U1[] I4 >I4'
```

#### `conga_send`
Sends data on a connection or creates/sends a command.
```c
int32_t conga_send(
    uintptr_t  handle,
    wchar_t*   name,           // connection or command name
    uint8_t*   data,
    int32_t    data_len,
    uint8_t*   headers,        // user-defined headers (NULL if none)
    int32_t    headers_len,
    int32_t    close_flag      // 0=noop, 1=close conn, 2=close cmd, 3=Sent event
);
```
```apl
'I4 congasharp|conga_send P <0T <U1[] I4 <U1[] I4 I4'
```

#### `conga_respond`
Sends final response for a command (closes the command).
```c
int32_t conga_respond(uintptr_t handle, wchar_t* name, uint8_t* data, int32_t data_len);
```
```apl
'I4 congasharp|conga_respond P <0T <U1[] I4'
```

#### `conga_progress`
Sends an interim progress message for a command.
```c
int32_t conga_progress(uintptr_t handle, wchar_t* name, uint8_t* data, int32_t data_len);
```
```apl
'I4 congasharp|conga_progress P <0T <U1[] I4'
```

#### `conga_close`
Closes a specific object (server, client, connection, or command).
```c
int32_t conga_close(uintptr_t handle, wchar_t* name);
```
```apl
'I4 congasharp|conga_close P <0T'
```

#### `conga_setprop`
Sets a property value. Value is a JSON string.
```c
int32_t conga_setprop(uintptr_t handle, wchar_t* obj, wchar_t* prop, wchar_t* json_value);
```
```apl
'I4 congasharp|conga_setprop P <0T <0T <0T'
```

#### `conga_getprop`
Gets a property value as a JSON string.
```c
int32_t conga_getprop(
    uintptr_t  handle,
    wchar_t*   obj,
    wchar_t*   prop,
    wchar_t*   out_json,
    int32_t    out_json_cap,
    int32_t*   out_json_len
);
```
```apl
'I4 congasharp|conga_getprop P <0T <0T >0T[] I4 >I4'
```

#### `conga_tree`
Returns the object hierarchy as a JSON string.
```c
int32_t conga_tree(
    uintptr_t  handle,
    wchar_t*   obj,
    wchar_t*   out_json,
    int32_t    out_json_cap,
    int32_t*   out_json_len
);
```
```apl
'I4 congasharp|conga_tree P <0T >0T[] I4 >I4'
```

#### `conga_describe`
Returns all properties of an object as a JSON object.
```c
int32_t conga_describe(
    uintptr_t  handle,
    wchar_t*   obj,
    wchar_t*   out_json,
    int32_t    out_json_cap,
    int32_t*   out_json_len
);
```
```apl
'I4 congasharp|conga_describe P <0T >0T[] I4 >I4'
```

#### `conga_names`
Returns child names of an object as a JSON array of strings.
```c
int32_t conga_names(
    uintptr_t  handle,
    wchar_t*   obj,
    wchar_t*   out_json,
    int32_t    out_json_cap,
    int32_t*   out_json_len
);
```
```apl
'I4 congasharp|conga_names P <0T >0T[] I4 >I4'
```

## 8. Wire Protocol Specification

### 8.1 Overview

Conga-Sharp uses its own wire protocol, not compatible with original Conga. Every message on the wire consists of a **fixed header** followed by an optional **variable section**.

### 8.2 Fixed Header (52 bytes)

```
Offset  Size  Field         Description
──────  ────  ──────────    ──────────────────────────────────────────
 0       1    Version       Protocol version (1 for MVP)
 1       1    MsgType       Message type
 2       2    Flags         Bit field (compression, features)
 4       4    Magic         Magic number for stream validation
 8      32    CmdName       Command name (UTF-8, null-padded, unused if not Command mode)
40       4    HeadersLen    Length of user headers section in bytes
44       4    PayloadLen    Length of payload section in bytes
48       4    HeaderCRC     CRC-32C of bytes 0..47
```

**Total fixed header: 52 bytes.**

### 8.3 Variable Section

```
Offset          Size              Field         Description
──────          ────              ──────────    ──────────────────
52              HeadersLen        UserHeaders   Key-value pairs
52+HeadersLen   PayloadLen        Payload       Application data
52+H+P          4                 PayloadCRC    CRC-32C of UserHeaders + Payload
```

The PayloadCRC field is present only when the `HasPayloadCRC` flag is set.

### 8.4 MsgType Values

| Code | Name     | Description |
|------|----------|-------------|
| 0x01 | Data     | Regular send (all modes) |
| 0x02 | Respond  | Final command response |
| 0x03 | Progress | Interim command progress |
| 0x04 | Control  | Reserved for future control messages |

### 8.5 Flags Field (16 bits)

| Bits  | Name           | Description |
|-------|----------------|-------------|
| 0–2   | Compression    | 0=None, 1=Deflate, 2=LZ4, 3=Zstd, 4–7=Reserved |
| 3     | HasUserHeaders | 1 if UserHeaders section is present |
| 4     | HasPayloadCRC  | 1 if PayloadCRC is appended |
| 5–15  | Reserved       | Must be 0 |

### 8.6 CRC Validation Strategy

1. **Read 52 bytes** (fixed header)
2. **Validate HeaderCRC** (CRC-32C of bytes 0–47) — if invalid, close connection (corrupt stream)
3. **Check PayloadLen** — if exceeding buffer limits, reject before allocating
4. **Read variable section** (HeadersLen + PayloadLen + optional 4 bytes CRC)
5. **Validate PayloadCRC** if HasPayloadCRC flag set (CRC-32C of UserHeaders + Payload)

This two-stage CRC design prevents wasteful allocations on corrupt or malicious data.

### 8.7 User Headers Encoding

Length-prefixed string key-value pairs:
```
[uint16 key_len][key_bytes (UTF-8)][uint32 value_len][value_bytes]...
```
Repeated until HeadersLen bytes consumed. Keys are UTF-8 strings, values are opaque byte sequences.

### 8.8 Compression

Compression is **per-message**, indicated by the Flags compression bits:
- **0 = None** — payload is uncompressed
- **1 = Deflate** (zlib) — `System.IO.Compression.DeflateStream`
- **2 = LZ4** — via K4os.Compression.LZ4
- **3 = Zstd** — via ZstdSharp.Port
- **4–7 = Reserved**

The sender selects the algorithm; the receiver reads the Flags to determine how to decompress. No connection-level handshake — both sides must support all algorithms.

### 8.9 Mode-Specific Behavior

Modes fall into two categories based on whether the Conga-Sharp wire protocol is used:

#### Unframed Modes (no Conga-Sharp wire protocol)

These modes send and receive **raw TCP bytes** with no protocol header, CRC, compression, or user headers. They are designed for interoperating with arbitrary TCP servers and clients (FTP, SMTP, custom protocols, etc.) that do not speak the Conga-Sharp wire protocol.

| Mode | Wire Behavior |
|------|--------------|
| **Raw** | Bytes are sent and received as-is over TCP. Each `conga_send` writes bytes directly to the socket. Each TCP read produces a Receive event with whatever bytes arrived. No framing, no EOM detection. |
| **Text** | Bytes are sent as-is (like Raw). On receive, bytes are accumulated until an EOM (end-of-message) byte pattern is detected, then a Receive event is delivered containing everything up to and including the EOM. Partial matches across TCP reads are handled correctly. |

#### Framed Modes (Conga-Sharp wire protocol)

These modes use the full wire protocol described in sections 8.2–8.8 (52-byte header, CRC-32C, compression, user headers). **Both endpoints must be Conga-Sharp.** These modes are for application-to-application communication where you control both sides.

| Mode | Wire Behavior |
|------|--------------|
| **BlkRaw** | Each `conga_send` produces one wire frame. Each received frame produces a Block or BlockLast event. The wire protocol header provides length-based framing, CRC integrity, optional compression, and user headers. |
| **BlkText** | Same as BlkRaw, but received payload is treated as text. EOM detection may additionally apply within the framed payload. |
| **Command** | Full wire protocol with CmdName field in the header identifying the command. MsgType distinguishes Data/Respond/Progress. Supports parallel named commands per connection. |

#### Implications

- `SocketPipeline` must support two read strategies: **raw byte stream** (Raw/Text) and **frame-based** (BlkRaw/BlkText/Command).
- Compression, CRC, user headers, and the Magic number are only relevant to framed modes.
- `conga_send` in Raw/Text modes ignores the `headers` parameter and `close_flag` values 2 and 3 (Command-mode-specific). Only `close_flag=0` (noop) and `close_flag=1` (close connection) apply.

## 9. Event System

### 9.1 Event Types

| Code | Name      | Description | Data |
|------|-----------|-------------|------|
| 1    | Connect   | New inbound connection accepted | — |
| 2    | Receive   | Data received | payload bytes |
| 3    | Block     | Block of data received (not last) | payload bytes |
| 4    | BlockLast | Final block of data received | payload bytes |
| 5    | Progress  | Command progress message | payload bytes |
| 6    | Sent      | Send completed (close_flag=3) | — |
| 7    | Closed    | Connection/object closed | — |
| 8    | Timeout   | Wait timed out | — |
| 9    | Error     | Error on object | error info bytes |

### 9.2 Event Delivery

- Events are queued in a `ConcurrentQueue` per root
- `conga_wait` dequeues events, blocking with timeout
- Wait can target a specific object (filters events) or root (all events)
- When `conga_shutdown` is called, all pending waits unblock with error code `2002`

## 10. Properties

### 10.1 MVP Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| EventMode | int | 0 | 0=queued, 1=callback (future) |
| Protocol | string | "IPv4" | "IPv4" or "IPv6" |
| LocalAddr | array | — | `["addr", port]` (read-only after start) |
| PeerAddr | array | — | `["addr", port]` (read-only, connections) |
| KeepAlive | array | `[0,0]` | `[idle_ms, interval_ms]` |
| EOM | array | `[]` | End-of-message byte sequences, e.g. `[[13,10]]` |
| Magic | int | auto | Magic number for wire protocol validation |
| Pause | int | 0 | 1=pause event generation |
| BufferSize | int | 16384 | Receive buffer size |
| ConnectionOnly | int | 0 | 1=only Connect events (no data) |
| ReadyStrategy | string | "auto" | Connection readiness strategy |
| PropList | array | — | List of property names (read-only) |
| Trace | int | 0 | 0=off, 1=errors, 2=connections, 3=messages, 4=wire |
| TraceFile | string | "" | Path to trace log file |
| LocalPort | int | — | Actual bound port (read-only, useful for ephemeral) |
| TCPLookup | string | "auto" | DNS lookup behavior |

### 10.2 JSON Encoding for SetProp/GetProp

All property values are JSON-encoded strings:
```
conga_setprop(h, "S1", "KeepAlive", "[1000,2000]")
conga_setprop(h, "S1", "EOM", "[[13,10]]")
conga_setprop(h, "S1", "Protocol", "\"IPv4\"")
conga_setprop(h, "S1", "Trace", "2")
conga_getprop(h, "S1", "LocalAddr", buf, cap, &len)  → "[\"127.0.0.1\",5000]"
conga_getprop(h, ".", "PropList", buf, cap, &len)     → "[\"EventMode\",\"Protocol\",...]"
```

## 11. Error Codes

### 11.1 Reused Conga Codes

| Code | Name | Description |
|------|------|-------------|
| 0 | Success | Operation completed successfully |
| 1 | WaitTimeout | Wait timed out (not an error per se, returned as Timeout event) |
| 100 | Timeout | General timeout |
| 1002 | InvalidName | Object name not found or invalid |
| 1003 | InvalidMode | Unsupported connection mode |
| 1009 | NameInUse | Requested name already exists |
| 1010 | NotServer | Operation requires a server object |
| 1011 | NotClient | Operation requires a client object |
| 1119 | SocketClosed | Connection was closed |
| 1135 | BufferExceeded | Data exceeds buffer size |

### 11.2 Conga-Sharp Specific Codes (2000+)

| Code | Name | Description |
|------|------|-------------|
| 2001 | BufferTooSmall | Output buffer too small (required size in out_len) |
| 2002 | ShuttingDown | Root is shutting down |
| 2003 | CRCFailure | Header or payload CRC check failed |
| 2004 | CompressionError | Compression or decompression failed |
| 2005 | InvalidProperty | Unknown property name |
| 2006 | PropertyReadOnly | Cannot set a read-only property |
| 2007 | ObjectNotReady | Object not yet started/connected |
| 2008 | ObjectAlreadyStarted | Object has already been started |
| 2009 | InvalidHandle | Handle is null or invalid |
| 2010 | ProtocolError | Wire protocol violation |
| 2011 | InvalidJSON | Property value is not valid JSON |
| 2012 | ConnectFailed | TCP connection failed |
| 2013 | BindFailed | Server could not bind to address/port |

## 12. Future Roadmap (Post-MVP)

| Feature | Priority | Notes |
|---------|----------|-------|
| TLS/SSL (secure sockets) | High | Certificate management, SNI |
| HTTP mode (REST) | High | Request/response parsing |
| WebSocket mode | Medium | Upgrade from HTTP |
| Multiple named roots | Medium | Independent root isolation |
| AllowEndPoints/DenyEndPoints | Medium | IP filtering on servers |
| Nested array return (Tree) | Low | "New trick" to be specified |
| Linux/macOS support | Low | .NET AOT supports multi-RID |
| ARM64 Windows | Low | Easy to add with multi-RID build |
| Callback-based event delivery | Low | Alternative to polling |
| Connection-level compression negotiation | Low | Handshake instead of per-message |
