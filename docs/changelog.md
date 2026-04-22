# Changelog

## Unreleased

### Breaking: `conga_send` ABI change — returns resolved handle name

**Issue:** `conga_send` did not return the auto-generated or resolved command/message handle, violating `DRC.Send` parity (Section A.26 of the Conga User Guide). Callers had no way to obtain the handle needed to `Wait` on a specific command.

**Resolution:** Extended `conga_send` with three new parameters:

| Parameter | Type | Description |
|-----------|------|-------------|
| `out_name` | `wchar_t*` | Caller-allocated buffer receiving the resolved handle name |
| `out_name_cap` | `int32_t` | Capacity of `out_name` in `wchar_t` units |
| `out_name_len` | `int32_t*` | Output: actual length written (excluding null terminator) |

**Behavior changes:**
- Sending with a base client/connection name (e.g. `"C1"`) now auto-generates a unique handle (`"C1.Auto00000000"`, `"C1.Auto00000001"`, ...) and returns it via `out_name`.
- Sending with an explicit dotted name (e.g. `"C1.MyCmd"`) returns the name as-is.
- Command-mode server-side `Send` is now rejected with `InvalidMode` (1010) — servers must use `conga_respond`/`conga_progress`.
- If `out_name` is `NULL`, the send proceeds without writing the name (backward-compatible for callers that don't need it).
- If `out_name_cap` is too small, returns `BufferTooSmall` (2001) *before* sending (send is not undoable).

**Affected files:**
- `src/CongaSharp/NativeExportsNetworking.cs` — rewritten `CongaSend`
- `src/CongaSharp/Core/ObjectRegistry.cs` — added `GenerateAutoName()`
- `tests/CongaSharp.Tests/NativeExports/NativeApi.cs` — updated test binding
- `apl/CongaSharp.apln` — updated `⎕NA` declaration and `Send`/`SendEx` bodies
- `tests/external/python/conga_api.py` — updated ctypes argtypes and `send()` method
- `docs/PRD.md` — updated `conga_send` C signature and semantics
