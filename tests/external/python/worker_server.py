from __future__ import annotations

import argparse
import base64
import json
import sys
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


def _wait_for_event(api: CongaApi, name_filter: str, timeout_ms: int) -> WaitResult:
    evt = api.wait(name_filter, timeout_ms)
    if evt is None:
        raise RuntimeError(f"Timed out waiting for event on filter '{name_filter}'")
    return evt


def main() -> int:
    parser = argparse.ArgumentParser(description="Conga-Sharp external harness server worker")
    parser.add_argument("--config", required=True)
    parser.add_argument("--ready-file", required=True)
    parser.add_argument("--result-file", required=True)
    args = parser.parse_args()

    config_path = Path(args.config)
    ready_path = Path(args.ready_file)
    result_path = Path(args.result_file)

    api: CongaApi | None = None
    server_name = ""

    try:
        cfg = _load_config(config_path)
        mode = cfg["mode"]
        buffer_size = int(cfg.get("buffer_size", 16384))
        timeout_ms = int(cfg.get("timeout_ms", 10000))
        warmup = int(cfg.get("warmup", 0))
        measured = int(cfg.get("measured", 1))
        iterations = warmup + measured
        request_payload = _decode_b64(cfg["request_payload_b64"])
        response_payload = _decode_b64(cfg["response_payload_b64"])
        progress_payload = _decode_b64(cfg.get("progress_payload_b64", ""))
        compression = int(cfg.get("compression", 0))
        compression_level = int(cfg.get("compression_level", 0))

        api = CongaApi(cfg["dll_path"])
        api.init()
        server_name = api.create_server(name="", addr="", port=0, mode=mode, buffer_size=buffer_size)

        if mode == "Text":
            api.set_prop(server_name, "EOM", TEXT_EOM_JSON)

        api.start_server(server_name)
        local_port = int(api.get_prop(server_name, "LocalPort"))
        _write_json(ready_path, {"port": local_port, "server_name": server_name})

        # Connection establish event
        connect_evt = _wait_for_event(api, server_name, timeout_ms)
        if connect_evt.event_name != "Connect":
            raise RuntimeError(f"Expected Connect event, got '{connect_evt.event_name}'")
        connection_name = connect_evt.object_name

        for i in range(iterations):
            evt = _wait_for_event(api, server_name, timeout_ms)

            if mode == "Command":
                if evt.event_name != "Receive":
                    raise RuntimeError(f"Expected Command Receive, got '{evt.event_name}'")
                if not evt.object_name.startswith(connection_name + "."):
                    raise RuntimeError(f"Unexpected command object '{evt.object_name}'")
                if not _payload_matches(mode, request_payload, evt.payload):
                    raise RuntimeError("Server received unexpected command payload")

                if progress_payload:
                    api.progress(
                        evt.object_name,
                        progress_payload,
                        compression=compression,
                        compression_level=compression_level,
                    )
                api.respond(
                    evt.object_name,
                    response_payload,
                    compression=compression,
                    compression_level=compression_level,
                )
            else:
                if evt.event_name not in DATA_EVENTS:
                    raise RuntimeError(f"Expected data event, got '{evt.event_name}'")
                if evt.object_name != connection_name:
                    raise RuntimeError(f"Expected object '{connection_name}', got '{evt.object_name}'")
                if not _payload_matches(mode, request_payload, evt.payload):
                    raise RuntimeError("Server received unexpected payload")

                api.send(
                    connection_name,
                    response_payload,
                    headers=None,
                    close_flag=0,
                    compression=compression,
                    compression_level=compression_level,
                )

        result = {
            "ok": True,
            "mode": mode,
            "iterations": iterations,
            "connection_name": connection_name,
            "server_name": server_name,
            "port": local_port,
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
                if server_name:
                    api.close(server_name)
            except Exception:
                pass
            try:
                api.shutdown()
            except Exception:
                pass


if __name__ == "__main__":
    raise SystemExit(main())
