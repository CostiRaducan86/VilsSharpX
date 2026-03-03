using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SharpPcap;

namespace VilsSharpX;

/// <summary>
/// Captures Nichia Frame Ethernet (NFE) packets from the network using
/// SharpPcap and reassembles fragmented frames for rendering on pane B.
///
/// Protocol:
///   Ethertype 0x88B5 (IEEE 802 Local Experimental 1)
///   18-byte NFE header + up to 1482 bytes pixel data per fragment
///   12 fragments per 256×64 = 16 384-byte frame
///
/// This class follows the same pattern as <see cref="AvtpLiveCapture"/>.
/// </summary>
public sealed class NichiaEthCapture : IDisposable
{
    // ── NFE protocol constants ──
    private const ushort NfeEthertype = 0x88B5;
    private const ushort NfeMagic = 0x4E49; // "NI"
    private const int NfeHdrLen = 18;
    private const int EthHdrLen = 14; // dst(6) + src(6) + ethertype(2)
    private const int MinPacketLen = EthHdrLen + NfeHdrLen;

    // ── Image geometry ──
    private const int NichiaW = 256;
    private const int NichiaH = 64;
    private const int FrameBytes = NichiaW * NichiaH; // 16 384

    // ── BPF filter ──
    // Combined: include AVTP (0x22F0) ethertypes because AvtpLiveCapture,
    // NichiaEthCapture and AvtpRvfTransmitter all share the SAME SharpPcap
    // device object from CaptureDeviceList.Instance (singleton).  The LAST
    // filter set on the device wins, so it must pass BOTH ethertypes.
    // Each handler already validates its own ethertype/format in code.
    private const string NfeBpfFilter =
        "ether proto 0x88b5 or ether proto 0x22f0"
        + " or (vlan and ether proto 0x22f0)"
        + " or (vlan and vlan and ether proto 0x22f0)";

    // ── Capture state ──
    private readonly ICaptureDevice _device;
    private readonly Action<string>? _log;

    // ── Reassembly state ──
    // We track the current frame being assembled by its frameSeq.
    // When a new frameSeq arrives, the previous (incomplete) frame is discarded.
    private ushort _currentFrameSeq;
    private readonly byte[] _frameBuf = new byte[FrameBytes];
    private readonly bool[] _fragReceived = new bool[12]; // max 12 fragments
    private int _fragsExpected;
    private int _fragsReceived;
    private bool _assembling;

    // ── Statistics ──
    private uint _framesCompleted;
    private uint _framesDropped; // incomplete frames discarded
    private long _totalPackets;
    private long _totalBytes;

    // ── FPS estimation ──
    private readonly Stopwatch _fpsSw = Stopwatch.StartNew();
    private int _fpsFrameCount;
    private double _fpsEma;
    private const double FpsAlpha = 0.3;
    private const double FpsWindowSec = 0.5;

    /// <summary>
    /// Fired on the pcap callback thread when a complete 256×64 frame is
    /// reassembled.  Subscribers must marshal to the UI thread.
    /// </summary>
    public event Action<byte[], LvdsFrameMeta>? OnFrameReady;

    /// <summary>Current EMA-smoothed FPS.</summary>
    public double FpsEma => _fpsEma;

    /// <summary>Number of complete frames received.</summary>
    public uint FramesCompleted => _framesCompleted;

    /// <summary>Frames dropped due to incomplete fragment sets.</summary>
    public uint FramesDropped => _framesDropped;

    /// <summary>Total raw packets captured.</summary>
    public long TotalPackets => _totalPackets;

    public bool IsCapturing { get; private set; }

    private NichiaEthCapture(ICaptureDevice device, Action<string>? log)
    {
        _device = device;
        _log = log;
    }

    // ── Factory / Start ──

    public static NichiaEthCapture Start(
        string? deviceHint,
        Action<string>? log)
    {
        var devices = CaptureDeviceList.Instance;
        if (devices.Count == 0)
            throw new InvalidOperationException("No capture devices found. Is Npcap installed?");

        ICaptureDevice? dev = null;

        if (!string.IsNullOrWhiteSpace(deviceHint))
        {
            string hint = deviceHint.Trim();
            dev = devices.FirstOrDefault(d =>
                (!string.IsNullOrEmpty(d.Name) && d.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(d.Description) && d.Description.Contains(hint, StringComparison.OrdinalIgnoreCase)));
        }

        // Auto-pick: prefer Ethernet adapters, avoid loopback/virtual
        dev ??= devices
            .OrderByDescending(d => ScoreForAutoPick(d))
            .FirstOrDefault(d => !LooksLikeLoopback(d))
            ?? devices[0];

        log?.Invoke($"[nfe] using device: name='{dev.Name}' desc='{dev.Description}'");

        var cap = new NichiaEthCapture(dev, log);

        // Open in promiscuous mode (so we don't need our NIC's MAC to match).
        cap._device.Open(DeviceModes.Promiscuous, 1000);

        try
        {
            cap._device.Filter = NfeBpfFilter;
        }
        catch
        {
            log?.Invoke("[nfe] BPF filter failed; capturing all traffic (ethertype check in code).");
        }

        cap._device.OnPacketArrival += cap.Device_OnPacketArrival;
        cap._device.StartCapture();
        cap.IsCapturing = true;

        return cap;
    }

    // ── Packet arrival handler ──

    private void Device_OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var raw = e.GetPacket();
            var data = raw?.Data;
            if (data == null || data.Length < MinPacketLen) return;

            Interlocked.Increment(ref _totalPackets);
            Interlocked.Add(ref _totalBytes, data.Length);

            // Verify ethertype (bytes 12-13, big-endian)
            ushort ethertype = (ushort)((data[12] << 8) | data[13]);
            if (ethertype != NfeEthertype) return;

            // Parse NFE header (starts at byte 14)
            int hdrOff = EthHdrLen;
            ushort magic = ReadBE16(data, hdrOff + 0);
            if (magic != NfeMagic) return;

            ushort frameSeq = ReadBE16(data, hdrOff + 2);
            byte fragIdx = data[hdrOff + 4];
            byte fragCnt = data[hdrOff + 5];
            ushort dataOffset = ReadBE16(data, hdrOff + 6);
            ushort dataLen = ReadBE16(data, hdrOff + 8);
            ushort width = ReadBE16(data, hdrOff + 10);
            ushort height = ReadBE16(data, hdrOff + 12);
            // uint timestamp = ReadBE32(data, hdrOff + 14); // available if needed

            // Sanity checks
            if (width != NichiaW || height != NichiaH) return;
            if (fragIdx >= fragCnt || fragCnt == 0 || fragCnt > 20) return;
            if (dataOffset + dataLen > FrameBytes) return;
            if (hdrOff + NfeHdrLen + dataLen > data.Length) return;

            // New frame sequence? Discard any in-progress assembly.
            if (!_assembling || frameSeq != _currentFrameSeq)
            {
                if (_assembling && _fragsReceived > 0 && _fragsReceived < _fragsExpected)
                    _framesDropped++;

                _currentFrameSeq = frameSeq;
                _fragsExpected = fragCnt;
                _fragsReceived = 0;
                Array.Clear(_fragReceived, 0, _fragReceived.Length);
                _assembling = true;
            }

            // Copy pixel data into frame buffer (if not already received)
            if (fragIdx < _fragReceived.Length && !_fragReceived[fragIdx])
            {
                Buffer.BlockCopy(data, hdrOff + NfeHdrLen, _frameBuf, dataOffset, dataLen);
                _fragReceived[fragIdx] = true;
                _fragsReceived++;
            }

            // All fragments received → emit complete frame
            if (_fragsReceived >= _fragsExpected)
            {
                _assembling = false;
                _framesCompleted++;
                UpdateFps();

                // Copy frame buffer (callers may hold reference)
                var frameOut = new byte[FrameBytes];
                Buffer.BlockCopy(_frameBuf, 0, frameOut, 0, FrameBytes);

                var meta = new LvdsFrameMeta
                {
                    FrameId = _framesCompleted,
                    Width = NichiaW,
                    Height = NichiaH,
                    LinesReceived = NichiaH,
                    ValidLines = NichiaH,
                    LinesExpected = NichiaH,
                    LineValidityMask = CreateAllTrueMask(NichiaH),
                    SyncLosses = 0,
                    CrcErrors = 0,
                    ParityErrors = 0,
                    TotalBytes = Interlocked.Read(ref _totalBytes),
                };

                OnFrameReady?.Invoke(frameOut, meta);
            }
        }
        catch
        {
            // Swallow capture callback errors to prevent capture thread death.
        }
    }

    // ── FPS ──

    private void UpdateFps()
    {
        Interlocked.Increment(ref _fpsFrameCount);
        if (_fpsSw.Elapsed.TotalSeconds >= FpsWindowSec)
        {
            double sec = _fpsSw.Elapsed.TotalSeconds;
            double instant = Interlocked.Exchange(ref _fpsFrameCount, 0) / sec;
            _fpsSw.Restart();
            _fpsEma = _fpsEma <= 0 ? instant : _fpsEma * (1.0 - FpsAlpha) + instant * FpsAlpha;
        }
    }

    // ── Helpers ──

    private static ushort ReadBE16(byte[] buf, int off)
        => (ushort)((buf[off] << 8) | buf[off + 1]);

    // private static uint ReadBE32(byte[] buf, int off)
    //     => (uint)((buf[off] << 24) | (buf[off + 1] << 16) | (buf[off + 2] << 8) | buf[off + 3]);

    private static bool[] CreateAllTrueMask(int count)
    {
        var mask = new bool[count];
        Array.Fill(mask, true);
        return mask;
    }

    // ── Device scoring (same heuristics as AvtpLiveCapture) ──

    private static bool LooksLikeLoopback(ICaptureDevice d)
    {
        var name = d.Name ?? string.Empty;
        var desc = d.Description ?? string.Empty;
        return name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreForAutoPick(ICaptureDevice d)
    {
        if (LooksLikeLoopback(d)) return int.MinValue / 2;
        var desc = d.Description ?? string.Empty;
        int score = 0;
        if (desc.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)) score += 60;
        if (desc.Contains("Gigabit", StringComparison.OrdinalIgnoreCase)) score += 10;
        if (desc.Contains("Intel", StringComparison.OrdinalIgnoreCase)) score += 5;
        if (desc.Contains("Realtek", StringComparison.OrdinalIgnoreCase)) score += 5;
        if (desc.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Wireless", StringComparison.OrdinalIgnoreCase)) score -= 5;
        if (desc.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)) score -= 200;
        if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) score -= 80;
        return score;
    }

    // ── IDisposable ──

    public void Dispose()
    {
        IsCapturing = false;
        try { _device.OnPacketArrival -= Device_OnPacketArrival; } catch { }
        try { _device.StopCapture(); } catch { }
        try { _device.Close(); } catch { }
    }
}
