namespace CongaSharp.Protocol;

using System.Buffers.Binary;

/// <summary>
/// Writes complete wire protocol frames to a Stream.
/// </summary>
public static class FrameWriter
{
    /// <summary>
    /// Writes a complete frame to the stream.
    /// </summary>
    public static void WriteFrame(
        Stream stream,
        MsgType msgType,
        Guid correlationId,
        ReadOnlySpan<byte> payload,
        IReadOnlyDictionary<string, byte[]>? userHeaders = null,
        CompressionAlgorithm compression = CompressionAlgorithm.None,
        int compressionLevel = 0,
        uint magic = 0,
        bool includePayloadCrc = true)
    {
        var compressedPayload = Compression.Compress(compression, payload, compressionLevel);
        var uncompressedLen = (uint)payload.Length;

        var headersBytes = (userHeaders != null && userHeaders.Count > 0)
            ? UserHeaders.Encode(userHeaders)
            : Array.Empty<byte>();

        bool hasHeaders = headersBytes.Length > 0;
        var flags = FrameFlags.Create(compression, hasHeaders, includePayloadCrc);

        var header = new FrameHeader
        {
            Version = FrameHeader.CurrentVersion,
            MsgType = msgType,
            Flags = flags,
            Magic = magic,
            CorrelationId = correlationId,
            HeadersLen = (uint)headersBytes.Length,
            PayloadLen = (uint)compressedPayload.Length,
            UncompressedLen = uncompressedLen,
            HeaderCrc = 0
        };

        Span<byte> headerBuf = stackalloc byte[FrameHeader.Size];
        header.WriteTo(headerBuf);

        var headerCrc = Crc32C.ComputeHeaderCrc(headerBuf[..FrameHeader.CrcOffset]);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuf[FrameHeader.CrcOffset..], headerCrc);

        stream.Write(headerBuf);

        if (headersBytes.Length > 0)
            stream.Write(headersBytes);

        if (compressedPayload.Length > 0)
            stream.Write(compressedPayload);

        if (includePayloadCrc)
        {
            var payloadCrc = Crc32C.ComputePayloadCrc(headersBytes, compressedPayload);
            Span<byte> crcBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, payloadCrc);
            stream.Write(crcBuf);
        }

        stream.Flush();
    }
}
