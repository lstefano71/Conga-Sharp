namespace CongaSharp.Networking;

using System.Buffers.Binary;
using CongaSharp.Errors;
using CongaSharp.Protocol;

/// <summary>
/// Async versions of FrameReader/FrameWriter for use in SocketPipeline.
/// Replicates the wire protocol logic using async stream I/O.
/// </summary>
public static class AsyncFrameIO
{
    /// <summary>
    /// Reads a complete wire protocol frame asynchronously.
    /// Two-stage CRC: header CRC validated before payload allocation.
    /// </summary>
    public static async Task<FrameReadResult> ReadFrameAsync(
        Stream stream, CancellationToken ct, int maxPayloadSize = 64 * 1024 * 1024)
    {
        // Stage 1: Read 52-byte header
        var headerBuf = new byte[FrameHeader.Size];
        var bytesRead = await ReadExactAsync(stream, headerBuf, ct).ConfigureAwait(false);
        if (bytesRead < FrameHeader.Size)
            return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed };

        // Stage 2: Validate header CRC
        var expectedCrc = Crc32C.ComputeHeaderCrc(headerBuf.AsSpan(0, FrameHeader.CrcOffset));
        var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(FrameHeader.CrcOffset));
        if (expectedCrc != actualCrc)
            return new FrameReadResult { ErrorCode = ErrorCodes.CrcFailure };

        var header = FrameHeader.ReadFrom(headerBuf);

        // Stage 3: Validate payload size
        if (header.PayloadLen > maxPayloadSize)
            return new FrameReadResult { ErrorCode = ErrorCodes.BufferExceeded, Header = header };

        // Stage 4: Read user headers
        var headersBytes = Array.Empty<byte>();
        if (header.HeadersLen > 0)
        {
            headersBytes = new byte[header.HeadersLen];
            if (await ReadExactAsync(stream, headersBytes, ct).ConfigureAwait(false) < (int)header.HeadersLen)
                return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed, Header = header };
        }

        // Stage 5: Read compressed payload
        var compressedPayload = Array.Empty<byte>();
        if (header.PayloadLen > 0)
        {
            compressedPayload = new byte[header.PayloadLen];
            if (await ReadExactAsync(stream, compressedPayload, ct).ConfigureAwait(false) < (int)header.PayloadLen)
                return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed, Header = header };
        }

        // Stage 6: Validate payload CRC if present
        if (FrameFlags.GetHasPayloadCrc(header.Flags))
        {
            var crcBuf = new byte[4];
            if (await ReadExactAsync(stream, crcBuf, ct).ConfigureAwait(false) < 4)
                return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed, Header = header };

            var expectedPayloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuf);

            var combined = new byte[headersBytes.Length + compressedPayload.Length];
            headersBytes.CopyTo(combined, 0);
            compressedPayload.CopyTo(combined, headersBytes.Length);

            var actualPayloadCrc = Crc32C.ComputePayloadCrc(combined);
            if (expectedPayloadCrc != actualPayloadCrc)
                return new FrameReadResult { ErrorCode = ErrorCodes.CrcFailure, Header = header };
        }

        // Stage 7: Decompress payload
        byte[] payload;
        try
        {
            var algo = FrameFlags.GetCompression(header.Flags);
            payload = Compression.Decompress(algo, compressedPayload);
        }
        catch
        {
            return new FrameReadResult { ErrorCode = ErrorCodes.CompressionError, Header = header };
        }

        // Stage 8: Decode user headers
        var userHeaders = FrameFlags.GetHasUserHeaders(header.Flags) && headersBytes.Length > 0
            ? UserHeaders.Decode(headersBytes)
            : new Dictionary<string, byte[]>();

        return new FrameReadResult
        {
            ErrorCode = ErrorCodes.Success,
            Header = header,
            Payload = payload,
            UserHeaders = userHeaders
        };
    }

    /// <summary>
    /// Writes a complete wire protocol frame asynchronously.
    /// </summary>
    public static async Task WriteFrameAsync(
        Stream stream,
        MsgType msgType,
        string cmdName,
        byte[] payload,
        Dictionary<string, byte[]>? userHeaders,
        CompressionAlgorithm compression,
        uint magic,
        CancellationToken ct)
    {
        var compressedPayload = Compression.Compress(compression, payload);

        var headersBytes = (userHeaders != null && userHeaders.Count > 0)
            ? UserHeaders.Encode(userHeaders)
            : Array.Empty<byte>();

        bool hasHeaders = headersBytes.Length > 0;
        bool includePayloadCrc = true;
        var flags = FrameFlags.Create(compression, hasHeaders, includePayloadCrc);

        var header = new FrameHeader
        {
            Version = FrameHeader.CurrentVersion,
            MsgType = msgType,
            Flags = flags,
            Magic = magic,
            CmdName = cmdName ?? "",
            HeadersLen = (uint)headersBytes.Length,
            PayloadLen = (uint)compressedPayload.Length,
            HeaderCrc = 0
        };

        var headerBuf = new byte[FrameHeader.Size];
        header.WriteTo(headerBuf);

        var headerCrc = Crc32C.ComputeHeaderCrc(headerBuf.AsSpan(0, FrameHeader.CrcOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.AsSpan(FrameHeader.CrcOffset), headerCrc);

        await stream.WriteAsync(headerBuf, ct).ConfigureAwait(false);

        if (headersBytes.Length > 0)
            await stream.WriteAsync(headersBytes, ct).ConfigureAwait(false);

        if (compressedPayload.Length > 0)
            await stream.WriteAsync(compressedPayload, ct).ConfigureAwait(false);

        if (includePayloadCrc)
        {
            var combinedLen = headersBytes.Length + compressedPayload.Length;
            var combined = new byte[combinedLen];
            headersBytes.CopyTo(combined, 0);
            compressedPayload.CopyTo(combined, headersBytes.Length);

            var payloadCrc = Crc32C.ComputePayloadCrc(combined);
            var crcBuf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, payloadCrc);
            await stream.WriteAsync(crcBuf, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }
}
