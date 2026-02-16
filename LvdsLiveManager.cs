using System;
using System.Threading;

namespace VilsSharpX;

/// <summary>
/// Manages the LVDS serial capture pipeline for Pane B.
///
/// Architecture:
///   Pico 2 (LogicAnalyzer board)
///     → captures TTL UART signal via PIO (post-LVDS receiver)
///     → forwards raw bytes over USB CDC (virtual COM port)
///     → LvdsUartCapture reads COM port
///     → LvdsFrameReassembler parses sync/header/pixels
///     → OnFrameReady event fires with cropped active-area frame
///     → MainWindow renders on Pane B
///
/// Analogous to <see cref="LiveCaptureManager"/> for AVTP Ethernet.
/// </summary>
public sealed class LvdsLiveManager : IDisposable
{
    private readonly Action<string> _log;
    private readonly object _lock = new();

    // Serial capture
    private LvdsUartCapture? _capture;

    // Frame reassembler
    private LvdsFrameReassembler _reassembler;

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

    // ── Construction ────────────────────────────────────────────────────

    public LvdsLiveManager(LsmDeviceType deviceType, double signalLostTimeoutSec, Action<string> log)
    {
        _log = log ?? (_ => { });
        _deviceType = deviceType;
        _config = LvdsProtocol.GetUartConfig(deviceType);
        _signalLostTimeout = TimeSpan.FromSeconds(signalLostTimeoutSec);

        _lvdsFrame = new byte[_config.ActiveBytes];
        _reassembler = new LvdsFrameReassembler(_config.FrameWidth, _config.FrameHeight, _config.ActiveHeight);
        _reassembler.OnFrameReady += OnReassembledFrame;
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
                _reassembler.Reset();
                _hasFrame = false;
                _lastFrameUtc = DateTime.MinValue;
                _bytesReceived = 0;

                _capture = LvdsUartCapture.Start(portName, _config, OnSerialData, _log);
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

            // Rebuild reassembler and frame buffer for new dimensions
            _reassembler.OnFrameReady -= OnReassembledFrame;
            _reassembler = new LvdsFrameReassembler(_config.FrameWidth, _config.FrameHeight, _config.ActiveHeight);
            _reassembler.OnFrameReady += OnReassembledFrame;

            _lvdsFrame = new byte[_config.ActiveBytes];
            _hasFrame = false;
            _lastFrameUtc = DateTime.MinValue;

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
    /// Clear frame state (for signal loss handling).
    /// </summary>
    public void ClearFrame()
    {
        _hasFrame = false;
        _lastFrameUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Get diagnostic summary string.
    /// </summary>
    public string GetDiagnostics()
    {
        var r = _reassembler;
        return $"LVDS: port={ActivePort ?? "none"}, frames={r.FrameCount}, " +
               $"syncLoss={r.SyncLossCount}, bytes={r.TotalBytesReceived:N0}";
    }

    /// <summary>
    /// List available COM ports.
    /// </summary>
    public static string[] ListPorts() => LvdsUartCapture.ListPorts();

    // ── Callbacks ───────────────────────────────────────────────────────

    private void OnSerialData(byte[] buffer, int count)
    {
        Interlocked.Add(ref _bytesReceived, count);
        _reassembler.Push(buffer, count);
    }

    private void OnReassembledFrame(byte[] frame, LvdsFrameMeta meta)
    {
        // Store latest frame
        if (frame.Length <= _lvdsFrame.Length)
        {
            Buffer.BlockCopy(frame, 0, _lvdsFrame, 0, frame.Length);
            _hasFrame = true;
            _lastFrameUtc = DateTime.UtcNow;
        }

        // Forward to subscribers
        OnFrameReady?.Invoke(frame, meta);
    }

    // ── IDisposable ─────────────────────────────────────────────────────

    public void Dispose()
    {
        StopCapture();
        _reassembler.OnFrameReady -= OnReassembledFrame;
    }
}
