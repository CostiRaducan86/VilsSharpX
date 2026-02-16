using System;
using System.Buffers;

namespace VilsSharpX;

/// <summary>
/// Reassembles LVDS UART byte stream into complete video frames.
///
/// Protocol (inferred from Saleae captures — to be refined):
///   [SYNC0=0x5D] [SYNC1=0x53] [HDR...] [PIXEL_DATA: W×H bytes] [optional checksum]
///
/// The reassembler uses a state machine:
///   WaitSync0 → WaitSync1 → ReadHeader → ReadPixels → EmitFrame → WaitSync0
///
/// Frame data is emitted as a cropped active-area buffer (metadata lines removed).
/// </summary>
public sealed class LvdsFrameReassembler
{
    // ── Configuration ───────────────────────────────────────────────────
    private readonly int _frameWidth;
    private readonly int _frameHeightLvds;   // full LVDS height (incl. metadata)
    private readonly int _activeHeight;       // cropped active height
    private readonly int _metaLines;

    // ── State machine ───────────────────────────────────────────────────
    private enum State { WaitSync0, WaitSync1, ReadHeader, ReadPixels, SkipMetadata }

    private State _state = State.WaitSync0;

    // Header buffer (small, fixed)
    private readonly byte[] _headerBuf = new byte[LvdsProtocol.MinHeaderLen];
    private int _headerPos;

    // Pixel accumulation buffer (W × H_LVDS)
    private readonly byte[] _pixelBuf;
    private int _pixelPos;

    // Frame counter
    private uint _frameCount;

    // Diagnostic counters
    private int _syncLossCount;
    private long _totalBytesReceived;

    // ── Events ──────────────────────────────────────────────────────────
    /// <summary>
    /// Fired when a complete frame is reassembled.
    /// The byte[] is a NEW buffer containing only the active area (W × ActiveHeight).
    /// </summary>
    public event Action<byte[], LvdsFrameMeta>? OnFrameReady;

    /// <summary>
    /// Creates a new LVDS frame reassembler.
    /// </summary>
    public LvdsFrameReassembler(int width, int lvdsHeight, int activeHeight)
    {
        _frameWidth = width;
        _frameHeightLvds = lvdsHeight;
        _activeHeight = activeHeight;
        _metaLines = lvdsHeight - activeHeight;

        int totalPixels = width * lvdsHeight;
        _pixelBuf = new byte[totalPixels];
    }

    /// <summary>
    /// Total bytes expected for pixel data in a full LVDS frame.
    /// </summary>
    public int PixelBytesExpected => _frameWidth * _frameHeightLvds;

    /// <summary>
    /// Active-area pixel count (what gets emitted).
    /// </summary>
    public int ActivePixelBytes => _frameWidth * _activeHeight;

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Push a chunk of received bytes into the reassembler.
    /// Called from the serial receive thread — must be fast.
    /// </summary>
    public void Push(byte[] data, int offset, int count)
    {
        _totalBytesReceived += count;

        for (int i = offset; i < offset + count; i++)
        {
            byte b = data[i];

            switch (_state)
            {
                case State.WaitSync0:
                    if (b == LvdsProtocol.Sync0)
                        _state = State.WaitSync1;
                    break;

                case State.WaitSync1:
                    if (b == LvdsProtocol.Sync1_Ecu || b == LvdsProtocol.Sync1_Lsm)
                    {
                        _headerBuf[0] = LvdsProtocol.Sync0;
                        _headerBuf[1] = b;
                        _headerPos = 2;
                        _state = State.ReadHeader;
                    }
                    else if (b == LvdsProtocol.Sync0)
                    {
                        // Stay in WaitSync1 — could be a repeated sync byte
                    }
                    else
                    {
                        _syncLossCount++;
                        _state = State.WaitSync0;
                    }
                    break;

                case State.ReadHeader:
                    if (_headerPos < _headerBuf.Length)
                    {
                        _headerBuf[_headerPos++] = b;
                    }

                    if (_headerPos >= LvdsProtocol.MinHeaderLen)
                    {
                        // Header complete — start pixel accumulation
                        _pixelPos = 0;
                        _state = State.ReadPixels;
                    }
                    break;

                case State.ReadPixels:
                    {
                        // Accumulate pixel data up to the expected active area bytes.
                        // We read W × ActiveHeight first (the visible image).
                        int activeBytes = _frameWidth * _activeHeight;
                        if (_pixelPos < activeBytes)
                        {
                            _pixelBuf[_pixelPos++] = b;
                        }

                        if (_pixelPos >= activeBytes)
                        {
                            if (_metaLines > 0)
                            {
                                // Still need to skip/consume metadata lines
                                _pixelPos = 0; // reuse as skip counter
                                _state = State.SkipMetadata;
                            }
                            else
                            {
                                EmitFrame();
                            }
                        }
                    }
                    break;

                case State.SkipMetadata:
                    {
                        int metaBytes = _frameWidth * _metaLines;
                        _pixelPos++;
                        if (_pixelPos >= metaBytes)
                        {
                            EmitFrame();
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Convenience overload — push entire buffer.
    /// </summary>
    public void Push(byte[] data, int count) => Push(data, 0, count);

    /// <summary>
    /// Reset reassembler state (e.g. on device type change or reconnect).
    /// </summary>
    public void Reset()
    {
        _state = State.WaitSync0;
        _headerPos = 0;
        _pixelPos = 0;
        _syncLossCount = 0;
        _totalBytesReceived = 0;
        _frameCount = 0;
    }

    // ── Diagnostics ─────────────────────────────────────────────────────
    public uint FrameCount => _frameCount;
    public int SyncLossCount => _syncLossCount;
    public long TotalBytesReceived => _totalBytesReceived;

    // ── Internals ───────────────────────────────────────────────────────

    private void EmitFrame()
    {
        _frameCount++;

        // Copy active area to a new buffer for the subscriber
        int activeBytes = _frameWidth * _activeHeight;
        var frame = new byte[activeBytes];
        Buffer.BlockCopy(_pixelBuf, 0, frame, 0, activeBytes);

        var meta = new LvdsFrameMeta
        {
            FrameId = _frameCount,
            Width = _frameWidth,
            Height = _activeHeight,
            SyncLosses = _syncLossCount,
            TotalBytes = _totalBytesReceived,
            HeaderByte2 = _headerBuf[1],
            HeaderByte3 = _headerBuf.Length > 2 ? _headerBuf[2] : (byte)0,
        };

        _state = State.WaitSync0;
        _headerPos = 0;
        _pixelPos = 0;

        OnFrameReady?.Invoke(frame, meta);
    }
}

/// <summary>
/// Metadata about a reassembled LVDS frame.
/// </summary>
public sealed record LvdsFrameMeta
{
    public uint FrameId { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int SyncLosses { get; init; }
    public long TotalBytes { get; init; }
    public byte HeaderByte2 { get; init; }
    public byte HeaderByte3 { get; init; }
}
