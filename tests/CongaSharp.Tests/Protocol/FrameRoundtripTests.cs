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

        FrameWriter.WriteFrame(ms, MsgType.Data, "", payload);
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
        FrameWriter.WriteFrame(ms, MsgType.Data, "", ReadOnlySpan<byte>.Empty);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Empty(result.Payload);
    }

    [Fact]
    public void CommandName_Roundtrip()
    {
        using var ms = new MemoryStream();
        var payload = new byte[] { 1, 2, 3 };

        FrameWriter.WriteFrame(ms, MsgType.Respond, "MyCommand", payload);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Equal(MsgType.Respond, result.Header.MsgType);
        Assert.Equal("MyCommand", result.Header.CmdName);
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

        FrameWriter.WriteFrame(ms, msgType, "", payload);
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

        FrameWriter.WriteFrame(ms, MsgType.Data, "", payload, compression: algo);
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

        FrameWriter.WriteFrame(ms, MsgType.Data, "", payload, userHeaders: headers);
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

        FrameWriter.WriteFrame(ms, MsgType.Data, "", payload, includePayloadCrc: false);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void CorruptHeaderCrc_Detected()
    {
        using var ms = new MemoryStream();
        FrameWriter.WriteFrame(ms, MsgType.Data, "", new byte[] { 1, 2, 3 });

        var data = ms.ToArray();
        data[0] ^= 0xFF; // flip version byte

        using var ms2 = new MemoryStream(data);
        var result = FrameReader.ReadFrame(ms2);
        Assert.Equal(ErrorCodes.CrcFailure, result.ErrorCode);
    }

    [Fact]
    public void CorruptPayloadCrc_Detected()
    {
        using var ms = new MemoryStream();
        FrameWriter.WriteFrame(ms, MsgType.Data, "", new byte[] { 1, 2, 3 }, includePayloadCrc: true);

        var data = ms.ToArray();
        // Corrupt a payload byte (after the 56-byte header)
        if (data.Length > 57)
            data[57] ^= 0xFF;

        using var ms2 = new MemoryStream(data);
        var result = FrameReader.ReadFrame(ms2);
        Assert.Equal(ErrorCodes.CrcFailure, result.ErrorCode);
    }

    [Fact]
    public void PayloadTooLarge_Rejected()
    {
        using var ms = new MemoryStream();
        FrameWriter.WriteFrame(ms, MsgType.Data, "", new byte[1000]);

        ms.Position = 0;
        var result = FrameReader.ReadFrame(ms, maxPayloadSize: 100);
        Assert.Equal(ErrorCodes.BufferExceeded, result.ErrorCode);
    }

    [Fact]
    public void TruncatedStream_ReturnsSocketClosed()
    {
        using var ms = new MemoryStream(new byte[10]); // too short for header
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
        FrameWriter.WriteFrame(ms, MsgType.Data, "", new byte[] { 1 }, magic: 0xCAFEBABE);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Equal(0xCAFEBABEu, result.Header.Magic);
    }

    [Fact]
    public void MultipleFrames_Sequential()
    {
        using var ms = new MemoryStream();

        FrameWriter.WriteFrame(ms, MsgType.Data, "", new byte[] { 1 });
        FrameWriter.WriteFrame(ms, MsgType.Respond, "Cmd1", new byte[] { 2, 3 });
        FrameWriter.WriteFrame(ms, MsgType.Progress, "Cmd1", new byte[] { 4, 5, 6 });

        ms.Position = 0;

        var r1 = FrameReader.ReadFrame(ms);
        Assert.True(r1.Success);
        Assert.Equal(new byte[] { 1 }, r1.Payload);

        var r2 = FrameReader.ReadFrame(ms);
        Assert.True(r2.Success);
        Assert.Equal("Cmd1", r2.Header.CmdName);
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

        FrameWriter.WriteFrame(ms, MsgType.Data, "TestCmd", payload,
            userHeaders: headers, compression: CompressionAlgorithm.LZ4, includePayloadCrc: true);
        ms.Position = 0;

        var result = FrameReader.ReadFrame(ms);
        Assert.True(result.Success);
        Assert.Equal(payload, result.Payload);
        Assert.Equal(headers["trace-id"], result.UserHeaders["trace-id"]);
        Assert.Equal("TestCmd", result.Header.CmdName);
    }

    [Fact]
    public void LargePayload_Roundtrip()
    {
        using var ms = new MemoryStream();
        var payload = new byte[100_000];
        Random.Shared.NextBytes(payload);

        FrameWriter.WriteFrame(ms, MsgType.Data, "", payload, compression: CompressionAlgorithm.Zstd);
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

        FrameWriter.WriteFrame(ms, MsgType.Data, "", payload, compression: algo);
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

        FrameWriter.WriteFrame(ms, MsgType.Data, "", payload, compression: CompressionAlgorithm.LZ4, includePayloadCrc: false);

        var data = ms.ToArray();
        // Tamper UncompressedLen at offset 48 (little-endian) — set it to wrong value
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48), 9999);
        // Recompute HeaderCRC at offset 52 so the header CRC check still passes
        var newCrc = CongaSharp.Protocol.Crc32C.ComputeHeaderCrc(data.AsSpan(0, FrameHeader.CrcOffset));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(52), newCrc);

        using var ms2 = new MemoryStream(data);
        var result = FrameReader.ReadFrame(ms2);
        Assert.Equal(ErrorCodes.CompressionError, result.ErrorCode);
    }

    [Fact]
    public void UncompressedLen_ExceedsMaxPayloadSize_Rejected()
    {
        using var ms = new MemoryStream();
        // Write a frame compressed with LZ4 — uncompressed is large
        var payload = new byte[1000];
        FrameWriter.WriteFrame(ms, MsgType.Data, "", payload, compression: CompressionAlgorithm.LZ4, includePayloadCrc: false);
        ms.Position = 0;

        // maxPayloadSize smaller than the uncompressed payload
        var result = FrameReader.ReadFrame(ms, maxPayloadSize: 500);
        Assert.Equal(ErrorCodes.BufferExceeded, result.ErrorCode);
    }
}
