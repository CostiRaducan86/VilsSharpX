using System;
using System.IO.Ports;
using System.Threading;

namespace VilsSharpX;

/// <summary>
/// Low-level serial port capture for LVDS UART signals.
/// Connects to a Raspberry Pi Pico 2 (LogicAnalyzer board) that forwards
/// decoded UART bytes from the LVDS link over USB CDC (virtual COM port).
///
/// The Pico 2 firmware is responsible for:
///   1. Capturing the TTL-level UART signal (post NBA3N012C receiver) via PIO
///   2. Framing the raw pixel bytes and forwarding them over USB CDC
///
/// This class handles the PC-side serial port I/O and pumps received bytes
/// into an <see cref="LvdsFrameReassembler"/> for frame reconstruction.
/// </summary>
public sealed class LvdsUartCapture : IDisposable
{
    private SerialPort? _port;
    private readonly Action<byte[], int> _onDataReceived;
    private readonly Action<string>? _log;
    private volatile bool _disposed;
    private Thread? _readThread;

    // Pre-allocated read buffer (eliminates GC pressure at ~849 KB/s)
    private const int ReadBufferSize = 65536;
    private readonly byte[] _readBuf = new byte[ReadBufferSize];

    public string PortName { get; }
    public bool IsOpen => _port?.IsOpen == true;

    /// <summary>
    /// Creates a new LVDS UART capture instance.
    /// </summary>
    /// <param name="portName">COM port name (e.g. "COM3").</param>
    /// <param name="config">UART configuration (baud rate, parity, etc.).</param>
    /// <param name="onDataReceived">Callback invoked with (buffer, bytesRead) on the serial thread.</param>
    /// <param name="log">Optional diagnostic logger.</param>
    private LvdsUartCapture(string portName, LvdsUartConfig config,
                            Action<byte[], int> onDataReceived, Action<string>? log)
    {
        PortName = portName;
        _onDataReceived = onDataReceived;
        _log = log;

        _port = new SerialPort(portName)
        {
            BaudRate = config.BaudRate,
            DataBits = config.DataBits,
            Parity = config.Parity,
            StopBits = config.StopBits,
            Handshake = Handshake.None,
            ReadBufferSize = ReadBufferSize * 2,
            ReadTimeout = 500,
            WriteTimeout = 500,
            DtrEnable = true,
            RtsEnable = false,
        };
    }

    /// <summary>
    /// Opens the serial port and starts a dedicated read thread.
    /// Using a blocking read loop (instead of DataReceived event) ensures the Windows
    /// USB driver always has a pending read URB, maximizing USB bulk IN throughput.
    /// The pre-allocated buffer eliminates GC allocations at high data rates.
    /// </summary>
    public static LvdsUartCapture Start(string portName, LvdsUartConfig config,
                                         Action<byte[], int> onDataReceived, Action<string>? log)
    {
        var capture = new LvdsUartCapture(portName, config, onDataReceived, log);
        try
        {
            capture._port!.Open();
            capture._port.DiscardInBuffer();

            // Start dedicated read thread (high priority to avoid USB stalls)
            capture._readThread = new Thread(capture.ReadLoop)
            {
                Name = $"LVDS-{portName}",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            capture._readThread.Start();

            log?.Invoke($"[lvds-uart] opened {portName} @ {config.BaudRate} baud, " +
                        $"{config.DataBits}{config.Parity.ToString()[0]}{(config.StopBits == StopBits.One ? "1" : "2")} " +
                        $"(dedicated read thread)");
        }
        catch (Exception ex)
        {
            capture.Dispose();
            throw new InvalidOperationException($"Failed to open {portName}: {ex.Message}", ex);
        }
        return capture;
    }

    /// <summary>
    /// Lists available serial (COM) ports on the system.
    /// </summary>
    public static string[] ListPorts()
    {
        try
        {
            return SerialPort.GetPortNames();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Dedicated read loop running on a background thread.
    /// Uses blocking <see cref="System.IO.Stream.Read"/> on the BaseStream to ensure
    /// the Windows USB driver always has a pending read URB.  BaseStream bypasses
    /// SerialPort's internal locking and character decoding, reducing overhead at
    /// high data rates (~849 KB/s).
    /// The pre-allocated <see cref="_readBuf"/> is safe to reuse because the callback
    /// (<see cref="LvdsFrameReassembler.Push(byte[], int)"/>) processes bytes synchronously.
    /// </summary>
    private void ReadLoop()
    {
        // Grab BaseStream reference once — avoids repeated property access
        System.IO.Stream? stream = null;
        try
        {
            stream = _port?.BaseStream;
        }
        catch { /* port may have closed */ }

        while (!_disposed && stream != null)
        {
            try
            {
                int read = stream.Read(_readBuf, 0, _readBuf.Length);
                if (read > 0)
                    _onDataReceived(_readBuf, read);
                else if (read == 0)
                    Thread.Sleep(1); // EOF-like condition, avoid tight spin
            }
            catch (TimeoutException)
            {
                // Normal when no data arrives within ReadTimeout — loop back
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (System.IO.IOException) when (_disposed)
            {
                break; // Port closed during dispose
            }
            catch (Exception ex) when (!_disposed)
            {
                _log?.Invoke($"[lvds-uart] read error: {ex.Message}");
                Thread.Sleep(10); // Avoid tight loop on persistent errors
            }
        }
    }

    /// <summary>
    /// Sends raw bytes to the Pico 2 (e.g. configuration commands).
    /// </summary>
    public void Send(byte[] data)
    {
        if (_disposed || _port == null || !_port.IsOpen) return;
        try
        {
            _port.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[lvds-uart] write error: {ex.Message}");
        }
    }

    /// <summary>
    /// Send device-mode command to Pico 2 firmware.
    /// 'N' = Nichia (12.5 Mbps), 'O' = Osram (20 Mbps).
    /// </summary>
    public void SendModeCommand(bool isNichia)
    {
        byte cmd = (byte)(isNichia ? 'N' : 'O');
        Send(new[] { cmd });
        _log?.Invoke($"[lvds-uart] sent mode command: {(char)cmd}");
    }

    /// <summary>
    /// Send 'B' command to reboot Pico 2 into USB bootloader (BOOTSEL mode).
    /// After this, the COM port will disconnect and the Pico 2 will appear
    /// as a USB mass-storage drive (RPI-RP2) for UF2 flashing.
    /// </summary>
    public void SendBootloaderCommand()
    {
        Send(new[] { (byte)'B' });
        _log?.Invoke("[lvds-uart] sent bootloader command 'B' — Pico 2 will reboot into BOOTSEL mode");
    }

    /// <summary>
    /// Send 'B' command to a Pico 2 on the specified COM port to enter BOOTSEL mode,
    /// even when no capture session is active.
    /// Opens the port briefly at 115200 baud (command channel), sends 'B', and closes.
    /// </summary>
    public static void SendBootloaderCommandTo(string portName, Action<string>? log = null)
    {
        using var port = new SerialPort(portName)
        {
            BaudRate = 115200,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReadTimeout = 500,
            WriteTimeout = 500,
            DtrEnable = true,
        };
        port.Open();
        port.Write(new byte[] { (byte)'B' }, 0, 1);
        log?.Invoke($"[lvds-uart] sent bootloader command 'B' to {portName}");
        System.Threading.Thread.Sleep(100); // let the byte go out
        port.Close();
    }

    /// <summary>
    /// Query firmware status by sending 'S' command to Pico 2 on the specified COM port.
    /// Opens the port briefly, sends 'S', waits for a response, and returns it.
    /// Expected response: "MODE=NICHIA BAUD=12500000 BYTES=nnn\n"
    /// Returns null if no response within timeout.
    /// </summary>
    public static string? QueryFirmwareStatus(string portName, Action<string>? log = null, int waitMs = 500)
    {
        try
        {
            using var port = new SerialPort(portName)
            {
                BaudRate = 115200,
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                ReadTimeout = waitMs,
                WriteTimeout = 500,
                DtrEnable = true,
            };
            port.Open();

            // Let DTR settle and firmware process connection event
            Thread.Sleep(100);
            port.DiscardInBuffer();

            port.Write(new byte[] { (byte)'S' }, 0, 1);
            log?.Invoke($"[lvds-uart] sent status query 'S' to {portName}");

            // Wait for firmware to respond
            Thread.Sleep(waitMs);

            int available = port.BytesToRead;
            if (available <= 0)
            {
                log?.Invoke($"[lvds-uart] no status response from {portName}");
                return null;
            }

            var buf = new byte[Math.Min(available, 1024)];
            int read = port.Read(buf, 0, buf.Length);

            // The response may be interleaved with PIO data bytes.
            // Extract only printable ASCII characters and look for the "MODE=" pattern.
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < read; i++)
            {
                char c = (char)buf[i];
                if (c >= ' ' && c <= '~') sb.Append(c);  // printable ASCII
                else if (c == '\n' || c == '\r') sb.Append(' ');
            }

            string rawText = sb.ToString().Trim();
            log?.Invoke($"[lvds-uart] raw response ({read} bytes, {rawText.Length} printable): {rawText}");

            // Try to extract the "MODE=..." status line from the response
            int modeIdx = rawText.IndexOf("MODE=", StringComparison.OrdinalIgnoreCase);
            if (modeIdx >= 0)
            {
                string response = rawText[modeIdx..].Trim();
                log?.Invoke($"[lvds-uart] firmware status from {portName}: {response}");
                return response;
            }

            // No "MODE=" found — return whatever printable text we got (or null if empty)
            if (rawText.Length > 0)
            {
                log?.Invoke($"[lvds-uart] unexpected response from {portName}: {rawText}");
                return rawText;
            }

            log?.Invoke($"[lvds-uart] no printable response from {portName} ({read} raw bytes)");
            return null;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[lvds-uart] status query failed on {portName}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_port != null)
            {
                if (_port.IsOpen)
                {
                    _port.DiscardInBuffer();
                    _port.Close();
                }
                _port.Dispose();
                _port = null;
            }

            // Wait for read thread to exit (Close() above unblocks the Read call)
            _readThread?.Join(2000);
            _readThread = null;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[lvds-uart] dispose error: {ex.Message}");
        }
    }
}
