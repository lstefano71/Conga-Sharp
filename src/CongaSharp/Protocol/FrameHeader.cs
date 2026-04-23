namespace CongaSharp.Protocol;

using System.Buffers.Binary;

/// <summary>
/// 40-byte wire protocol frame header.
/// Serialized in little-endian byte order.
/// </summary>
public struct FrameHeader
{
    public const int Size = 40;
    public const int CrcOffset = 36;
    public const byte CurrentVersion = 1;

    public byte Version;          // offset 0
    public MsgType MsgType;       // offset 1
    public ushort Flags;          // offset 2
    public uint Magic;            // offset 4
    public Guid CorrelationId;    // offset 8, 16 bytes little-endian
    public uint HeadersLen;       // offset 24
    public uint PayloadLen;       // offset 28
    public uint UncompressedLen;  // offset 32: original payload size before compression (= PayloadLen when not compressed)
    public uint HeaderCrc;        // offset 36

    /// <summary>
    /// Writes the header to a 40-byte span. Does NOT compute HeaderCrc — caller must do that after.
    /// </summary>
    public readonly void WriteTo(Span<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes");

        buffer[0] = Version;
        buffer[1] = (byte)MsgType;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], Magic);

        // CorrelationId: 16 bytes little-endian
        CorrelationId.TryWriteBytes(buffer.Slice(8, 16), bigEndian: false, out _);

        BinaryPrimitives.WriteUInt32LittleEndian(buffer[24..], HeadersLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[28..], PayloadLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[32..], UncompressedLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[36..], HeaderCrc);
    }

    /// <summary>
    /// Reads a header from a 40-byte span.
    /// </summary>
    public static FrameHeader ReadFrom(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes");

        return new FrameHeader
        {
            Version = buffer[0],
            MsgType = (MsgType)buffer[1],
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..]),
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]),
            CorrelationId = new Guid(buffer.Slice(8, 16), bigEndian: false),
            HeadersLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer[24..]),
            PayloadLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer[28..]),
            UncompressedLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer[32..]),
            HeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer[36..])
        };
    }
}
