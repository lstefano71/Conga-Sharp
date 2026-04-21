# Copilot Instructions for Conga-Sharp

## What This Project Is

Conga-Sharp is a C# 14 / .NET 10 NativeAOT reimplementation of Dyalog's Conga TCP/IP framework. It compiles to a native Windows DLL (`congasharp.dll`) with C-callable exports, consumed by Dyalog APL via the `⎕NA` foreign function interface.

**Read these before making changes:**
- `docs/PRD.md` — Full API spec, wire protocol, events, error codes
- `docs/implementation-plan.md` — Project structure, phases, file responsibilities

## Key Architecture Constraints

### NativeAOT — This Shapes Everything
The DLL is compiled with `<PublishAot>true</PublishAot>`. This means:
- **No reflection-based serialization.** Use `System.Text.Json` source generators (`JsonSerializerContext`) for all JSON work.
- **No `dynamic`, no `Assembly.Load`.** Everything must be statically resolvable.
- **All exceptions must be caught at the export boundary.** An unhandled exception in an `[UnmanagedCallersOnly]` function crashes the host process (Dyalog APL).
- **Test the published DLL**, not just `dotnet run`. Trimming only happens on publish — AOT-specific failures won't appear in debug builds.

### C ABI via `[UnmanagedCallersOnly]`
Every public function in `NativeExports.cs` uses `[UnmanagedCallersOnly(EntryPoint = "conga_xxx")]`. The calling convention constraints:
- Return type: `int` (error code, 0 = success)
- Strings: `char*` (wchar_t on Windows, UTF-16LE)
- Byte arrays: `byte*` + `int` length
- Opaque handle: `nint` (pointer-sized)
- Output buffers: caller-allocated `char*`/`byte*` + capacity. If too small, return error `2001` and write required size to an `int*` out-param.

### Two Categories of Connection Modes
- **Unframed (Raw, Text):** Raw TCP bytes, no wire protocol. For interop with arbitrary TCP servers (FTP, SMTP, etc.).
- **Framed (BlkRaw, BlkText, Command):** Full Conga-Sharp wire protocol (52-byte header, CRC-32C, compression, user headers). Both endpoints must be Conga-Sharp.

Never add wire protocol framing to Raw or Text mode. `SocketPipeline` must support both read strategies.

### Three-Phase Object Lifecycle
Servers and clients follow: **create** (allocate, no I/O) → **setprop** (configure) → **start/connect** (go live). This eliminates race conditions between configuration and first network event. Never combine creation and socket opening into a single call.

### Property System
All property values are JSON strings through the C API (`conga_setprop`/`conga_getprop`). Even integers: `"2"`. Arrays: `"[1000,2000]"`. This unifies the ABI to a single function pair regardless of property type.

## Conventions

### Error Handling Pattern
```csharp
[UnmanagedCallersOnly(EntryPoint = "conga_xxx")]
public static int Xxx(/* params */)
{
    try
    {
        // ... implementation
        return ErrorCodes.Success; // 0
    }
    catch (Exception ex)
    {
        TraceLogger.LogError(ex);
        return ErrorCodes.MapException(ex);
    }
}
```

### Error Codes
- `0`: success
- `1–1999`: reused from original Conga where there's a 1:1 match (e.g., 100=timeout, 1002=invalid name, 1119=socket closed)
- `2000+`: Conga-Sharp-specific (2001=buffer too small, 2002=shutting down, 2003=CRC failure, etc.)

Full list in `docs/PRD.md` §11.

### Thread Safety
The library is called from concurrent APL threads. Use `ConcurrentDictionary` for registries, `ConcurrentQueue` + `SemaphoreSlim` for the event queue. Internally async (.NET socket APIs), externally synchronous blocking.

### Wire Protocol
Framed protocols are based on a 52-byte fixed header with two-stage CRC-32C validation (header CRC checked before allocating payload buffer). See `docs/PRD.md` §8 for the full spec. CRC is hardware-accelerated via `System.IO.Hashing.Crc32C`.

## Build & Test

Once the project is scaffolded (Phase 0):

```powershell
# Build
dotnet build src\CongaSharp\CongaSharp.csproj

# Run tests
dotnet test tests\CongaSharp.Tests\CongaSharp.Tests.csproj

# Run a single test
dotnet test tests\CongaSharp.Tests --filter "FullyQualifiedName~FrameHeaderTests.Roundtrip"

# Publish NativeAOT DLL (this is the real validation)
dotnet publish src\CongaSharp\CongaSharp.csproj -c Release -r win-x64

# Verify exports
dumpbin /exports src\CongaSharp\bin\Release\net10.0\win-x64\publish\congasharp.dll
```

### Note on `dumpbin`

`dumpbin` needs `D:\Program Files\Microsoft Visual Studio\2026\VC\Auxiliary\Build\vcvars64.bat`

## Reference Documentation

- `docs/reference/Conga_User_Guide.pdf.md` — Original Conga 3.6 reference (the system being reimplemented)
- `docs/reference/qNA.md` — Dyalog `⎕NA` FFI documentation (how APL calls into the DLL)

## Instructions

- New features need a test
- Bugs fixed need a non-regression test
- you can run Dyalog APL scripts with the skill dyalog-apl-runner