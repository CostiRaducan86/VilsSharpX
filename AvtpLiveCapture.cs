using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SharpPcap;

namespace VideoStreamPlayer;

public sealed class AvtpLiveCapture : IDisposable
{
    private readonly ICaptureDevice _device;
    private readonly Action<RvfChunk> _onChunk;

    private uint _seq;
    private uint _frameId;

    private const string AvtpBpfFilter = "ether proto 0x22f0 or (vlan and ether proto 0x22f0) or (vlan and vlan and ether proto 0x22f0)";

    private AvtpLiveCapture(ICaptureDevice device, Action<RvfChunk> onChunk)
    {
        _device = device;
        _onChunk = onChunk;
    }

    public static IReadOnlyList<(string Name, string Description)> ListDevicesSafe()
    {
        try
        {
            var list = CaptureDeviceList.Instance;
            var outList = new List<(string, string)>(list.Count);
            foreach (var d in list)
            {
                string name = d.Name ?? string.Empty;
                string desc = d.Description ?? string.Empty;
                outList.Add((name, desc));
            }
            return outList;
        }
        catch
        {
            return Array.Empty<(string, string)>();
        }
    }

    public static AvtpLiveCapture Start(
        string? deviceHint,
        Action<string>? log,
        Action<RvfChunk> onChunk)
    {
        var devices = CaptureDeviceList.Instance;
        if (devices.Count == 0)
            throw new InvalidOperationException("No capture devices found. Is Npcap installed?");

        static bool LooksLikeLoopback(ICaptureDevice d)
        {
            var name = d.Name ?? string.Empty;
            var desc = d.Description ?? string.Empty;
            return name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase);
        }

        static bool LooksLikeWanMiniport(ICaptureDevice d)
        {
            var name = d.Name ?? string.Empty;
            var desc = d.Description ?? string.Empty;
            return name.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase);
        }

        static bool LooksLikeVirtualOrTunnel(ICaptureDevice d)
        {
            var name = d.Name ?? string.Empty;
            var desc = d.Description ?? string.Empty;

            // Heuristics: only used for AUTO selection.
            return name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Teredo", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Teredo", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ISATAP", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("ISATAP", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
        }

        static int ScoreForAutoPick(ICaptureDevice d)
        {
            if (LooksLikeLoopback(d)) return int.MinValue / 2;

            var name = d.Name ?? string.Empty;
            var desc = d.Description ?? string.Empty;

            int score = 0;

            if (desc.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)) score += 60;
            if (desc.Contains("Gigabit", StringComparison.OrdinalIgnoreCase)) score += 10;

            if (desc.Contains("Intel", StringComparison.OrdinalIgnoreCase)) score += 5;
            if (desc.Contains("Realtek", StringComparison.OrdinalIgnoreCase)) score += 5;
            if (desc.Contains("Marvell", StringComparison.OrdinalIgnoreCase)) score += 5;

            if (desc.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) || desc.Contains("Wireless", StringComparison.OrdinalIgnoreCase)) score -= 5;

            if (LooksLikeWanMiniport(d)) score -= 200;
            if (LooksLikeVirtualOrTunnel(d)) score -= 80;

            // Some miniports/tunnels are described as "Microsoft" only.
            if (desc.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) score -= 10;

            // Prefer proper NPF_{GUID} devices over anything weird.
            if (name.Contains("\\Device\\NPF_{", StringComparison.OrdinalIgnoreCase)) score += 3;

            return score;
        }

        ICaptureDevice? dev = null;

        if (!string.IsNullOrWhiteSpace(deviceHint))
        {
            string hint = deviceHint.Trim();
            dev = devices.FirstOrDefault(d =>
                (!string.IsNullOrEmpty(d.Name) && d.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(d.Description) && d.Description.Contains(hint, StringComparison.OrdinalIgnoreCase)));
        }

        if (dev == null)
        {
            // Auto-pick: try to select a "real" Ethernet adapter and avoid WAN miniports.
            dev = devices
                .OrderByDescending(ScoreForAutoPick)
                .FirstOrDefault(d => !LooksLikeLoopback(d))
                ?? devices[0];
        }

        log?.Invoke($"[avtp-live] using device: name='{dev.Name}' desc='{dev.Description}'");
        if (LooksLikeWanMiniport(dev))
            log?.Invoke("[avtp-live] warning: selected a WAN Miniport. This usually won't see ECU/VN5620 traffic; pick the actual Ethernet adapter in the NIC dropdown.");

        var cap = new AvtpLiveCapture(dev, onChunk);

        // Promiscuous so we don't depend on destination MAC.
        // Timeout to allow clean shutdown.
        cap._device.Open(DeviceModes.Promiscuous, 1000);

        // BPF filter: AVTP ethertype (0x22F0), robust for VLAN and QinQ.
        // Some setups tag the stream (802.1Q / 802.1ad), so match both plain and vlan encapsulated.
        try
        {
            cap._device.Filter = AvtpBpfFilter;
        }
        catch
        {
            // Some devices (e.g., loopback) don't support VLAN primitives in BPF.
            // Try a simple ethertype-only filter as a fallback.
            try { cap._device.Filter = "ether proto 0x22f0"; } catch { /* ignore */ }
        }

        cap._device.OnPacketArrival += cap.Device_OnPacketArrival;
        cap._device.StartCapture();

        return cap;
    }

    private void Device_OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var raw = e.GetPacket();
            var data = raw?.Data;
            if (data == null || data.Length == 0) return;

            if (!AvtpRvfParser.TryParseAvtpRvfEthernet(data, out int line1, out bool endFrame, out var payload))
                return;

            var chunk = new RvfChunk(
                Width: (ushort)RvfProtocol.W,
                Height: (ushort)RvfProtocol.H,
                LineNumber1Based: (ushort)line1,
                NumLines: 4,
                EndFrame: endFrame,
                FrameId: _frameId,
                Seq: _seq++,
                Payload: payload);

            _onChunk(chunk);

            if (endFrame) _frameId++;
        }
        catch
        {
            // swallow capture callback errors (we don't want capture thread to die)
        }
    }

    public void Dispose()
    {
        try { _device.OnPacketArrival -= Device_OnPacketArrival; } catch { }
        try { _device.StopCapture(); } catch { }
        try { _device.Close(); } catch { }
    }
}
