namespace CongaSharp.Protocol;

public enum CompressionAlgorithm : byte
{
    None = 0,
    Deflate = 1,
    LZ4 = 2,
    Zstd = 3
}
