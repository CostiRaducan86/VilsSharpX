using System;
using System.Linq;
using System.Threading;
using SharpPcap;

namespace VilsSharpX;

public sealed class LsmCanDiagCapture : IDisposable
{
    private const string CombinedBpfFilter =
        "ether proto 0x88b5 or ether proto 0x22f0"
        + " or (vlan and ether proto 0x22f0)"
        + " or (vlan and vlan and ether proto 0x22f0)";

    private readonly ICaptureDevice _device;
    private readonly Action<string>? _log;
    private long _totalPackets;
    private long _parserErrors;
    private long _diagMagicMatches;
    private long _niMagicMatches;
    private long _osMagicMatches;
    private long _other88b5Matches;
    private long _diagRejected;
    private volatile string _lastParserError = string.Empty;

    public event Action<LsmCanDiagRecord>? OnRecordReady;

    public long TotalPackets => Interlocked.Read(ref _totalPackets);
    public long ParserErrors => Interlocked.Read(ref _parserErrors);
    public long DiagMagicMatches => Interlocked.Read(ref _diagMagicMatches);
    public long NiMagicMatches => Interlocked.Read(ref _niMagicMatches);
    public long OsMagicMatches => Interlocked.Read(ref _osMagicMatches);
    public long Other88b5Matches => Interlocked.Read(ref _other88b5Matches);
    /// <summary>CD-magic packets the parser rejected (e.g. firmware/app protocol version mismatch).</summary>
    public long DiagRejected => Interlocked.Read(ref _diagRejected);
    public string DeviceDescription => _device.Description ?? _device.Name ?? string.Empty;

    /// <summary>True when this capture was opened on the NIC that <paramref name="hint"/> selects.</summary>
    public bool MatchesDevice(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return true;
        hint = hint.Trim();
        return (_device.Name?.Contains(hint, StringComparison.OrdinalIgnoreCase) ?? false)
            || (_device.Description?.Contains(hint, StringComparison.OrdinalIgnoreCase) ?? false);
    }
    public string LastParserError => _lastParserError;
    public bool IsCapturing { get; private set; }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _totalPackets, 0);
        Interlocked.Exchange(ref _parserErrors, 0);
        Interlocked.Exchange(ref _diagMagicMatches, 0);
        Interlocked.Exchange(ref _niMagicMatches, 0);
        Interlocked.Exchange(ref _osMagicMatches, 0);
        Interlocked.Exchange(ref _other88b5Matches, 0);
        Interlocked.Exchange(ref _diagRejected, 0);
        _lastParserError = string.Empty;
    }

    private LsmCanDiagCapture(ICaptureDevice device, Action<string>? log)
    {
        _device = device;
        _log = log;
    }

    public static LsmCanDiagCapture Start(string? deviceHint, Action<string>? log)
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
            .OrderByDescending(ScoreForAutoPick)
            .FirstOrDefault(d => !LooksLikeLoopback(d))
            ?? devices[0];

        log?.Invoke($"[can] using device: name='{dev.Name}' desc='{dev.Description}'");

        var capture = new LsmCanDiagCapture(dev, log);
        capture._device.Open(DeviceModes.Promiscuous, 1000);

        try
        {
            capture._device.Filter = CombinedBpfFilter;
        }
        catch
        {
            log?.Invoke("[can] BPF filter failed; capturing all traffic (protocol validated in code).");
        }

        capture._device.OnPacketArrival += capture.Device_OnPacketArrival;
        capture._device.StartCapture();
        capture.IsCapturing = true;
        return capture;
    }

    private void Device_OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var data = e.GetPacket()?.Data;
            if (data == null || data.Length == 0)
                return;

            Interlocked.Increment(ref _totalPackets);

            if (LsmCanDiagParser.TryParseEthernet(data, out var record) && record != null)
            {
                Interlocked.Increment(ref _diagMagicMatches);
                OnRecordReady?.Invoke(record);
            }
            else if (data.Length >= 16)
            {
                // classify 0x88B5 magic even when parser rejects packet
                int offset = 12;
                ushort et = (ushort)((data[offset] << 8) | data[offset + 1]);
                if ((et == 0x8100 || et == 0x88A8) && data.Length >= 18)
                {
                    offset += 4;
                    et = (ushort)((data[offset] << 8) | data[offset + 1]);
                }

                if ((et == 0x8100 || et == 0x88A8) && data.Length >= 22)
                {
                    offset += 4;
                    et = (ushort)((data[offset] << 8) | data[offset + 1]);
                }

                if (et == 0x88B5 && data.Length >= offset + 4)
                {
                    ushort magic = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
                    switch (magic)
                    {
                        case 0x4344:
                            if (Interlocked.Increment(ref _diagRejected) == 1 && data.Length >= offset + 12)
                            {
                                int ver = data[offset + 4];
                                int type = data[offset + 5];
                                int len = (data[offset + 8] << 8) | data[offset + 9];
                                _log?.Invoke($"[can] CD packet rejected by parser: version={ver}, recordType={type}, payloadLen={len}, frameLen={data.Length} (expected v2/type 1/len {LsmCanDiagRecord.PayloadLength})");
                            }
                            break;
                        case 0x4E49:
                            Interlocked.Increment(ref _niMagicMatches);
                            break;
                        case 0x4F53:
                            Interlocked.Increment(ref _osMagicMatches);
                            break;
                        default:
                            Interlocked.Increment(ref _other88b5Matches);
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _parserErrors);
            _lastParserError = ex.Message;
            _log?.Invoke($"[can] parser/capture error: {ex.Message}");
        }
    }

    private static bool LooksLikeLoopback(ICaptureDevice device)
    {
        var name = device.Name ?? string.Empty;
        var desc = device.Description ?? string.Empty;
        return name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreForAutoPick(ICaptureDevice device)
    {
        if (LooksLikeLoopback(device)) return int.MinValue / 2;

        var desc = device.Description ?? string.Empty;
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

    public void Dispose()
    {
        IsCapturing = false;
        try { _device.OnPacketArrival -= Device_OnPacketArrival; } catch { }
        try { _device.StopCapture(); } catch { }
        try { _device.Close(); } catch { }
    }
}