using System;

namespace VilsSharpX;

/// <summary>
/// CRC algorithms for LVDS line packet verification.
///
/// Nichia: CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflection)
///   — matches Infineon TLD816K LED driver ioHwAbsTLD816K_Crc16()
///   — also matches AUTOSAR Crc_CalculateCRC16
///
/// Osram: CRC-32 (ISO-HDLC / Ethernet / ZIP, poly 0x04C11DB7, init 0xFFFFFFFF, reflected)
///   — matches TriCore __crc32lw intrinsic with __shuffle bit-reflection
///   — seed = UART_APPL_2_CRC32_SEED_VALUE (0xFFFFFFFF)
/// </summary>
public static class LvdsCrc
{
    // ── CRC-16/CCITT-FALSE ──────────────────────────────────────────────
    // Poly:    0x1021
    // Init:    0xFFFF
    // RefIn:   false
    // RefOut:  false
    // XorOut:  0x0000
    // Used by: Nichia (ioHwAbsTLD816K_Crc16)
    //
    // Uses a 256-entry lookup table for performance.

    private static readonly ushort[] Crc16Table = BuildCrc16Table(0x1021);

    private static ushort[] BuildCrc16Table(ushort poly)
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)(i << 8);
            for (int j = 0; j < 8; j++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ poly : crc << 1);
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Compute CRC-16/CCITT-FALSE over the given data span.
    /// </summary>
    public static ushort ComputeCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte idx = (byte)((crc >> 8) ^ data[i]);
            crc = (ushort)((crc << 8) ^ Crc16Table[idx]);
        }
        return crc;
    }

    /// <summary>
    /// Compute CRC-16 over a byte array with offset and length.
    /// </summary>
    public static ushort ComputeCrc16(byte[] data, int offset, int length)
    {
        return ComputeCrc16(data.AsSpan(offset, length));
    }

    /// <summary>
    /// Verify a Nichia line CRC.
    /// CRC bytes are big-endian: [CRC_H][CRC_L] after pixel data.
    /// </summary>
    public static bool VerifyNichiaCrc(byte[] pixelData, int pixelOffset, int pixelLen,
                                        byte crcHi, byte crcLo)
    {
        ushort computed = ComputeCrc16(pixelData, pixelOffset, pixelLen);
        ushort expected = (ushort)((crcHi << 8) | crcLo);
        return computed == expected;
    }


    // ── CRC-32 (ISO-HDLC / Ethernet / ZIP) ─────────────────────────────
    // Poly:    0x04C11DB7   (reflected: 0xEDB88320)
    // Init:    0xFFFFFFFF
    // RefIn:   true
    // RefOut:  true
    // XorOut:  0xFFFFFFFF
    // Used by: Osram (TriCore __crc32lw + __shuffle)

    private static readonly uint[] Crc32Table = BuildCrc32Table(0xEDB88320u);

    private static uint[] BuildCrc32Table(uint poly)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Compute CRC-32 (ISO-HDLC) over the given data span.
    /// </summary>
    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte idx = (byte)(crc ^ data[i]);
            crc = (crc >> 8) ^ Crc32Table[idx];
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Compute CRC-32 over a byte array with offset and length.
    /// </summary>
    public static uint ComputeCrc32(byte[] data, int offset, int length)
    {
        return ComputeCrc32(data.AsSpan(offset, length));
    }

    /// <summary>
    /// Verify an Osram line CRC.
    /// CRC32 bytes are stored little-endian (TriCore is little-endian).
    /// </summary>
    public static bool VerifyOsramCrc(byte[] pixelData, int pixelOffset, int pixelLen,
                                       byte crc0, byte crc1, byte crc2, byte crc3)
    {
        uint computed = ComputeCrc32(pixelData, pixelOffset, pixelLen);
        uint expected = (uint)(crc0 | (crc1 << 8) | (crc2 << 16) | (crc3 << 24));
        return computed == expected;
    }
}
