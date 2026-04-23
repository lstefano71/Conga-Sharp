namespace CongaSharp.Marshalling;

using CongaSharp.Errors;

/// <summary>
/// Converts between wchar_t* (C ABI) and .NET strings.
/// </summary>
public static class StringMarshaller
{
  /// <summary>
  /// Reads a null-terminated wchar_t* into a .NET string.
  /// Returns null if pointer is null.
  /// </summary>
  public static unsafe string? ReadFromPointer(char* ptr)
  {
    if (ptr == null) return null;
    return new string(ptr);
  }

  /// <summary>
  /// Writes a string to a caller-allocated wchar_t buffer.
  /// Returns Success if it fits, BufferTooSmall if not.
  /// Always sets requiredLen to the needed capacity (including null terminator).
  /// </summary>
  public static unsafe int WriteToBuffer(string value, char* buffer, int bufferCap, int* outRequiredLen)
  {
    int required = value.Length + 1; // +1 for null terminator
    if (outRequiredLen != null)
      *outRequiredLen = required;

    if (buffer == null || bufferCap < required)
      return ErrorCodes.BufferTooSmall;

    for (int i = 0; i < value.Length; i++)
      buffer[i] = value[i];
    buffer[value.Length] = '\0';

    return ErrorCodes.Success;
  }

  /// <summary>
  /// Writes a string to a buffer. Simplified overload without outRequiredLen.
  /// </summary>
  public static unsafe int WriteToBuffer(string value, char* buffer, int bufferCap)
  {
    return WriteToBuffer(value, buffer, bufferCap, null);
  }
}
