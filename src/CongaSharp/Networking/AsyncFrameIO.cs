namespace CongaSharp.Networking;

using System.Buffers;
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
        var pool = ArrayPool<byte>.Shared;
        var headerBuf = pool.Rent(FrameHeader.Size);
        try
        {
            // Stage 1: Read 52-byte header
            var bytesRead = await ReadExactAsync(stream, headerBuf, FrameHeader.Size, ct).ConfigureAwait(false);
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

            // Stage 4: Read user headers (keep as new byte[] — owned by FrameReadResult)
            var headersBytes = Array.Empty<byte>();
            if (header.HeadersLen > 0)
            {
                headersBytes = new byte[header.HeadersLen];
                if (await ReadExactAsync(stream, headersBytes, (int)header.HeadersLen, ct).ConfigureAwait(false) < (int)header.HeadersLen)
                    return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed, Header = header };
            }

            // Stage 5: Read compressed payload (keep as new byte[] — owned by FrameReadResult)
            var compressedPayload = Array.Empty<byte>();
            if (header.PayloadLen > 0)
            {
                compressedPayload = new byte[header.PayloadLen];
                if (await ReadExactAsync(stream, compressedPayload, (int)header.PayloadLen, ct).ConfigureAwait(false) < (int)header.PayloadLen)
                    return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed, Header = header };
            }

            // Stage 6: Validate payload CRC if present
            if (FrameFlags.GetHasPayloadCrc(header.Flags))
            {
                var crcBuf = pool.Rent(4);
                try
                {
                    if (await ReadExactAsync(stream, crcBuf, 4, ct).ConfigureAwait(false) < 4)
                        return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed, Header = header };

                    var expectedPayloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuf.AsSpan(0, 4));
                    var actualPayloadCrc = Crc32C.ComputePayloadCrc(headersBytes, compressedPayload);
                    if (expectedPayloadCrc != actualPayloadCrc)
                        return new FrameReadResult { ErrorCode = ErrorCodes.CrcFailure, Header = header };
                }
                finally
                {
                    pool.Return(crcBuf);
                }
            }

            // Stage 7: Decompress payload
            byte[] payload;
            try
            {
                var algo = FrameFlags.GetCompression(header.Flags);
                payload = Compression.Decompress(algo, compressedPayload);
            }
            catch (Exception)
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
        finally
        {
            pool.Return(headerBuf);
        }
    }

    /// <summary>
    /// Writes a complete wire protocol frame asynchronously.
    /// </summary>
    public static async Task WriteFrameAsync(
        Stream stream,
        MsgType msgType,
        string cmdName,
        ReadOnlyMemory<byte> payload,
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

        // Calculate total frame size: header + user headers + payload + CRC
        int crcSize = includePayloadCrc ? 4 : 0;
        int totalSize = FrameHeader.Size + headersBytes.Length + compressedPayload.Length + crcSize;

        var pool = ArrayPool<byte>.Shared;
        var frameBuf = pool.Rent(totalSize);
        try
        {
            var span = frameBuf.AsSpan();

            // Write header (52 bytes)
            header.WriteTo(span[..FrameHeader.Size]);
            var headerCrc = Crc32C.ComputeHeaderCrc(span[..FrameHeader.CrcOffset]);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(FrameHeader.CrcOffset, 4), headerCrc);

            int offset = FrameHeader.Size;

            // Write user headers
            if (headersBytes.Length > 0)
            {
                headersBytes.CopyTo(span[offset..]);
                offset += headersBytes.Length;
            }

            // Write compressed payload
            if (compressedPayload.Length > 0)
            {
                compressedPayload.Span.CopyTo(span[offset..]);
                offset += compressedPayload.Length;
            }

            // Write payload CRC
            if (includePayloadCrc)
            {
                var payloadCrc = Crc32C.ComputePayloadCrc(headersBytes, compressedPayload.Span);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), payloadCrc);
                offset += 4;
            }

            // Single write for the entire frame
            await stream.WriteAsync(frameBuf.AsMemory(0, totalSize), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            pool.Return(frameBuf);
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, count - totalRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }
}
