# CongaSharp.ExternalBenchmark

C# harness that exercises `congasharp.dll` **exclusively through the C ABI** (P/Invoke), the same way the Python external tests do. Designed for profiling in Visual Studio — server and client run on separate threads in the same process, each with their own `conga_init` handle.

## Prerequisites

A published NativeAOT DLL:

```powershell
dotnet publish src\CongaSharp\CongaSharp.csproj -c Release -r win-x64
```

The DLL will be at `src\CongaSharp\bin\Release\net10.0\win-x64\publish\congasharp.dll`.

## Usage

```
CongaSharp.ExternalBenchmark --dll <path> <command> [options]
```

### Commands

| Command      | Description |
|--------------|-------------|
| `functional` | One iteration per mode — quick smoke test for correctness |
| `benchmark`  | Full matrix with warmup + measured iterations and latency stats |
| `run-all`    | Runs `functional` then `benchmark` |

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `--dll <path>` | *(required)* | Path to the published `congasharp.dll` |
| `--timeout-ms <ms>` | `60000` | Timeout per Wait/Connect operation |
| `--depth <level>` | `deep` | Scenario depth: `smoke`, `balanced`, or `deep` |
| `--full-compression` | `true` | Include all compression algorithm × level combos |
| `--benchmark-md <path>` | *(none)* | Write results as a markdown table to this file |
| `--benchmark-out <path>` | *(none)* | Write benchmark results as JSON |
| `--functional-out <path>` | *(none)* | Write functional results as JSON |

### Depth levels

| Depth | Payload sizes | Scenarios (full compression) |
|-------|--------------|------------------------------|
| `smoke` | 1 KB, 16 KB | ~140 |
| `balanced` | 1 KB, 100 KB | ~140 |
| `deep` | 1 KB, 100 KB, 1 MB | ~210 |

### Examples

```powershell
# Quick functional smoke test
dotnet run --project tests\external\csharp\CongaSharp.ExternalBenchmark -- ^
  --dll src\CongaSharp\bin\Release\net10.0\win-x64\publish\congasharp.dll ^
  functional

# Smoke benchmark with markdown output
dotnet run --project tests\external\csharp\CongaSharp.ExternalBenchmark -- ^
  --dll src\CongaSharp\bin\Release\net10.0\win-x64\publish\congasharp.dll ^
  benchmark --depth smoke --benchmark-md docs\benchmarks-external-csharp.md

# Full deep run with JSON export
dotnet run --project tests\external\csharp\CongaSharp.ExternalBenchmark -- ^
  --dll src\CongaSharp\bin\Release\net10.0\win-x64\publish\congasharp.dll ^
  run-all --depth deep --benchmark-md docs\benchmarks-external-csharp.md ^
  --benchmark-out docs\external-api-benchmarks-csharp.json
```

## Profiling in Visual Studio

1. Open `CongaSharp.sln`
2. Set **CongaSharp.ExternalBenchmark** as the startup project
3. In **Project Properties → Debug → Command line arguments**, enter:
   ```
   --dll <full-path-to-congasharp.dll> functional
   ```
4. Use **Debug → Performance Profiler** (Alt+F2) to run with CPU Usage, .NET Object Allocation, or other collectors
