from __future__ import annotations

import argparse
import base64
import json
import sys
import time
import traceback
from pathlib import Path
from typing import Any

from conga_api import CongaApi, WaitResult


TEXT_EOM_JSON = "[[13,10]]"
TEXT_EOM_BYTES = b"\r\n"
DATA_EVENTS = {"Receive", "Block", "BlockLast"}


def _decode_b64(value: str) -> bytes:
    return base64.b64decode(value.encode("ascii"))


def _load_config(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _write_json(path: Path, data: dict[str, Any]) -> None:
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def _payload_matches(mode: str, expected: bytes, received: bytes) -> bool:
    if mode == "Text":
        return received == expected or received == expected + TEXT_EOM_BYTES
    return received == expected


def _next_event(api: CongaApi, name_filter: str, timeout_ms: int) -> WaitResult:
    evt = api.wait(name_filter, timeout_ms)
    if evt is None:
        raise RuntimeError(f"Timed out waiting for event on filter '{name_filter}'")
    return evt


def main() -> int:
    parser = argparse.ArgumentParser(description="Conga-Sharp external harness client worker")
    parser.add_argument("--config", required=True)
    parser.add_argument("--result-file", required=True)
    args = parser.parse_args()

    config_path = Path(args.config)
    result_path = Path(args.result_file)
    api: CongaApi | None = None
    client_name = ""

    try:
        cfg = _load_config(config_path)
        mode = cfg["mode"]
        timeout_ms = int(cfg.get("timeout_ms", 10000))
        buffer_size = int(cfg.get("buffer_size", 16384))
        warmup = int(cfg.get("warmup", 0))
        measured = int(cfg.get("measured", 1))
        iterations = warmup + measured
        compression = int(cfg.get("compression", 0))
        compression_level = int(cfg.get("compression_level", 0))
        request_payload = _decode_b64(cfg["request_payload_b64"])
        response_payload = _decode_b64(cfg["response_payload_b64"])
        progress_payload = _decode_b64(cfg.get("progress_payload_b64", ""))
        command_name = cfg.get("command_name", "Ping")
        server_addr = cfg.get("server_addr", "127.0.0.1")
        server_port = int(cfg["server_port"])

        api = CongaApi(cfg["dll_path"])
        api.init()
        client_name = api.create_client(
            name="",
            addr=server_addr,
            port=server_port,
            mode=mode,
            buffer_size=buffer_size,
        )

        if mode == "Text":
            api.set_prop(client_name, "EOM", TEXT_EOM_JSON)

        api.connect_client(client_name, timeout_ms)

        latencies_ms: list[float] = []
        progress_events = 0

        for i in range(iterations):
            command_target = f"{client_name}.{command_name}" if mode == "Command" else client_name
            start = time.perf_counter()
            api.send(
                command_target,
                request_payload,
                headers=None,
                close_flag=0,
                compression=compression,
                compression_level=compression_level,
            )

            got_final = False
            while not got_final:
                evt = _next_event(api, client_name, timeout_ms)

                # Skip non-data events that are not relevant for assertion timing.
                if mode != "Command" and evt.event_name not in DATA_EVENTS:
                    continue

                if mode == "Command":
                    if evt.event_name == "Progress":
                        if progress_payload and not _payload_matches(mode, progress_payload, evt.payload):
                            raise RuntimeError("Client received unexpected progress payload")
                        progress_events += 1
                        continue

                    if evt.event_name != "Receive":
                        continue

                    if evt.object_name != command_target:
                        raise RuntimeError(f"Expected command object '{command_target}', got '{evt.object_name}'")
                    if not _payload_matches(mode, response_payload, evt.payload):
                        raise RuntimeError("Client received unexpected response payload")
                    got_final = True
                else:
                    if not _payload_matches(mode, response_payload, evt.payload):
                        raise RuntimeError("Client received unexpected response payload")
                    got_final = True

            elapsed_ms = (time.perf_counter() - start) * 1000.0
            if i >= warmup:
                latencies_ms.append(elapsed_ms)

        result = {
            "ok": True,
            "mode": mode,
            "client_name": client_name,
            "iterations": iterations,
            "measured_iterations": measured,
            "warmup_iterations": warmup,
            "latencies_ms": latencies_ms,
            "progress_events": progress_events,
            "payload_size": len(request_payload),
            "compression": compression,
            "compression_level": compression_level,
        }
        _write_json(result_path, result)
        return 0
    except Exception as ex:
        error = {
            "ok": False,
            "error": str(ex),
            "traceback": traceback.format_exc(),
        }
        try:
            _write_json(result_path, error)
        except Exception:
            pass
        print(json.dumps(error), file=sys.stderr)
        return 1
    finally:
        if api is not None:
            try:
                if client_name:
                    api.close(client_name)
            except Exception:
                pass
            try:
                api.shutdown()
            except Exception:
                pass


if __name__ == "__main__":
    raise SystemExit(main())
