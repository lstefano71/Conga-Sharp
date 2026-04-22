from __future__ import annotations

import argparse
import json
import math
import random
import socket
import statistics
import threading
import time
from pathlib import Path
from typing import Any


def _json_save(path: Path, data: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def _format_size(size: int) -> str:
    if size >= 1024 * 1024:
        return f"{size // (1024 * 1024)} MB"
    if size >= 1024:
        return f"{size // 1024} KB"
    return f"{size} B"


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


def _make_payload(size: int, pattern: str) -> bytes:
    if pattern == "Compressible":
        base = b"A" * 256
        return (base * ((size // len(base)) + 1))[:size]
    rng = random.Random(42 + size)
    return bytes(rng.getrandbits(8) for _ in range(size))


def _read_exact(sock: socket.socket, total: int) -> bytes:
    out = bytearray()
    while len(out) < total:
        chunk = sock.recv(total - len(out))
        if not chunk:
            raise RuntimeError("Socket closed while reading")
        out.extend(chunk)
    return bytes(out)


def _server_thread(
    ready_event: threading.Event,
    stop_event: threading.Event,
    bind_port_box: list[int],
    request_size: int,
    response_payload: bytes,
    iterations: int,
    error_box: list[str],
) -> None:
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as srv:
            srv.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            srv.bind(("127.0.0.1", 0))
            srv.listen(1)
            bind_port_box.append(srv.getsockname()[1])
            ready_event.set()

            conn, _ = srv.accept()
            with conn:
                conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                for _ in range(iterations):
                    _ = _read_exact(conn, request_size)
                    conn.sendall(response_payload)
    except Exception as ex:
        error_box.append(str(ex))
    finally:
        stop_event.set()


def _scenario(
    payload_size: int,
    pattern: str,
    warmup: int,
    measured: int,
    timeout_s: float,
) -> dict[str, Any]:
    iterations = warmup + measured
    request_payload = _make_payload(payload_size, pattern)
    response_payload = _make_payload(payload_size, "Compressible")

    ready_event = threading.Event()
    stop_event = threading.Event()
    bind_port_box: list[int] = []
    error_box: list[str] = []

    t = threading.Thread(
        target=_server_thread,
        args=(
            ready_event,
            stop_event,
            bind_port_box,
            len(request_payload),
            response_payload,
            iterations,
            error_box,
        ),
        daemon=True,
    )
    t.start()

    if not ready_event.wait(timeout=timeout_s):
        raise RuntimeError("Baseline server did not become ready")

    port = bind_port_box[0]
    latencies_ms: list[float] = []

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as clt:
        clt.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        clt.settimeout(timeout_s)
        clt.connect(("127.0.0.1", port))

        for i in range(iterations):
            start = time.perf_counter()
            clt.sendall(request_payload)
            got = _read_exact(clt, len(response_payload))
            elapsed_ms = (time.perf_counter() - start) * 1000.0
            if got != response_payload:
                raise RuntimeError("Baseline client received unexpected payload")
            if i >= warmup:
                latencies_ms.append(elapsed_ms)

    if not stop_event.wait(timeout=timeout_s):
        raise RuntimeError("Baseline server did not finish")
    t.join(timeout=timeout_s)

    if error_box:
        raise RuntimeError(f"Baseline server error: {error_box[0]}")

    p50 = _percentile(latencies_ms, 0.50)
    p95 = _percentile(latencies_ms, 0.95)
    return {
        "mode": "NativeSocket",
        "payload_size": payload_size,
        "pattern": pattern,
        "compression": 0,
        "compression_name": "None",
        "compression_level": 0,
        "warmup_iterations": warmup,
        "measured_iterations": measured,
        "latency_p50_ms": p50,
        "latency_p95_ms": p95,
        "throughput_p50_mb_s": _throughput_mb_per_s(payload_size, p50),
        "throughput_p95_mb_s": _throughput_mb_per_s(payload_size, p95),
        "messages_p50_s": _message_rate_per_s(p50),
        "messages_p95_s": _message_rate_per_s(p95),
    }


def _matrix(depth: str) -> tuple[list[int], int, int]:
    if depth == "deep":
        return [1024, 1024 * 100, 1024 * 1024], 2, 8
    if depth == "balanced":
        return [1024, 1024 * 100], 1, 5
    return [1024, 1024 * 16], 1, 3


def _render_markdown(results: list[dict[str, Any]]) -> str:
    now = time.strftime("%Y-%m-%dT%H:%M:%S%z")
    lines = [
        "# Native Python Socket Baseline Benchmarks (No framing, no compression)",
        "",
        f"Run timestamp: `{now}`",
        "",
        "| Mode | Size | Pattern | Compression | Level | Thrpt p50 (MB/s) | Thrpt p95 (MB/s) | Msg/s p50 | Lat p50 (ms) | Lat p95 (ms) |",
        "|------|------|---------|-------------|-------|------------------:|------------------:|----------:|-------------:|-------------:|",
    ]
    for r in results:
        lines.append(
            "| "
            f"{r['mode']} | {_format_size(int(r['payload_size']))} | {r['pattern']} | {r['compression_name']} | {r['compression_level']} | "
            f"{r['throughput_p50_mb_s']:.1f} | {r['throughput_p95_mb_s']:.1f} | "
            f"{r['messages_p50_s']:.1f} | {r['latency_p50_ms']:.2f} | {r['latency_p95_ms']:.2f} |"
        )
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description="Native Python socket baseline benchmark")
    parser.add_argument("--depth", choices=["smoke", "balanced", "deep"], default="deep")
    parser.add_argument("--timeout-s", type=float, default=30.0)
    parser.add_argument("--json-out", default="")
    parser.add_argument("--md-out", default="")
    args = parser.parse_args()

    sizes, warmup, measured = _matrix(args.depth)
    patterns = ["Random", "Compressible"]
    results: list[dict[str, Any]] = []

    total = len(sizes) * len(patterns)
    idx = 0
    for size in sizes:
        for pattern in patterns:
            idx += 1
            print(f"[{idx}/{total}] baseline size={size} pattern={pattern}", flush=True)
            results.append(_scenario(size, pattern, warmup, measured, args.timeout_s))

    out = {
        "kind": "baseline-benchmark",
        "depth": args.depth,
        "scenario_count": len(results),
        "results": results,
        "aggregate_latency_p50_ms_median": statistics.median(r["latency_p50_ms"] for r in results),
    }

    if args.json_out:
        _json_save(Path(args.json_out), out)
    if args.md_out:
        Path(args.md_out).write_text(_render_markdown(results), encoding="utf-8")

    print(json.dumps(out, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
