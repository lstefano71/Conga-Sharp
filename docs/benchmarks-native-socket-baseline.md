# Native Python Socket Baseline Benchmarks (No framing, no compression)

Run timestamp: `2026-04-22T14:34:53+0200`

| Mode | Size | Pattern | Compression | Level | Thrpt p50 (MB/s) | Thrpt p95 (MB/s) | Msg/s p50 | Lat p50 (ms) | Lat p95 (ms) |
|------|------|---------|-------------|-------|------------------:|------------------:|----------:|-------------:|-------------:|
| NativeSocket | 1 KB | Random | None | 0 | 18.8 | 15.0 | 19230.8 | 0.05 | 0.07 |
| NativeSocket | 1 KB | Compressible | None | 0 | 18.4 | 13.0 | 18832.4 | 0.05 | 0.07 |
| NativeSocket | 100 KB | Random | None | 0 | 715.4 | 270.7 | 7326.0 | 0.14 | 0.36 |
| NativeSocket | 100 KB | Compressible | None | 0 | 731.5 | 704.6 | 7490.6 | 0.13 | 0.14 |
| NativeSocket | 1 MB | Random | None | 0 | 282.7 | 258.1 | 282.7 | 3.54 | 3.87 |
| NativeSocket | 1 MB | Compressible | None | 0 | 306.9 | 251.6 | 306.9 | 3.26 | 3.97 |
