from __future__ import annotations

import ctypes
from dataclasses import dataclass
from pathlib import Path
from typing import Optional


SUCCESS = 0
WAIT_TIMEOUT = 1
BUFFER_TOO_SMALL = 2001


class CongaError(RuntimeError):
    def __init__(self, operation: str, code: int) -> None:
        super().__init__(f"{operation} failed with rc={code}")
        self.operation = operation
        self.code = code


@dataclass(frozen=True)
class WaitResult:
    object_name: str
    event_name: str
    event_code: int
    payload: bytes
    headers: bytes


def _build_u8_buffer(data: Optional[bytes]):
    if not data:
        return None, 0
    buff = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
    return buff, len(data)


class CongaApi:
    def __init__(self, dll_path: str) -> None:
        path = Path(dll_path)
        if not path.is_file():
            raise FileNotFoundError(f"DLL not found: {path}")

        self._dll = ctypes.WinDLL(str(path))
        self._bind_signatures()
        self.handle: Optional[ctypes.c_void_p] = None

    def _bind_signatures(self) -> None:
        dll = self._dll

        dll.conga_version.argtypes = [ctypes.c_void_p, ctypes.c_int]
        dll.conga_version.restype = ctypes.c_int

        dll.conga_init.argtypes = [ctypes.POINTER(ctypes.c_void_p)]
        dll.conga_init.restype = ctypes.c_int

        dll.conga_shutdown.argtypes = [ctypes.c_void_p]
        dll.conga_shutdown.restype = ctypes.c_int

        dll.conga_close.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p]
        dll.conga_close.restype = ctypes.c_int

        dll.conga_srv_create.argtypes = [
            ctypes.c_void_p,
            ctypes.c_wchar_p,
            ctypes.c_wchar_p,
            ctypes.c_int,
            ctypes.c_wchar_p,
            ctypes.c_int,
            ctypes.c_void_p,
            ctypes.c_int,
            ctypes.POINTER(ctypes.c_int),
        ]
        dll.conga_srv_create.restype = ctypes.c_int

        dll.conga_srv_start.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p]
        dll.conga_srv_start.restype = ctypes.c_int

        dll.conga_clt_create.argtypes = [
            ctypes.c_void_p,
            ctypes.c_wchar_p,
            ctypes.c_wchar_p,
            ctypes.c_int,
            ctypes.c_wchar_p,
            ctypes.c_int,
            ctypes.c_void_p,
            ctypes.c_int,
            ctypes.POINTER(ctypes.c_int),
        ]
        dll.conga_clt_create.restype = ctypes.c_int

        dll.conga_clt_connect.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
        dll.conga_clt_connect.restype = ctypes.c_int

        dll.conga_wait.argtypes = [
            ctypes.c_void_p,
            ctypes.c_wchar_p,
            ctypes.c_int,
            ctypes.c_void_p,
            ctypes.c_int,
            ctypes.c_void_p,
            ctypes.c_int,
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_ubyte),
            ctypes.c_int,
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_ubyte),
            ctypes.c_int,
            ctypes.POINTER(ctypes.c_int),
        ]
        dll.conga_wait.restype = ctypes.c_int

        dll.conga_send.argtypes = [
            ctypes.c_void_p,
            ctypes.c_wchar_p,
            ctypes.POINTER(ctypes.c_ubyte),
            ctypes.c_int,
            ctypes.POINTER(ctypes.c_ubyte),
            ctypes.c_int,
            ctypes.c_int,
            ctypes.c_int,
            ctypes.c_int,
            ctypes.c_int,       # track: 0=global queue, 1=per-command mailbox
            ctypes.c_wchar_p,
            ctypes.c_int,
            ctypes.POINTER(ctypes.c_int),
        ]
        dll.conga_send.restype = ctypes.c_int

        dll.conga_respond.argtypes = [
            ctypes.c_void_p,
            ctypes.c_wchar_p,
            ctypes.POINTER(ctypes.c_ubyte),
            ctypes.c_int,
            ctypes.c_int,
            ctypes.c_int,
        ]
        dll.conga_respond.restype = ctypes.c_int

        dll.conga_progress.argtypes = [
            ctypes.c_void_p,
            ctypes.c_wchar_p,
            ctypes.POINTER(ctypes.c_ubyte),
            ctypes.c_int,
            ctypes.c_int,
            ctypes.c_int,
        ]
        dll.conga_progress.restype = ctypes.c_int

        dll.conga_setprop.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_wchar_p]
        dll.conga_setprop.restype = ctypes.c_int

        dll.conga_getprop.argtypes = [
            ctypes.c_void_p,
            ctypes.c_wchar_p,
            ctypes.c_wchar_p,
            ctypes.c_void_p,
            ctypes.c_int,
            ctypes.POINTER(ctypes.c_int),
        ]
        dll.conga_getprop.restype = ctypes.c_int

    def _check(self, operation: str, rc: int) -> None:
        if rc != SUCCESS:
            raise CongaError(operation, rc)

    def version(self) -> str:
        buff = ctypes.create_unicode_buffer(256)
        rc = self._dll.conga_version(buff, 256)
        self._check("conga_version", rc)
        return buff.value

    def init(self) -> ctypes.c_void_p:
        out = ctypes.c_void_p()
        rc = self._dll.conga_init(ctypes.byref(out))
        self._check("conga_init", rc)
        self.handle = out
        return out

    def shutdown(self) -> None:
        if self.handle is None:
            return
        rc = self._dll.conga_shutdown(self.handle)
        self._check("conga_shutdown", rc)
        self.handle = None

    def close(self, name: str) -> None:
        rc = self._dll.conga_close(self._require_handle(), name)
        self._check("conga_close", rc)

    def create_server(self, name: str, addr: str, port: int, mode: str, buffer_size: int) -> str:
        out_name = ctypes.create_unicode_buffer(512)
        out_len = ctypes.c_int()
        rc = self._dll.conga_srv_create(
            self._require_handle(),
            name,
            addr,
            port,
            mode,
            buffer_size,
            out_name,
            512,
            ctypes.byref(out_len),
        )
        self._check("conga_srv_create", rc)
        return out_name.value

    def start_server(self, name: str) -> None:
        rc = self._dll.conga_srv_start(self._require_handle(), name)
        self._check("conga_srv_start", rc)

    def create_client(self, name: str, addr: str, port: int, mode: str, buffer_size: int) -> str:
        out_name = ctypes.create_unicode_buffer(512)
        out_len = ctypes.c_int()
        rc = self._dll.conga_clt_create(
            self._require_handle(),
            name,
            addr,
            port,
            mode,
            buffer_size,
            out_name,
            512,
            ctypes.byref(out_len),
        )
        self._check("conga_clt_create", rc)
        return out_name.value

    def connect_client(self, name: str, timeout_ms: int) -> None:
        rc = self._dll.conga_clt_connect(self._require_handle(), name, timeout_ms)
        self._check("conga_clt_connect", rc)

    def set_prop(self, obj: str, prop: str, json_value: str) -> None:
        rc = self._dll.conga_setprop(self._require_handle(), obj, prop, json_value)
        self._check("conga_setprop", rc)

    def get_prop(self, obj: str, prop: str, initial_cap: int = 256) -> str:
        cap = max(64, initial_cap)
        while True:
            out_json = ctypes.create_unicode_buffer(cap)
            out_len = ctypes.c_int()
            rc = self._dll.conga_getprop(
                self._require_handle(), obj, prop, out_json, cap, ctypes.byref(out_len)
            )
            if rc == BUFFER_TOO_SMALL and out_len.value > cap:
                cap = out_len.value
                continue
            self._check("conga_getprop", rc)
            return out_json.value

    def wait(
        self,
        name_filter: str,
        timeout_ms: int,
        out_obj_cap: int = 1024,
        out_event_cap: int = 1024,
        out_data_cap: int = 2 * 1024 * 1024,
        out_headers_cap: int = 64 * 1024,
    ) -> Optional[WaitResult]:
        out_obj = ctypes.create_unicode_buffer(out_obj_cap)
        out_event = ctypes.create_unicode_buffer(out_event_cap)
        out_event_code = ctypes.c_int()
        out_data = (ctypes.c_ubyte * out_data_cap)()
        out_data_len = ctypes.c_int()
        out_headers = (ctypes.c_ubyte * out_headers_cap)()
        out_headers_len = ctypes.c_int()

        rc = self._dll.conga_wait(
            self._require_handle(),
            name_filter,
            timeout_ms,
            out_obj,
            out_obj_cap,
            out_event,
            out_event_cap,
            ctypes.byref(out_event_code),
            out_data,
            out_data_cap,
            ctypes.byref(out_data_len),
            out_headers,
            out_headers_cap,
            ctypes.byref(out_headers_len),
        )

        if rc == WAIT_TIMEOUT:
            return None
        self._check("conga_wait", rc)

        data_count = min(out_data_cap, out_data_len.value)
        headers_count = min(out_headers_cap, out_headers_len.value)
        data = bytes(out_data[:data_count]) if data_count > 0 else b""
        headers = bytes(out_headers[:headers_count]) if headers_count > 0 else b""

        return WaitResult(
            object_name=out_obj.value,
            event_name=out_event.value,
            event_code=out_event_code.value,
            payload=data,
            headers=headers,
        )

    def send(
        self,
        name: str,
        data: bytes,
        headers: Optional[bytes] = None,
        close_flag: int = 0,
        compression: int = 0,
        compression_level: int = 0,
        track: int = 0,
    ) -> str:
        """Send data and return the resolved handle name.

        Args:
            track: 0=events go to global queue (default), 1=create per-command
                   mailbox for race-safe specific Wait (Command mode only).
        """
        data_buff, data_len = _build_u8_buffer(data)
        hdr_buff, hdr_len = _build_u8_buffer(headers)
        out_name = ctypes.create_unicode_buffer(256)
        out_name_len = ctypes.c_int(0)
        rc = self._dll.conga_send(
            self._require_handle(),
            name,
            data_buff,
            data_len,
            hdr_buff,
            hdr_len,
            close_flag,
            compression,
            compression_level,
            track,
            out_name,
            256,
            ctypes.byref(out_name_len),
        )
        self._check("conga_send", rc)
        return out_name.value

    def respond(self, name: str, data: bytes, compression: int = 0, compression_level: int = 0) -> None:
        data_buff, data_len = _build_u8_buffer(data)
        rc = self._dll.conga_respond(
            self._require_handle(), name, data_buff, data_len, compression, compression_level
        )
        self._check("conga_respond", rc)

    def progress(self, name: str, data: bytes, compression: int = 0, compression_level: int = 0) -> None:
        data_buff, data_len = _build_u8_buffer(data)
        rc = self._dll.conga_progress(
            self._require_handle(), name, data_buff, data_len, compression, compression_level
        )
        self._check("conga_progress", rc)

    def _require_handle(self) -> ctypes.c_void_p:
        if self.handle is None:
            raise RuntimeError("Conga handle not initialized. Call init() first.")
        return self.handle
