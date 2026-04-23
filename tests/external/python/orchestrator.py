from __future__ import annotations

import argparse
import base64
import json
import math
import random
import statistics
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any


SCRIPT_DIR = Path(__file__).resolve().parent
SERVER_WORKER = SCRIPT_DIR / "worker_server.py"
CLIENT_WORKER = SCRIPT_DIR / "worker_client.py"

COMPRESSION_LEVELS: dict[int, list[int]] = {
    0: [0],            # None
    1: [1, 2, 3],      # Deflate
    2: [0, 6, 12],     # LZ4
    3: [1, 3, 10, 22], # Zstd
}
COMPRESSION_NAMES = {0: "None", 1: "Deflate", 2: "LZ4", 3: "Zstd"}


@dataclass(frozen=True)
class Scenario:
    mode: str
    payload_size: int
    pattern: str
    compression: int
    compression_level: int
    warmup: int
    measured: int


def _b64(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


def _json_load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _json_save(path: Path, data: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def _percentile(values: list[float], fraction: float) -> float:
    if not values:
        raise ValueError("No values for percentile")
    ordered = sorted(values)
    idx = max(0, min(len(ordered) - 1, math.ceil(fraction * len(ordered)) - 1))
    return ordered[idx]


def _throughput_mb_per_s(size_bytes: int, latency_ms: float) -> float:
    return (size_bytes / (1024.0 * 1024.0)) / (latency_ms / 1000.0)


def _message_rate_per_s(latency_ms: float) -> float:
    return 1000.0 / latency_ms


def _make_payload(size: int, pattern: str, text_mode: bool) -> bytes:
    if text_mode:
        if pattern == "Compressible":
            base = b"A" * 256
            data = (base * ((size // len(base)) + 1))[:size]
        else:
            alphabet = b"abcdefghijklmnopqrstuvwxyz0123456789"
            rng = random.Random(42 + size)
            data = bytes(alphabet[rng.randrange(len(alphabet))] for _ in range(size))
        return data + b"\r\n"

    if pattern == "Compressible":
        base = b"A" * 256
        return (base * ((size // len(base)) + 1))[:size]

    rng = random.Random(42 + size)
    return bytes(rng.getrandbits(8) for _ in range(size))


def _run_pair(
    python_exe: str,
    dll_path: Path,
    scenario: Scenario,
    timeout_ms: int,
) -> dict[str, Any]:
    with tempfile.TemporaryDirectory(prefix="conga_ext_") as tmp:
        tmp_dir = Path(tmp)
        server_cfg = tmp_dir / "server-config.json"
        client_cfg = tmp_dir / "client-config.json"
        ready_file = tmp_dir / "server-ready.json"
        server_result_file = tmp_dir / "server-result.json"
        client_result_file = tmp_dir / "client-result.json"

        text_mode = scenario.mode == "Text"
        request_payload = _make_payload(scenario.payload_size, scenario.pattern, text_mode)
        response_payload = _make_payload(scenario.payload_size, "Compressible", text_mode)
        progress_payload = b"progress-step\r\n" if text_mode else b"progress-step"

        server_config = {
            "dll_path": str(dll_path),
            "mode": scenario.mode,
            "buffer_size": max(16384, scenario.payload_size + 1024),
            "timeout_ms": timeout_ms,
            "warmup": scenario.warmup,
            "measured": scenario.measured,
            "request_payload_b64": _b64(request_payload),
            "response_payload_b64": _b64(response_payload),
            "progress_payload_b64": _b64(progress_payload) if scenario.mode == "Command" else "",
            "compression": scenario.compression,
            "compression_level": scenario.compression_level,
            "command_name": "BenchCmd",
        }
        _json_save(server_cfg, server_config)

        server_proc = subprocess.Popen(
            [
                python_exe,
                str(SERVER_WORKER),
                "--config",
                str(server_cfg),
                "--ready-file",
                str(ready_file),
                "--result-file",
                str(server_result_file),
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )

        deadline = time.time() + (timeout_ms / 1000.0) + 20.0
        while time.time() < deadline and not ready_file.exists():
            if server_proc.poll() is not None:
                break
            time.sleep(0.05)

        if not ready_file.exists():
            out, err = server_proc.communicate(timeout=5)
            raise RuntimeError(f"Server worker did not become ready. stdout={out} stderr={err}")

        ready_data = _json_load(ready_file)
        client_config = {
            "dll_path": str(dll_path),
            "mode": scenario.mode,
            "buffer_size": max(16384, scenario.payload_size + 1024),
            "timeout_ms": timeout_ms,
            "server_addr": "127.0.0.1",
            "server_port": int(ready_data["port"]),
            "warmup": scenario.warmup,
            "measured": scenario.measured,
            "request_payload_b64": _b64(request_payload),
            "response_payload_b64": _b64(response_payload),
            "progress_payload_b64": _b64(progress_payload) if scenario.mode == "Command" else "",
            "compression": scenario.compression,
            "compression_level": scenario.compression_level,
            "command_name": "BenchCmd",
        }
        _json_save(client_cfg, client_config)

        client_proc = subprocess.Popen(
            [
                python_exe,
                str(CLIENT_WORKER),
                "--config",
                str(client_cfg),
                "--result-file",
                str(client_result_file),
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )

        client_out, client_err = client_proc.communicate(timeout=max(30, timeout_ms // 1000 + 30))
        server_out, server_err = server_proc.communicate(timeout=max(30, timeout_ms // 1000 + 30))

        if client_proc.returncode != 0:
            raise RuntimeError(f"Client worker failed: stdout={client_out} stderr={client_err}")
        if server_proc.returncode != 0:
            raise RuntimeError(f"Server worker failed: stdout={server_out} stderr={server_err}")

        server_result = _json_load(server_result_file)
        client_result = _json_load(client_result_file)

        if not server_result.get("ok", False):
            raise RuntimeError(f"Server result error: {server_result}")
        if not client_result.get("ok", False):
            raise RuntimeError(f"Client result error: {client_result}")

        latencies = [float(v) for v in client_result["latencies_ms"]]
        p50 = _percentile(latencies, 0.50)
        p95 = _percentile(latencies, 0.95)
        return {
            "mode": scenario.mode,
            "payload_size": scenario.payload_size,
            "pattern": scenario.pattern,
            "compression": scenario.compression,
            "compression_name": COMPRESSION_NAMES[scenario.compression],
            "compression_level": scenario.compression_level,
            "warmup_iterations": scenario.warmup,
            "measured_iterations": scenario.measured,
            "latency_p50_ms": p50,
            "latency_p95_ms": p95,
            "throughput_p50_mb_s": _throughput_mb_per_s(len(request_payload), p50),
            "throughput_p95_mb_s": _throughput_mb_per_s(len(request_payload), p95),
            "messages_p50_s": _message_rate_per_s(p50),
            "messages_p95_s": _message_rate_per_s(p95),
            "client_name": client_result.get("client_name"),
            "server_name": server_result.get("server_name"),
            "progress_events": client_result.get("progress_events", 0),
        }


def _functional_scenarios() -> list[Scenario]:
    return [
        Scenario("Raw", 4096, "Random", 0, 0, 0, 1),
        Scenario("Text", 2048, "Compressible", 0, 0, 0, 1),
        Scenario("BlkRaw", 64 * 1024, "Random", 2, 6, 0, 1),
        Scenario("BlkText", 64 * 1024, "Compressible", 3, 3, 0, 1),
        Scenario("Command", 4096, "Compressible", 1, 2, 0, 1),
    ]


def _benchmark_scenarios(depth: str, full_compression: bool) -> list[Scenario]:
    if depth == "deep":
        sizes = [1024, 1024 * 100, 1024 * 1024]
        warmup, measured = 5, 30
    elif depth == "balanced":
        sizes = [1024, 1024 * 100]
        warmup, measured = 5, 30
    else:
        sizes = [1024, 1024 * 16]
        warmup, measured = 5, 30

    modes = ["Raw", "Text", "BlkRaw", "BlkText", "Command"]
    patterns = ["Random", "Compressible"]
    scenarios: list[Scenario] = []

    for mode in modes:
        is_framed = mode in {"BlkRaw", "BlkText", "Command"}
        for size in sizes:
            for pattern in patterns:
                if not is_framed:
                    scenarios.append(Scenario(mode, size, pattern, 0, 0, warmup, measured))
                    continue

                levels_map = COMPRESSION_LEVELS if full_compression else {0: [0], 2: [6]}
                for compression, levels in levels_map.items():
                    for level in levels:
                        scenarios.append(Scenario(mode, size, pattern, compression, level, warmup, measured))
    return scenarios


def _render_markdown(results: list[dict[str, Any]], title: str) -> str:
    now = time.strftime("%Y-%m-%dT%H:%M:%S%z")
    lines = [
        f"# {title}",
        "",
        f"Run timestamp: `{now}`",
        "",
        "| Mode | Size | Pattern | Compression | Level | Thrpt p50 (MB/s) | Thrpt p95 (MB/s) | Msg/s p50 | Lat p50 (ms) | Lat p95 (ms) |",
        "|------|------|---------|-------------|-------|------------------:|------------------:|----------:|-------------:|-------------:|",
    ]
    for r in results:
        size_label = _format_size(int(r["payload_size"]))
        lines.append(
            "| "
            f"{r['mode']} | {size_label} | {r['pattern']} | {r['compression_name']} | {r['compression_level']} | "
            f"{r['throughput_p50_mb_s']:.1f} | {r['throughput_p95_mb_s']:.1f} | "
            f"{r['messages_p50_s']:.1f} | {r['latency_p50_ms']:.2f} | {r['latency_p95_ms']:.2f} |"
        )
    return "\n".join(lines) + "\n"


def _format_size(size: int) -> str:
    if size >= 1024 * 1024:
        return f"{size // (1024 * 1024)} MB"
    if size >= 1024:
        return f"{size // 1024} KB"
    return f"{size} B"


def _run_functional(args: argparse.Namespace) -> dict[str, Any]:
    scenarios = _functional_scenarios()
    results = []
    for scenario in scenarios:
        results.append(_run_pair(args.python, Path(args.dll), scenario, args.timeout_ms))
    summary = {"kind": "functional", "count": len(results), "results": results}
    if args.functional_out:
        _json_save(Path(args.functional_out), summary)
    return summary


def _run_benchmark(args: argparse.Namespace) -> dict[str, Any]:
    scenarios = _benchmark_scenarios(args.depth, args.full_compression)
    results = []
    for idx, scenario in enumerate(scenarios, start=1):
        print(
            f"[{idx}/{len(scenarios)}] mode={scenario.mode} size={scenario.payload_size} "
            f"pattern={scenario.pattern} compression={scenario.compression} level={scenario.compression_level}",
            flush=True,
        )
        results.append(_run_pair(args.python, Path(args.dll), scenario, args.timeout_ms))

    benchmark = {
        "kind": "benchmark",
        "depth": args.depth,
        "full_compression": args.full_compression,
        "scenario_count": len(results),
        "results": results,
        "aggregate_latency_p50_ms_median": statistics.median(r["latency_p50_ms"] for r in results),
    }
    if args.benchmark_out:
        _json_save(Path(args.benchmark_out), benchmark)
    if args.benchmark_md:
        md = _render_markdown(results, "External C-API Benchmarks (Python Harness)")
        Path(args.benchmark_md).write_text(md, encoding="utf-8")
    return benchmark


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="External C-API orchestrator for Conga-Sharp")
    parser.add_argument("--python", default=sys.executable)
    parser.add_argument("--dll", required=True, help="Path to published congasharp.dll")
    parser.add_argument("--timeout-ms", type=int, default=60000)

    sub = parser.add_subparsers(dest="command", required=True)

    p_func = sub.add_parser("functional", help="Run functional mode coverage")
    p_func.add_argument("--functional-out", default="")

    p_bench = sub.add_parser("benchmark", help="Run benchmark matrix")
    p_bench.add_argument("--depth", choices=["smoke", "balanced", "deep"], default="deep")
    p_bench.add_argument("--full-compression", action="store_true", default=True)
    p_bench.add_argument("--benchmark-out", default="")
    p_bench.add_argument("--benchmark-md", default="")

    p_all = sub.add_parser("run-all", help="Run functional and benchmark suite")
    p_all.add_argument("--depth", choices=["smoke", "balanced", "deep"], default="deep")
    p_all.add_argument("--full-compression", action="store_true", default=True)
    p_all.add_argument("--functional-out", default="")
    p_all.add_argument("--benchmark-out", default="")
    p_all.add_argument("--benchmark-md", default="")
    return parser


def main() -> int:
    parser = _build_parser()
    args = parser.parse_args()

    dll_path = Path(args.dll)
    if not dll_path.is_file():
        raise FileNotFoundError(f"DLL not found: {dll_path}")

    if args.command == "functional":
        out = _run_functional(args)
    elif args.command == "benchmark":
        out = _run_benchmark(args)
    else:
        functional = _run_functional(args)
        benchmark = _run_benchmark(args)
        out = {"functional": functional, "benchmark": benchmark}

    print(json.dumps(out, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
