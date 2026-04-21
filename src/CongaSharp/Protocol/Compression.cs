namespace CongaSharp.Protocol;

using System.IO.Compression;
using K4os.Compression.LZ4;
using ZstdSharp;

/// <summary>
/// Per-message compression/decompression dispatcher.
/// </summary>
public static class Compression
{
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
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data);
        }
        return output.ToArray();
    }

    private static byte[] DecompressDeflate(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
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
        using var compressor = new Compressor();
        return compressor.Wrap(data).ToArray();
    }

    private static byte[] DecompressZstd(ReadOnlySpan<byte> data)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(data).ToArray();
    }
}
