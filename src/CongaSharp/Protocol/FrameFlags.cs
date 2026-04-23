namespace CongaSharp.Protocol;

/// <summary>
/// Helpers for the 16-bit Flags field in the wire protocol header.
/// </summary>
public static class FrameFlags
{
  private const int CompressionMask = 0b111;         // bits 0-2
  private const int HasUserHeadersBit = 1 << 3;      // bit 3
  private const int HasPayloadCrcBit = 1 << 4;       // bit 4

  public static ushort Create(CompressionAlgorithm compression, bool hasUserHeaders, bool hasPayloadCrc)
  {
    ushort flags = (ushort)((byte)compression & CompressionMask);
    if (hasUserHeaders) flags |= HasUserHeadersBit;
    if (hasPayloadCrc) flags |= HasPayloadCrcBit;
    return flags;
  }

  public static CompressionAlgorithm GetCompression(ushort flags)
      => (CompressionAlgorithm)(flags & CompressionMask);

  public static bool GetHasUserHeaders(ushort flags)
      => (flags & HasUserHeadersBit) != 0;

  public static bool GetHasPayloadCrc(ushort flags)
      => (flags & HasPayloadCrcBit) != 0;
}
