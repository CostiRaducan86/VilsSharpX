namespace VilsSharpX;

/// <summary>
/// LVDS serial protocol constants for ECU↔LSM communication.
///
/// Protocol (per-line packet, confirmed by Saleae captures + ECU firmware source):
///
///   Nichia line packet (260 bytes):
///     [0x5D] [addr|parity] [256 pixels] [CRC16_H] [CRC16_L]
///     - Byte 0: sync = 0x5D
///     - Byte 1: [odd_parity_bit:1][row_address:7]  (row 0 → 0x80)
///     - Bytes 2..257: pixel data (X_PIX_NIC = 256)
///     - Bytes 258..259: CRC16 over pixel data only (bytes 2..257)
///       CRC16 algorithm: ioHwAbsTLD816K_Crc16 (poly TBD)
///
///   Osram line packet (326 bytes, tentative):
///     [0x5D] [row_addr] [320 pixels] [CRC32 × 4 bytes]
///     - Byte 0: sync = 0x5D
///     - Byte 1: row address (raw, UART-level 8O1 handles parity)
///     - Bytes 2..321: pixel data (320 px)
///     - Bytes 322..325: CRC32 with seed (UART_APPL_2_CRC32_SEED_VALUE)
///       Uses hardware __crc32lw intrinsic with __shuffle bit-reflection
///
///   Lines are sent sequentially row 0..H-1, with an idle gap between lines.
///   CRC covers pixel data only (NOT sync/row byte).
///
/// Physical layer: onsemi NBA3N011S (driver) / NBA3N012C (receiver) LVDS
/// transceivers at 3.3V. Data layer is asynchronous UART (8-bit, LSB-first,
/// non-inverted).
/// </summary>
public static class LvdsProtocol
{
    // ── Sync / framing ──────────────────────────────────────────────────
    /// <summary>Line sync byte (0x5D), first byte of every line packet.</summary>
    public const byte SyncByte = 0x5D;

    /// <summary>Header bytes per line packet: sync(1) + row_byte(1).</summary>
    public const int LineHeaderLen = 2;

    // ── Baud rates ──────────────────────────────────────────────────────
    /// <summary>Nichia LVDS baud rate: 12.5 Mbps, 8N1 (no parity).</summary>
    public const int BaudRateNichia = 12_500_000;

    /// <summary>Osram LVDS baud rate: 20 Mbps, 8O1 (odd parity).</summary>
    public const int BaudRateOsram = 20_000_000;

    // ── Frame dimensions ────────────────────────────────────────────────
    /// <summary>Osram LVDS frame: 320×84 (active 320×80 + 4 metadata lines).</summary>
    public const int OsramW = 320;
    public const int OsramH_Lvds = 84;
    public const int OsramH_Active = 80;

    /// <summary>Nichia LVDS frame: 256×68 (active 256×64 + 4 metadata lines).</summary>
    public const int NichiaW = 256;
    public const int NichiaH_Lvds = 68;
    public const int NichiaH_Active = 64;

    /// <summary>Metadata lines at bottom of LVDS frame to be cropped.</summary>
    public const int MetadataLines = 4;

    // ── CRC ─────────────────────────────────────────────────────────────
    /// <summary>Nichia per-line CRC: CRC16 (2 bytes), computed over pixel data only.</summary>
    public const int NichiaCrcLen = 2;

    /// <summary>Osram per-line CRC: CRC32 (4 bytes), computed over pixel data only.</summary>
    public const int OsramCrcLen = 4;

    // ── Row address helpers ─────────────────────────────────────────────

    /// <summary>
    /// Extract the 7-bit row address from byte 1 of a Nichia line packet.
    /// Format: [odd_parity_bit:1][row_address:7]
    /// Example: row 0 → byte1 = 0x80 (parity=1, addr=0x00).
    /// </summary>
    public static int ExtractNichiaRow(byte rowByte) => rowByte & 0x7F;

    /// <summary>
    /// Compute the Nichia row byte from a row index (odd parity in MSB).
    /// </summary>
    public static byte MakeNichiaRowByte(int row)
    {
        byte addr = (byte)(row & 0x7F);
        int ones = System.Numerics.BitOperations.PopCount((uint)addr);
        // Odd parity: set MSB so total 1-bits (including parity) is odd
        byte parity = (byte)((ones % 2 == 0) ? 0x80 : 0x00);
        return (byte)(parity | addr);
    }

    /// <summary>
    /// Verify the odd-parity bit in a Nichia row byte.
    /// Returns true if parity is correct.
    /// </summary>
    public static bool VerifyNichiaRowParity(byte rowByte)
    {
        int ones = System.Numerics.BitOperations.PopCount((uint)rowByte);
        return (ones % 2) == 1; // odd parity → total 1-bits must be odd
    }

    /// <summary>
    /// Extract row address from byte 1 of an Osram line packet.
    /// Osram uses 8O1 UART (hardware parity), so byte 1 is the raw row number.
    /// </summary>
    public static int ExtractOsramRow(byte rowByte) => rowByte;

    // ── UART parameters per device type ─────────────────────────────────

    /// <summary>
    /// Returns the UART configuration for the given device type.
    /// </summary>
    public static LvdsUartConfig GetUartConfig(LsmDeviceType deviceType)
    {
        bool isNichia = deviceType == LsmDeviceType.Nichia;
        return deviceType switch
        {
            LsmDeviceType.Nichia => new LvdsUartConfig
            {
                BaudRate = BaudRateNichia,
                DataBits = 8,
                Parity = System.IO.Ports.Parity.None,
                StopBits = System.IO.Ports.StopBits.One,
                FrameWidth = NichiaW,
                FrameHeight = NichiaH_Lvds,
                ActiveHeight = NichiaH_Active,
                CrcLen = NichiaCrcLen,
                IsNichia = true,
            },
            // Osram 2.0 and 2.05 share the same UART parameters
            _ => new LvdsUartConfig
            {
                BaudRate = BaudRateOsram,
                DataBits = 8,
                Parity = System.IO.Ports.Parity.Odd,
                StopBits = System.IO.Ports.StopBits.One,
                FrameWidth = OsramW,
                FrameHeight = OsramH_Lvds,
                ActiveHeight = OsramH_Active,
                CrcLen = OsramCrcLen,
                IsNichia = false,
            },
        };
    }
}

/// <summary>
/// UART configuration for a specific LVDS device type.
/// </summary>
public sealed class LvdsUartConfig
{
    public int BaudRate { get; init; }
    public int DataBits { get; init; }
    public System.IO.Ports.Parity Parity { get; init; }
    public System.IO.Ports.StopBits StopBits { get; init; }

    /// <summary>Full LVDS frame width (pixels per line).</summary>
    public int FrameWidth { get; init; }

    /// <summary>Full LVDS frame height including metadata lines.</summary>
    public int FrameHeight { get; init; }

    /// <summary>Active (cropped) frame height.</summary>
    public int ActiveHeight { get; init; }

    /// <summary>CRC bytes at the end of each line packet.</summary>
    public int CrcLen { get; init; }

    /// <summary>True for Nichia (row byte has software parity), false for Osram.</summary>
    public bool IsNichia { get; init; }

    /// <summary>Total bytes in a single line packet: sync(1) + row(1) + W + CRC.</summary>
    public int LinePacketLen => LvdsProtocol.LineHeaderLen + FrameWidth + CrcLen;

    /// <summary>Total bytes in a full LVDS frame (width × height with metadata).</summary>
    public int FrameBytes => FrameWidth * FrameHeight;

    /// <summary>Total bytes in the active (cropped) frame.</summary>
    public int ActiveBytes => FrameWidth * ActiveHeight;

    /// <summary>Number of UART bits per byte (data + start + stop + parity).</summary>
    public int BitsPerByte => 1 + DataBits + (Parity != System.IO.Ports.Parity.None ? 1 : 0)
                              + (StopBits == System.IO.Ports.StopBits.Two ? 2 : 1);
}
