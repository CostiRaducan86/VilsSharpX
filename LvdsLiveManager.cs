using System;
using System.Diagnostics;
using System.Threading;

namespace VilsSharpX;

/// <summary>
/// Manages the LVDS serial capture pipeline for Pane B.
///
/// Architecture:
///   Pico 2 (LogicAnalyzer board)
///     → captures TTL UART signal via PIO (post-LVDS receiver)
///     → firmware parses LVDS lines, assembles complete frames on-chip
///     → sends "cooked" frame packets over USB CDC:
///       [0xFE][0xED][frame_id:2B][width:2B][height:2B][pixels:W×H]
///     → LvdsUartCapture reads COM port
///     → LvdsCookedFrameReceiver parses cooked frame packets
///     → OnFrameReady event fires with complete frame
///     → MainWindow renders on Pane B
///
/// The firmware handles frame skipping to match USB bandwidth,
/// so every frame received by the host is complete and CRC-verified.
///
/// Analogous to <see cref="LiveCaptureManager"/> for AVTP Ethernet.
/// </summary>
public sealed class LvdsLiveManager : IDisposable
{
    private readonly Action<string> _log;
    private readonly object _lock = new();

    // Serial capture
    private LvdsUartCapture? _capture;

    // Cooked frame receiver (firmware sends complete frame packets)
    private LvdsCookedFrameReceiver _receiver;

    // Current configuration
    private LvdsUartConfig _config;
    private LsmDeviceType _deviceType;

    // Frame buffer (latest complete frame at active resolution)
    private byte[] _lvdsFrame;
    private volatile bool _hasFrame;
    private DateTime _lastFrameUtc = DateTime.MinValue;



    // Signal loss detection
    private readonly TimeSpan _signalLostTimeout;

    // Diagnostics
    private long _bytesReceived;

    // Firmware status interception (for querying during active capture)
    private volatile bool _interceptStatus;
    private Action<string>? _statusCallback;

    // FPS estimation (EMA-based, matching app convention)
    private readonly double _fpsWindowSec;
    private readonly double _fpsAlpha;
    private readonly Stopwatch _fpsSw = new();
    private int _fpsFrameCount;
    private double _fpsEma;

    // ── Events ──────────────────────────────────────────────────────────

    /// <summary>
    /// Fired when a complete LVDS frame is ready (active area only, metadata cropped).
    /// The byte[] buffer is a fresh copy safe for the subscriber to hold.
    /// </summary>
    public event Action<byte[], LvdsFrameMeta>? OnFrameReady;

    // ── Properties ──────────────────────────────────────────────────────

    public bool HasFrame => _hasFrame;
    public byte[] LvdsFrame => _lvdsFrame;
    public DateTime LastFrameUtc => _lastFrameUtc;
    public bool IsCapturing => _capture?.IsOpen == true;
    public string? ActivePort => _capture?.PortName;

    public int FrameWidth => _config.FrameWidth;
    public int ActiveHeight => _config.ActiveHeight;

    /// <summary>Current LVDS FPS estimate (EMA-smoothed).</summary>
    public double FpsEma => _fpsEma;

    /// <summary>Total raw bytes received from the serial port (atomically read).</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>
    /// Send 'S' command to the Pico 2 firmware through the active capture connection
    /// and intercept the status response from the data stream.
    /// The firmware stops PIO, sends "MODE=...\n", then restarts capture.
    /// </summary>
    public void QueryFirmwareStatusDuringCapture(Action<string> onResponse)
    {
        if (_capture == null || !_capture.IsOpen)
        {
            onResponse("ERROR: no active capture");
            return;
        }
        _statusCallback = onResponse;
        _interceptStatus = true;
        _capture.Send(new byte[] { (byte)'S' });
        _log("[lvds] sent firmware status query 'S' over active capture");
    }

    /// <summary>
    /// Snapshot of reassembler statistics for periodic UI refresh.
    /// This allows the UI to show progress even before complete frames arrive.
    /// </summary>
    public (uint FrameCount, int SyncLosses, int CrcErrors, int ParityErrors, long TotalBytes) GetReassemblerStats()
    {
        var r = _receiver;
        return (r.FrameCount, r.SyncLossCount, r.CrcErrorCount, r.ParityErrorCount, r.TotalBytesReceived);
    }

    // ── Construction ────────────────────────────────────────────────────

    public LvdsLiveManager(LsmDeviceType deviceType, double signalLostTimeoutSec, Action<string> log,
                           double fpsWindowSec = 0.25, double fpsAlpha = 0.30)
    {
        _log = log ?? (_ => { });
        _deviceType = deviceType;
        _config = LvdsProtocol.GetUartConfig(deviceType);
        _signalLostTimeout = TimeSpan.FromSeconds(signalLostTimeoutSec);
        _fpsWindowSec = fpsWindowSec;
        _fpsAlpha = fpsAlpha;

        _lvdsFrame = new byte[_config.ActiveBytes];
        _receiver = new LvdsCookedFrameReceiver();
        _receiver.OnFrameReady += OnReceivedFrame;
    }

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Start capturing from the specified COM port.
    /// </summary>
    public void StartCapture(string portName)
    {
        lock (_lock)
        {
            StopCapture();

            try
            {
                _receiver.Dispose();
                _receiver = new LvdsCookedFrameReceiver();
                _receiver.OnFrameReady += OnReceivedFrame;
                _hasFrame = false;
                _lastFrameUtc = DateTime.MinValue;
                _bytesReceived = 0;
                _hexDumpDone = false;
                _fpsEma = 0;
                _fpsFrameCount = 0;
                _fpsSw.Restart();

                _capture = LvdsUartCapture.Start(portName, _config, OnSerialData, _log);

                // Tell Pico 2 firmware which UART mode to use
                _capture.SendModeCommand(_config.IsNichia);

                _log($"[lvds] capture started on {portName} for {_deviceType.GetDisplayName()}");
            }
            catch (Exception ex)
            {
                _log($"[lvds] failed to start capture on {portName}: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Stop the current capture session.
    /// </summary>
    public void StopCapture()
    {
        lock (_lock)
        {
            if (_capture != null)
            {
                var port = _capture.PortName;
                _capture.Dispose();
                _capture = null;
                _log($"[lvds] capture stopped ({port})");
            }
        }
    }

    /// <summary>
    /// Reconfigure for a different device type (changes baud rate, frame dimensions).
    /// Stops any active capture.
    /// </summary>
    public void ReconfigureForDevice(LsmDeviceType deviceType)
    {
        lock (_lock)
        {
            StopCapture();

            _deviceType = deviceType;
            _config = LvdsProtocol.GetUartConfig(deviceType);

            // Rebuild receiver and frame buffer for new dimensions
            _receiver.OnFrameReady -= OnReceivedFrame;
            _receiver.Dispose();
            _receiver = new LvdsCookedFrameReceiver();
            _receiver.OnFrameReady += OnReceivedFrame;

            _lvdsFrame = new byte[_config.ActiveBytes];
            _hasFrame = false;
            _lastFrameUtc = DateTime.MinValue;
            _fpsEma = 0;
            _fpsFrameCount = 0;
            _fpsSw.Restart();

            _log($"[lvds] reconfigured for {deviceType.GetDisplayName()}: " +
                 $"{_config.FrameWidth}×{_config.ActiveHeight} @ {_config.BaudRate} baud");
        }
    }

    /// <summary>
    /// Check if signal has been lost (no frame for timeout period).
    /// </summary>
    public bool IsSignalLost()
    {
        return _hasFrame
            && _lastFrameUtc != DateTime.MinValue
            && (DateTime.UtcNow - _lastFrameUtc) > _signalLostTimeout;
    }

    /// <summary>
    /// Push data from a simulated source into the reassembler.
    /// Allows pipeline testing without a real serial port.
    /// </summary>
    public void PushSimulatedData(byte[] data, int count)
    {
        Interlocked.Add(ref _bytesReceived, count);
        _receiver.Push(data, count);
    }

    /// <summary>
    /// Clear frame state (for signal loss handling).
    /// </summary>
    public void ClearFrame()
    {
        _hasFrame = false;
        _lastFrameUtc = DateTime.MinValue;
        Array.Clear(_lvdsFrame);
    }

    /// <summary>
    /// Get diagnostic summary string.
    /// </summary>
    public string GetDiagnostics()
    {
        var r = _receiver;
        return $"LVDS: port={ActivePort ?? "none"}, frames={r.FrameCount}, " +
               $"syncLoss={r.SyncLossCount}, crcErr={r.CrcErrorCount}, " +
               $"parityErr={r.ParityErrorCount}, fps={_fpsEma:F1}, " +
               $"bytes={r.TotalBytesReceived:N0}";
    }

    /// <summary>
    /// List available COM ports.
    /// </summary>
    public static string[] ListPorts() => LvdsUartCapture.ListPorts();

    /// <summary>
    /// Send 'B' command to reboot the Pico 2 into USB bootloader (BOOTSEL mode).
    /// If a capture session is active, sends via the open port and stops capture.
    /// If not capturing, opens the specified port briefly and sends the command.
    /// After this, the COM port will disappear and RPI-RP2 drive will appear.
    /// </summary>
    public void EnterBootloader(string? portName = null)
    {
        lock (_lock)
        {
            if (_capture != null && _capture.IsOpen)
            {
                var port = _capture.PortName;
                _log($"[lvds] sending bootloader command to {port}...");
                _capture.SendBootloaderCommand();
                // Give a moment then stop capture (port will disconnect)
                Thread.Sleep(200);
                StopCapture();
            }
            else if (!string.IsNullOrEmpty(portName))
            {
                _log($"[lvds] sending bootloader command to {portName} (not capturing)...");
                LvdsUartCapture.SendBootloaderCommandTo(portName, _log);
            }
            else
            {
                _log("[lvds] cannot enter bootloader: no port specified and no active capture");
            }
        }
    }

    /// <summary>
    /// Static helper: send 'B' boot command to a Pico 2 on the given port.
    /// </summary>
    public static void EnterBootloader(string portName, Action<string>? log = null)
        => LvdsUartCapture.SendBootloaderCommandTo(portName, log);

    // ── Callbacks ───────────────────────────────────────────────────────

    /// <summary>Number of initial bytes to hex-dump when capture starts (for debugging).</summary>
    private const int HexDumpBytes = 128;
    private volatile bool _hexDumpDone;

    private void OnSerialData(byte[] buffer, int count)
    {
        long prev = Interlocked.Read(ref _bytesReceived);
        Interlocked.Add(ref _bytesReceived, count);

        // Log a hex dump of the very first bytes for debugging PIO byte issues
        if (!_hexDumpDone && prev < HexDumpBytes)
        {
            int dumpLen = (int)Math.Min(count, HexDumpBytes - prev);
            var hex = new System.Text.StringBuilder(dumpLen * 3 + 40);
            hex.Append($"[lvds] first-bytes hex (offset {prev}, len {dumpLen}): ");
            for (int i = 0; i < dumpLen; i++)
            {
                hex.Append(buffer[i].ToString("X2"));
                if (i < dumpLen - 1) hex.Append(' ');
            }
            _log(hex.ToString());
            if (prev + count >= HexDumpBytes) _hexDumpDone = true;
        }

        // Intercept firmware status response if a query is pending.
        // The firmware stops PIO data flow before sending "MODE=...\n",
        // so the status string arrives as a clean burst in the data stream.
        if (_interceptStatus)
        {
            for (int i = 0; i <= count - 5; i++)
            {
                if (buffer[i] == (byte)'M' && buffer[i + 1] == (byte)'O' &&
                    buffer[i + 2] == (byte)'D' && buffer[i + 3] == (byte)'E' &&
                    buffer[i + 4] == (byte)'=')
                {
                    // Extract from "MODE=" to newline or end of buffer
                    int end = i + 5;
                    while (end < count && buffer[end] != (byte)'\n') end++;
                    string response = System.Text.Encoding.ASCII.GetString(buffer, i, end - i).Trim();
                    _interceptStatus = false;
                    _log($"[lvds] firmware status intercepted: {response}");
                    var cb = _statusCallback;
                    _statusCallback = null;
                    cb?.Invoke(response);
                    // Don't push status text through receiver
                    // (PIO was paused anyway, so no real pixel data is mixed in)
                    return;
                }
            }
        }

        _receiver.Push(buffer, count);
    }

    private void OnReceivedFrame(byte[] frame, LvdsFrameMeta meta)
    {
        // Update FPS estimate
        Interlocked.Increment(ref _fpsFrameCount);
        if (_fpsSw.Elapsed.TotalSeconds >= _fpsWindowSec)
        {
            var sec = _fpsSw.Elapsed.TotalSeconds;
            double instantFps = Interlocked.Exchange(ref _fpsFrameCount, 0) / sec;
            _fpsSw.Restart();
            _fpsEma = _fpsEma <= 0 ? instantFps
                : _fpsEma * (1.0 - _fpsAlpha) + instantFps * _fpsAlpha;
        }

        // Firmware sends complete, CRC-verified frames.
        // No persistent merging needed — just copy and forward.
        int needed = frame.Length;
        if (_lvdsFrame.Length != needed)
            _lvdsFrame = new byte[needed];
        Buffer.BlockCopy(frame, 0, _lvdsFrame, 0, needed);
        _hasFrame = true;
        _lastFrameUtc = DateTime.UtcNow;

        OnFrameReady?.Invoke(frame, meta);
    }

    // ── IDisposable ─────────────────────────────────────────────────────

    public void Dispose()
    {
        StopCapture();
        _receiver.OnFrameReady -= OnReceivedFrame;
    }
}
