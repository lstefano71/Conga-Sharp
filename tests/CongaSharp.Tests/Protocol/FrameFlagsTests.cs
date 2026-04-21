using Xunit;

namespace CongaSharp.Tests.Protocol;

using CongaSharp.Protocol;

public class FrameFlagsTests
{
    [Fact]
    public void Create_NoFlags()
    {
        var flags = FrameFlags.Create(CompressionAlgorithm.None, false, false);
        Assert.Equal(0, flags);
    }

    [Fact]
    public void Create_AllFlags()
    {
        var flags = FrameFlags.Create(CompressionAlgorithm.Zstd, true, true);
        Assert.Equal(CompressionAlgorithm.Zstd, FrameFlags.GetCompression(flags));
        Assert.True(FrameFlags.GetHasUserHeaders(flags));
        Assert.True(FrameFlags.GetHasPayloadCrc(flags));
    }

    [Fact]
    public void Compression_Roundtrip_AllAlgorithms()
    {
        foreach (var algo in Enum.GetValues<CompressionAlgorithm>())
        {
            var flags = FrameFlags.Create(algo, false, false);
            Assert.Equal(algo, FrameFlags.GetCompression(flags));
        }
    }

    [Fact]
    public void HasUserHeaders_Independent()
    {
        var flags = FrameFlags.Create(CompressionAlgorithm.None, true, false);
        Assert.True(FrameFlags.GetHasUserHeaders(flags));
        Assert.False(FrameFlags.GetHasPayloadCrc(flags));
    }

    [Fact]
    public void HasPayloadCrc_Independent()
    {
        var flags = FrameFlags.Create(CompressionAlgorithm.None, false, true);
        Assert.False(FrameFlags.GetHasUserHeaders(flags));
        Assert.True(FrameFlags.GetHasPayloadCrc(flags));
    }
}
