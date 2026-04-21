namespace CongaSharp.Protocol;

using System.Buffers;
using System.IO.Compression;
using K4os.Compression.LZ4;
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
        if (data.Length == 0 || algo == CompressionAlgorithm.None)
            return data; // Return same memory slice, zero copy
        return Compress(algo, data.Span);
    }

    /// <summary>
    /// Overload accepting <c>byte[]</c> to avoid a copy when <paramref name="algo"/> is <see cref="CompressionAlgorithm.None"/>.
    /// </summary>
    public static byte[] Compress(CompressionAlgorithm algo, byte[] data)
    {
        if (data.Length == 0 || algo == CompressionAlgorithm.None)
            return data; // Return same reference, zero copy
        return Compress(algo, data.AsSpan());
    }

    public static byte[] Compress(CompressionAlgorithm algo, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || algo == CompressionAlgorithm.None)
            return data.ToArray();

        return algo switch
        {
            CompressionAlgorithm.Deflate => CompressDeflate(data),
            CompressionAlgorithm.LZ4 => CompressLZ4(data),
            CompressionAlgorithm.Zstd => CompressZstd(data),
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

        return algo switch
        {
            CompressionAlgorithm.Deflate => DecompressDeflate(data),
            CompressionAlgorithm.LZ4 => DecompressLZ4(data),
            CompressionAlgorithm.Zstd => DecompressZstd(data),
            _ => throw new ArgumentOutOfRangeException(nameof(algo))
        };
    }

    private static byte[] CompressDeflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length); // Pre-size to avoid growth reallocations
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data);
        }
        return output.GetBuffer().AsSpan(0, (int)output.Position).ToArray();
    }

    private static byte[] DecompressDeflate(ReadOnlySpan<byte> data)
    {
        var pool = ArrayPool<byte>.Shared;
        var inputBuf = pool.Rent(data.Length);
        try
        {
            data.CopyTo(inputBuf);
            using var input = new MemoryStream(inputBuf, 0, data.Length, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(data.Length * 2);
            deflate.CopyTo(output);
            return output.GetBuffer().AsSpan(0, (int)output.Position).ToArray();
        }
        finally
        {
            pool.Return(inputBuf);
        }
    }

    private static byte[] CompressLZ4(ReadOnlySpan<byte> data)
    {
        return LZ4Pickler.Pickle(data);
    }

    private static byte[] DecompressLZ4(ReadOnlySpan<byte> data)
    {
        return LZ4Pickler.Unpickle(data);
    }

    private static byte[] CompressZstd(ReadOnlySpan<byte> data)
    {
        var compressor = t_compressor ??= new Compressor();
        return compressor.Wrap(data).ToArray();
    }

    private static byte[] DecompressZstd(ReadOnlySpan<byte> data)
    {
        var decompressor = t_decompressor ??= new Decompressor();
        return decompressor.Unwrap(data).ToArray();
    }
}
