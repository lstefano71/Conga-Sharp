namespace CongaSharp.Marshalling;

using CongaSharp.Errors;

/// <summary>
/// Converts between byte* + length (C ABI) and .NET byte arrays/spans.
/// </summary>
public static class BufferMarshaller
{
  /// <summary>
  /// Creates a ReadOnlySpan from a byte* and length. Returns empty span if null.
  /// </summary>
  public static unsafe ReadOnlySpan<byte> ReadFromPointer(byte* ptr, int length)
  {
    if (ptr == null || length <= 0)
      return ReadOnlySpan<byte>.Empty;
    return new ReadOnlySpan<byte>(ptr, length);
  }

  /// <summary>
  /// Writes bytes to a caller-allocated buffer.
  /// Returns Success if it fits, BufferTooSmall if not.
  /// Always sets outActualLen to the number of bytes that would be written.
  /// </summary>
  public static unsafe int WriteToBuffer(ReadOnlySpan<byte> data, byte* buffer, int bufferCap, int* outActualLen)
  {
    if (outActualLen != null)
      *outActualLen = data.Length;

    if (buffer == null || bufferCap < data.Length)
      return ErrorCodes.BufferTooSmall;

    data.CopyTo(new Span<byte>(buffer, bufferCap));
    return ErrorCodes.Success;
  }
}
