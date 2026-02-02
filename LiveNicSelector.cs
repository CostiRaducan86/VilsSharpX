using System;
using System.Linq;
using System.Windows.Controls;
using SharpPcap;

namespace VideoStreamPlayer
{
    /// <summary>
    /// Manages live network interface selection for AVTP capture/transmit.
    /// </summary>
    public sealed class LiveNicSelector
    {
        /// <summary>
        /// Item for NIC combo box display.
        /// </summary>
        public sealed class LiveNicItem
        {
            public required string Display { get; init; }
            public required string? DeviceName { get; init; }
            public override string ToString() => Display;
        }

        /// <summary>
        /// Refreshes the NIC combo box with available capture devices.
        /// </summary>
        public void RefreshNicList(ComboBox? cmbLiveNic, string? currentDeviceHint)
        {
            if (cmbLiveNic == null) return;

            cmbLiveNic.Items.Clear();
            cmbLiveNic.Items.Add(new LiveNicItem { Display = "<Auto>", DeviceName = null });

            var devs = AvtpLiveCapture.ListDevicesSafe();
            foreach (var d in devs)
            {
                string name = d.Name ?? string.Empty;
                string desc = d.Description ?? string.Empty;
                string display = NetworkInterfaceUtils.DescribeCaptureDeviceForUi(name, desc);
                cmbLiveNic.Items.Add(new LiveNicItem { Display = display, DeviceName = name });
            }

            int idx = 0;
            if (!string.IsNullOrWhiteSpace(currentDeviceHint))
            {
                for (int i = 1; i < cmbLiveNic.Items.Count; i++)
                {
                    if (cmbLiveNic.Items[i] is LiveNicItem item
                        && string.Equals(item.DeviceName, currentDeviceHint, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }
            }
            cmbLiveNic.SelectedIndex = idx;
        }

        /// <summary>
        /// Gets the selected device name from combo box, or null for Auto.
        /// </summary>
        public string? GetSelectedDeviceName(ComboBox? cmbLiveNic)
        {
            if (cmbLiveNic?.SelectedItem is LiveNicItem item && !string.IsNullOrWhiteSpace(item.DeviceName))
                return item.DeviceName;
            return null;
        }

        /// <summary>
        /// Resolves the pcap device name for TX, trying combo selection first, then hint, then first non-loopback.
        /// </summary>
        public string? GetTxPcapDeviceNameOrNull(ComboBox? cmbLiveNic, string? deviceHint)
        {
            // 1) If user explicitly selected from the combo, use that NPF name
            try
            {
                if (cmbLiveNic?.SelectedItem is LiveNicItem item && !string.IsNullOrWhiteSpace(item.DeviceName))
                    return item.DeviceName;
            }
            catch { /* ignore */ }

            // 2) Fallback: try to find by hint (description/MAC) in the pcap device list
            string hint = deviceHint ?? string.Empty;

            try
            {
                var devs = CaptureDeviceList.Instance
                    .OfType<SharpPcap.LibPcap.LibPcapLiveDevice>()
                    .ToList();

                // a) Match by hint in Name/Description
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    var hit = devs.FirstOrDefault(d =>
                        (!string.IsNullOrWhiteSpace(d.Description) && d.Description.Contains(hint, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(d.Name) && d.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)));

                    if (hit != null) return hit.Name;
                }

                // b) Alternative fallback: first non-loopback card
                var first = devs.FirstOrDefault(d => !NetworkInterfaceUtils.LooksLikeLoopbackDevice(d.Name, d.Description));
                return first?.Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Updates the enabled state of the NIC combo based on mode.
        /// </summary>
        public void UpdateLiveUiEnabledState(ComboBox? cmbLiveNic, bool isLiveMode)
        {
            if (cmbLiveNic != null)
                cmbLiveNic.IsEnabled = isLiveMode;
        }
    }
}
