using System;
using System.Linq;
using SharpPcap;
using SharpPcap.LibPcap;

namespace VilsSharpX;

/// <summary>
/// Locks the AURIX AVTP reassembler onto a single Ethernet source MAC.
/// Protocol: ethertype 0x88B5, magic "CM" (0x434D), cmd 0x0D = AVTP_SOURCE.
/// Payload byte [17] = arm (0 = accept every source), bytes [18..23] = source MAC.
/// </summary>
public static class AvtpSourceFilterCommand
{
    private const ushort Ethertype = 0x88B5;
    private const ushort MagicCommand = 0x434D;  // "CM"
    private const byte CmdAvtpSource = 0x0D;

    /// <summary>
    /// Sends the AVTP source filter to the SmartVisio Box.
    /// Sends 3× for reliability (no ACK protocol).
    /// </summary>
    /// <param name="srcMac">Source MAC to follow, or null/empty to accept every source.</param>
    public static void Send(string pcapDeviceName, string? srcMac, Action<string>? log = null)
    {
        byte[]? mac = ParseMacOrNull(srcMac);

        var pkt = new byte[60];

        // Dst MAC: broadcast
        pkt[0] = pkt[1] = pkt[2] = pkt[3] = pkt[4] = pkt[5] = 0xFF;

        // Src MAC: locally-administered (same as the other command senders)
        pkt[6] = 0x02; pkt[7] = 0x0A; pkt[8] = 0xF0;
        pkt[9] = 0x4E; pkt[10] = 0x49; pkt[11] = 0x02;

        pkt[12] = (byte)(Ethertype >> 8);
        pkt[13] = (byte)(Ethertype & 0xFF);

        pkt[14] = (byte)(MagicCommand >> 8);
        pkt[15] = (byte)(MagicCommand & 0xFF);
        pkt[16] = CmdAvtpSource;
        pkt[17] = mac != null ? (byte)1 : (byte)0;
        if (mac != null)
            Buffer.BlockCopy(mac, 0, pkt, 18, 6);

        var existing = CaptureDeviceList.Instance
            .OfType<LibPcapLiveDevice>()
            .FirstOrDefault(d => string.Equals(d.Name, pcapDeviceName, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            log?.Invoke($"[cmd] NIC not found for AVTP source command: {pcapDeviceName}");
            return;
        }

        var txDev = new LibPcapLiveDevice(existing.Interface);
        txDev.Open(DeviceModes.Promiscuous, read_timeout: 1);
        try
        {
            for (int i = 0; i < 3; i++)
                txDev.SendPacket(pkt);

            log?.Invoke(mac != null
                ? $"[cmd] Sent AVTP_SOURCE → lock on {srcMac}"
                : "[cmd] Sent AVTP_SOURCE → accept every source");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[cmd] Send error: {ex.Message}");
        }
        finally
        {
            txDev.Close();
        }
    }

    private static byte[]? ParseMacOrNull(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return null;

        var parts = mac.Split(':', '-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6)
            return null;

        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                return null;
        }
        return bytes;
    }
}
