using System;
using System.Buffers.Binary;

namespace VideoStreamPlayer;

public static class AvtpRvfParser
{
    // Matches CAPL: 0x22F0 (AVTP) and subtype RVF, with pixel payload starting at byte 32.
    private const ushort EtherTypeAvtp = 0x22F0;
    private const int AvtpPayloadOffset = 32;

    private const int W = RvfProtocol.W;
    private const int H = RvfProtocol.H;
    private const int NumLines = 4;
    private const int PayloadBytes = W * NumLines; // 1280

    public static bool TryParseAvtpRvfEthernet(ReadOnlySpan<byte> frame, out int line1, out bool endFrame, out byte[] payload)
    {
        line1 = 0;
        endFrame = false;
        payload = Array.Empty<byte>();

        if (frame.Length < 14) return false;
        int o = 12;

        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(o, 2));
        o += 2;

        // Skip VLAN tags (0x8100 802.1Q, 0x88A8 802.1ad)
        while (etherType == 0x8100 || etherType == 0x88A8)
        {
            if (frame.Length < o + 4) return false;
            // TCI
            o += 2;
            etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(o, 2));
            o += 2;
        }

        if (etherType != EtherTypeAvtp) return false;

        // At this point, o is start of AVTP payload (myStream.byte(0) in CAPL)
        var avtp = frame.Slice(o);
        if (avtp.Length < AvtpPayloadOffset + PayloadBytes) return false;

        // CAPL mapping (from docs/canoe/Avtp_new.can):
        // - endFrame: byte 22 is 0x80 or 0x90 => bit 0x10 indicates end
        // - line number (first line in this packet): byte 31 (1,5,9,...,77)
        // - pixel payload: starts at byte 32, length 1280
        endFrame = (avtp[22] & 0x10) != 0;
        line1 = avtp[31];
        if (line1 <= 0 || line1 > H) return false;

        payload = avtp.Slice(AvtpPayloadOffset, PayloadBytes).ToArray();
        return true;
    }
}
