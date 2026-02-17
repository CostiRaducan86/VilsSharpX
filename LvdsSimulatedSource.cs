using System;
using System.Threading;
using System.Threading.Tasks;

namespace VilsSharpX;

/// <summary>
/// Generates synthetic LVDS frames and feeds them as "cooked frame" packets
/// into a <see cref="LvdsCookedFrameReceiver"/> for pipeline testing without hardware.
///
/// Produces frames with a moving gradient pattern so that visual correctness
/// is easily verified in Pane B. Each frame is emitted as a cooked packet:
///   [0xFE][0xED][frame_id:2B LE][width:2B LE][height:2B LE][pixels:W×H]
///
/// This matches the format sent by the Pico 2 frame-aware firmware.
///
/// Usage:
///   1. Create with desired device type and target FPS.
///   2. Subscribe <see cref="OnDataGenerated"/> (same signature as serial callback).
///   3. Call <see cref="Start"/> to begin generating.
///   4. Call <see cref="Stop"/> to stop.
/// </summary>
public sealed class LvdsSimulatedSource : IDisposable
{
    private readonly LvdsUartConfig _config;
    private readonly double _targetFps;
    private readonly Action<string>? _log;

    private CancellationTokenSource? _cts;
    private Task? _genTask;
    private uint _frameCounter;

    /// <summary>
    /// Callback with generated raw byte data — same signature as serial receive.
    /// (buffer, byteCount)
    /// </summary>
    public event Action<byte[], int>? OnDataGenerated;

    /// <summary>
    /// Creates a new simulated LVDS data source.
    /// </summary>
    /// <param name="deviceType">Nichia or Osram — determines dimensions, baud, CRC.</param>
    /// <param name="targetFps">Target frame rate (e.g. 30, 60). Default 30.</param>
    /// <param name="log">Optional diagnostic logger.</param>
    public LvdsSimulatedSource(LsmDeviceType deviceType, double targetFps = 30, Action<string>? log = null)
    {
        _config = LvdsProtocol.GetUartConfig(deviceType);
        _targetFps = targetFps > 0 ? targetFps : 30;
        _log = log;
    }

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    /// <summary>Start generating frames on a background thread.</summary>
    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _frameCounter = 0;
        var ct = _cts.Token;

        _genTask = Task.Run(() => GenerateLoop(ct), ct);
        _log?.Invoke("[lvds-sim] simulated source started");
    }

    /// <summary>Stop generating.</summary>
    public void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { _genTask?.Wait(500); } catch { /* ignore */ }
        _cts.Dispose();
        _cts = null;
        _genTask = null;
        _log?.Invoke("[lvds-sim] simulated source stopped");
    }

    public void Dispose() => Stop();

    // ── Generation loop ─────────────────────────────────────────────────

    private void GenerateLoop(CancellationToken ct)
    {
        int w = _config.FrameWidth;
        int activeH = _config.ActiveHeight; // active rows only (firmware crops metadata)

        // Cooked frame packet: 8-byte header + w*activeH pixel bytes
        int packetLen = 8 + w * activeH;
        var packet = new byte[packetLen];

        // Frame interval
        int frameIntervalMs = (int)(1000.0 / _targetFps);

        while (!ct.IsCancellationRequested)
        {
            var frameStart = DateTime.UtcNow;
            _frameCounter++;

            // ── Build cooked frame header ─────────────────────────
            packet[0] = 0xFE;  // magic
            packet[1] = 0xED;
            packet[2] = (byte)(_frameCounter & 0xFF);
            packet[3] = (byte)((_frameCounter >> 8) & 0xFF);
            packet[4] = (byte)(w & 0xFF);
            packet[5] = (byte)((w >> 8) & 0xFF);
            packet[6] = (byte)(activeH & 0xFF);
            packet[7] = (byte)((activeH >> 8) & 0xFF);

            // ── Build pixel data — moving gradient pattern ────────
            int pixOff = 8;
            for (int row = 0; row < activeH; row++)
            {
                byte rowBase = (byte)((_frameCounter * 3 + row * 4) & 0xFF);
                for (int col = 0; col < w; col++)
                {
                    packet[pixOff++] = (byte)((rowBase + col) & 0xFF);
                }
            }

            // Emit complete cooked frame packet
            var clone = new byte[packetLen];
            Buffer.BlockCopy(packet, 0, clone, 0, packetLen);
            OnDataGenerated?.Invoke(clone, packetLen);

            // Wait for remainder of frame interval
            var elapsed = DateTime.UtcNow - frameStart;
            int sleepMs = frameIntervalMs - (int)elapsed.TotalMilliseconds;
            if (sleepMs > 0)
            {
                try { Task.Delay(sleepMs, ct).Wait(ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
