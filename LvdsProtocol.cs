namespace VilsSharpX;

/// <summary>
/// LVDS serial protocol constants for ECU↔LSM communication.
/// The physical layer uses onsemi NBA3N011S (driver) / NBA3N012C (receiver) LVDS transceivers
/// at 3.3V. The data layer is asynchronous UART (8-bit, LSB-first, non-inverted).
/// </summary>
public static class LvdsProtocol
{
    // ── Sync / framing ──────────────────────────────────────────────────
    /// <summary>Start-of-frame sync byte 1 (observed in Saleae captures).</summary>
    public const byte Sync0 = 0x5D;

    /// <summary>Start-of-frame sync byte 2 for ECU→LSM direction.</summary>
    public const byte Sync1_Ecu = 0x53;

    /// <summary>Start-of-frame sync byte 2 for LSM→ECU direction (response).</summary>
    public const byte Sync1_Lsm = 0x33;

    /// <summary>Minimum header length after sync bytes (to be refined after full protocol analysis).</summary>
    public const int MinHeaderLen = 4;

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

    // ── UART parameters per device type ─────────────────────────────────

    /// <summary>
    /// Returns the UART configuration for the given device type.
    /// </summary>
    public static LvdsUartConfig GetUartConfig(LsmDeviceType deviceType)
    {
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

    /// <summary>Full LVDS frame width (pixels).</summary>
    public int FrameWidth { get; init; }

    /// <summary>Full LVDS frame height including metadata lines.</summary>
    public int FrameHeight { get; init; }

    /// <summary>Active (cropped) frame height.</summary>
    public int ActiveHeight { get; init; }

    /// <summary>Total bytes in a full LVDS frame (width × height with metadata).</summary>
    public int FrameBytes => FrameWidth * FrameHeight;

    /// <summary>Total bytes in the active (cropped) frame.</summary>
    public int ActiveBytes => FrameWidth * ActiveHeight;

    /// <summary>Number of UART bits per byte (data + start + stop + parity).</summary>
    public int BitsPerByte => 1 + DataBits + (Parity != System.IO.Ports.Parity.None ? 1 : 0)
                              + (StopBits == System.IO.Ports.StopBits.Two ? 2 : 1);
}
