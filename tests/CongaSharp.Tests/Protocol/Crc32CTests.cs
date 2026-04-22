namespace CongaSharp.Tests.Protocol;

using System.Linq;
using CongaSharp.Protocol;
using Xunit;

public class Crc32CTests
{
    [Fact]
    public void EmptyData_ReturnsZero()
    {
        var crc = Crc32C.Compute(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0u, crc);
    }

    [Fact]
    public void KnownVector_123456789()
    {
        // CRC-32C of ASCII "123456789" = 0xE3069283
        var data = System.Text.Encoding.ASCII.GetBytes("123456789");
        var crc = Crc32C.Compute(data);
        Assert.Equal(0xE3069283u, crc);
    }

    [Fact]
    public void SameData_SameCrc()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        Assert.Equal(Crc32C.Compute(data), Crc32C.Compute(data));
    }

    [Fact]
    public void DifferentData_DifferentCrc()
    {
        var a = new byte[] { 1, 2, 3 };
        var b = new byte[] { 1, 2, 4 };
        Assert.NotEqual(Crc32C.Compute(a), Crc32C.Compute(b));
    }

    [Fact]
    public void ComputeHeaderCrc_UsesCrcOffsetBytes()
    {
        var header = new byte[FrameHeader.Size];
        header[0] = 1; // version
        var crc = Crc32C.ComputeHeaderCrc(header);
        Assert.NotEqual(0u, crc); // non-trivial data
    }

    [Fact]
    public void ComputeHeaderCrc_TooShort_Throws()
    {
        Assert.Throws<ArgumentException>(() => Crc32C.ComputeHeaderCrc(new byte[10]));
    }

    [Fact]
    public void ChainedCrc_MatchesSingleBuffer()
    {
        var part1 = new byte[] { 1, 2, 3, 4, 5 };
        var part2 = new byte[] { 6, 7, 8, 9, 10 };
        var combined = part1.Concat(part2).ToArray();

        var singleCrc = Crc32C.Compute(combined);
        var chainedCrc = Crc32C.Compute(part1, part2);
        Assert.Equal(singleCrc, chainedCrc);
    }

    [Fact]
    public void ChainedPayloadCrc_MatchesSingleBuffer()
    {
        var headers = new byte[] { 0x01, 0x02, 0x03 };
        var payload = new byte[1000];
        Random.Shared.NextBytes(payload);
        var combined = headers.Concat(payload).ToArray();

        var singleCrc = Crc32C.ComputePayloadCrc(combined);
        var chainedCrc = Crc32C.ComputePayloadCrc(headers, payload);
        Assert.Equal(singleCrc, chainedCrc);
    }
}
