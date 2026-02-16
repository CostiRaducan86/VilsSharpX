using System;
using System.Threading;
using System.Threading.Tasks;

namespace VilsSharpX;

/// <summary>
/// Generates synthetic LVDS line packets and feeds them into an
/// <see cref="LvdsFrameReassembler"/> for pipeline testing without hardware.
///
/// Produces frames with a moving gradient pattern so that visual correctness
/// is easily verified in Pane B. Each line packet is correctly formatted:
///   [0x5D] [row_byte] [W pixels] [CRC]
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
    private readonly bool _isNichia;
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
        _isNichia = _config.IsNichia;
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
        int h = _config.FrameHeight;      // full LVDS height (incl. metadata)
        int linePacketLen = _config.LinePacketLen;
        int crcLen = _config.CrcLen;

        // Pre-allocate packet buffer for one line
        var linePacket = new byte[linePacketLen];

        // Frame interval
        int frameIntervalMs = (int)(1000.0 / _targetFps);

        while (!ct.IsCancellationRequested)
        {
            var frameStart = DateTime.UtcNow;
            _frameCounter++;

            // Generate each line of the frame
            for (int row = 0; row < h && !ct.IsCancellationRequested; row++)
            {
                // ── Build line packet ─────────────────────────────────
                int pos = 0;

                // Sync byte
                linePacket[pos++] = LvdsProtocol.SyncByte;

                // Row byte
                linePacket[pos++] = _isNichia
                    ? LvdsProtocol.MakeNichiaRowByte(row)
                    : (byte)row;

                // Pixel data — moving gradient pattern
                // Gradient shifts by frame counter to create animation
                byte rowBase = (byte)((_frameCounter * 3 + row * 4) & 0xFF);
                for (int col = 0; col < w; col++)
                {
                    linePacket[pos++] = (byte)((rowBase + col) & 0xFF);
                }

                // CRC over pixel data only (bytes 2..2+W-1)
                if (_isNichia)
                {
                    ushort crc = LvdsCrc.ComputeCrc16(linePacket, LvdsProtocol.LineHeaderLen, w);
                    linePacket[pos++] = (byte)(crc >> 8);   // big-endian
                    linePacket[pos++] = (byte)(crc & 0xFF);
                }
                else
                {
                    uint crc = LvdsCrc.ComputeCrc32(linePacket, LvdsProtocol.LineHeaderLen, w);
                    linePacket[pos++] = (byte)(crc & 0xFF);          // little-endian
                    linePacket[pos++] = (byte)((crc >> 8) & 0xFF);
                    linePacket[pos++] = (byte)((crc >> 16) & 0xFF);
                    linePacket[pos++] = (byte)((crc >> 24) & 0xFF);
                }

                // Emit line packet (clone buffer since subscriber may hold reference)
                var clone = new byte[linePacketLen];
                Buffer.BlockCopy(linePacket, 0, clone, 0, linePacketLen);
                OnDataGenerated?.Invoke(clone, linePacketLen);
            }

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
