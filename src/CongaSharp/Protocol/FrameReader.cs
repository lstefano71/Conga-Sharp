namespace CongaSharp.Protocol;

using CongaSharp.Buffers;
using CongaSharp.Errors;

using System.Buffers;
using System.Buffers.Binary;

/// <summary>
/// Result of reading a frame from a stream.
/// Implements <see cref="IDisposable"/> to return pooled buffers.
/// </summary>
public sealed class FrameReadResult : IDisposable
{
  public int ErrorCode { get; init; }
  public FrameHeader Header { get; init; }
  public ReadOnlyMemory<byte> Payload { get; init; }
  public ReadOnlyMemory<byte> RawUserHeaders { get; init; }
  public bool Success => ErrorCode == ErrorCodes.Success;

  /// <summary>
  /// Optional owner for the Payload buffer.
  /// Use <see cref="TakePayloadOwner"/> for move-only transfer.
  /// </summary>
  internal IDisposable? PayloadOwner
  {
    get => _payloadOwner;
    init => _payloadOwner = value;
  }
  private IDisposable? _payloadOwner;

  /// <summary>
  /// Optional owner for the RawUserHeaders buffer.
  /// Use <see cref="TakeRawUserHeadersOwner"/> for move-only transfer.
  /// </summary>
  internal IDisposable? RawUserHeadersOwner
  {
    get => _rawUserHeadersOwner;
    init => _rawUserHeadersOwner = value;
  }
  private IDisposable? _rawUserHeadersOwner;

  internal IDisposable? TakePayloadOwner()
      => Interlocked.Exchange(ref _payloadOwner, null);

  internal IDisposable? TakeRawUserHeadersOwner()
      => Interlocked.Exchange(ref _rawUserHeadersOwner, null);

  /// <summary>
  /// Convenience: lazily decodes raw user headers into a dictionary.
  /// Use for tests or when structured access is needed.
  /// </summary>
  public Dictionary<string, byte[]> DecodedUserHeaders =>
      RawUserHeaders.IsEmpty
          ? new Dictionary<string, byte[]>()
          : UserHeaders.Decode(RawUserHeaders.Span);

  /// <summary>
  /// Returns pooled buffers. Safe to call multiple times.
  /// </summary>
  public void Dispose()
  {
    Interlocked.Exchange(ref _payloadOwner, null)?.Dispose();
    Interlocked.Exchange(ref _rawUserHeadersOwner, null)?.Dispose();
  }
}

/// <summary>
/// Reads complete wire protocol frames from a Stream.
/// Two-stage CRC validation: header CRC checked before payload allocation.
/// </summary>
public static class FrameReader
{
  /// <summary>
  /// Reads a complete frame from the stream.
  /// Payload and user header buffers are rented from the pool —
  /// the caller takes ownership via <see cref="FrameReadResult"/> (IDisposable).
  /// </summary>
  public static FrameReadResult ReadFrame(Stream stream, int maxPayloadSize = 64 * 1024 * 1024)
  {
    var pool = ArrayPool<byte>.Shared;
    var headerBuf = pool.Rent(FrameHeader.Size);
    try {
      // Stage 1: Read 40-byte header
      var bytesRead = ReadExact(stream, headerBuf, FrameHeader.Size);
      if (bytesRead < FrameHeader.Size)
        return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed };

      // Stage 2: Validate header CRC (bytes 0-35 vs bytes 36-39)
      var expectedCrc = Crc32C.ComputeHeaderCrc(headerBuf.AsSpan(0, FrameHeader.CrcOffset));
      var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(FrameHeader.CrcOffset));
      if (expectedCrc != actualCrc)
        return new FrameReadResult { ErrorCode = ErrorCodes.CrcFailure };

      var header = FrameHeader.ReadFrom(headerBuf);

      // Stage 3: Validate both wire and decompressed sizes before allocating
      if (header.PayloadLen > maxPayloadSize || header.UncompressedLen > maxPayloadSize)
        return new FrameReadResult { ErrorCode = ErrorCodes.BufferExceeded, Header = header };

      // Stage 4: Read user headers into pooled buffer
      PooledByteBuffer? headersOwner = null;
      ReadOnlyMemory<byte> headersMemory = default;
      if (header.HeadersLen > 0) {
        headersOwner = PooledByteBuffer.Rent((int)header.HeadersLen);
        if (ReadExact(stream, headersOwner.DangerousGetArray(), (int)header.HeadersLen) < (int)header.HeadersLen) {
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
        if (ReadExact(stream, compressedOwner.DangerousGetArray(), (int)header.PayloadLen) < (int)header.PayloadLen) {
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
          if (ReadExact(stream, crcBuf, 4) < 4) {
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
      IMemoryOwner<byte>? payloadOwner;
      ReadOnlyMemory<byte> payloadMemory;
      try {
        var algo = FrameFlags.GetCompression(header.Flags);
        payloadOwner = Compression.DecompressPooled(algo, compressedMemory.Span, compressedOwner);
        compressedOwner = null; // consumed by DecompressPooled
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

  private static int ReadExact(Stream stream, byte[] buffer, int count)
  {
    int totalRead = 0;
    while (totalRead < count) {
      var read = stream.Read(buffer, totalRead, count - totalRead);
      if (read == 0) break;
      totalRead += read;
    }
    return totalRead;
  }
}
