namespace CongaSharp.Protocol;

using System.Buffers.Binary;
using System.Text;

/// <summary>
/// Encodes and decodes user-defined header key-value pairs for the wire protocol.
/// Format: [uint16 key_len][key UTF-8][uint32 value_len][value bytes]...
/// </summary>
public static class UserHeaders
{
  /// <summary>
  /// Encodes a dictionary of headers into wire format bytes.
  /// </summary>
  public static byte[] Encode(IReadOnlyDictionary<string, byte[]> headers)
  {
    if (headers == null || headers.Count == 0)
      return Array.Empty<byte>();

    using var ms = new MemoryStream();
    Span<byte> keyLenBuf = stackalloc byte[2];
    Span<byte> valLenBuf = stackalloc byte[4];

    foreach (var (key, value) in headers) {
      var keyBytes = Encoding.UTF8.GetBytes(key);

      // Key length (uint16)
      BinaryPrimitives.WriteUInt16LittleEndian(keyLenBuf, (ushort)keyBytes.Length);
      ms.Write(keyLenBuf);

      // Key bytes
      ms.Write(keyBytes);

      // Value length (uint32)
      BinaryPrimitives.WriteUInt32LittleEndian(valLenBuf, (uint)(value?.Length ?? 0));
      ms.Write(valLenBuf);

      // Value bytes
      if (value != null && value.Length > 0)
        ms.Write(value);
    }
    return ms.ToArray();
  }

  /// <summary>
  /// Decodes wire format bytes into a dictionary of headers.
  /// </summary>
  public static Dictionary<string, byte[]> Decode(ReadOnlySpan<byte> data)
  {
    var result = new Dictionary<string, byte[]>();
    var offset = 0;

    while (offset < data.Length) {
      // Key length
      if (offset + 2 > data.Length) break;
      var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
      offset += 2;

      // Key bytes
      if (offset + keyLen > data.Length) break;
      var key = Encoding.UTF8.GetString(data.Slice(offset, keyLen));
      offset += keyLen;

      // Value length
      if (offset + 4 > data.Length) break;
      var valLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
      offset += 4;

      // Value bytes
      if (offset + valLen > data.Length) break;
      var value = data.Slice(offset, valLen).ToArray();
      offset += valLen;

      result[key] = value;
    }

    return result;
  }
}
