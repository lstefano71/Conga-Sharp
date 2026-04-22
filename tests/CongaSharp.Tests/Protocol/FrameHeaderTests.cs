using Xunit;

namespace CongaSharp.Tests.Protocol;

using CongaSharp.Protocol;

public class FrameHeaderTests
{
    [Fact]
    public void Size_Is56()
    {
        Assert.Equal(56, FrameHeader.Size);
    }

    [Fact]
    public void Roundtrip_AllFields()
    {
        var original = new FrameHeader
        {
            Version = 1,
            MsgType = MsgType.Respond,
            Flags = FrameFlags.Create(CompressionAlgorithm.LZ4, true, true),
            Magic = 0xDEADBEEF,
            CmdName = "MyCommand",
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
        Assert.Equal(original.CmdName, decoded.CmdName);
        Assert.Equal(original.HeadersLen, decoded.HeadersLen);
        Assert.Equal(original.PayloadLen, decoded.PayloadLen);
        Assert.Equal(original.UncompressedLen, decoded.UncompressedLen);
        Assert.Equal(original.HeaderCrc, decoded.HeaderCrc);
    }

    [Fact]
    public void Roundtrip_EmptyCmdName()
    {
        var original = new FrameHeader { Version = 1, MsgType = MsgType.Data, CmdName = "" };
        var buffer = new byte[FrameHeader.Size];
        original.WriteTo(buffer);
        var decoded = FrameHeader.ReadFrom(buffer);
        Assert.Equal("", decoded.CmdName);
    }

    [Fact]
    public void Roundtrip_NullCmdName()
    {
        var original = new FrameHeader { Version = 1, MsgType = MsgType.Data, CmdName = null! };
        var buffer = new byte[FrameHeader.Size];
        original.WriteTo(buffer);
        var decoded = FrameHeader.ReadFrom(buffer);
        Assert.Equal("", decoded.CmdName);
    }

    [Fact]
    public void CmdName_Truncated_At31Bytes()
    {
        var longName = new string('A', 50);
        var original = new FrameHeader { Version = 1, MsgType = MsgType.Data, CmdName = longName };
        var buffer = new byte[FrameHeader.Size];
        original.WriteTo(buffer);
        var decoded = FrameHeader.ReadFrom(buffer);
        Assert.Equal(31, decoded.CmdName.Length);
    }

    [Fact]
    public void CmdName_Unicode()
    {
        var original = new FrameHeader { Version = 1, MsgType = MsgType.Data, CmdName = "Ünïcödé" };
        var buffer = new byte[FrameHeader.Size];
        original.WriteTo(buffer);
        var decoded = FrameHeader.ReadFrom(buffer);
        Assert.Equal("Ünïcödé", decoded.CmdName);
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
    public void ByteLayout_UncompressedLen_AtOffset48()
    {
        var header = new FrameHeader { Version = 1, MsgType = MsgType.Data, UncompressedLen = 0x01020304u };
        var buf = new byte[FrameHeader.Size];
        header.WriteTo(buf);
        // Little-endian: 0x01020304 → bytes 04 03 02 01
        Assert.Equal(0x04, buf[48]);
        Assert.Equal(0x03, buf[49]);
        Assert.Equal(0x02, buf[50]);
        Assert.Equal(0x01, buf[51]);
    }

    [Fact]
    public void ByteLayout_HeaderCrc_AtOffset52()
    {
        var header = new FrameHeader { Version = 1, MsgType = MsgType.Data, HeaderCrc = 0xAABBCCDDu };
        var buf = new byte[FrameHeader.Size];
        header.WriteTo(buf);
        Assert.Equal(0xDD, buf[52]);
        Assert.Equal(0xCC, buf[53]);
        Assert.Equal(0xBB, buf[54]);
        Assert.Equal(0xAA, buf[55]);
    }

    [Fact]
    public void AllMsgTypes_Roundtrip()
    {
        foreach (var msgType in Enum.GetValues<MsgType>())
        {
            var header = new FrameHeader { Version = 1, MsgType = msgType, CmdName = "" };
            var buf = new byte[FrameHeader.Size];
            header.WriteTo(buf);
            var decoded = FrameHeader.ReadFrom(buf);
            Assert.Equal(msgType, decoded.MsgType);
        }
    }
}
