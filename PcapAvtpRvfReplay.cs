using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoStreamPlayer;

public static class PcapAvtpRvfReplay
{
    private const int W = RvfProtocol.W;
    private const int H = RvfProtocol.H;
    private const int NumLines = 4;
    private const int PayloadBytes = W * NumLines; // 1280

    public static async Task ReplayAsync(
        string path,
        Action<RvfChunk> onChunk,
        Action<string>? log,
        CancellationToken ct,
        double speed = 1.0,
        ManualResetEventSlim? pauseGate = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("PCAP file not found", path);
        if (speed <= 0) speed = 1.0;

        using var fs = File.OpenRead(path);

        uint first = ReadU32LE(fs);
        fs.Position = 0;

        if (first == 0x0A0D0D0Au)
        {
            await ReplayPcapNgAsync(fs, onChunk, log, ct, speed, pauseGate).ConfigureAwait(false);
        }
        else
        {
            await ReplayPcapAsync(fs, onChunk, log, ct, speed, pauseGate).ConfigureAwait(false);
        }
    }

    private static async Task ReplayPcapAsync(
        Stream fs,
        Action<RvfChunk> onChunk,
        Action<string>? log,
        CancellationToken ct,
        double speed,
        ManualResetEventSlim? pauseGate)
    {
        // PCAP global header (24 bytes)
        uint magic = ReadU32LE(fs);
        bool swap = false;
        bool tsIsNano = false;
        if (magic == 0xD4C3B2A1u || magic == 0x4D3CB2A1u)
            swap = true;
        else if (magic != 0xA1B2C3D4u && magic != 0xA1B23C4Du)
            throw new InvalidDataException($"Unknown PCAP magic 0x{magic:X8}");

        // 0xA1B23C4D / 0x4D3CB2A1 => timestamps are in nanoseconds (tsSub)
        if (magic == 0xA1B23C4Du || magic == 0x4D3CB2A1u)
            tsIsNano = true;

        ushort vMajor = ReadU16(fs, swap);
        ushort vMinor = ReadU16(fs, swap);
        _ = ReadU32(fs, swap); // thiszone
        _ = ReadU32(fs, swap); // sigfigs
        _ = ReadU32(fs, swap); // snaplen
        uint network = ReadU32(fs, swap);

        if (network != 1)
            log?.Invoke($"PCAP linktype={network} (expected Ethernet=1). Will still try.");

        long? lastTsUs = null;
        uint seq = 0;
        uint frameId = 0;
        int packets = 0;
        int avtpMatches = 0;
        int chunks = 0;

        while (fs.Position < fs.Length)
        {
            ct.ThrowIfCancellationRequested();
            pauseGate?.Wait(ct);
            if (!TryReadPacketHeader(fs, swap, out uint tsSec, out uint tsSub, out uint inclLen)) break;

            packets++;

            byte[] data = new byte[inclLen];
            ReadExactly(fs, data);

            long tsUs = tsIsNano
                ? ((long)tsSec * 1_000_000L + (tsSub / 1000L))
                : ((long)tsSec * 1_000_000L + tsSub);

            pauseGate?.Wait(ct);
            await DelayByTimestampAsync(lastTsUs, tsUs, speed, ct).ConfigureAwait(false);
            lastTsUs = tsUs;

            if (AvtpRvfParser.TryParseAvtpRvfEthernet(data, out var line1, out var endFrame, out var payload))
            {
                avtpMatches++;
                onChunk(new RvfChunk((ushort)W, (ushort)H, (ushort)line1, (byte)NumLines, endFrame, frameId, seq++, payload));
                chunks++;
                if (endFrame) frameId++;
            }
        }

        log?.Invoke($"PCAP replay finished. packets={packets} avtpMatches={avtpMatches} chunks={chunks} frames={frameId}");
    }

    private static bool TryReadPacketHeader(Stream fs, bool swap, out uint tsSec, out uint tsSub, out uint inclLen)
    {
        tsSec = tsSub = inclLen = 0;
        Span<byte> hdr = stackalloc byte[16];
        int r = fs.Read(hdr);
        if (r == 0) return false;
        if (r != hdr.Length) throw new EndOfStreamException();

        tsSec = ReadU32(hdr.Slice(0, 4), swap);
        tsSub = ReadU32(hdr.Slice(4, 4), swap);
        inclLen = ReadU32(hdr.Slice(8, 4), swap);
        _ = ReadU32(hdr.Slice(12, 4), swap); // origLen
        return true;
    }

    private static async Task ReplayPcapNgAsync(
        Stream fs,
        Action<RvfChunk> onChunk,
        Action<string>? log,
        CancellationToken ct,
        double speed,
        ManualResetEventSlim? pauseGate)
    {
        // Minimal PCAPNG support: SHB + IDB + EPB/SPB.
        // Endianness is per-section; we support little endian sections.

        var ifaces = new Dictionary<uint, (ushort linkType, double tsResSeconds)>();
        long? lastTsUs = null;
        uint seq = 0;
        uint frameId = 0;
        int packets = 0;
        int avtpMatches = 0;
        int chunks = 0;

        while (fs.Position < fs.Length)
        {
            ct.ThrowIfCancellationRequested();
            pauseGate?.Wait(ct);

            uint blockType = ReadU32LE(fs);
            uint totalLen = ReadU32LE(fs);
            if (totalLen < 12) throw new InvalidDataException("PCAPNG: invalid block length");

            long blockStart = fs.Position - 8;
            int bodyLen = checked((int)totalLen - 12);

            switch (blockType)
            {
                case 0x0A0D0D0Au: // SHB
                    {
                        // skip body, but validate byte-order magic
                        byte[] body = new byte[bodyLen];
                        ReadExactly(fs, body);
                        uint bom = ReadU32LE(body, 0);
                        if (bom != 0x1A2B3C4Du)
                            log?.Invoke($"PCAPNG: unsupported endianness BOM=0x{bom:X8} (expected 0x1A2B3C4D). Attempting anyway.");
                        break;
                    }

                case 0x00000001u: // IDB
                    {
                        byte[] body = new byte[bodyLen];
                        ReadExactly(fs, body);

                        ushort linkType = ReadU16LE(body, 0);
                        // ushort reserved = ...
                        // uint snapLen = ...

                        uint ifaceId = (uint)ifaces.Count;

                        // Default timestamp resolution: 1e-6 seconds (microseconds)
                        double tsRes = 1e-6;

                        // Parse options: (code uint16, length uint16, value padded to 32-bit)
                        int o = 8;
                        while (o + 4 <= body.Length)
                        {
                            ushort code = ReadU16LE(body, o);
                            ushort len = ReadU16LE(body, o + 2);
                            o += 4;
                            if (code == 0) break;
                            if (o + len > body.Length) break;
                            if (code == 9 && len >= 1) // if_tsresol
                            {
                                byte v = body[o];
                                bool isPowerOf2 = (v & 0x80) != 0;
                                int exp = v & 0x7F;
                                tsRes = isPowerOf2 ? 1.0 / (1 << exp) : Math.Pow(10, -exp);
                            }
                            int padded = (len + 3) & ~3;
                            o += padded;
                        }

                        ifaces[ifaceId] = (linkType, tsRes);
                        break;
                    }

                case 0x00000006u: // EPB
                    {
                        byte[] hdr = new byte[20];
                        ReadExactly(fs, hdr);
                        uint ifaceId = ReadU32LE(hdr, 0);
                        uint tsHigh = ReadU32LE(hdr, 4);
                        uint tsLow = ReadU32LE(hdr, 8);
                        uint capLen = ReadU32LE(hdr, 12);
                        _ = ReadU32LE(hdr, 16); // pktLen

                        int remainingBody = bodyLen - hdr.Length;
                        if (remainingBody < 0) throw new InvalidDataException("PCAPNG: EPB too short");

                        byte[] packet = new byte[capLen];
                        ReadExactly(fs, packet);
                        packets++;

                        // skip options (remaining bytes minus packet bytes)
                        int optionsLen = remainingBody - (int)capLen;
                        if (optionsLen > 0) fs.Position += optionsLen;

                        double tsRes = ifaces.TryGetValue(ifaceId, out var inf) ? inf.tsResSeconds : 1e-6;
                        ulong ts = ((ulong)tsHigh << 32) | tsLow;
                        long tsUs = (long)(ts * tsRes * 1_000_000.0);

                        pauseGate?.Wait(ct);
                        await DelayByTimestampAsync(lastTsUs, tsUs, speed, ct).ConfigureAwait(false);
                        lastTsUs = tsUs;

                        if (AvtpRvfParser.TryParseAvtpRvfEthernet(packet, out var line1, out var endFrame, out var payload))
                        {
                            avtpMatches++;
                            onChunk(new RvfChunk((ushort)W, (ushort)H, (ushort)line1, (byte)NumLines, endFrame, frameId, seq++, payload));
                            chunks++;
                            if (endFrame) frameId++;
                        }
                        break;
                    }

                case 0x00000003u: // SPB
                    {
                        // Simple Packet Block: uint32 origLen then packet data (captured length = totalLen - 16)
                        uint origLen = ReadU32LE(fs);
                        int capLen = bodyLen - 4;
                        byte[] packet = new byte[capLen];
                        ReadExactly(fs, packet);
                        _ = origLen;
                        packets++;

                        if (AvtpRvfParser.TryParseAvtpRvfEthernet(packet, out var line1, out var endFrame, out var payload))
                        {
                            avtpMatches++;
                            onChunk(new RvfChunk((ushort)W, (ushort)H, (ushort)line1, (byte)NumLines, endFrame, frameId, seq++, payload));
                            chunks++;
                            if (endFrame) frameId++;
                        }
                        break;
                    }

                default:
                    // skip unknown block body
                    fs.Position += bodyLen;
                    break;
            }

            // trailing total length
            uint totalLen2 = ReadU32LE(fs);
            if (totalLen2 != totalLen)
            {
                // Try to recover by seeking to computed end
                fs.Position = blockStart + totalLen;
            }
        }

        log?.Invoke($"PCAPNG replay finished. packets={packets} avtpMatches={avtpMatches} chunks={chunks} frames={frameId}");
    }

    // Parsing is shared with live capture via AvtpRvfParser.

    private static async Task DelayByTimestampAsync(long? lastTsUs, long tsUs, double speed, CancellationToken ct)
    {
        if (!lastTsUs.HasValue) return;
        long deltaUs = tsUs - lastTsUs.Value;
        if (deltaUs <= 0) return;

        double ms = (deltaUs / 1000.0) / speed;
        if (ms <= 0.1) return;
        if (ms > 250) ms = 250; // avoid huge pauses on sparse captures
        await Task.Delay(TimeSpan.FromMilliseconds(ms), ct).ConfigureAwait(false);
    }

    private static void ReadExactly(Stream s, Span<byte> buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int r = s.Read(buf.Slice(read));
            if (r <= 0) throw new EndOfStreamException();
            read += r;
        }
    }

    private static void ReadExactly(Stream s, byte[] buf) => ReadExactly(s, buf.AsSpan());

    private static ushort ReadU16(Stream s, bool swap)
    {
        Span<byte> b = stackalloc byte[2];
        ReadExactly(s, b);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(b);
        return swap ? BinaryPrimitives.ReverseEndianness(v) : v;
    }

    private static uint ReadU32(Stream s, bool swap)
    {
        Span<byte> b = stackalloc byte[4];
        ReadExactly(s, b);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(b);
        return swap ? BinaryPrimitives.ReverseEndianness(v) : v;
    }

    private static uint ReadU32LE(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        ReadExactly(s, b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, bool swap)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(b);
        return swap ? BinaryPrimitives.ReverseEndianness(v) : v;
    }

    private static ushort ReadU16LE(byte[] b, int offset)
    {
        return (ushort)(b[offset] | (b[offset + 1] << 8));
    }

    private static uint ReadU32LE(byte[] b, int offset)
    {
        return (uint)(b[offset]
            | (b[offset + 1] << 8)
            | (b[offset + 2] << 16)
            | (b[offset + 3] << 24));
    }
}
