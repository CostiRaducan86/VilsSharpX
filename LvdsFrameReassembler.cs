using System;

namespace VilsSharpX;

/// <summary>
/// Reassembles LVDS UART byte stream into complete video frames.
///
/// Protocol (confirmed by Saleae captures + ECU firmware source):
///   Each line is an independent packet:
///     [0x5D] [row_byte] [pixel_0 .. pixel_{W-1}] [CRC (2 or 4 bytes)]
///
///   Nichia: 260 bytes/line (1+1+256+2), CRC16 over pixels only, 68 lines/frame
///   Osram:  326 bytes/line (1+1+320+4), CRC32 over pixels only, 84 lines/frame
///
///   Row byte encodes the line address:
///     Nichia: [odd_parity_bit:1][row_address:7]  (row 0 = 0x80)
///     Osram:  raw row address (UART hardware handles parity via 8O1)
///
///   Frame boundary: detected when all H lines received, or when a line
///   that was already received appears again (rollover to next frame).
///
/// State machine (per-line):
///   WaitSync → ReadRowByte → ReadPixels → ReadCrc → PlaceLine → (repeat)
/// </summary>
public sealed class LvdsFrameReassembler
{
    // ── Configuration ───────────────────────────────────────────────────
    private readonly int _frameWidth;
    private readonly int _frameHeightLvds;   // total lines (incl. metadata)
    private readonly int _activeHeight;       // cropped active height
    private readonly int _crcLen;
    private readonly bool _isNichia;

    // ── State machine ───────────────────────────────────────────────────
    private enum State { WaitSync, ReadRowByte, ReadPixels, ReadCrc }

    private State _state = State.WaitSync;

    // Current line being parsed
    private byte _currentRowByte;
    private readonly byte[] _linePixels;      // pixel buffer for one line (W bytes)
    private int _pixelPos;
    private readonly byte[] _crcBuf;          // CRC buffer for one line
    private int _crcPos;

    // ── Frame accumulation ──────────────────────────────────────────────
    private readonly byte[] _frameBuf;        // W × H_LVDS pixel buffer
    private readonly bool[] _lineReceived;    // which lines have been placed
    private int _linesReceived;

    // Frame counter
    private uint _frameCount;

    // Diagnostic counters
    private int _syncLossCount;
    private int _crcErrorCount;
    private int _parityErrorCount;
    private long _totalBytesReceived;

    // Diagnostic logging — logs first N lines for debug
    private Action<string>? _log;
    private int _diagLogLinesRemaining;
    private const int DiagLogLinesMax = 10; // log first N lines of capture session

    // ── Events ──────────────────────────────────────────────────────────
    /// <summary>
    /// Fired when a complete frame is reassembled.
    /// The byte[] is a NEW buffer containing only the active area (W × ActiveHeight).
    /// </summary>
    public event Action<byte[], LvdsFrameMeta>? OnFrameReady;

    /// <summary>
    /// Creates a new LVDS frame reassembler for line-based protocol parsing.
    /// </summary>
    public LvdsFrameReassembler(int width, int lvdsHeight, int activeHeight, int crcLen, bool isNichia,
                                 Action<string>? log = null)
    {
        _frameWidth = width;
        _frameHeightLvds = lvdsHeight;
        _activeHeight = activeHeight;
        _crcLen = crcLen;
        _isNichia = isNichia;
        _log = log;
        _diagLogLinesRemaining = DiagLogLinesMax;

        _linePixels = new byte[width];
        _crcBuf = new byte[Math.Max(crcLen, 4)]; // at least 4 for CRC32
        _frameBuf = new byte[width * lvdsHeight];
        _lineReceived = new bool[lvdsHeight];
    }

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>Active-area pixel count (what gets emitted).</summary>
    public int ActivePixelBytes => _frameWidth * _activeHeight;

    public uint FrameCount => _frameCount;
    public int SyncLossCount => _syncLossCount;
    public int CrcErrorCount => _crcErrorCount;
    public int ParityErrorCount => _parityErrorCount;
    public long TotalBytesReceived => _totalBytesReceived;
    public int LinesReceivedInCurrentFrame => _linesReceived;

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
                case State.WaitSync:
                    if (b == LvdsProtocol.SyncByte)
                    {
                        _state = State.ReadRowByte;
                    }
                    // else: discard byte, stay in WaitSync
                    break;

                case State.ReadRowByte:
                    if (b == LvdsProtocol.SyncByte)
                    {
                        // Consecutive sync bytes — previous sync was noise; stay here
                        // (this handles the case where a 0x5D in data is misinterpreted as sync)
                        _syncLossCount++;
                        break;
                    }
                    _currentRowByte = b;
                    _pixelPos = 0;
                    _state = State.ReadPixels;
                    break;

                case State.ReadPixels:
                    _linePixels[_pixelPos++] = b;
                    if (_pixelPos >= _frameWidth)
                    {
                        _crcPos = 0;
                        _state = State.ReadCrc;
                    }
                    break;

                case State.ReadCrc:
                    _crcBuf[_crcPos++] = b;
                    if (_crcPos >= _crcLen)
                    {
                        PlaceLine();
                        _state = State.WaitSync;
                    }
                    break;
            }
        }
    }

    /// <summary>Convenience overload — push entire buffer.</summary>
    public void Push(byte[] data, int count) => Push(data, 0, count);

    /// <summary>Reset reassembler state (e.g. on device type change or reconnect).</summary>
    public void Reset()
    {
        _state = State.WaitSync;
        _pixelPos = 0;
        _crcPos = 0;
        _linesReceived = 0;
        _syncLossCount = 0;
        _crcErrorCount = 0;
        _parityErrorCount = 0;
        _totalBytesReceived = 0;
        _frameCount = 0;
        _diagLogLinesRemaining = DiagLogLinesMax;
        Array.Clear(_frameBuf);
        Array.Clear(_lineReceived);
    }

    // ── Internals ───────────────────────────────────────────────────────

    private void PlaceLine()
    {
        // Extract row address
        int row = _isNichia
            ? LvdsProtocol.ExtractNichiaRow(_currentRowByte)
            : LvdsProtocol.ExtractOsramRow(_currentRowByte);

        // Verify Nichia row-byte parity (diagnostic only, don't discard line)
        if (_isNichia && !LvdsProtocol.VerifyNichiaRowParity(_currentRowByte))
        {
            _parityErrorCount++;
        }

        // Bounds check — if row is outside expected range, treat as framing error
        if (row < 0 || row >= _frameHeightLvds)
        {
            _syncLossCount++;
            return;
        }

        // TODO: CRC verification (diagnostic only — don't discard data)
        // Nichia: CRC16 = _crcBuf[0]<<8 | _crcBuf[1], computed over _linePixels[0..W-1]
        // Osram:  CRC32 from _crcBuf[0..3], computed over _linePixels with seed
        // For now, we accept all lines regardless of CRC.

        // CRC verification (diagnostic — count errors but don't discard data)
        bool crcOk = true;
        if (_isNichia && _crcLen >= 2)
        {
            if (!LvdsCrc.VerifyNichiaCrc(_linePixels, 0, _frameWidth,
                                          _crcBuf[0], _crcBuf[1]))
            {
                _crcErrorCount++;
                crcOk = false;
                // Log first 5 CRC mismatches with detail
                if (_crcErrorCount <= 5 && _log != null)
                {
                    ushort computed = LvdsCrc.ComputeCrc16(_linePixels, 0, _frameWidth);
                    ushort received = (ushort)((_crcBuf[0] << 8) | _crcBuf[1]);
                    _log($"[lvds-crc] CRC16 MISMATCH row={row}: computed=0x{computed:X4} received=0x{received:X4} " +
                         $"px[0..3]=[{_linePixels[0]:X2} {_linePixels[1]:X2} {_linePixels[2]:X2} {_linePixels[3]:X2}]");
                }
            }
        }
        else if (!_isNichia && _crcLen >= 4)
        {
            if (!LvdsCrc.VerifyOsramCrc(_linePixels, 0, _frameWidth,
                                         _crcBuf[0], _crcBuf[1], _crcBuf[2], _crcBuf[3]))
            {
                _crcErrorCount++;
                crcOk = false;
                if (_crcErrorCount <= 5 && _log != null)
                {
                    uint computed = LvdsCrc.ComputeCrc32(_linePixels, 0, _frameWidth);
                    uint received = (uint)(_crcBuf[0] | (_crcBuf[1] << 8) | (_crcBuf[2] << 16) | (_crcBuf[3] << 24));
                    _log($"[lvds-crc] CRC32 MISMATCH row={row}: computed=0x{computed:X8} received=0x{received:X8} " +
                         $"px[0..3]=[{_linePixels[0]:X2} {_linePixels[1]:X2} {_linePixels[2]:X2} {_linePixels[3]:X2}]");
                }
            }
        }

        // ── Diagnostic hex log (first N lines of capture session) ───
        if (_diagLogLinesRemaining > 0 && _log != null)
        {
            _diagLogLinesRemaining--;
            string crcHex = _isNichia
                ? $"{_crcBuf[0]:X2} {_crcBuf[1]:X2}"
                : $"{_crcBuf[0]:X2} {_crcBuf[1]:X2} {_crcBuf[2]:X2} {_crcBuf[3]:X2}";
            _log($"[lvds-diag] line row={row} rowByte=0x{_currentRowByte:X2} " +
                 $"px[0..5]=[{_linePixels[0]:X2} {_linePixels[1]:X2} {_linePixels[2]:X2} {_linePixels[3]:X2} {_linePixels[4]:X2} {_linePixels[5]:X2}] " +
                 $"CRC=[{crcHex}] {(crcOk ? "OK" : "FAIL")}");
        }

        // ── Frame boundary detection ────────────────────────────────────
        // If this row was already received in the current frame,
        // we've wrapped around to the next frame → emit current + start fresh.
        if (_lineReceived[row] && _linesReceived > 0)
        {
            EmitFrame();
        }

        // Place pixel data into the correct row position in the frame buffer
        Buffer.BlockCopy(_linePixels, 0, _frameBuf, row * _frameWidth, _frameWidth);

        if (!_lineReceived[row])
        {
            _lineReceived[row] = true;
            _linesReceived++;
        }

        // Also emit when all lines have been received (normal completion)
        if (_linesReceived >= _frameHeightLvds)
        {
            EmitFrame();
        }
    }

    private void EmitFrame()
    {
        _frameCount++;

        // Copy active area to a new buffer (top activeHeight lines, crop metadata)
        int activeBytes = _frameWidth * _activeHeight;
        var frame = new byte[activeBytes];
        Buffer.BlockCopy(_frameBuf, 0, frame, 0, activeBytes);

        var meta = new LvdsFrameMeta
        {
            FrameId = _frameCount,
            Width = _frameWidth,
            Height = _activeHeight,
            LinesReceived = _linesReceived,
            LinesExpected = _frameHeightLvds,
            SyncLosses = _syncLossCount,
            CrcErrors = _crcErrorCount,
            ParityErrors = _parityErrorCount,
            TotalBytes = _totalBytesReceived,
        };

        // Reset line tracking for next frame
        _linesReceived = 0;
        Array.Clear(_lineReceived);
        // Keep _frameBuf content — new lines will overwrite

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
    /// <summary>Lines actually received before this frame was emitted.</summary>
    public int LinesReceived { get; init; }
    /// <summary>Expected total lines per frame (H_LVDS).</summary>
    public int LinesExpected { get; init; }
    public int SyncLosses { get; init; }
    public int CrcErrors { get; init; }
    public int ParityErrors { get; init; }
    public long TotalBytes { get; init; }
}
