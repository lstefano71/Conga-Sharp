namespace CongaSharp.Protocol;

using CongaSharp.Buffers;

using K4os.Compression.LZ4;

using System.Buffers;
using System.IO.Compression;

using ZstdSharp;

/// <summary>
/// Per-message compression/decompression dispatcher.
/// </summary>
public static class Compression
{
  [ThreadStatic]
  private static Compressor? t_compressor;

  [ThreadStatic]
  private static Decompressor? t_decompressor;

  /// <summary>
  /// Overload accepting <c>ReadOnlyMemory&lt;byte&gt;</c> to avoid a copy when
  /// <paramref name="algo"/> is <see cref="CompressionAlgorithm.None"/>.
  /// Returns the same memory slice (zero copy) for None; otherwise compresses via Span.
  /// </summary>
  public static ReadOnlyMemory<byte> Compress(CompressionAlgorithm algo, ReadOnlyMemory<byte> data)
  {
    return Compress(algo, data, 0);
  }

  /// <summary>
  /// Overload accepting <c>ReadOnlyMemory&lt;byte&gt;</c> with compression level.
  /// Returns the same memory slice (zero copy) for None; otherwise compresses via Span.
  /// </summary>
  public static ReadOnlyMemory<byte> Compress(CompressionAlgorithm algo, ReadOnlyMemory<byte> data, int level)
  {
    if (data.Length == 0 || algo == CompressionAlgorithm.None)
      return data;
    return Compress(algo, data.Span, level);
  }

  /// <summary>
  /// Overload accepting <c>byte[]</c> to avoid a copy when <paramref name="algo"/> is <see cref="CompressionAlgorithm.None"/>.
  /// </summary>
  public static byte[] Compress(CompressionAlgorithm algo, byte[] data)
  {
    return Compress(algo, data, 0);
  }

  /// <summary>
  /// Overload accepting <c>byte[]</c> with compression level.
  /// </summary>
  public static byte[] Compress(CompressionAlgorithm algo, byte[] data, int level)
  {
    if (data.Length == 0 || algo == CompressionAlgorithm.None)
      return data;
    return Compress(algo, data.AsSpan(), level);
  }

  public static byte[] Compress(CompressionAlgorithm algo, ReadOnlySpan<byte> data)
  {
    return Compress(algo, data, 0);
  }

  public static byte[] Compress(CompressionAlgorithm algo, ReadOnlySpan<byte> data, int level)
  {
    if (data.IsEmpty || algo == CompressionAlgorithm.None)
      return data.ToArray();

    return algo switch {
      CompressionAlgorithm.Deflate => CompressDeflate(data, level),
      CompressionAlgorithm.LZ4 => CompressLZ4(data, level),
      CompressionAlgorithm.Zstd => CompressZstd(data, level),
      _ => throw new ArgumentOutOfRangeException(nameof(algo))
    };
  }

  /// <summary>
  /// Overload accepting <c>byte[]</c> to avoid a copy when <paramref name="algo"/> is <see cref="CompressionAlgorithm.None"/>.
  /// </summary>
  public static byte[] Decompress(CompressionAlgorithm algo, byte[] data, int maxDecompressedSize = int.MaxValue)
  {
    if (data.Length == 0 || algo == CompressionAlgorithm.None)
      return data; // Return same reference, zero copy
    return Decompress(algo, data.AsSpan(), maxDecompressedSize);
  }

  public static byte[] Decompress(CompressionAlgorithm algo, ReadOnlySpan<byte> data, int maxDecompressedSize = int.MaxValue)
  {
    if (data.IsEmpty || algo == CompressionAlgorithm.None)
      return data.ToArray();

    return algo switch {
      CompressionAlgorithm.Deflate => DecompressDeflate(data),
      CompressionAlgorithm.LZ4 => DecompressLZ4(data),
      CompressionAlgorithm.Zstd => DecompressZstd(data),
      _ => throw new ArgumentOutOfRangeException(nameof(algo))
    };
  }

  /// <summary>
  /// Decompresses data into a pooled buffer.
  /// For <see cref="CompressionAlgorithm.None"/>: if <paramref name="sourceOwner"/> is provided,
  /// transfers ownership (zero-copy); otherwise rents a new buffer and copies.
  /// For compressed algorithms: decompresses into a new pooled buffer.
  /// </summary>
  public static IMemoryOwner<byte> DecompressPooled(
      CompressionAlgorithm algo,
      ReadOnlySpan<byte> data,
      IMemoryOwner<byte>? sourceOwner = null)
  {
    if (data.IsEmpty) {
      sourceOwner?.Dispose();
      return PooledByteBuffer.Rent(0);
    }

    if (algo == CompressionAlgorithm.None) {
      // Zero-copy: transfer ownership of the source buffer if available
      if (sourceOwner != null)
        return sourceOwner;

      var buf = PooledByteBuffer.Rent(data.Length);
      data.CopyTo(buf.Memory.Span);
      return buf;
    }

    // Decompress via existing path (library allocates internally),
    // then copy into a pooled buffer.
    // NOTE: Dispose sourceOwner AFTER Decompress — data may be a span over its buffer.
    var decompressed = Decompress(algo, data);
    sourceOwner?.Dispose();
    var pooled = PooledByteBuffer.Rent(decompressed.Length);
    decompressed.CopyTo(pooled.Memory.Span);
    return pooled;
  }

  private static byte[] CompressDeflate(ReadOnlySpan<byte> data, int level)
  {
    var clevel = level switch {
      0 => System.IO.Compression.CompressionLevel.Fastest,
      1 => System.IO.Compression.CompressionLevel.Fastest,
      2 => System.IO.Compression.CompressionLevel.Optimal,
      3 => System.IO.Compression.CompressionLevel.SmallestSize,
      _ => System.IO.Compression.CompressionLevel.Optimal
    };
    using var output = new MemoryStream(data.Length);
    using (var deflate = new DeflateStream(output, clevel, leaveOpen: true)) {
      deflate.Write(data);
    }
    return output.GetBuffer().AsSpan(0, (int)output.Position).ToArray();
  }

  private static byte[] DecompressDeflate(ReadOnlySpan<byte> data)
  {
    var pool = ArrayPool<byte>.Shared;
    var inputBuf = pool.Rent(data.Length);
    try {
      data.CopyTo(inputBuf);
      using var input = new MemoryStream(inputBuf, 0, data.Length, writable: false);
      using var deflate = new DeflateStream(input, CompressionMode.Decompress);
      using var output = new MemoryStream(data.Length * 2);
      deflate.CopyTo(output);
      return output.GetBuffer().AsSpan(0, (int)output.Position).ToArray();
    } finally {
      pool.Return(inputBuf);
    }
  }

  private static byte[] CompressLZ4(ReadOnlySpan<byte> data, int level)
  {
    var lz4Level = level switch {
      0 => LZ4Level.L00_FAST,
      >= 1 and <= 2 => LZ4Level.L00_FAST,
      >= 3 and <= 5 => LZ4Level.L03_HC,
      >= 6 and <= 8 => LZ4Level.L06_HC,
      >= 9 and <= 11 => LZ4Level.L09_HC,
      >= 12 => LZ4Level.L12_MAX,
      _ => LZ4Level.L00_FAST
    };
    return LZ4Pickler.Pickle(data, lz4Level);
  }

  private static byte[] DecompressLZ4(ReadOnlySpan<byte> data)
  {
    return LZ4Pickler.Unpickle(data);
  }

  private static byte[] CompressZstd(ReadOnlySpan<byte> data, int level)
  {
    var zstdLevel = level > 0 ? level : Compressor.DefaultCompressionLevel;
    var compressor = t_compressor;
    if (compressor == null || compressor.Level != zstdLevel) {
      compressor?.Dispose();
      compressor = new Compressor(zstdLevel);
      t_compressor = compressor;
    }
    return compressor.Wrap(data).ToArray();
  }

  private static byte[] DecompressZstd(ReadOnlySpan<byte> data)
  {
    var decompressor = t_decompressor ??= new Decompressor();
    return decompressor.Unwrap(data).ToArray();
  }
}
