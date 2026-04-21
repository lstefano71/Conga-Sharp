namespace CongaSharp.Protocol;

using System.Buffers.Binary;
using System.Text;

/// <summary>
/// 52-byte wire protocol frame header.
/// Serialized in little-endian byte order.
/// </summary>
public struct FrameHeader
{
    public const int Size = 52;
    public const int CrcOffset = 48;
    public const byte CurrentVersion = 1;

    public byte Version;        // offset 0
    public MsgType MsgType;     // offset 1
    public ushort Flags;        // offset 2
    public uint Magic;          // offset 4
    public string CmdName;      // offset 8, 32 bytes UTF-8 null-padded
    public uint HeadersLen;     // offset 40
    public uint PayloadLen;     // offset 44
    public uint HeaderCrc;      // offset 48

    /// <summary>
    /// Writes the header to a 52-byte span. Does NOT compute HeaderCrc — caller must do that after.
    /// </summary>
    public readonly void WriteTo(Span<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes");

        buffer[0] = Version;
        buffer[1] = (byte)MsgType;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], Magic);

        // CmdName: 32 bytes UTF-8, null-padded
        buffer.Slice(8, 32).Clear();
        if (!string.IsNullOrEmpty(CmdName))
        {
            var cmdBytes = Encoding.UTF8.GetBytes(CmdName);
            var len = Math.Min(cmdBytes.Length, 31); // leave room for null
            cmdBytes.AsSpan(0, len).CopyTo(buffer.Slice(8, 32));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buffer[40..], HeadersLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[44..], PayloadLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[48..], HeaderCrc);
    }

    /// <summary>
    /// Reads a header from a 52-byte span.
    /// </summary>
    public static FrameHeader ReadFrom(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes");

        var header = new FrameHeader
        {
            Version = buffer[0],
            MsgType = (MsgType)buffer[1],
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..]),
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]),
            HeadersLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer[40..]),
            PayloadLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer[44..]),
            HeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer[48..])
        };

        // Read CmdName: find first null in the 32-byte region
        var cmdSpan = buffer.Slice(8, 32);
        var nullIdx = cmdSpan.IndexOf((byte)0);
        var cmdLen = nullIdx >= 0 ? nullIdx : 32;
        header.CmdName = cmdLen > 0 ? Encoding.UTF8.GetString(cmdSpan[..cmdLen]) : "";

        return header;
    }
}
