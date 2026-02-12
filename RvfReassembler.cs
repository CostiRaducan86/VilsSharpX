// DEPRECATED: RVF reassembler
//
// This file contains the RVF reassembly logic used to reconstruct 320×80 frames from
// incoming RVF/AVTP chunks. The reassembler is kept in source for traceability and
// potential future reuse, but the project disables automatic UDP/RVFU reception by
// default and uses the Ethernet/AVTP capture as the primary live input.

using System;
namespace VilsSharpX;

public sealed record RvfChunk(
    ushort Width,
    ushort Height,
    ushort LineNumber1Based,
    byte NumLines,
    bool EndFrame,
    uint FrameId,
    uint Seq,
    byte[] Payload);

public sealed class RvfReassembler
{
    public const int W = 320;
    public const int H = 80;

    private readonly int _w;
    private readonly int _h;
    private readonly byte[] _frame;
    private readonly bool[] _lineWritten;

    private uint _lastSeq;
    private bool _haveLastSeq;

    private int _linesWrittenThisFrame;
    private int _seqGapsThisFrame;

    public event Action<byte[], FrameMeta>? OnFrameReady;

    /// <summary>
    /// Creates a new RVF reassembler with the specified resolution.
    /// </summary>
    /// <param name="width">Frame width (default: 320)</param>
    /// <param name="height">Frame height (default: 80)</param>
    public RvfReassembler(int width = W, int height = H)
    {
        _w = width;
        _h = height;
        _frame = new byte[_w * _h];
        _lineWritten = new bool[_h];
    }

    public void ResetAll()
    {
        _haveLastSeq = false;
        _lastSeq = 0;
        ResetFrameState();
    }

    public void ResetFrameState()
    {
        Array.Clear(_lineWritten, 0, _lineWritten.Length);
        _linesWrittenThisFrame = 0;
        _seqGapsThisFrame = 0;
    }

    public void Push(RvfChunk c)
    {
        // basic guards (we keep it strict for your stream)
        if (c.Width != _w) return;
        if (c.Height != _h) return;
        if (c.NumLines == 0) return;

        // sequence gap detect
        if (_haveLastSeq)
        {
            uint expected = _lastSeq + 1;
            if (c.Seq != expected) _seqGapsThisFrame++;
        }
        _lastSeq = c.Seq;
        _haveLastSeq = true;

        // line number in your CAPL is 1,5,9,... (step = numLines)
        int startLine0 = c.LineNumber1Based - 1;
        if (startLine0 < 0 || startLine0 >= _h) return;

        int lines = c.NumLines;
        int bytesPerLine = _w;

        // payload is numLines*W bytes
        if (c.Payload.Length < lines * bytesPerLine) return;

        for (int l = 0; l < lines; l++)
        {
            int y = startLine0 + l;
            if (y < 0 || y >= _h) continue;

            int dst = y * _w;
            int src = l * _w;

            Buffer.BlockCopy(c.Payload, src, _frame, dst, _w);

            if (!_lineWritten[y])
            {
                _lineWritten[y] = true;
                _linesWrittenThisFrame++;
            }
        }

        if (c.EndFrame)
        {
            // emit a COPY so UI can render while we assemble next frames
            var outFrame = new byte[_w * _h];
            Buffer.BlockCopy(_frame, 0, outFrame, 0, outFrame.Length);

            var meta = new FrameMeta(
                frameId: c.FrameId,
                seq: c.Seq,
                linesWritten: _linesWrittenThisFrame,
                seqGaps: _seqGapsThisFrame);

            OnFrameReady?.Invoke(outFrame, meta);

            ResetFrameState();
        }
    }
}

public sealed record FrameMeta(uint frameId, uint seq, int linesWritten, int seqGaps);
