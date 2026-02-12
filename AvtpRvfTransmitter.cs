// AvtpRvfTransmitter.cs
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpPcap;
using SharpPcap.LibPcap;

namespace VilsSharpX
{
    public sealed class AvtpRvfTransmitter : IDisposable
    {
        // Image geometry
        public const int W = 320;
        public const int H = 80;
        private const int LinesPerPacket = 4;
        private const int PayloadBytesPerPacket = W * LinesPerPacket; // 1280
        private const int PacketsPerFrame = H / LinesPerPacket;        // 20

        // Ethernet/VLAN/AVTP sizes
        private const int EthernetHeaderLen = 14; // dst(6)+src(6)+type(2)
        private const int VlanTagLen = 4;         // TPID(2)+TCI(2)
        private const int AvtpPayloadLen = 1312;  // bytes 0..1311
        private const int FrameLen = EthernetHeaderLen + VlanTagLen + AvtpPayloadLen; // 1330

        private readonly LibPcapLiveDevice _dev;

        private readonly byte[] _srcMac;
        private readonly byte[] _dstMac;
        private readonly ushort _vlanTci;   // PCP/DEI/VID
        private readonly ushort _etherType; // 0x22F0

        private readonly byte[] _streamId = new byte[8];
        private byte _seq;

        public AvtpRvfTransmitter(
            string npfDeviceName,
            string srcMac = "3C:CE:15:00:00:19",
            string dstMac = "01:00:5E:16:00:12",
            int vlanId = 70,
            int vlanPriority = 5,
            byte streamIdLastByte = 0x50,
            ushort etherType = 0x22F0)
        {
            // IMPORTANT: we need an injection-capable device => LibPcapLiveDevice
            var dev = CaptureDeviceList.Instance
                .OfType<LibPcapLiveDevice>()
                .FirstOrDefault(d => string.Equals(d.Name, npfDeviceName, StringComparison.OrdinalIgnoreCase));

            if (dev == null)
                throw new InvalidOperationException($"LibPcapLiveDevice not found for: {npfDeviceName}");

            _dev = dev;

            // Open for send (and optionally capture); promiscuous is fine
            _dev.Open(DeviceModes.Promiscuous, read_timeout: 1);

            _srcMac = ParseMac(srcMac);
            _dstMac = ParseMac(dstMac);

            if (vlanId is < 0 or > 4095) throw new ArgumentOutOfRangeException(nameof(vlanId));
            if (vlanPriority is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(vlanPriority));

            // TCI: PCP(3) | DEI(1=0) | VID(12)
            _vlanTci = (ushort)(((vlanPriority & 0x7) << 13) | (vlanId & 0x0FFF));
            _etherType = etherType;

            // Stream ID: first 6 bytes from srcMac + 0x00 + streamIdLastByte (like CAPL)
            Buffer.BlockCopy(_srcMac, 0, _streamId, 0, 6);
            _streamId[6] = 0x00;
            _streamId[7] = streamIdLastByte;

            _seq = 0;
        }

        public void Dispose()
        {
            try { _dev?.Close(); } catch { /* ignore */ }
        }

        public Task SendFrame320x80Async(byte[] gray8_320x80, CancellationToken ct = default)
        {
            if (gray8_320x80 == null) throw new ArgumentNullException(nameof(gray8_320x80));
            if (gray8_320x80.Length != W * H)
                throw new ArgumentException($"Expected {W * H} bytes (320x80 Gray8). Got {gray8_320x80.Length}.");

            int headerCounter = 1; // 1,5,9,...,77 then wrap (CAPL style)

            for (int p = 0; p < PacketsPerFrame; p++)
            {
                ct.ThrowIfCancellationRequested();

                bool endFrame = (p == PacketsPerFrame - 1);
                var ethFrame = BuildOnePacket(gray8_320x80, headerCounter, endFrame);

                // THIS is the key: LibPcapLiveDevice supports SendPacket(byte[])
                _dev.SendPacket(ethFrame);

                headerCounter += 4;
                if (headerCounter == 0x51) headerCounter = 1;
            }

            return Task.CompletedTask;
        }

        private byte[] BuildOnePacket(byte[] img, int headerCounter, bool endFrame)
        {
            var b = new byte[FrameLen];
            int o = 0;

            // Ethernet dst/src
            Buffer.BlockCopy(_dstMac, 0, b, o, 6); o += 6;
            Buffer.BlockCopy(_srcMac, 0, b, o, 6); o += 6;

            // VLAN TPID + TCI
            WriteU16BE(b, o, 0x8100); o += 2;
            WriteU16BE(b, o, _vlanTci); o += 2;

            // EtherType 0x22F0
            WriteU16BE(b, o, _etherType); o += 2;

            int payloadBase = o; // start of 1312 bytes AVTP payload

            // AVTP/RVF header (matches your CAPL)
            b[payloadBase + 0] = 0x07; // subtype RVF
            b[payloadBase + 1] = (headerCounter == 1) ? (byte)0x81 : (byte)0x80; // start flag
            b[payloadBase + 2] = _seq++; // sequence
            b[payloadBase + 3] = 0x00;

            Buffer.BlockCopy(_streamId, 0, b, payloadBase + 4, 8);

            // timestamp = 0
            b[payloadBase + 12] = 0x00;
            b[payloadBase + 13] = 0x00;
            b[payloadBase + 14] = 0x00;
            b[payloadBase + 15] = 0x00;

            // Gateway info
            b[payloadBase + 16] = 0x01;
            b[payloadBase + 17] = 0x40;
            b[payloadBase + 18] = 0x00;
            b[payloadBase + 19] = 0x50;

            // Stream data length 0x0508 (1288)
            b[payloadBase + 20] = 0x05;
            b[payloadBase + 21] = 0x08;

            // Protocol specific header
            b[payloadBase + 22] = endFrame ? (byte)0x90 : (byte)0x80;
            b[payloadBase + 23] = 0x00;

            // Fixed bytes 24..30
            b[payloadBase + 24] = 0x00;
            b[payloadBase + 25] = 0x10;
            b[payloadBase + 26] = 0x30;
            b[payloadBase + 27] = 0x44;
            b[payloadBase + 28] = 0x00;
            b[payloadBase + 29] = 0x00;
            b[payloadBase + 30] = 0x00;

            // line number (1-based) like CAPL headerCounter
            b[payloadBase + 31] = (byte)headerCounter;

            // Copy 4 lines (1280 bytes) into payload[32..]
            int line0 = headerCounter - 1; // 0-based
            int srcOffset = line0 * W;
            int dstOffset = payloadBase + 32;

            Buffer.BlockCopy(img, srcOffset, b, dstOffset, PayloadBytesPerPacket);

            return b;
        }

        private static void WriteU16BE(byte[] b, int o, ushort v)
        {
            b[o + 0] = (byte)(v >> 8);
            b[o + 1] = (byte)(v & 0xFF);
        }

        private static byte[] ParseMac(string mac)
        {
            var parts = mac.Split(':', '-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 6) throw new FormatException($"Invalid MAC: {mac}");
            return parts.Select(p => Convert.ToByte(p, 16)).ToArray();
        }
    }
}
