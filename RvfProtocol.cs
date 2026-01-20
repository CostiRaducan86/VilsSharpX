namespace VideoStreamPlayer;

public static class RvfProtocol
{
    public const int DefaultPort = 50070;

    // "RVFU"(4) + ver(1) + w(2) + h(2) + line(2) + numLines(1) + end(1) + frameId(4) + seq(4)
    public const int HeaderSize = 21;

    public const int W = 320;
    public const int H = 80;

    public const byte Version = 1;

    public static bool HasMagic(ReadOnlySpan<byte> b)
        => b.Length >= 4 && b[0] == (byte)'R' && b[1] == (byte)'V' && b[2] == (byte)'F' && b[3] == (byte)'U';
}
