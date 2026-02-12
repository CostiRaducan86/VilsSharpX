using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using SharpPcap;

namespace VilsSharpX
{
    /// <summary>
    /// Utilities for network interface discovery and AVTP device handling.
    /// </summary>
    public static class NetworkInterfaceUtils
    {
        /// <summary>
        /// BPF filter for capturing AVTP frames (ethertype 0x22F0), including VLAN-tagged variants.
        /// </summary>
        public static string GetAvtpBpfFilter()
            => "ether proto 0x22f0 or (vlan and ether proto 0x22f0) or (vlan and vlan and ether proto 0x22f0)";

        /// <summary>
        /// Extracts GUID from an Npcap device name like \\Device\\NPF_{GUID}.
        /// </summary>
        public static string? TryExtractGuidFromPcapDeviceName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            int open = name.IndexOf('{');
            int close = name.IndexOf('}', open + 1);
            if (open < 0 || close < 0 || close <= open) return null;

            string guid = name.Substring(open + 1, close - open - 1);
            return guid.Length >= 32 ? guid : null;
        }

        /// <summary>
        /// Finds a NetworkInterface by its GUID (from Npcap device name).
        /// </summary>
        public static NetworkInterface? TryFindNetworkInterfaceByGuid(string? guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (string.Equals(ni.Id, guid, StringComparison.OrdinalIgnoreCase))
                        return ni;
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        /// <summary>
        /// Creates a user-friendly description of a capture device for UI display.
        /// </summary>
        public static string DescribeCaptureDeviceForUi(string? pcapName, string? pcapDesc)
        {
            string name = pcapName ?? string.Empty;
            string desc = pcapDesc ?? string.Empty;

            string? guid = TryExtractGuidFromPcapDeviceName(name);
            var ni = TryFindNetworkInterfaceByGuid(guid);
            if (ni == null)
            {
                return string.IsNullOrWhiteSpace(desc) ? name : $"{desc}  ({name})";
            }

            string ips = string.Empty;
            try
            {
                var ipProps = ni.GetIPProperties();
                var v4 = new List<string>();
                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua?.Address != null && ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        v4.Add(ua.Address.ToString());
                }
                if (v4.Count > 0) ips = string.Join(",", v4);
            }
            catch
            {
                // ignore
            }

            string up = ni.OperationalStatus == OperationalStatus.Up ? "Up" : ni.OperationalStatus.ToString();
            string ipPart = string.IsNullOrWhiteSpace(ips) ? string.Empty : $" {ips}";

            string mac = string.Empty;
            try
            {
                var pa = ni.GetPhysicalAddress();
                if (pa != null)
                {
                    var b = pa.GetAddressBytes();
                    if (b.Length == 6) mac = string.Join("-", b.Select(x => x.ToString("X2")));
                }
            }
            catch { /* ignore */ }

            string macPart = string.IsNullOrWhiteSpace(mac) ? string.Empty : $" {mac}";

            return $"{ni.Name} [{up}]{ipPart}{macPart} — {desc}  ({name})";
        }

        /// <summary>
        /// Checks if a device appears to be a loopback adapter.
        /// </summary>
        public static bool LooksLikeLoopbackDevice(string? name, string? desc)
        {
            var n = name ?? string.Empty;
            var d = desc ?? string.Empty;
            return n.Contains("NPF_Loopback", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Loopback", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if Ethernet frame data has ethertype 0x22F0 (AVTP), including VLAN-tagged variants.
        /// </summary>
        public static bool IsEthernetAvtp22F0(byte[] data)
        {
            if (data == null || data.Length < 14) return false;

            static ushort ReadU16BE(byte[] b, int offset)
                => (ushort)((b[offset] << 8) | b[offset + 1]);

            ushort type = ReadU16BE(data, 12);
            if (type == 0x22F0) return true;

            // VLAN tags: 0x8100 (802.1Q), 0x88A8 (802.1ad), 0x9100 (common QinQ)
            if (type == 0x8100 || type == 0x88A8 || type == 0x9100)
            {
                if (data.Length < 18) return false;
                ushort inner = ReadU16BE(data, 16);
                if (inner == 0x22F0) return true;

                // QinQ: VLAN-in-VLAN
                if ((inner == 0x8100 || inner == 0x88A8 || inner == 0x9100) && data.Length >= 22)
                {
                    ushort inner2 = ReadU16BE(data, 20);
                    return inner2 == 0x22F0;
                }
            }

            return false;
        }

        /// <summary>
        /// Probes a single capture device for AVTP frames over a duration.
        /// </summary>
        public static int ProbeSingleDeviceForAvtp(ICaptureDevice dev, int durationMs)
        {
            int count = 0;
            void OnArrival(object s, PacketCapture e)
            {
                try
                {
                    var raw = e.GetPacket();
                    var data = raw?.Data;
                    if (data == null || data.Length == 0) return;

                    if (IsEthernetAvtp22F0(data))
                        Interlocked.Increment(ref count);
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                dev.OnPacketArrival += OnArrival;

                dev.Open(DeviceModes.Promiscuous, 250);
                try { dev.Filter = GetAvtpBpfFilter(); } catch { /* ignore */ }

                dev.StartCapture();
                Thread.Sleep(durationMs);
            }
            finally
            {
                try { dev.StopCapture(); } catch { }
                try { dev.Close(); } catch { }
                try { dev.OnPacketArrival -= OnArrival; } catch { }
            }

            return count;
        }

        /// <summary>
        /// Gets the TX pcap device name from a hint string, searching available devices.
        /// </summary>
        public static string? GetTxPcapDeviceNameFromHint(string? hint)
        {
            try
            {
                var devs = CaptureDeviceList.Instance
                    .OfType<SharpPcap.LibPcap.LibPcapLiveDevice>()
                    .ToList();

                // a) match by hint in Name/Description
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    var hit = devs.FirstOrDefault(d =>
                        (!string.IsNullOrWhiteSpace(d.Description) && d.Description.Contains(hint, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(d.Name) && d.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)));

                    if (hit != null) return hit.Name;
                }

                // b) alternative fallback: first non-loopback card
                var first = devs.FirstOrDefault(d => !LooksLikeLoopbackDevice(d.Name, d.Description));
                return first?.Name;
            }
            catch
            {
                return null;
            }
        }
    }
}
