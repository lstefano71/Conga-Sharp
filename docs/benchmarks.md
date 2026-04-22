# Benchmarks

Current benchmark snapshot from:

```powershell
dotnet test tests\CongaSharp.Tests\CongaSharp.Tests.csproj --no-build --filter "FullyQualifiedName~CompressionBenchmarkSummary" --logger "console;verbosity=detailed"
```

Run timestamp: `2026-04-22T11:24:22.090+02:00`  
Test: `CongaSharp.Tests.Integration.FullIntegrationTests.CompressionBenchmarkSummary`  
Warmup iterations: `3`  
Measured iterations: `12` (reported as p50/p95)

## Results

```text
Compression  |     Size | Pattern       | Thrpt p50 | Thrpt p95 | Msg/s p50 |  Time p50 |  Time p95 |  Ratio
-----------------------------------------------------------------------------------------------------------------
None         |     1 KB | Random        |     21,4 |     14,6 |  21929,8 |      0,0 ms |      0,1 ms | 1,000
None         |     1 KB | Compressible  |     21,4 |     17,1 |  21881,8 |      0,0 ms |      0,1 ms | 1,000
None         |   100 KB | Random        |    186,7 |    139,3 |   1911,7 |      0,5 ms |      0,7 ms | 1,000
None         |   100 KB | Compressible  |    191,8 |     24,6 |   1964,3 |      0,5 ms |      4,0 ms | 1,000
None         |     1 MB | Random        |    208,1 |    178,5 |    208,1 |      4,8 ms |      5,6 ms | 1,000
None         |     1 MB | Compressible  |    204,5 |    172,9 |    204,5 |      4,9 ms |      5,8 ms | 1,000
None         |    10 MB | Random        |    184,3 |    145,5 |     18,4 |     54,3 ms |     68,7 ms | 1,000
None         |    10 MB | Compressible  |    163,3 |    135,7 |     16,3 |     61,2 ms |     73,7 ms | 1,000
Deflate      |     1 KB | Random        |     11,3 |     10,3 |  11587,5 |      0,1 ms |      0,1 ms | 1,058
Deflate      |     1 KB | Compressible  |     22,1 |     15,4 |  22624,4 |      0,0 ms |      0,1 ms | 0,042
Deflate      |   100 KB | Random        |     49,7 |     43,3 |    508,4 |      2,0 ms |      2,3 ms | 1,054
Deflate      |   100 KB | Compressible  |    356,5 |    322,6 |   3651,0 |      0,3 ms |      0,3 ms | 0,011
Deflate      |     1 MB | Random        |     37,3 |     33,9 |     37,3 |     26,8 ms |     29,5 ms | 1,055
Deflate      |     1 MB | Compressible  |    453,5 |    236,6 |    453,5 |      2,2 ms |      4,2 ms | 0,011
Deflate      |    10 MB | Random        |     41,3 |     38,1 |      4,1 |    242,3 ms |    262,5 ms | 1,055
Deflate      |    10 MB | Compressible  |    491,8 |    304,1 |     49,2 |     20,3 ms |     32,9 ms | 0,011
LZ4          |     1 KB | Random        |     21,5 |     13,0 |  22026,4 |      0,0 ms |      0,1 ms | 1,001
LZ4          |     1 KB | Compressible  |     27,8 |     22,2 |  28490,0 |      0,0 ms |      0,0 ms | 0,046
LZ4          |   100 KB | Random        |    178,3 |     14,3 |   1825,5 |      0,5 ms |      6,8 ms | 1,000
LZ4          |   100 KB | Compressible  |    801,1 |    750,6 |   8203,4 |      0,1 ms |      0,1 ms | 0,004
LZ4          |     1 MB | Random        |    202,3 |    151,0 |    202,3 |      4,9 ms |      6,6 ms | 1,000
LZ4          |     1 MB | Compressible  |   1090,6 |    152,3 |   1090,6 |      0,9 ms |      6,6 ms | 0,004
LZ4          |    10 MB | Random        |    146,4 |    109,2 |     14,6 |     68,3 ms |     91,6 ms | 1,000
LZ4          |    10 MB | Compressible  |   1966,2 |   1189,3 |    196,6 |      5,1 ms |      8,4 ms | 0,004
Zstd         |     1 KB | Random        |      8,9 |      0,5 |   9082,7 |      0,1 ms |      2,0 ms | 1,010
Zstd         |     1 KB | Compressible  |     13,1 |     12,1 |  13369,0 |      0,1 ms |      0,1 ms | 0,047
Zstd         |   100 KB | Random        |    123,9 |     15,4 |   1268,4 |      0,8 ms |      6,4 ms | 1,000
Zstd         |   100 KB | Compressible  |    464,1 |    138,3 |   4752,9 |      0,2 ms |      0,7 ms | 0,000
Zstd         |     1 MB | Random        |    114,3 |     50,2 |    114,3 |      8,7 ms |     19,9 ms | 1,000
Zstd         |     1 MB | Compressible  |    364,6 |    305,8 |    364,6 |      2,7 ms |      3,3 ms | 0,000
Zstd         |    10 MB | Random        |    128,9 |     92,2 |     12,9 |     77,6 ms |    108,5 ms | 1,000
Zstd         |    10 MB | Compressible  |   1074,7 |    615,4 |    107,5 |      9,3 ms |     16,2 ms | 0,000
```

## Notes

- Numbers are environment-dependent and should be compared only against runs captured with the same machine, runtime, and load conditions.
- Decimal values use the current locale formatting from test output.
