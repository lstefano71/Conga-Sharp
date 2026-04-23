namespace CongaSharp.Protocol;

using CongaSharp.Errors;

using System.Buffers.Binary;

/// <summary>
/// Result of reading a frame from a stream.
/// </summary>
public sealed class FrameReadResult
{
  public int ErrorCode { get; init; }
  public FrameHeader Header { get; init; }
  public byte[] Payload { get; init; } = Array.Empty<byte>();
  public Dictionary<string, byte[]>? UserHeaders { get; init; }
  public bool Success => ErrorCode == ErrorCodes.Success;
}

/// <summary>
/// Reads complete wire protocol frames from a Stream.
/// Two-stage CRC validation: header CRC checked before payload allocation.
/// </summary>
public static class FrameReader
{
  /// <summary>
  /// Reads a complete frame from the stream.
  /// </summary>
  public static FrameReadResult ReadFrame(Stream stream, int maxPayloadSize = 64 * 1024 * 1024)
  {
    // Stage 1: Read 40-byte header
    var headerBuf = new byte[FrameHeader.Size];
    var bytesRead = ReadExact(stream, headerBuf);
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

    // Stage 4: Read user headers
    var headersBytes = Array.Empty<byte>();
    if (header.HeadersLen > 0) {
      headersBytes = new byte[header.HeadersLen];
      if (ReadExact(stream, headersBytes) < (int)header.HeadersLen)
        return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed, Header = header };
    }

    // Stage 5: Read compressed payload
    var compressedPayload = Array.Empty<byte>();
    if (header.PayloadLen > 0) {
      compressedPayload = new byte[header.PayloadLen];
      if (ReadExact(stream, compressedPayload) < (int)header.PayloadLen)
        return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed, Header = header };
    }

    // Stage 6: Validate payload CRC if present
    if (FrameFlags.GetHasPayloadCrc(header.Flags)) {
      var crcBuf = new byte[4];
      if (ReadExact(stream, crcBuf) < 4)
        return new FrameReadResult { ErrorCode = ErrorCodes.SocketClosed, Header = header };

      var expectedPayloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuf);

      var actualPayloadCrc = Crc32C.ComputePayloadCrc(headersBytes, compressedPayload);
      if (expectedPayloadCrc != actualPayloadCrc)
        return new FrameReadResult { ErrorCode = ErrorCodes.CrcFailure, Header = header };
    }

    // Stage 7: Decompress payload and verify uncompressed size
    byte[] payload;
    try {
      var algo = FrameFlags.GetCompression(header.Flags);
      payload = Compression.Decompress(algo, compressedPayload);
    } catch (Exception) {
      return new FrameReadResult { ErrorCode = ErrorCodes.CompressionError, Header = header };
    }

    if (payload.Length != (int)header.UncompressedLen)
      return new FrameReadResult { ErrorCode = ErrorCodes.CompressionError, Header = header };

    // Stage 8: Decode user headers
    var userHeaders = FrameFlags.GetHasUserHeaders(header.Flags) && headersBytes.Length > 0
        ? UserHeaders.Decode(headersBytes)
        : new Dictionary<string, byte[]>();

    return new FrameReadResult {
      ErrorCode = ErrorCodes.Success,
      Header = header,
      Payload = payload,
      UserHeaders = userHeaders
    };
  }

  private static int ReadExact(Stream stream, byte[] buffer)
  {
    int totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
      if (read == 0) break;
      totalRead += read;
    }
    return totalRead;
  }
}
