using LibUsbDotNet;
using LibUsbDotNet.Main;
using System;
using System.Threading.Tasks;

namespace VilsSharpX
{
    public class PicoUsbVendor : IDisposable
    {
        private UsbDevice? _usbDevice;
        private UsbEndpointReader? _reader;
        private UsbEndpointWriter? _writer;
        private bool _connected = false;

        public async Task<bool> ConnectAsync()
        {
            return await Task.Run(() =>
            {
                var finder = new UsbDeviceFinder(0x2E8A, 0x000B);
                _usbDevice = UsbDevice.OpenUsbDevice(finder);
                if (_usbDevice == null)
                {
                    DiagnosticLogger.Log("[usb] Device not found (VID=0x2E8A, PID=0x000B)");
                    return false;
                }

                DiagnosticLogger.Log($"[usb] Device found: {_usbDevice.Info}");

                if (_usbDevice is IUsbDevice wholeUsbDevice)
                {
                    try
                    {
                        wholeUsbDevice.SetConfiguration(1);
                        wholeUsbDevice.ClaimInterface(0);
                        DiagnosticLogger.Log("[usb] Claimed interface 0, configuration 1");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Log($"[usb] Error claiming interface/config: {ex.Message}");
                    }
                }

                // Enumerate endpoints for debug (LibUsbDotNet)
                var config = _usbDevice.Configs[0];
                foreach (var iface in config.InterfaceInfoList)
                {
                    DiagnosticLogger.Log($"[usb] Interface {iface.Descriptor.InterfaceID}, AltSetting {iface.Descriptor.AlternateID}");
                    foreach (var ep in iface.EndpointInfoList)
                    {
                        var addr = ep.Descriptor.EndpointID;
                        string dir = (addr & 0x80) != 0 ? "IN" : "OUT";
                        DiagnosticLogger.Log($"[usb]  Endpoint Addr=0x{addr:X2} ({dir}), Type={ep.Descriptor.Attributes & 0x3}, MaxPacket={ep.Descriptor.MaxPacketSize}");
                    }
                }

                try
                {
                    _reader = _usbDevice.OpenEndpointReader(ReadEndpointID.Ep01);
                    DiagnosticLogger.Log("[usb] Opened EndpointReader Ep01 (0x81)");
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Log($"[usb] Failed to open EndpointReader Ep01: {ex.Message}");
                }
                try
                {
                    _writer = _usbDevice.OpenEndpointWriter(WriteEndpointID.Ep01);
                    DiagnosticLogger.Log("[usb] Opened EndpointWriter Ep01 (0x01)");
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Log($"[usb] Failed to open EndpointWriter Ep01: {ex.Message}");
                }
                _connected = _reader != null;
                return _connected;
            });
        }

        public async Task<byte[]> ReadAsync(int length)
        {
            return await Task.Run(() =>
            {
                if (!_connected) throw new InvalidOperationException("Device not connected");
                if (_reader == null) throw new InvalidOperationException("USB EndpointReader is null");
                byte[] buffer = new byte[length];
                int bytesRead;
                var ec = _reader.Read(buffer, 5000, out bytesRead);
                if (ec == ErrorCode.None && bytesRead > 0)
                {
                    if (bytesRead == length) return buffer;
                    var outb = new byte[bytesRead];
                    Array.Copy(buffer, outb, bytesRead);
                    return outb;
                }
                return Array.Empty<byte>();
            });
        }

        public async Task WriteAsync(byte[] data)
        {
            await Task.Run(() =>
            {
                if (!_connected) throw new InvalidOperationException("Device not connected");
                if (_writer == null) throw new InvalidOperationException("USB EndpointWriter is null");
                _writer.Write(data, 0, data.Length, 5000, out _);
            });
        }

        public void Dispose()
        {
            try
            {
                if (_usbDevice != null)
                {
                    if (_usbDevice.IsOpen)
                    {
                        if (_usbDevice is IUsbDevice whole)
                        {
                            try { whole.ReleaseInterface(0); } catch { }
                        }
                        _usbDevice.Close();
                    }
                    UsbDevice.Exit();
                }
            }
            catch { }
            _connected = false;
        }
    }
}