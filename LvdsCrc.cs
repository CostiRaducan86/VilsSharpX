using System;

namespace VilsSharpX;

/// <summary>
/// CRC algorithms for LVDS line packet verification.
///
/// Nichia: CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflection)
///   — matches Infineon TLD816K LED driver ioHwAbsTLD816K_Crc16()
///   — also matches AUTOSAR Crc_CalculateCRC16
///
/// Osram: CRC-32 matching ECU (DAG_PLU_HD) uart_appl_fast_channel_process_2:
///   — MSB-first CRC, poly 0x04C11DB7, raw seed 0xDEADAFFE
///   — bytes processed in reversed 4-byte groups, each byte bit-reflected
///   — final byte-swap (no additional XOR)
///   — ECU seed (HW format) = UART_APPL_2_CRC32_SEED_VALUE (0x800A4A84)
///   — 25600 zero bytes → 0x66844BF6
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


    // ── CRC-32 (Osram ECU algorithm) ───────────────────────────────────
    // MSB-first CRC, poly 0x04C11DB7, raw seed 0xDEADAFFE
    // Bytes in 4-byte groups, reversed within group, NO byte reflection
    // Final: bswap (no additional XOR)
    // Used by: Osram (ECU uart_appl_fast_channel_process_2)

    private const uint OsramCrc32RawSeed = 0xDEADAFFEu;

    private static readonly uint[] OsramCrc32Table = BuildOsramCrc32Table();

    private static uint[] BuildOsramCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i << 24;
            for (int j = 0; j < 8; j++)
                crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
            table[i] = crc;
        }
        return table;
    }

    private static uint BSwap32(uint v)
    {
        return ((v >> 24) & 0xFFu) | ((v >> 8) & 0xFF00u) |
               ((v << 8) & 0xFF0000u) | ((v << 24) & 0xFF000000u);
    }

    /// <summary>
    /// Compute Osram CRC-32 over the given data span.
    /// Sequential byte processing (byte 0, 1, 2, …) matching ECU behavior.
    /// </summary>
    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = OsramCrc32RawSeed;
        for (int i = 0; i < data.Length; i++)
        {
            byte idx = (byte)((crc >> 24) ^ data[i]);
            crc = (crc << 8) ^ OsramCrc32Table[idx];
        }
        return BSwap32(crc);
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


    // ── Self-test ───────────────────────────────────────────────────────

    /// <summary>
    /// Run CRC self-test with known test vectors.
    /// Returns a list of (testName, passed, detail) results.
    /// Throws nothing; all failures are reported in the return value.
    /// </summary>
    public static List<(string Name, bool Passed, string Detail)> RunSelfTest()
    {
        var results = new List<(string Name, bool Passed, string Detail)>();

        // ── CRC-16/CCITT-FALSE standard check value ─────────────────
        // "123456789" → 0x29B1  (per CRC catalogue)
        {
            byte[] data = System.Text.Encoding.ASCII.GetBytes("123456789");
            ushort computed = ComputeCrc16(data);
            const ushort expected = 0x29B1;
            results.Add(("CRC16 '123456789'", computed == expected,
                $"computed=0x{computed:X4}, expected=0x{expected:X4}"));
        }

        // ── CRC-16 empty data ───────────────────────────────────────
        {
            ushort computed = ComputeCrc16(ReadOnlySpan<byte>.Empty);
            const ushort expected = 0xFFFF; // init value, no data processed
            results.Add(("CRC16 empty", computed == expected,
                $"computed=0x{computed:X4}, expected=0x{expected:X4}"));
        }

        // ── CRC-16 all-zero pixel line (Nichia 256 px) ──────────────
        {
            byte[] px = new byte[LvdsProtocol.NichiaW]; // 256 zeros
            ushort computed = ComputeCrc16(px);
            // Build a Nichia verify packet to test VerifyNichiaCrc
            byte crcHi = (byte)(computed >> 8);
            byte crcLo = (byte)(computed & 0xFF);
            bool ok = VerifyNichiaCrc(px, 0, px.Length, crcHi, crcLo);
            results.Add(("CRC16 Nichia 256×0x00", ok,
                $"CRC=0x{computed:X4}, verify={ok}"));
        }

        // ── CRC-16 gradient pixel line (Nichia) ─────────────────────
        {
            byte[] px = new byte[LvdsProtocol.NichiaW];
            for (int i = 0; i < px.Length; i++) px[i] = (byte)i;
            ushort computed = ComputeCrc16(px);
            byte crcHi = (byte)(computed >> 8);
            byte crcLo = (byte)(computed & 0xFF);
            bool ok = VerifyNichiaCrc(px, 0, px.Length, crcHi, crcLo);
            results.Add(("CRC16 Nichia gradient", ok,
                $"CRC=0x{computed:X4}, verify={ok}"));
        }

        // ── Osram CRC-32 known test vectors ─────────────────────────
        // 25600 zero bytes → 0x66844BF6  (verified against Saleae + WinIDEA)
        {
            byte[] px = new byte[25600];
            uint computed = ComputeCrc32(px);
            const uint expected = 0x66844BF6;
            results.Add(("CRC32 Osram 25600×0x00", computed == expected,
                $"computed=0x{computed:X8}, expected=0x{expected:X8}"));
        }

        // ── CRC-32 all-zero pixel line (Osram 320 px) ───────────────
        // 320 zero bytes → 0x20238E83
        {
            byte[] px = new byte[LvdsProtocol.OsramW]; // 320 zeros
            uint computed = ComputeCrc32(px);
            const uint expected = 0x20238E83;
            results.Add(("CRC32 Osram 320×0x00", computed == expected,
                $"CRC=0x{computed:X8}, expected=0x{expected:X8}"));
        }

        // ── CRC-32 Osram verify round-trip (320 zeros) ──────────────
        {
            byte[] px = new byte[LvdsProtocol.OsramW]; // 320 zeros
            uint computed = ComputeCrc32(px);
            byte c0 = (byte)(computed & 0xFF);
            byte c1 = (byte)((computed >> 8) & 0xFF);
            byte c2 = (byte)((computed >> 16) & 0xFF);
            byte c3 = (byte)((computed >> 24) & 0xFF);
            bool ok = VerifyOsramCrc(px, 0, px.Length, c0, c1, c2, c3);
            results.Add(("CRC32 Osram verify round-trip", ok,
                $"CRC=0x{computed:X8}, verify={ok}"));
        }

        // ── CRC-32 gradient pixel line (Osram) ──────────────────────
        {
            byte[] px = new byte[LvdsProtocol.OsramW];
            for (int i = 0; i < px.Length; i++) px[i] = (byte)(i & 0xFF);
            uint computed = ComputeCrc32(px);
            byte c0 = (byte)(computed & 0xFF);
            byte c1 = (byte)((computed >> 8) & 0xFF);
            byte c2 = (byte)((computed >> 16) & 0xFF);
            byte c3 = (byte)((computed >> 24) & 0xFF);
            bool ok = VerifyOsramCrc(px, 0, px.Length, c0, c1, c2, c3);
            results.Add(("CRC32 Osram gradient", ok,
                $"CRC=0x{computed:X8}, verify={ok}"));
        }

        // ── VerifyNichiaCrc with wrong CRC ──────────────────────────
        {
            byte[] px = new byte[LvdsProtocol.NichiaW];
            bool shouldBeFalse = VerifyNichiaCrc(px, 0, px.Length, 0xDE, 0xAD);
            results.Add(("CRC16 wrong CRC rejected", !shouldBeFalse,
                $"verify={shouldBeFalse} (expect false)"));
        }

        // ── VerifyOsramCrc with wrong CRC ───────────────────────────
        {
            byte[] px = new byte[LvdsProtocol.OsramW];
            bool shouldBeFalse = VerifyOsramCrc(px, 0, px.Length, 0xDE, 0xAD, 0xBE, 0xEF);
            results.Add(("CRC32 wrong CRC rejected", !shouldBeFalse,
                $"verify={shouldBeFalse} (expect false)"));
        }

        // ── Nichia row parity round-trip ────────────────────────────
        {
            bool allOk = true;
            string failDetail = "";
            for (int row = 0; row < LvdsProtocol.NichiaH_Lvds; row++)
            {
                byte rb = LvdsProtocol.MakeNichiaRowByte(row);
                int extracted = LvdsProtocol.ExtractNichiaRow(rb);
                bool parityOk = LvdsProtocol.VerifyNichiaRowParity(rb);
                if (extracted != row || !parityOk)
                {
                    allOk = false;
                    failDetail = $"row={row}, byte=0x{rb:X2}, extract={extracted}, parity={parityOk}";
                    break;
                }
            }
            results.Add(("Nichia row parity 0..67", allOk,
                allOk ? "All 68 rows encode/decode correctly" : failDetail));
        }

        return results;
    }

    /// <summary>
    /// Run self-test and format results as a multi-line string for logging.
    /// Returns (allPassed, formattedReport).
    /// </summary>
    public static (bool AllPassed, string Report) RunSelfTestFormatted()
    {
        var results = RunSelfTest();
        bool allPassed = true;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("╔══ LvdsCrc Self-Test ══════════════════════");
        foreach (var (name, passed, detail) in results)
        {
            string mark = passed ? "✓" : "✗";
            sb.AppendLine($"║ {mark} {name}: {detail}");
            if (!passed) allPassed = false;
        }
        sb.AppendLine(allPassed
            ? $"╚══ ALL {results.Count} TESTS PASSED ═══════════════════"
            : $"╚══ SOME TESTS FAILED ═══════════════════════");
        return (allPassed, sb.ToString());
    }
}
