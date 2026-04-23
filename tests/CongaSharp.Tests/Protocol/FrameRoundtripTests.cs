using Xunit;

namespace CongaSharp.Tests.Protocol;

using CongaSharp.Protocol;
using CongaSharp.Errors;

public class FrameRoundtripTests
{
    [Fact]
    public void SimpleData_Roundtrip()
    {
        using var ms = new MemoryStream();
        var payload = System.Text.Encoding.UTF8.GetBytes("Hello, World!");

        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, payload);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Equal(MsgType.Data, result.Header.MsgType);
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void EmptyPayload_Roundtrip()
    {
        using var ms = new MemoryStream();
        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, ReadOnlySpan<byte>.Empty);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Empty(result.Payload);
    }

    [Fact]
    public void CorrelationId_Roundtrip()
    {
        using var ms = new MemoryStream();
        var payload = new byte[] { 1, 2, 3 };
        var corrId = Guid.NewGuid();

        FrameWriter.WriteFrame(ms, MsgType.Respond, corrId, payload);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Equal(MsgType.Respond, result.Header.MsgType);
        Assert.Equal(corrId, result.Header.CorrelationId);
        Assert.Equal(payload, result.Payload);
    }

    [Theory]
    [InlineData(MsgType.Data)]
    [InlineData(MsgType.Respond)]
    [InlineData(MsgType.Progress)]
    [InlineData(MsgType.Control)]
    public void AllMsgTypes_Roundtrip(MsgType msgType)
    {
        using var ms = new MemoryStream();
        var payload = new byte[] { 42 };

        FrameWriter.WriteFrame(ms, msgType, Guid.Empty, payload);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Equal(msgType, result.Header.MsgType);
    }

    [Theory]
    [InlineData(CompressionAlgorithm.None)]
    [InlineData(CompressionAlgorithm.Deflate)]
    [InlineData(CompressionAlgorithm.LZ4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void AllCompression_Roundtrip(CompressionAlgorithm algo)
    {
        using var ms = new MemoryStream();
        var payload = System.Text.Encoding.UTF8.GetBytes("Compressible data " + new string('X', 500));

        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, payload, compression: algo);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success, $"Failed for {algo}: error {result.ErrorCode}");
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void UserHeaders_Roundtrip()
    {
        using var ms = new MemoryStream();
        var payload = new byte[] { 10, 20, 30 };
        var headers = new Dictionary<string, byte[]>
        {
            ["Key1"] = new byte[] { 1, 2, 3 },
            ["Key2"] = System.Text.Encoding.UTF8.GetBytes("value2")
        };

        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, payload, userHeaders: headers);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Equal(payload, result.Payload);
        Assert.Equal(2, result.UserHeaders.Count);
        Assert.Equal(headers["Key1"], result.UserHeaders["Key1"]);
        Assert.Equal(headers["Key2"], result.UserHeaders["Key2"]);
    }

    [Fact]
    public void WithoutPayloadCrc_Roundtrip()
    {
        using var ms = new MemoryStream();
        var payload = new byte[] { 1, 2, 3 };

        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, payload, includePayloadCrc: false);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void CorruptHeaderCrc_Detected()
    {
        using var ms = new MemoryStream();
        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, new byte[] { 1, 2, 3 });

        var data = ms.ToArray();
        data[0] ^= 0xFF;

        using var ms2 = new MemoryStream(data);
        var result = FrameReader.ReadFrame(ms2);
        Assert.Equal(ErrorCodes.CrcFailure, result.ErrorCode);
    }

    [Fact]
    public void CorruptPayloadCrc_Detected()
    {
        using var ms = new MemoryStream();
        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, new byte[] { 1, 2, 3 }, includePayloadCrc: true);

        var data = ms.ToArray();
        if (data.Length > 41)
            data[41] ^= 0xFF;

        using var ms2 = new MemoryStream(data);
        var result = FrameReader.ReadFrame(ms2);
        Assert.Equal(ErrorCodes.CrcFailure, result.ErrorCode);
    }

    [Fact]
    public void PayloadTooLarge_Rejected()
    {
        using var ms = new MemoryStream();
        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, new byte[1000]);

        ms.Position = 0;
        var result = FrameReader.ReadFrame(ms, maxPayloadSize: 100);
        Assert.Equal(ErrorCodes.BufferExceeded, result.ErrorCode);
    }

    [Fact]
    public void TruncatedStream_ReturnsSocketClosed()
    {
        using var ms = new MemoryStream(new byte[10]);
        var result = FrameReader.ReadFrame(ms);
        Assert.Equal(ErrorCodes.SocketClosed, result.ErrorCode);
    }

    [Fact]
    public void EmptyStream_ReturnsSocketClosed()
    {
        using var ms = new MemoryStream();
        var result = FrameReader.ReadFrame(ms);
        Assert.Equal(ErrorCodes.SocketClosed, result.ErrorCode);
    }

    [Fact]
    public void Magic_PreservedInRoundtrip()
    {
        using var ms = new MemoryStream();
        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, new byte[] { 1 }, magic: 0xCAFEBABE);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Equal(0xCAFEBABEu, result.Header.Magic);
    }

    [Fact]
    public void MultipleFrames_Sequential()
    {
        using var ms = new MemoryStream();
        var corrId1 = Guid.NewGuid();
        var corrId2 = Guid.NewGuid();

        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, new byte[] { 1 });
        FrameWriter.WriteFrame(ms, MsgType.Respond, corrId1, new byte[] { 2, 3 });
        FrameWriter.WriteFrame(ms, MsgType.Progress, corrId2, new byte[] { 4, 5, 6 });

        ms.Position = 0;

        var r1 = FrameReader.ReadFrame(ms);
        Assert.True(r1.Success);
        Assert.Equal(new byte[] { 1 }, r1.Payload);

        var r2 = FrameReader.ReadFrame(ms);
        Assert.True(r2.Success);
        Assert.Equal(corrId1, r2.Header.CorrelationId);
        Assert.Equal(new byte[] { 2, 3 }, r2.Payload);

        var r3 = FrameReader.ReadFrame(ms);
        Assert.True(r3.Success);
        Assert.Equal(MsgType.Progress, r3.Header.MsgType);
    }

    [Fact]
    public void CompressedWithHeaders_Roundtrip()
    {
        using var ms = new MemoryStream();
        var payload = System.Text.Encoding.UTF8.GetBytes("Big payload " + new string('A', 1000));
        var headers = new Dictionary<string, byte[]> { ["trace-id"] = new byte[] { 1, 2, 3, 4 } };
        var corrId = Guid.NewGuid();

        FrameWriter.WriteFrame(ms, MsgType.Data, corrId, payload,
            userHeaders: headers, compression: CompressionAlgorithm.LZ4, includePayloadCrc: true);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Equal(payload, result.Payload);
        Assert.Equal(headers["trace-id"], result.UserHeaders["trace-id"]);
        Assert.Equal(corrId, result.Header.CorrelationId);
    }

    [Fact]
    public void LargePayload_Roundtrip()
    {
        using var ms = new MemoryStream();
        var payload = new byte[100_000];
        Random.Shared.NextBytes(payload);

        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, payload, compression: CompressionAlgorithm.Zstd);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Equal(payload, result.Payload);
    }

    [Theory]
    [InlineData(CompressionAlgorithm.None)]
    [InlineData(CompressionAlgorithm.Deflate)]
    [InlineData(CompressionAlgorithm.LZ4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void UncompressedLen_MatchesOriginalPayloadSize(CompressionAlgorithm algo)
    {
        using var ms = new MemoryStream();
        var payload = System.Text.Encoding.UTF8.GetBytes("Test data " + new string('Z', 200));

        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, payload, compression: algo);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success, $"Failed for {algo}: {result.ErrorCode}");
        Assert.Equal((uint)payload.Length, result.Header.UncompressedLen);
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void TamperedUncompressedLen_DetectedAsCompressionError()
    {
        using var ms = new MemoryStream();
        var payload = System.Text.Encoding.UTF8.GetBytes("Hello " + new string('X', 300));

        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, payload, compression: CompressionAlgorithm.LZ4, includePayloadCrc: false);

        var data = ms.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 9999);
        var newCrc = CongaSharp.Protocol.Crc32C.ComputeHeaderCrc(data.AsSpan(0, FrameHeader.CrcOffset));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), newCrc);

        using var ms2 = new MemoryStream(data);
        var result = FrameReader.ReadFrame(ms2);
        Assert.Equal(ErrorCodes.CompressionError, result.ErrorCode);
    }

    [Fact]
    public void UncompressedLen_ExceedsMaxPayloadSize_Rejected()
    {
        using var ms = new MemoryStream();
        var payload = new byte[1000];
        FrameWriter.WriteFrame(ms, MsgType.Data, Guid.Empty, payload, compression: CompressionAlgorithm.LZ4, includePayloadCrc: false);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms, maxPayloadSize: 500);
        Assert.Equal(ErrorCodes.BufferExceeded, result.ErrorCode);
    }
}