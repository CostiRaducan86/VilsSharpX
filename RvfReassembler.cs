using System;
namespace VideoStreamPlayer;

public sealed class RvfReassembler
{
    public const int W = RvfProtocol.W;
    public const int H = RvfProtocol.H;

    private readonly byte[] _frame = new byte[W * H];
    private readonly bool[] _lineWritten = new bool[H];

    private uint _lastSeq;
    private bool _haveLastSeq;

    private int _linesWrittenThisFrame;
    private int _seqGapsThisFrame;

    public event Action<byte[], FrameMeta>? OnFrameReady;

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
        if (c.Width != W) return;
        if (c.Height != H) return;
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
        if (startLine0 < 0 || startLine0 >= H) return;

        int lines = c.NumLines;
        int bytesPerLine = W;

        // payload is numLines*W bytes
        if (c.Payload.Length < lines * bytesPerLine) return;

        for (int l = 0; l < lines; l++)
        {
            int y = startLine0 + l;
            if (y < 0 || y >= H) continue;

            int dst = y * W;
            int src = l * W;

            Buffer.BlockCopy(c.Payload, src, _frame, dst, W);

            if (!_lineWritten[y])
            {
                _lineWritten[y] = true;
                _linesWrittenThisFrame++;
            }
        }

        if (c.EndFrame)
        {
            // emit a COPY so UI can render while we assemble next frames
            var outFrame = new byte[W * H];
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
