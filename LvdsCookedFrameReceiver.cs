using System;
using System.Threading;

namespace VilsSharpX;

/// <summary>
/// Receives "cooked frame" packets from the Pico 2 frame-aware firmware.
///
/// The firmware parses LVDS lines on-chip, assembles complete frames,
/// and sends them as simple binary packets over USB CDC:
///
///   [0xFE] [0xED]                         — magic bytes
///   [frame_id_lo] [frame_id_hi]           — 16-bit frame counter (LE)
///   [width_lo] [width_hi]                 — frame width in pixels (LE)
///   [height_lo] [height_hi]               — active height in pixels (LE)
///   [width × height bytes of pixel data]  — row-major grayscale
///
/// Each frame received is COMPLETE (all rows present, CRC-verified by
/// firmware).  No reassembly, persistent merging, or CRC checking
/// needed on the host side.
/// </summary>
public sealed class LvdsCookedFrameReceiver : IDisposable
{
    private enum State
    {
        ScanMagic0,   // Looking for 0xFE
        ScanMagic1,   // Looking for 0xED (after 0xFE)
        ReadHeader,   // Reading 6 header bytes (frame_id, w, h)
        ReadPixels,   // Reading w×h pixel bytes
    }

    private const byte MAGIC_0 = 0xFE;
    private const byte MAGIC_1 = 0xED;
    private const int HDR_PAYLOAD_SIZE = 6;  // frame_id(2) + w(2) + h(2)
    private const int MAX_PIXEL_BYTES = 320 * 84; // Osram worst case

    private State _state = State.ScanMagic0;
    private readonly byte[] _hdrBuf = new byte[HDR_PAYLOAD_SIZE];
    private int _hdrPos;
    private byte[] _pixelBuf = Array.Empty<byte>();
    private int _pixelPos;
    private int _frameWidth;
    private int _frameHeight;
    private uint _fwFrameId;   // firmware frame counter from packet

    // Statistics
    private uint _frameCount;
    private long _totalBytes;
    private int _syncLosses;   // magic scan resets

    /// <summary>Number of complete frames received.</summary>
    public uint FrameCount => _frameCount;

    /// <summary>Total raw bytes pushed into this receiver.</summary>
    public long TotalBytesReceived => _totalBytes;

    /// <summary>Times the magic bytes scanner had to resync.</summary>
    public int SyncLossCount => _syncLosses;

    // These are tracked by firmware, not by this receiver.
    // Return 0 for API compatibility with GetReassemblerStats().
    public int CrcErrorCount => 0;
    public int ParityErrorCount => 0;

    /// <summary>
    /// Fired when a complete cooked frame is received.
    /// byte[] contains width × height pixels (row-major grayscale).
    /// </summary>
    public event Action<byte[], LvdsFrameMeta>? OnFrameReady;

    /// <summary>
    /// Push raw serial bytes from the USB CDC connection.
    /// The receiver parses cooked frame packets from the byte stream.
    /// </summary>
    public void Push(byte[] data, int count)
    {
        Interlocked.Add(ref _totalBytes, count);

        int i = 0;
        while (i < count)
        {
            switch (_state)
            {
                case State.ScanMagic0:
                    if (data[i] == MAGIC_0)
                        _state = State.ScanMagic1;
                    i++;
                    break;

                case State.ScanMagic1:
                    if (data[i] == MAGIC_1)
                    {
                        _state = State.ReadHeader;
                        _hdrPos = 0;
                    }
                    else if (data[i] == MAGIC_0)
                    {
                        // Stay in ScanMagic1 — might be FE FE ED
                    }
                    else
                    {
                        _syncLosses++;
                        _state = State.ScanMagic0;
                    }
                    i++;
                    break;

                case State.ReadHeader:
                {
                    int need = HDR_PAYLOAD_SIZE - _hdrPos;
                    int avail = count - i;
                    int take = Math.Min(need, avail);
                    Buffer.BlockCopy(data, i, _hdrBuf, _hdrPos, take);
                    _hdrPos += take;
                    i += take;

                    if (_hdrPos >= HDR_PAYLOAD_SIZE)
                    {
                        _fwFrameId = (uint)(_hdrBuf[0] | (_hdrBuf[1] << 8));
                        _frameWidth = _hdrBuf[2] | (_hdrBuf[3] << 8);
                        _frameHeight = _hdrBuf[4] | (_hdrBuf[5] << 8);

                        int pixelBytes = _frameWidth * _frameHeight;
                        if (pixelBytes > 0 && pixelBytes <= MAX_PIXEL_BYTES)
                        {
                            if (_pixelBuf.Length != pixelBytes)
                                _pixelBuf = new byte[pixelBytes];
                            _pixelPos = 0;
                            _state = State.ReadPixels;
                        }
                        else
                        {
                            // Invalid dimensions — resync
                            _syncLosses++;
                            _state = State.ScanMagic0;
                        }
                    }
                    break;
                }

                case State.ReadPixels:
                {
                    int need = _pixelBuf.Length - _pixelPos;
                    int avail = count - i;
                    int take = Math.Min(need, avail);
                    Buffer.BlockCopy(data, i, _pixelBuf, _pixelPos, take);
                    _pixelPos += take;
                    i += take;

                    if (_pixelPos >= _pixelBuf.Length)
                    {
                        EmitFrame();
                        _state = State.ScanMagic0;
                    }
                    break;
                }
            }
        }
    }

    private void EmitFrame()
    {
        _frameCount++;

        // Copy pixel data to a new buffer for the subscriber
        var frame = new byte[_pixelBuf.Length];
        Buffer.BlockCopy(_pixelBuf, 0, frame, 0, frame.Length);

        // Build all-valid line mask (firmware already CRC-verified everything)
        var lineValid = new bool[_frameHeight];
        Array.Fill(lineValid, true);

        var meta = new LvdsFrameMeta
        {
            FrameId = _frameCount,
            Width = _frameWidth,
            Height = _frameHeight,
            LinesReceived = _frameHeight,  // firmware sends complete frames
            ValidLines = _frameHeight,
            LinesExpected = _frameHeight,
            LineValidityMask = lineValid,
            SyncLosses = _syncLosses,
            CrcErrors = 0,        // CRC is checked on firmware side
            ParityErrors = 0,
            TotalBytes = _totalBytes,
        };

        OnFrameReady?.Invoke(frame, meta);
    }

    public void Dispose() { }
}
