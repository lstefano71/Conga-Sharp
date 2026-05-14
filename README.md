# Conga-Sharp

A reimplementation of Dyalog's Conga TCP/IP communication framework in C# 14 / .NET 10, compiled to a native Windows DLL via NativeAOT. Consumed by Dyalog APL via `⎕NA` using the [DWA Kit](https://github.com/dyalog/bridge-dwa) for direct workspace access — no buffer marshalling needed.

## Features

- **5 connection modes**: Raw, Text, BlkRaw, BlkText, Command
- **Command mode RPC**: named commands with Progress and Respond
- **Wire protocol**: 40-byte header, CRC-32C integrity (two-stage), per-message compression (Deflate/LZ4/Zstd) with level control, user-defined headers
- **Three-phase lifecycle**: create → configure → start (eliminates race conditions)
- **DWA Kit integration**: `conga_pp_wait` returns a 4-element nested APL vector directly into the workspace
- **Thread-safe**: concurrent APL threads supported
- **Two published DLLs**: `congasharp.dll` (C shim) + `congasharp_impl.dll` (NativeAOT)

## Quick Start

### Build & Publish

```powershell
dotnet publish src\CongaSharp\CongaSharp.csproj -c Release -r win-x64
```

Output: `src\CongaSharp\bin\Release\net10.0\win-x64\publish\congasharp.dll` (+ `congasharp_impl.dll`)

### Run C# Unit Tests

```powershell
dotnet test
```

### Use from Dyalog APL

```apl
⍝ Load the cover namespace
2 ⎕FIX 'file://path/to/apl/CongaSharp.apln'

⍝ Bind ⎕NA declarations (pass DLL path or '' for default)
CS.Bind 'path\to\publish\congasharp.dll'

⍝ Initialise
h ← CS.Init ⍬

⍝ Start a Command-mode server on port 8080
(rc sName) ← CS.Srv h '' '' 8080 'Command'

⍝ Wait for a connection (always rc=0, EventMode 1)
(rc obj evt data) ← CS.Wait h '.' 5000
⍝ → 0 'SRV00000000.CON00000000' 'Connect' ''

⍝ Wait for a command
(rc obj evt data) ← CS.Wait h '.' 5000
⍝ → 0 'SRV00000000.CON00000000.Cmd00000000' 'Receive' <bytes>

⍝ Send a response
CS.Respond h obj (1(220⌶)'Hello back')

⍝ Clean up
CS.Close h sName
CS.Shutdown h
```

### Client Side

```apl
CS.Bind 'path\to\publish\congasharp.dll'
h ← CS.Init ⍬

⍝ Connect to server
(rc cName) ← CS.Clt h '' '127.0.0.1' 8080 'Command'

⍝ Send a command (auto-generates handle)
sendData ← 1(220⌶)'Hello server'
(rc cmdHandle) ← CS.Send h cName sendData

⍝ Wait for response on specific handle
(rc obj evt data) ← CS.Wait h cmdHandle 5000

CS.Shutdown h
```

### Run APL Integration Tests

```apl
2 ⎕FIX 'file://path/to/apl/CongaSharp.apln'
2 ⎕FIX 'file://path/to/apl/CSTest.apln'
CSTest.RunAll 'path\to\publish\congasharp.dll'
```

## Connection Modes

| Mode | Framing | Use Case |
|------|---------|----------|
| **Raw** | Unframed | Arbitrary TCP servers (FTP, SMTP, etc.) |
| **Text** | Unframed | Line-oriented protocols (EOM detection) |
| **BlkRaw** | Framed | Binary block transfer between Conga-Sharp endpoints |
| **BlkText** | Framed | Text block transfer between Conga-Sharp endpoints |
| **Command** | Framed | RPC-style named commands with Progress/Respond |

**Unframed** modes (Raw, Text) send raw TCP bytes — no wire protocol header. Compatible with any TCP server.

**Framed** modes (BlkRaw, BlkText, Command) use the Conga-Sharp wire protocol (52-byte header, CRC, compression). Both sides must be Conga-Sharp.

## API Reference (DWA Exports)

APL consumers use the `conga_pp_*` exports via the CS cover namespace. All PP-suffixed exports use `LOCALP*` parameters for direct workspace access.

| Export | ⎕NA Pattern | Purpose |
|--------|-------------|---------|
| `conga_pp_init` | `P dll\|…` | Create root → handle |
| `conga_pp_shutdown` | `I4 dll\|… P` | Destroy root |
| `conga_pp_version` | `dll\|… >PP` | Version string |
| `conga_pp_srv_create` | `dll\|… P <PP <PP I4 <PP I4 >PP` | Create server → (rc name) |
| `conga_pp_srv_start` | `I4 dll\|… P <PP` | Start server |
| `conga_pp_clt_create` | `dll\|… P <PP <PP I4 <PP I4 >PP` | Create client → (rc name) |
| `conga_pp_clt_connect` | `I4 dll\|… P <PP I4` | Connect client |
| `conga_pp_wait` | `dll\|… P <PP I4 >PP` | Wait → (rc obj evt data) |
| `conga_pp_send` | `dll\|… P <PP <PP I4 I4 I4 >PP` | Send → (rc handle) |
| `conga_pp_respond` | `I4 dll\|… P <PP <PP I4` | Respond to command |
| `conga_pp_progress` | `I4 dll\|… P <PP <PP I4` | Progress event |
| `conga_pp_close` | `I4 dll\|… P <PP` | Close object |
| `conga_pp_exists` | `I4 dll\|… P <PP` | Check name exists |
| `conga_pp_names` | `dll\|… P <PP >PP` | Child names |
| `conga_pp_setprop` | `I4 dll\|… P <PP <PP <PP` | Set property |
| `conga_pp_getprop` | `I4 dll\|… P <PP <PP >PP` | Get property |

### Event semantics (EventMode 1)

Wait always returns rc=0. The event tuple `(rc objName eventType data)` carries all information:

| Event | objName | eventType | data |
|-------|---------|-----------|------|
| Timeout | `'.'` | `'Timeout'` | `100` |
| Connect | `'SRV0.CON0'` | `'Connect'` | `''` |
| Receive | `'SRV0.CON0'` | `'Receive'` | byte vector |
| Closed | `'SRV0.CON0'` | `'Closed'` | `1119` |

See [docs/PRD.md](docs/PRD.md) for full API specification and [docs/implementation-plan.md](docs/implementation-plan.md) for architecture details.

## Benchmarks

Current benchmark snapshot and reproduction command: [docs/benchmarks.md](docs/benchmarks.md).

External C-API benchmark artifacts (Python consumer + published DLL):
- Markdown summary: [docs/benchmarks-external.md](docs/benchmarks-external.md)
- Machine-readable JSON: [docs/external-api-benchmarks.json](docs/external-api-benchmarks.json)
- Functional scenario results: [docs/external-api-functional.json](docs/external-api-functional.json)

Native Python socket baseline artifacts (no framing, no compression):
- Markdown summary: `docs/benchmarks-native-socket-baseline.md`
- Machine-readable JSON: `docs/native-socket-baseline.json`

## External C-API Harness (Python)

The repository includes an external consumer harness that calls the published NativeAOT DLL via `ctypes`, using an orchestrator plus autonomous server/client worker processes:

- `tests/external/python/orchestrator.py`
- `tests/external/python/worker_server.py`
- `tests/external/python/worker_client.py`
- `tests/external/python/conga_api.py`

### Publish the DLL first (required)

```powershell
dotnet publish src\CongaSharp\CongaSharp.csproj -c Release -r win-x64
```

### Run functional all-mode coverage

```powershell
python tests\external\python\orchestrator.py `
  --dll src\CongaSharp\bin\Release\net10.0\win-x64\publish\congasharp.dll `
  functional `
  --functional-out docs\external-api-functional.json
```

### Run deep benchmark matrix (all modes, framed compression algorithms + levels)

```powershell
python tests\external\python\orchestrator.py `
  --dll src\CongaSharp\bin\Release\net10.0\win-x64\publish\congasharp.dll `
  benchmark `
  --depth deep `
  --full-compression `
  --benchmark-out docs\external-api-benchmarks.json `
  --benchmark-md docs\benchmarks-external.md
```

### Run both functional + benchmark

```powershell
python tests\external\python\orchestrator.py `
  --dll src\CongaSharp\bin\Release\net10.0\win-x64\publish\congasharp.dll `
  run-all `
  --depth deep `
  --full-compression `
  --functional-out docs\external-api-functional.json `
  --benchmark-out docs\external-api-benchmarks.json `
  --benchmark-md docs\benchmarks-external.md
```

### Run native Python socket baseline benchmark (no framing/compression)

```powershell
python tests\external\python\native_socket_baseline.py `
  --depth deep `
  --json-out docs\native-socket-baseline.json `
  --md-out docs\benchmarks-native-socket-baseline.md
```

## Events

| Code | Name | Description |
|------|------|-------------|
| 1 | Connect | New connection accepted |
| 2 | Receive | Data received |
| 3 | Block | Block received (not last) |
| 4 | BlockLast | Final block received |
| 5 | Progress | Command progress |
| 6 | Sent | Send completed (close_flag=3) |
| 7 | Closed | Object closed |
| 8 | Timeout | Wait timed out |
| 9 | Error | Error occurred |

## Requirements

- .NET 10 SDK (build only)
- Windows x64 (runtime)
- Dyalog APL 19.0+ (for `⎕NA` usage)

## Project Structure

```
src/CongaSharp/          # Main library (NativeAOT)
  Core/                  # Object model (Root, Server, Client, Connection, Command)
  Events/                # EventQueue, CongaEvent, EventType
  Modes/                 # IConnectionMode + 5 implementations
  Networking/            # SocketPipeline, AsyncFrameIO, DnsResolver
  Protocol/              # Wire protocol (FrameHeader, CRC, Compression, UserHeaders)
  Properties/            # PropertyStore, PropertyDefinitions
  Marshalling/           # String/Buffer/Handle marshalling
  Diagnostics/           # TraceLogger
  Errors/                # Error codes
  NativeExports.cs       # C API exports (query/property functions)
  NativeExportsNetworking.cs  # C API exports (networking functions)
tests/CongaSharp.Tests/  # xUnit tests
apl/CongaSharp.apln      # APL cover namespace
docs/PRD.md              # Product Requirements Document
docs/implementation-plan.md  # Implementation plan
docs/benchmarks.md       # Current benchmark snapshot
```

## License

See repository for license terms.
