using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VilsSharpX;

/// <summary>
/// Reads AVI files containing either uncompressed or MJPEG-compressed video frames
/// and exposes them as Gray8 top-down bitmaps.
/// </summary>
internal sealed class AviUncompressedVideoReader : IDisposable
{
    // FourCC for MJPEG: 'M'(0x4D) 'J'(0x4A) 'P'(0x50) 'G'(0x47) → little-endian 0x47504A4D
    private const uint FOURCC_MJPG = 0x47504A4D;

    private readonly FileStream _fs;
    private readonly BinaryReader _br;

    private readonly List<IndexEntry> _frames;
    private readonly long _moviDataStart;
    private readonly int _streamIndex;
    private readonly bool _isMjpeg;

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public int BitsPerPixel { get; }
    public int SourceStride { get; }
    public bool BottomUp { get; }
    public double FrameDurationMs { get; }
    public int FrameCount => _frames.Count;

    private AviUncompressedVideoReader(
        string path,
        FileStream fs,
        BinaryReader br,
        List<IndexEntry> frames,
        long moviDataStart,
        int streamIndex,
        int width,
        int height,
        int bitsPerPixel,
        int sourceStride,
        bool bottomUp,
        double frameDurationMs,
        bool isMjpeg)
    {
        Path = path;
        _fs = fs;
        _br = br;
        _frames = frames;
        _moviDataStart = moviDataStart;
        _streamIndex = streamIndex;
        Width = width;
        Height = height;
        BitsPerPixel = bitsPerPixel;
        SourceStride = sourceStride;
        BottomUp = bottomUp;
        FrameDurationMs = frameDurationMs;
        _isMjpeg = isMjpeg;
    }

    public static AviUncompressedVideoReader Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);

        try
        {
            ReadFourCC(br, "RIFF");
            _ = br.ReadUInt32(); // file size
            ReadFourCC(br, "AVI ");

            uint microSecPerFrame = 0;
            int? selectedStreamIndex = null;
            int currentStrlIndex = -1;

            int width = 0;
            int height = 0;
            int bitsPerPixel = 0;
            uint compression = 0;

            long moviDataStart = -1;
            var idx = new List<IndexEntry>(capacity: 4096);

            while (fs.Position + 8 <= fs.Length)
            {
                long chunkStart = fs.Position;
                string id = ReadFourCC(br);
                uint size = br.ReadUInt32();
                long dataStart = fs.Position;

                if (id == "LIST")
                {
                    string listType = ReadFourCC(br);
                    long listDataStart = fs.Position;
                    long listEnd = dataStart + size;

                    if (listType == "hdrl")
                    {
                        // parse header list
                        while (fs.Position + 8 <= listEnd)
                        {
                            long subStart = fs.Position;
                            string sid = ReadFourCC(br);
                            uint ssize = br.ReadUInt32();
                            long sdata = fs.Position;
                            long send = sdata + ssize;

                            if (sid == "avih")
                            {
                                microSecPerFrame = br.ReadUInt32();
                                // skip remainder
                                fs.Position = send;
                            }
                            else if (sid == "LIST")
                            {
                                string stype = ReadFourCC(br);
                                long sListEnd = sdata + ssize;

                                if (stype == "strl")
                                {
                                    currentStrlIndex++;
                                    bool isVideo = false;

                                    while (fs.Position + 8 <= sListEnd)
                                    {
                                        long stStart = fs.Position;
                                        string tid = ReadFourCC(br);
                                        uint tsize = br.ReadUInt32();
                                        long tdata = fs.Position;
                                        long tend = tdata + tsize;

                                        if (tid == "strh")
                                        {
                                            string fccType = ReadFourCC(br);
                                            _ = ReadFourCC(br); // fccHandler
                                            _ = br.ReadUInt32(); // flags
                                            _ = br.ReadUInt16(); // priority
                                            _ = br.ReadUInt16(); // language
                                            _ = br.ReadUInt32(); // initial frames
                                            _ = br.ReadUInt32(); // scale
                                            _ = br.ReadUInt32(); // rate
                                            _ = br.ReadUInt32(); // start
                                            _ = br.ReadUInt32(); // length
                                            fs.Position = tend;

                                            if (fccType == "vids")
                                                isVideo = true;

                                            if (isVideo && selectedStreamIndex == null)
                                                selectedStreamIndex = currentStrlIndex;
                                        }
                                        else if (tid == "strf")
                                        {
                                            // BITMAPINFOHEADER
                                            uint biSize = br.ReadUInt32();
                                            if (biSize < 40)
                                                throw new InvalidDataException("Unsupported BITMAPINFOHEADER size.");

                                            int biWidth = br.ReadInt32();
                                            int biHeight = br.ReadInt32();
                                            _ = br.ReadUInt16(); // planes
                                            ushort biBitCount = br.ReadUInt16();
                                            uint biCompression = br.ReadUInt32();
                                            _ = br.ReadUInt32(); // sizeImage
                                            _ = br.ReadInt32(); // xppm
                                            _ = br.ReadInt32(); // yppm
                                            _ = br.ReadUInt32(); // clrUsed
                                            _ = br.ReadUInt32(); // clrImportant

                                            // skip any palette/extra
                                            fs.Position = tend;

                                            if (currentStrlIndex == selectedStreamIndex)
                                            {
                                                width = biWidth;
                                                height = biHeight;
                                                bitsPerPixel = biBitCount;
                                                compression = biCompression;
                                            }
                                        }
                                        else
                                        {
                                            fs.Position = tend;
                                        }

                                        fs.Position = AlignToEven(fs.Position);
                                    }
                                }
                                else
                                {
                                    fs.Position = sListEnd;
                                }

                                fs.Position = AlignToEven(fs.Position);
                            }
                            else
                            {
                                fs.Position = send;
                                fs.Position = AlignToEven(fs.Position);
                            }

                            if (fs.Position <= subStart)
                                fs.Position = subStart + 8 + ssize;
                        }

                        fs.Position = listEnd;
                    }
                    else if (listType == "movi")
                    {
                        moviDataStart = listDataStart;
                        fs.Position = listEnd;
                    }
                    else
                    {
                        fs.Position = listEnd;
                    }

                    fs.Position = AlignToEven(fs.Position);
                }
                else if (id == "idx1")
                {
                    if (selectedStreamIndex == null)
                        selectedStreamIndex = 0;

                    int entries = (int)(size / 16);
                    for (int i = 0; i < entries; i++)
                    {
                        string ckid = ReadFourCC(br);
                        uint flags = br.ReadUInt32();
                        uint offset = br.ReadUInt32();
                        uint csize = br.ReadUInt32();

                        if (TryParseStreamChunkId(ckid, out var sIdx, out var suffix) && sIdx == selectedStreamIndex)
                        {
                            // keep only video frames for the selected stream
                            if (suffix == "db" || suffix == "dc")
                                idx.Add(new IndexEntry(ckid, flags, offset, csize));
                        }
                    }

                    fs.Position = dataStart + size;
                    fs.Position = AlignToEven(fs.Position);
                }
                else
                {
                    fs.Position = dataStart + size;
                    fs.Position = AlignToEven(fs.Position);
                }

                if (fs.Position <= chunkStart)
                    fs.Position = chunkStart + 8 + size;
            }

            if (moviDataStart < 0)
                throw new InvalidDataException("AVI: missing 'movi' list.");

            if (idx.Count == 0)
                throw new InvalidDataException("AVI: missing/empty idx1; only indexed AVIs are supported (EmitIndex1=true).");

            bool isMjpeg = compression == FOURCC_MJPG;
            if (compression != 0 && !isMjpeg)
                throw new InvalidDataException($"AVI: unsupported compression (0x{compression:X8}). Supported: uncompressed, MJPG.");

            if (width <= 0 || height == 0 || bitsPerPixel == 0)
                throw new InvalidDataException("AVI: missing/invalid BITMAPINFOHEADER.");

            bool bottomUp = height > 0;
            int absH = Math.Abs(height);

            int bytesPerPixel = isMjpeg ? 4 : bitsPerPixel switch
            {
                8 => 1,
                24 => 3,
                32 => 4,
                _ => throw new InvalidDataException($"AVI: unsupported BitsPerPixel={bitsPerPixel}. Supported: 8/24/32.")
            };

            int stride = isMjpeg ? AlignTo4(width * 4) : AlignTo4(width * bytesPerPixel);

            double frameMs = microSecPerFrame > 0 ? microSecPerFrame / 1000.0 : 40.0;
            if (frameMs < 1.0) frameMs = 1.0;

            // Keep idx entries in file order.
            int streamIndex = selectedStreamIndex ?? 0;
            return new AviUncompressedVideoReader(path, fs, br, idx, moviDataStart, streamIndex, width, absH, bitsPerPixel, stride, bottomUp, frameMs, isMjpeg);
        }
        catch
        {
            try { br.Dispose(); } catch { }
            try { fs.Dispose(); } catch { }
            throw;
        }
    }

    public byte[] ReadFrameAsGray8TopDown(int frameIndex, int cropW, int cropH)
    {
        if (frameIndex < 0 || frameIndex >= _frames.Count)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        var entry = _frames[frameIndex];
        long headerPos = ResolveChunkHeaderPosition(entry);
        _fs.Position = headerPos;
        string ckid = ReadFourCC(_br);
        uint size = _br.ReadUInt32();
        if (!string.Equals(ckid, entry.ChunkId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("AVI: index offset mismatch.");

        long dataPos = _fs.Position;
        int dataSize = checked((int)size);
        var raw = _br.ReadBytes(dataSize);

        // Data is padded to even boundary, but idx1 size does not include pad.
        // We ignore padding.

        // Convert/crop to Gray8 top-down
        var dst = new byte[cropW * cropH];

        // ── MJPEG path: decode the JPEG blob with WPF and extract Gray8 ──
        if (_isMjpeg)
        {
            return DecodeMjpegToGray8TopDown(raw, cropW, cropH);
        }

        // ── Uncompressed path ──

        int srcW = Width;
        int srcH = Height;
        int copyW = Math.Min(cropW, srcW);
        int copyH = Math.Min(cropH, srcH);

        if (BitsPerPixel == 8)
        {
            for (int y = 0; y < copyH; y++)
            {
                int srcRow = BottomUp ? (srcH - 1 - y) * SourceStride : y * SourceStride;
                int dstRow = y * cropW;

                Buffer.BlockCopy(raw, srcRow, dst, dstRow, copyW);
            }
            return dst;
        }

        int bpp = BitsPerPixel / 8;
        for (int y = 0; y < copyH; y++)
        {
            int srcY = BottomUp ? (srcH - 1 - y) : y;
            int srcRow = srcY * SourceStride;
            int dstRow = y * cropW;

            for (int x = 0; x < copyW; x++)
            {
                int si = srcRow + (x * bpp);
                byte b = raw[si + 0];
                byte g = raw[si + 1];
                byte r = raw[si + 2];
                int gray = (r * 299 + g * 587 + b * 114 + 500) / 1000;
                if (gray < 0) gray = 0;
                if (gray > 255) gray = 255;
                dst[dstRow + x] = (byte)gray;
            }
        }

        return dst;
    }

    /// <summary>
    /// Decodes an MJPEG frame (raw JPEG bytes) to Gray8 top-down using WPF imaging.
    /// </summary>
    private static byte[] DecodeMjpegToGray8TopDown(byte[] jpegData, int cropW, int cropH)
    {
        var dst = new byte[cropW * cropH];

        using var ms = new MemoryStream(jpegData);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return dst;

        BitmapSource frame = decoder.Frames[0];

        // Convert to Gray8 if needed.
        if (frame.Format != PixelFormats.Gray8)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);

        int srcW = frame.PixelWidth;
        int srcH = frame.PixelHeight;
        int copyW = Math.Min(cropW, srcW);
        int copyH = Math.Min(cropH, srcH);
        int srcStride = srcW; // Gray8 stride = width
        var pixels = new byte[srcStride * srcH];
        frame.CopyPixels(pixels, srcStride, 0);

        for (int y = 0; y < copyH; y++)
        {
            int srcRow = y * srcStride;
            int dstRow = y * cropW;
            Buffer.BlockCopy(pixels, srcRow, dst, dstRow, copyW);
        }

        return dst;
    }

    private long ResolveChunkHeaderPosition(IndexEntry entry)
    {
        // idx1 offsets are usually relative to the start of movi LIST data.
        // Some files store offsets pointing to chunk data instead of header.
        // Try a few candidates.
        long basePos = _moviDataStart + entry.Offset;
        long[] candidates = [basePos, basePos - 8, basePos - 4];

        foreach (var h in candidates)
        {
            if (h < 0 || h + 8 > _fs.Length) continue;
            _fs.Position = h;
            string id = ReadFourCC(_br);
            uint size = _br.ReadUInt32();
            if (string.Equals(id, entry.ChunkId, StringComparison.OrdinalIgnoreCase))
            {
                // sanity: size should match index size (or be very close)
                if (entry.Size == 0 || size == entry.Size)
                    return h;

                // accept anyway
                return h;
            }
        }

        // fallback: assume basePos is the header
        return basePos;
    }

    public void Dispose()
    {
        _br.Dispose();
        _fs.Dispose();
    }

    private static int AlignTo4(int x) => (x + 3) & ~3;

    private static long AlignToEven(long x) => (x + 1) & ~1;

    private static string ReadFourCC(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        if (b.Length < 4) throw new EndOfStreamException();
        return Encoding.ASCII.GetString(b);
    }

    private static void ReadFourCC(BinaryReader br, string expected)
    {
        string s = ReadFourCC(br);
        if (!string.Equals(s, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"AVI: expected '{expected}', got '{s}'.");
    }

    private static bool TryParseStreamChunkId(string ckid, out int streamIndex, out string suffix)
    {
        streamIndex = 0;
        suffix = string.Empty;
        if (ckid.Length != 4) return false;
        if (!char.IsDigit(ckid[0]) || !char.IsDigit(ckid[1])) return false;
        streamIndex = (ckid[0] - '0') * 10 + (ckid[1] - '0');
        suffix = ckid.Substring(2, 2);
        return true;
    }

    private readonly record struct IndexEntry(string ChunkId, uint Flags, uint Offset, uint Size);
}
