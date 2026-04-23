namespace CongaSharp.Networking;

using CongaSharp.Buffers;
using CongaSharp.Errors;
using CongaSharp.Protocol;

using System.Buffers;
using System.Buffers.Binary;

/// <summary>
/// Async versions of FrameReader/FrameWriter for use in SocketPipeline.
/// Replicates the wire protocol logic using async stream I/O.
/// </summary>
public static class AsyncFrameIO
{
  /// <summary>
  /// Reads a complete wire protocol frame asynchronously.
  /// Two-stage CRC: header CRC validated before payload allocation.
  /// Payload and user header buffers are rented from the pool —
  /// the caller takes ownership via <see cref="FrameReadResult"/> (IDisposable).
  /// </summary>
  public static async Task<FrameReadResult> ReadFrameAsync(
      Stream stream, CancellationToken ct, int maxPayloadSize = 64 * 1024 * 1024)
  {
    var pool = ArrayPool<byte>.Shared;
    var headerBuf = pool.Rent(FrameHeader.Size);
    try {
      // Stage 1: Read 40-byte header
      var bytesRead = await ReadExactAsync(stream, headerBuf, FrameHeader.Size, ct).ConfigureAwait(false);
      if (bytesRead < FrameHeader.Size)
        return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed };

      // Stage 2: Validate header CRC
      var expectedCrc = Crc32C.ComputeHeaderCrc(headerBuf.AsSpan(0, FrameHeader.CrcOffset));
      var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(FrameHeader.CrcOffset));
      if (expectedCrc != actualCrc)
        return new FrameReadResult { ErrorCode = ErrorCodes.CrcFailure };

      var header = FrameHeader.ReadFrom(headerBuf);

      // Stage 3: Validate payload size (both wire and decompressed)
      if (header.PayloadLen > maxPayloadSize || header.UncompressedLen > maxPayloadSize)
        return new FrameReadResult { ErrorCode = ErrorCodes.BufferExceeded, Header = header };

      // Stage 4: Read user headers into pooled buffer
      PooledByteBuffer? headersOwner = null;
      ReadOnlyMemory<byte> headersMemory = default;
      if (header.HeadersLen > 0) {
        headersOwner = PooledByteBuffer.Rent((int)header.HeadersLen);
        if (await ReadExactAsync(stream, headersOwner.DangerousGetArray(), (int)header.HeadersLen, ct).ConfigureAwait(false) < (int)header.HeadersLen) {
          headersOwner.Dispose();
          return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed, Header = header };
        }
        headersMemory = headersOwner.Memory;
      }

      // Stage 5: Read compressed payload into pooled buffer
      PooledByteBuffer? compressedOwner = null;
      ReadOnlyMemory<byte> compressedMemory = default;
      if (header.PayloadLen > 0) {
        compressedOwner = PooledByteBuffer.Rent((int)header.PayloadLen);
        if (await ReadExactAsync(stream, compressedOwner.DangerousGetArray(), (int)header.PayloadLen, ct).ConfigureAwait(false) < (int)header.PayloadLen) {
          compressedOwner.Dispose();
          headersOwner?.Dispose();
          return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed, Header = header };
        }
        compressedMemory = compressedOwner.Memory;
      }

      // Stage 6: Validate payload CRC if present
      if (FrameFlags.GetHasPayloadCrc(header.Flags)) {
        var crcBuf = pool.Rent(4);
        try {
          if (await ReadExactAsync(stream, crcBuf, 4, ct).ConfigureAwait(false) < 4) {
            compressedOwner?.Dispose();
            headersOwner?.Dispose();
            return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed, Header = header };
          }

          var expectedPayloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuf.AsSpan(0, 4));
          var actualPayloadCrc = Crc32C.ComputePayloadCrc(headersMemory.Span, compressedMemory.Span);
          if (expectedPayloadCrc != actualPayloadCrc) {
            compressedOwner?.Dispose();
            headersOwner?.Dispose();
            return new FrameReadResult { ErrorCode = ErrorCodes.CrcFailure, Header = header };
          }
        } finally {
          pool.Return(crcBuf);
        }
      }

      // Stage 7: Decompress payload into pooled buffer
      // For None: zero-copy transfer of compressedOwner (DecompressPooled returns it).
      // For compressed: decompress into new pooled buffer, compressedOwner is disposed.
      IMemoryOwner<byte>? payloadOwner;
      ReadOnlyMemory<byte> payloadMemory;
      try {
        var algo = FrameFlags.GetCompression(header.Flags);
        payloadOwner = Compression.DecompressPooled(algo, compressedMemory.Span, compressedOwner);
        // compressedOwner is now consumed (either transferred or disposed by DecompressPooled)
        compressedOwner = null;
        payloadMemory = payloadOwner.Memory;
      } catch (Exception) {
        compressedOwner?.Dispose();
        headersOwner?.Dispose();
        return new FrameReadResult { ErrorCode = ErrorCodes.CompressionError, Header = header };
      }

      if (payloadMemory.Length != (int)header.UncompressedLen) {
        payloadOwner.Dispose();
        headersOwner?.Dispose();
        return new FrameReadResult { ErrorCode = ErrorCodes.CompressionError, Header = header };
      }

      return new FrameReadResult {
        ErrorCode = ErrorCodes.Success,
        Header = header,
        Payload = payloadMemory,
        PayloadOwner = payloadOwner,
        RawUserHeaders = headersMemory,
        RawUserHeadersOwner = headersOwner
      };
    } finally {
      pool.Return(headerBuf);
    }
  }

  /// <summary>
  /// Writes a complete wire protocol frame asynchronously.
  /// </summary>
  public static async Task WriteFrameAsync(
      Stream stream,
      MsgType msgType,
      Guid correlationId,
      ReadOnlyMemory<byte> payload,
      Dictionary<string, byte[]>? userHeaders,
      CompressionAlgorithm compression,
      int compressionLevel,
      uint magic,
      CancellationToken ct)
  {
    var uncompressedLen = (uint)payload.Length;
    var compressedPayload = Compression.Compress(compression, payload, compressionLevel);

    var headersBytes = (userHeaders != null && userHeaders.Count > 0)
        ? UserHeaders.Encode(userHeaders)
        : Array.Empty<byte>();

    bool hasHeaders = headersBytes.Length > 0;
    bool includePayloadCrc = true;
    var flags = FrameFlags.Create(compression, hasHeaders, includePayloadCrc);

    var header = new FrameHeader {
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

    // Calculate total frame size: header + user headers + payload + CRC
    int crcSize = includePayloadCrc ? 4 : 0;
    int totalSize = FrameHeader.Size + headersBytes.Length + compressedPayload.Length + crcSize;

    var pool = ArrayPool<byte>.Shared;
    var frameBuf = pool.Rent(totalSize);
    try {
      var span = frameBuf.AsSpan();

      // Write header (52 bytes)
      header.WriteTo(span[..FrameHeader.Size]);
      var headerCrc = Crc32C.ComputeHeaderCrc(span[..FrameHeader.CrcOffset]);
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(FrameHeader.CrcOffset, 4), headerCrc);

      int offset = FrameHeader.Size;

      // Write user headers
      if (headersBytes.Length > 0) {
        headersBytes.CopyTo(span[offset..]);
        offset += headersBytes.Length;
      }

      // Write compressed payload
      if (compressedPayload.Length > 0) {
        compressedPayload.Span.CopyTo(span[offset..]);
        offset += compressedPayload.Length;
      }

      // Write payload CRC
      if (includePayloadCrc) {
        var payloadCrc = Crc32C.ComputePayloadCrc(headersBytes, compressedPayload.Span);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), payloadCrc);
        offset += 4;
      }

      // Single write for the entire frame
      await stream.WriteAsync(frameBuf.AsMemory(0, totalSize), ct).ConfigureAwait(false);
      await stream.FlushAsync(ct).ConfigureAwait(false);
    } finally {
      pool.Return(frameBuf);
    }
  }

  private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
  {
    int totalRead = 0;
    while (totalRead < count) {
      var read = await stream.ReadAsync(
          buffer.AsMemory(totalRead, count - totalRead), ct).ConfigureAwait(false);
      if (read == 0) break;
      totalRead += read;
    }
    return totalRead;
  }
}
