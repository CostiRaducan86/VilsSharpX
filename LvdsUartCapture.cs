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

    // Read buffer (64 KB to handle high-throughput UART data)
    private const int ReadBufferSize = 65536;

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
    /// Opens the serial port and starts asynchronous reading.
    /// </summary>
    public static LvdsUartCapture Start(string portName, LvdsUartConfig config,
                                         Action<byte[], int> onDataReceived, Action<string>? log)
    {
        var capture = new LvdsUartCapture(portName, config, onDataReceived, log);
        try
        {
            capture._port!.Open();
            capture._port.DiscardInBuffer();
            capture._port.DataReceived += capture.OnSerialDataReceived;

            log?.Invoke($"[lvds-uart] opened {portName} @ {config.BaudRate} baud, " +
                        $"{config.DataBits}{config.Parity.ToString()[0]}{(config.StopBits == StopBits.One ? "1" : "2")}");
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

    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_disposed || _port == null || !_port.IsOpen) return;

        try
        {
            int available = _port.BytesToRead;
            if (available <= 0) return;

            var buffer = new byte[Math.Min(available, ReadBufferSize)];
            int read = _port.Read(buffer, 0, buffer.Length);
            if (read > 0)
            {
                _onDataReceived(buffer, read);
            }
        }
        catch (TimeoutException)
        {
            // Normal during idle periods
        }
        catch (Exception ex) when (!_disposed)
        {
            _log?.Invoke($"[lvds-uart] read error: {ex.Message}");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_port != null)
            {
                _port.DataReceived -= OnSerialDataReceived;
                if (_port.IsOpen)
                {
                    _port.DiscardInBuffer();
                    _port.Close();
                }
                _port.Dispose();
                _port = null;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[lvds-uart] dispose error: {ex.Message}");
        }
    }
}
