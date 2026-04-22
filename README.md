# Conga-Sharp

A reimplementation of Dyalog's Conga TCP/IP communication framework in C# 14 / .NET 10, compiled to a native Windows DLL via NativeAOT. Exposes a C-compatible API consumed by Dyalog APL via `⎕NA`.

## Features

- **5 connection modes**: Raw, Text, BlkRaw, BlkText, Command
- **Command mode RPC**: named commands with Progress and Respond
- **New wire protocol**: 52-byte header, CRC-32C integrity (two-stage), per-message compression (Deflate/LZ4/Zstd) with level control, user-defined headers
- **Three-phase lifecycle**: create → configure → start (eliminates race conditions)
- **Thread-safe**: concurrent APL threads supported
- **Single native DLL**: no .NET runtime installation required
- **JSON output**: Tree, Describe, Names, GetProp return JSON (parsed with `⎕JSON`)

## Quick Start

### Build

```bash
dotnet publish src/CongaSharp/CongaSharp.csproj -r win-x64 -c Release
```

Output: `src/CongaSharp/bin/Release/net10.0/win-x64/publish/congasharp.dll`

### Run Tests

```bash
dotnet test
```

### Use from Dyalog APL

```apl
⍝ Load the cover namespace
2 ⎕FIX 'file://path/to/apl/CongaSharp.apln'

⍝ Point to the DLL by editing CS.DllPath if needed

⍝ Bind ⎕NA declarations
CS.Bind

⍝ Initialise
h ← CS.Init ⍬

⍝ Start a Command-mode server on port 8080
sName ← CS.Srv h '' '' 8080 'Command' 16384

⍝ Wait for a connection
obj evt code data hdrs ← CS.Wait h '' 5000
⍝ → obj='S1.CON0001'  evt='Connect'  code=1

⍝ Wait for a command
obj evt code data hdrs ← CS.Wait h '' 5000
⍝ → obj='S1.CON0001.Echo'  evt='Receive'  code=2  data=payload

⍝ Send a response
CS.Respond h 'S1.CON0001.Echo' (⎕UCS 'Hello back')

⍝ Clean up
CS.Close h sName
CS.Shutdown h
```

### Client Side

```apl
CS.Bind
h ← CS.Init ⍬

⍝ Connect to server
cName ← CS.Clt h '' '127.0.0.1' 8080 'Command' 16384 5000

⍝ Send a command named 'Echo'
payload ← ⎕UCS 'Hello server'
CS.SendEx h (cName,'.Echo') payload ⍬ 0 0 0

⍝ Wait for response
obj evt code data hdrs ← CS.Wait h '' 5000

CS.Shutdown h
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

## API Reference

All functions return `int32` (0 = success). Structured output is JSON.

| Export | Purpose |
|--------|---------|
| `conga_init` | Create root instance |
| `conga_shutdown` | Destroy root |
| `conga_srv_create` | Create server (no I/O) |
| `conga_srv_start` | Start server (bind + listen) |
| `conga_clt_create` | Create client (no I/O) |
| `conga_clt_connect` | Connect client |
| `conga_wait` | Wait for event |
| `conga_send` | Send data (headers, close flag, compression, level) |
| `conga_respond` | Final command response (compression, level) |
| `conga_progress` | Interim command progress (compression, level) |
| `conga_close` | Close object |
| `conga_setprop` | Set property (JSON) |
| `conga_getprop` | Get property (JSON) |
| `conga_tree` | Object hierarchy (JSON) |
| `conga_describe` | Object properties (JSON) |
| `conga_names` | Child names (JSON) |
| `conga_version` | Library version |

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
