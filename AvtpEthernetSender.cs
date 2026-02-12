using System;
using SharpPcap;
using SharpPcap.LibPcap;

namespace VilsSharpX
{
    public static class AvtpEthernetSender
    {
        // Finds the Ethernet device by partial description (case-insensitive) or MAC address
        public static ICaptureDevice? FindDevice(string descOrMac)
        {
            string macTarget = descOrMac.Replace("-", ":").ToLowerInvariant();
            foreach (var dev in CaptureDeviceList.Instance)
            {
                var desc = dev.Description ?? string.Empty;
                var name = dev.Name ?? string.Empty;
                // Try match by description
                if (desc.Contains(descOrMac, StringComparison.OrdinalIgnoreCase))
                    return dev;
                // Try match by MAC address in device name (e.g. NPF_{GUID} or MAC)
                if (!string.IsNullOrWhiteSpace(macTarget) && name.ToLowerInvariant().Contains(macTarget))
                    return dev;
                // Try to extract MAC from description (if present)
                if (!string.IsNullOrWhiteSpace(macTarget) && desc.ToLowerInvariant().Contains(macTarget))
                    return dev;
            }
            return null;
        }

        // Sends a raw Ethernet frame on the specified device
        public static void SendFrame(ICaptureDevice dev, byte[] frame)
        {
			dev.Open();
			try
			{
				if (dev is SharpPcap.LibPcap.LibPcapLiveDevice liveDev)
				{
					liveDev.SendPacket(frame);
				}
				else
				{
					throw new NotSupportedException("Device does not support raw packet injection (not a LibPcapLiveDevice)");
				}
			}
			finally
			{
				dev.Close();
			}
        }

        // Log all available devices and the selected one for debug
        public static void LogAllDevicesAndSelection(string descPart, Action<string> log)
        {
            var devs = CaptureDeviceList.Instance;
            log($"[avtp-eth] Available devices:");
            foreach (var d in devs)
            {
                log($"[avtp-eth]   {d.Description} ({d.Name})");
            }
            var sel = FindDevice(descPart);
            if (sel != null)
                log($"[avtp-eth] Selected device: {sel.Description} ({sel.Name})");
            else
                log($"[avtp-eth] No matching device found for: '{descPart}'");
        }
        }
    }
