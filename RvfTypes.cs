using System;

namespace VilsSharpX;

/// <summary>
/// Represents a single video frame with grayscale pixel data.
/// </summary>
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Data { get; }
    public DateTime TimestampUtc { get; }

    public Frame(int w, int h, byte[] data, DateTime tsUtc)
    {
        Width = w;
        Height = h;
        Stride = w;
        Data = (byte[])data.Clone(); // safe copy; later we can optimize with pooling
        TimestampUtc = tsUtc;
    }
}

/// <summary>
/// Represents a loaded PGM image.
/// </summary>
public sealed class PgmImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
}
