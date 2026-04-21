namespace CongaSharp.Protocol;

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

/// <summary>
/// CRC-32C (Castagnoli) helpers for wire protocol integrity checking.
/// Hardware-accelerated via SSE4.2 when available, with software table fallback.
/// </summary>
public static class Crc32C
{
    // CRC-32C lookup table (Castagnoli polynomial 0x1EDC6F41)
    private static readonly uint[] Table = GenerateTable();

    private static uint[] GenerateTable()
    {
        const uint polynomial = 0x82F63B78u; // reversed Castagnoli
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Computes CRC-32C of the first 48 bytes of the header (everything except the HeaderCRC field itself).
    /// </summary>
    public static uint ComputeHeaderCrc(ReadOnlySpan<byte> headerFirst48Bytes)
    {
        if (headerFirst48Bytes.Length < 48)
            throw new ArgumentException("Need at least 48 bytes for header CRC");
        return Compute(headerFirst48Bytes[..48]);
    }

    /// <summary>
    /// Computes CRC-32C of arbitrary data (used for payload CRC over user headers + payload).
    /// </summary>
    public static uint ComputePayloadCrc(ReadOnlySpan<byte> data)
    {
        return Compute(data);
    }

    /// <summary>
    /// Computes CRC-32C of the given data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        return Sse42.IsSupported ? ComputeHardware(data) : ComputeSoftware(data);
    }

    private static uint ComputeHardware(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        int i = 0;

        // Process 8 bytes at a time if 64-bit SSE4.2 is available
        if (Sse42.X64.IsSupported)
        {
            while (i + 8 <= data.Length)
            {
                ulong chunk = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i));
                crc = (uint)Sse42.X64.Crc32(crc, chunk);
                i += 8;
            }
        }

        // Process 4 bytes at a time
        while (i + 4 <= data.Length)
        {
            uint chunk = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i));
            crc = Sse42.Crc32(crc, chunk);
            i += 4;
        }

        // Process remaining bytes
        while (i < data.Length)
        {
            crc = Sse42.Crc32(crc, data[i]);
            i++;
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint ComputeSoftware(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = (crc >> 8) ^ Table[(byte)(crc ^ b)];
        return crc ^ 0xFFFFFFFFu;
    }
}
