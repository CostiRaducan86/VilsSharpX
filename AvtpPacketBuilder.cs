using System;

namespace VideoStreamPlayer
{
    public static class AvtpPacketBuilder
    {
        // Fixed MAC and IP for AVTP (as in WireShark capture)
        public static readonly byte[] DestMac = { 0x01, 0x00, 0x5e, 0x16, 0x00, 0x12 };
        public static readonly byte[] SrcMac = { 0x3c, 0xce, 0x15, 0x00, 0x19, 0x81 }; // Example, replace with your adapter's MAC
        public const ushort EthTypeVlan = 0x8100;
        public const ushort EthTypeAvtp = 0x22f0;
        public const ushort VlanTag = 0x0005; // Priority 5, VLAN 0

        // Build a single AVTP Ethernet frame (VLAN + AVTP header + payload)
        public static byte[] BuildAvtpFrame(byte[] payload)
        {
            // Ethernet II + VLAN (18 bytes) + AVTP header (12 bytes) + payload
            var frame = new byte[18 + 12 + payload.Length];
            int i = 0;
            // Ethernet II
            Buffer.BlockCopy(DestMac, 0, frame, i, 6); i += 6;
            Buffer.BlockCopy(SrcMac, 0, frame, i, 6); i += 6;
            frame[i++] = (byte)(EthTypeVlan >> 8); frame[i++] = (byte)(EthTypeVlan & 0xFF);
            frame[i++] = (byte)(VlanTag >> 8); frame[i++] = (byte)(VlanTag & 0xFF);
            frame[i++] = (byte)(EthTypeAvtp >> 8); frame[i++] = (byte)(EthTypeAvtp & 0xFF);
            // AVTP header (dummy, 12 bytes)
            for (int j = 0; j < 12; j++) frame[i++] = 0x14; // Fill with 0x14 as in capture
            // Payload
            Buffer.BlockCopy(payload, 0, frame, i, payload.Length);
            return frame;
        }
    }
}
