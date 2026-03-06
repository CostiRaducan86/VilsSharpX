using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SharpPcap;

namespace VilsSharpX;

/// <summary>
/// Captures Osram Frame Ethernet (OFE) packets from the network using
/// SharpPcap and reassembles fragmented frames for rendering on pane B.
///
/// Protocol (same as Nichia, different magic/geometry):
///   Ethertype 0x88B5 (IEEE 802 Local Experimental 1)
///   18-byte header + up to 1482 bytes pixel data per fragment
///   ~18 fragments per 320×80 = 25600-byte frame
///
/// Magic bytes: 0x4F53 ("OS") — distinguishes from Nichia ("NI" = 0x4E49).
/// </summary>
public sealed class OsramEthCapture : IDisposable
{
    // ── Protocol constants ──
    private const ushort OfeEthertype = 0x88B5;
    private const ushort OfeMagic = 0x4F53; // "OS"
    private const int OfeHdrLen = 18;
    private const int EthHdrLen = 14; // dst(6) + src(6) + ethertype(2)
    private const int MinPacketLen = EthHdrLen + OfeHdrLen;

    // ── Image geometry ──
    private const int OsramW = 320;
    private const int OsramH = 80;
    private const int FrameBytes = OsramW * OsramH; // 25600

    // ── BPF filter ──
    // Combined: include AVTP (0x22F0) ethertypes because AvtpLiveCapture,
    // NichiaEthCapture/OsramEthCapture and AvtpRvfTransmitter all share the
    // SAME SharpPcap device object from CaptureDeviceList.Instance (singleton).
    // The LAST filter set on the device wins, so it must pass ALL ethertypes.
    // Each handler validates its own ethertype/magic in code.
    private const string OfeBpfFilter =
        "ether proto 0x88b5 or ether proto 0x22f0"
        + " or (vlan and ether proto 0x22f0)"
        + " or (vlan and vlan and ether proto 0x22f0)";

    // ── Capture state ──
    private readonly ICaptureDevice _device;
    private readonly Action<string>? _log;

    // ── Reassembly state ──
    private ushort _currentFrameSeq;
    private readonly byte[] _frameBuf = new byte[FrameBytes];
    private readonly bool[] _fragReceived = new bool[20]; // max ~18 fragments
    private int _fragsExpected;
    private int _fragsReceived;
    private bool _assembling;

    // ── Statistics ──
    private uint _framesCompleted;
    private uint _framesDropped;
    private long _totalPackets;
    private long _totalBytes;

    // ── FPS estimation ──
    private readonly Stopwatch _fpsSw = Stopwatch.StartNew();
    private int _fpsFrameCount;
    private double _fpsEma;
    private const double FpsAlpha = 0.3;
    private const double FpsWindowSec = 0.5;

    /// <summary>
    /// Fired on the pcap callback thread when a complete 320×80 frame is
    /// reassembled.  Subscribers must marshal to the UI thread.
    /// </summary>
    public event Action<byte[], LvdsFrameMeta>? OnFrameReady;

    public double FpsEma => _fpsEma;
    public uint FramesCompleted => _framesCompleted;
    public uint FramesDropped => _framesDropped;
    public long TotalPackets => _totalPackets;
    public bool IsCapturing { get; private set; }

    private OsramEthCapture(ICaptureDevice device, Action<string>? log)
    {
        _device = device;
        _log = log;
    }

    // ── Factory / Start ──

    public static OsramEthCapture Start(string? deviceHint, Action<string>? log)
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

        dev ??= devices
            .OrderByDescending(d => ScoreForAutoPick(d))
            .FirstOrDefault(d => !LooksLikeLoopback(d))
            ?? devices[0];

        log?.Invoke($"[ofe] using device: name='{dev.Name}' desc='{dev.Description}'");

        var cap = new OsramEthCapture(dev, log);
        cap._device.Open(DeviceModes.Promiscuous, 1000);

        try
        {
            cap._device.Filter = OfeBpfFilter;
        }
        catch
        {
            log?.Invoke("[ofe] BPF filter failed; capturing all traffic (ethertype check in code).");
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
            if (ethertype != OfeEthertype) return;

            // Parse header (starts at byte 14)
            int hdrOff = EthHdrLen;
            ushort magic = ReadBE16(data, hdrOff + 0);
            if (magic != OfeMagic) return;

            ushort frameSeq = ReadBE16(data, hdrOff + 2);
            byte fragIdx = data[hdrOff + 4];
            byte fragCnt = data[hdrOff + 5];
            ushort dataOffset = ReadBE16(data, hdrOff + 6);
            ushort dataLen = ReadBE16(data, hdrOff + 8);
            ushort width = ReadBE16(data, hdrOff + 10);
            ushort height = ReadBE16(data, hdrOff + 12);

            // Sanity checks
            if (width != OsramW || height != OsramH) return;
            if (fragIdx >= fragCnt || fragCnt == 0 || fragCnt > 20) return;
            if (dataOffset + dataLen > FrameBytes) return;
            if (hdrOff + OfeHdrLen + dataLen > data.Length) return;

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

            // Copy pixel data into frame buffer
            if (fragIdx < _fragReceived.Length && !_fragReceived[fragIdx])
            {
                Buffer.BlockCopy(data, hdrOff + OfeHdrLen, _frameBuf, dataOffset, dataLen);
                _fragReceived[fragIdx] = true;
                _fragsReceived++;
            }

            // All fragments received → emit complete frame
            if (_fragsReceived >= _fragsExpected)
            {
                _assembling = false;
                _framesCompleted++;
                UpdateFps();

                var frameOut = new byte[FrameBytes];
                Buffer.BlockCopy(_frameBuf, 0, frameOut, 0, FrameBytes);

                var meta = new LvdsFrameMeta
                {
                    FrameId = _framesCompleted,
                    Width = OsramW,
                    Height = OsramH,
                    LinesReceived = OsramH,
                    ValidLines = OsramH,
                    LinesExpected = OsramH,
                    LineValidityMask = CreateAllTrueMask(OsramH),
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

    private static bool[] CreateAllTrueMask(int count)
    {
        var mask = new bool[count];
        Array.Fill(mask, true);
        return mask;
    }

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
