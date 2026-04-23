using Xunit;

namespace CongaSharp.Tests.Protocol;

using CongaSharp.Protocol;

public class FrameHeaderTests
{
    [Fact]
    public void Size_Is40()
    {
        Assert.Equal(40, FrameHeader.Size);
    }

    [Fact]
    public void Roundtrip_AllFields()
    {
        var corrId = Guid.NewGuid();
        var original = new FrameHeader
        {
            Version = 1,
            MsgType = MsgType.Respond,
            Flags = FrameFlags.Create(CompressionAlgorithm.LZ4, true, true),
            Magic = 0xDEADBEEF,
            CorrelationId = corrId,
            HeadersLen = 128,
            PayloadLen = 2048,
            UncompressedLen = 4096,
            HeaderCrc = 0x12345678
        };

        var buffer = new byte[FrameHeader.Size];
        original.WriteTo(buffer);
        var decoded = FrameHeader.ReadFrom(buffer);

        Assert.Equal(original.Version, decoded.Version);
        Assert.Equal(original.MsgType, decoded.MsgType);
        Assert.Equal(original.Flags, decoded.Flags);
        Assert.Equal(original.Magic, decoded.Magic);
        Assert.Equal(original.CorrelationId, decoded.CorrelationId);
        Assert.Equal(original.HeadersLen, decoded.HeadersLen);
        Assert.Equal(original.PayloadLen, decoded.PayloadLen);
        Assert.Equal(original.UncompressedLen, decoded.UncompressedLen);
        Assert.Equal(original.HeaderCrc, decoded.HeaderCrc);
    }

    [Fact]
    public void Roundtrip_EmptyCorrelationId()
    {
        var original = new FrameHeader { Version = 1, MsgType = MsgType.Data, CorrelationId = Guid.Empty };
        var buffer = new byte[FrameHeader.Size];
        original.WriteTo(buffer);
        var decoded = FrameHeader.ReadFrom(buffer);
        Assert.Equal(Guid.Empty, decoded.CorrelationId);
    }

    [Fact]
    public void Roundtrip_RandomCorrelationId()
    {
        var corrId = Guid.NewGuid();
        var original = new FrameHeader { Version = 1, MsgType = MsgType.Data, CorrelationId = corrId };
        var buffer = new byte[FrameHeader.Size];
        original.WriteTo(buffer);
        var decoded = FrameHeader.ReadFrom(buffer);
        Assert.Equal(corrId, decoded.CorrelationId);
    }

    [Fact]
    public void WriteTo_BufferTooSmall_Throws()
    {
        var header = new FrameHeader { Version = 1 };
        Assert.Throws<ArgumentException>(() => header.WriteTo(new byte[10]));
    }

    [Fact]
    public void ReadFrom_BufferTooSmall_Throws()
    {
        Assert.Throws<ArgumentException>(() => FrameHeader.ReadFrom(new byte[10]));
    }

    [Fact]
    public void ByteLayout_Version_AtOffset0()
    {
        var header = new FrameHeader { Version = 42, MsgType = MsgType.Data };
        var buf = new byte[FrameHeader.Size];
        header.WriteTo(buf);
        Assert.Equal(42, buf[0]);
    }

    [Fact]
    public void ByteLayout_MsgType_AtOffset1()
    {
        var header = new FrameHeader { Version = 1, MsgType = MsgType.Progress };
        var buf = new byte[FrameHeader.Size];
        header.WriteTo(buf);
        Assert.Equal(0x03, buf[1]);
    }

    [Fact]
    public void ByteLayout_UncompressedLen_AtOffset32()
    {
        var header = new FrameHeader { Version = 1, MsgType = MsgType.Data, UncompressedLen = 0x01020304u };
        var buf = new byte[FrameHeader.Size];
        header.WriteTo(buf);
        // Little-endian: 0x01020304 → bytes 04 03 02 01
        Assert.Equal(0x04, buf[32]);
        Assert.Equal(0x03, buf[33]);
        Assert.Equal(0x02, buf[34]);
        Assert.Equal(0x01, buf[35]);
    }

    [Fact]
    public void ByteLayout_HeaderCrc_AtOffset36()
    {
        var header = new FrameHeader { Version = 1, MsgType = MsgType.Data, HeaderCrc = 0xAABBCCDDu };
        var buf = new byte[FrameHeader.Size];
        header.WriteTo(buf);
        Assert.Equal(0xDD, buf[36]);
        Assert.Equal(0xCC, buf[37]);
        Assert.Equal(0xBB, buf[38]);
        Assert.Equal(0xAA, buf[39]);
    }

    [Fact]
    public void AllMsgTypes_Roundtrip()
    {
        foreach (var msgType in Enum.GetValues<MsgType>())
        {
            var header = new FrameHeader { Version = 1, MsgType = msgType };
            var buf = new byte[FrameHeader.Size];
            header.WriteTo(buf);
            var decoded = FrameHeader.ReadFrom(buf);
            Assert.Equal(msgType, decoded.MsgType);
        }
    }
}
