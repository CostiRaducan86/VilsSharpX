using SharpAvi.Output;
using SharpAvi;
using SharpAvi.Codecs;
using ClosedXML.Excel;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoStreamPlayer;

public sealed class AviTripletRecorder : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride24;
    private readonly int _stride32;
    private readonly int _stride8;

    private readonly byte _deadband;

    private readonly string? _compareReportPath;

    private readonly AviWriter _writerA;
    private readonly IAviVideoStream _streamA;
    private readonly AviWriter _writerB;
    private readonly IAviVideoStream _streamB;
    private readonly AviWriter _writerD;
    private readonly IAviVideoStream _streamD;

    private readonly BlockingCollection<FrameSet> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    private readonly byte[] _a8;
    private readonly byte[] _b8;
    private readonly byte[] _d32;

    private byte[]? _lastAForReport;
    private byte[]? _lastBForReport;
    private int _lastFrameNrForReport;

    public AviTripletRecorder(string pathA, string pathB, string pathD, int width, int height, int fps, int queueCapacity = 300, string? compareCsvPath = null, byte compareDeadband = 0)
    {
        if (string.IsNullOrWhiteSpace(pathA)) throw new ArgumentException("Path is required", nameof(pathA));
        if (string.IsNullOrWhiteSpace(pathB)) throw new ArgumentException("Path is required", nameof(pathB));
        if (string.IsNullOrWhiteSpace(pathD)) throw new ArgumentException("Path is required", nameof(pathD));

        EnsureParentDirExists(pathA);
        EnsureParentDirExists(pathB);
        EnsureParentDirExists(pathD);

        _width = width;
        _height = height;
        _stride24 = width * 3;
        _stride32 = width * 4;
        _stride8 = AlignTo4(width);

        _deadband = compareDeadband;

        _compareReportPath = string.IsNullOrWhiteSpace(compareCsvPath) ? null : compareCsvPath;

        _a8 = new byte[_stride8 * height];
        _b8 = new byte[_stride8 * height];
        _d32 = new byte[_stride32 * height];

        _writerA = CreateGray8Writer(pathA, fps, out _streamA);
        _writerB = CreateGray8Writer(pathB, fps, out _streamB);
        _writerD = CreateBgr32Writer(pathD, fps, out _streamD);

        _queue = new BlockingCollection<FrameSet>(new ConcurrentQueue<FrameSet>(), Math.Max(1, queueCapacity));
        _worker = Task.Run(WorkerLoop);
    }

    private static void EnsureParentDirExists(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    public bool TryEnqueue(byte[] aGray, byte[] bGray, byte[] dBgrTopDown)
    {
        if (_queue.IsAddingCompleted) return false;
        return _queue.TryAdd(new FrameSet(aGray, bGray, dBgrTopDown));
    }

    public void Dispose()
    {
        try { _queue.CompleteAdding(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { }

        try { _writerA.Close(); } catch { }
        try { _writerB.Close(); } catch { }
        try { _writerD.Close(); } catch { }

        _cts.Dispose();
        _queue.Dispose();
    }

    public static void SaveSingleFrameCompareXlsx(
        string path,
        int frameNr,
        byte[] aGrayTopDown,
        byte[] bGrayTopDown,
        int w,
        int h,
        byte deviationThreshold)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required", nameof(path));
        if (aGrayTopDown == null) throw new ArgumentNullException(nameof(aGrayTopDown));
        if (bGrayTopDown == null) throw new ArgumentNullException(nameof(bGrayTopDown));
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w));
        if (aGrayTopDown.Length < w * h) throw new ArgumentException("A buffer too small", nameof(aGrayTopDown));
        if (bGrayTopDown.Length < w * h) throw new ArgumentException("B buffer too small", nameof(bGrayTopDown));

        string finalPath = EnsureXlsxExtension(path);
        string sheetName = MakeSheetName(Math.Max(1, frameNr));
        WriteCompareXlsx(finalPath, sheetName, aGrayTopDown, bGrayTopDown, w, h, deadband: deviationThreshold);
    }

    private AviWriter CreateGray8Writer(string path, int fps, out IAviVideoStream stream)
    {
        var writer = new AviWriter(path)
        {
            FramesPerSecond = fps,
            EmitIndex1 = true
        };

        // SharpAvi supports uncompressed 8bpp streams; a grayscale palette is implied.
        // Uncompressed streams expect bottom-up DIB-style frames.
        stream = writer.AddVideoStream(_width, _height, BitsPerPixel.Bpp8);
        stream.Codec = CodecIds.Uncompressed;
        return writer;
    }

    private AviWriter CreateBgr32Writer(string path, int fps, out IAviVideoStream stream)
    {
        var writer = new AviWriter(path)
        {
            FramesPerSecond = fps,
            EmitIndex1 = true
        };

        // Encoding streams expect TOP-DOWN BGR32 input frames.
        // Uncompressed encoder (very compatible, but large).
        stream = writer.AddEncodingVideoStream(new UncompressedVideoEncoder(_width, _height), ownsEncoder: true, width: _width, height: _height);
        return writer;
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var set in _queue.GetConsumingEnumerable(_cts.Token))
            {
                if (set.AGray.Length < _width * _height) continue;
                if (set.BGray.Length < _width * _height) continue;
                if (set.DBgrTopDown.Length < _stride24 * _height) continue;

                _lastAForReport = set.AGray;
                _lastBForReport = set.BGray;
                _lastFrameNrForReport++;

                Gray8ToGray8BottomUp(set.AGray, _a8, _width, _height, _stride8);
                Gray8ToGray8BottomUp(set.BGray, _b8, _width, _height, _stride8);
                Bgr24ToBgr32TopDown(set.DBgrTopDown, _d32, _width, _height);

                _streamA.WriteFrame(true, _a8, 0, _a8.Length);
                _streamB.WriteFrame(true, _b8, 0, _b8.Length);
                _streamD.WriteFrame(true, _d32, 0, _d32.Length);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        finally
        {
            try
            {
                if (_compareReportPath != null)
                {
                    var a = _lastAForReport;
                    var b = _lastBForReport;
                    int frameNr = _lastFrameNrForReport;
                    string finalPath = EnsureXlsxExtension(_compareReportPath);
                    string sheetName = MakeSheetName(frameNr);

                    if (a != null && b != null && a.Length >= _width * _height && b.Length >= _width * _height)
                        WriteCompareXlsx(finalPath, sheetName, a, b, _width, _height, deadband: _deadband);
                    else
                        WriteCompareXlsx(finalPath, sheetName, null, null, _width, _height, deadband: _deadband);
                }
            }
            catch
            {
                // ignore report I/O errors
            }
        }
    }

    private static int AlignTo4(int x) => (x + 3) & ~3;

    private static void Gray8ToGray8BottomUp(byte[] grayTopDown, byte[] dstGrayBottomUp, int w, int h, int dstStride)
    {
        if (dstStride < w) throw new ArgumentOutOfRangeException(nameof(dstStride));

        for (int y = 0; y < h; y++)
        {
            int srcRow = y * w;
            int dstRow = (h - 1 - y) * dstStride;

            Buffer.BlockCopy(grayTopDown, srcRow, dstGrayBottomUp, dstRow, w);
            for (int i = w; i < dstStride; i++) dstGrayBottomUp[dstRow + i] = 0;
        }
    }

    private static void WriteCompareXlsx(string path, string sheetName, byte[]? aGrayTopDown, byte[]? bGrayTopDown, int w, int h, byte deadband)
    {
        EnsureParentDirExists(path);

        int maxPos = 0;
        int maxNeg = 0;
        int darkPixels = 0;
        long sum = 0;
        long sumSq = 0;
        int count = 0;

        // Keep a list so we can write a dedicated tab.
        var darkList = new List<DarkPixel>(capacity: 64);

        if (aGrayTopDown != null && bGrayTopDown != null)
        {
            int n = w * h;
            count = n;
            for (int i = 0; i < n; i++)
            {
                // Deviation is ECU output minus input: B - A
                int diff = bGrayTopDown[i] - aGrayTopDown[i];

                // Dark pixel: A has signal but ECU outputs 0
                if (aGrayTopDown[i] > 0 && bGrayTopDown[i] == 0)
                {
                    darkPixels++;
                    // x/y for the index i
                    int y = i / w;
                    int x = i - (y * w);
                    darkList.Add(new DarkPixel(i + 1, x, y, diff, aGrayTopDown[i], bGrayTopDown[i]));
                }

                if (diff > 0 && diff > maxPos) maxPos = diff;
                if (diff < 0 && diff < maxNeg) maxNeg = diff;
                sum += diff;
                sumSq += (long)diff * diff;
            }
        }

        double mean = count > 0 ? (double)sum / count : 0.0;

        // Requirement: average deviation must be INT rounded.
        int meanInt = (int)Math.Round(mean, MidpointRounding.AwayFromZero);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(string.IsNullOrWhiteSpace(sheetName) ? "Frame" : sheetName);

        // Match the existing "single-cell line" style seen in Excel.
        ws.Cell(1, 1).Value = $"maximum positive deviation: {maxPos}";
        ws.Cell(2, 1).Value = $"maximum negative deviation: {maxNeg}";
        ws.Cell(3, 1).Value = $"total number of pixels with deviation: {count}";
        ws.Cell(4, 1).Value = $"average deviation: {meanInt}";
        ws.Cell(5, 1).Value = $"deviation threshold: {deadband}";
        ws.Cell(6, 1).Value = $"total number of dark pixels: {darkPixels}";

        int headerRow = 9;
        ws.Cell(headerRow, 1).Value = "pixel_ID";
        ws.Cell(headerRow, 2).Value = "x-Pos";
        ws.Cell(headerRow, 3).Value = "y-Pos";
        ws.Cell(headerRow, 4).Value = "deviation";

        if (aGrayTopDown != null && bGrayTopDown != null)
        {
            int row = headerRow + 1;
            for (int y = 0; y < h; y++)
            {
                int baseIdx = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx = baseIdx + x;
                    int pixelId = idx + 1;
                    // Deviation is ECU output minus input: B - A
                    int diff = bGrayTopDown[idx] - aGrayTopDown[idx];

                    bool isDarkPixel = aGrayTopDown[idx] > 0 && bGrayTopDown[idx] == 0;

                    ws.Cell(row, 1).Value = pixelId;
                    ws.Cell(row, 2).Value = x;
                    ws.Cell(row, 3).Value = y;
                    ws.Cell(row, 4).Value = diff;

                    if (isDarkPixel)
                    {
                        var r = ws.Range(row, 1, row, 4);
                        // High-visibility highlight for dark pixels (easy to spot while scrolling)
                        r.Style.Fill.BackgroundColor = XLColor.DarkRed;
                        r.Style.Font.FontColor = XLColor.White;
                        r.Style.Font.Bold = true;
                    }
                    row++;
                }
            }

            // Dedicated list tab for quick review.
            var wsDark = wb.Worksheets.Add("DarkPixels");
            wsDark.Cell(1, 1).Value = "Dark pixels (A>0 && B==0)";
            wsDark.Cell(2, 1).Value = $"Frame: {sheetName}";
            wsDark.Cell(3, 1).Value = $"Deviation threshold: {deadband}";
            wsDark.Cell(4, 1).Value = $"Total dark pixels: {darkPixels}";

            int dh = 6;
            wsDark.Cell(dh, 1).Value = "pixel_ID";
            wsDark.Cell(dh, 2).Value = "x-Pos";
            wsDark.Cell(dh, 3).Value = "y-Pos";
            wsDark.Cell(dh, 4).Value = "deviation";
            wsDark.Cell(dh, 5).Value = "A";
            wsDark.Cell(dh, 6).Value = "B";
            var hdr = wsDark.Range(dh, 1, dh, 6);
            hdr.Style.Font.Bold = true;

            int dr = dh + 1;
            foreach (var dp in darkList)
            {
                wsDark.Cell(dr, 1).Value = dp.PixelId;
                wsDark.Cell(dr, 2).Value = dp.X;
                wsDark.Cell(dr, 3).Value = dp.Y;
                wsDark.Cell(dr, 4).Value = dp.Deviation;
                wsDark.Cell(dr, 5).Value = dp.A;
                wsDark.Cell(dr, 6).Value = dp.B;

                var rr = wsDark.Range(dr, 1, dr, 6);
                rr.Style.Fill.BackgroundColor = XLColor.DarkRed;
                rr.Style.Font.FontColor = XLColor.White;
                rr.Style.Font.Bold = true;
                dr++;
            }

            wsDark.Columns(1, 6).AdjustToContents();
            wsDark.SheetView.FreezeRows(dh);
        }

        wb.SaveAs(path);
    }

    private readonly record struct DarkPixel(int PixelId, int X, int Y, int Deviation, byte A, byte B);

    private static string MakeSheetName(int frameNr)
    {
        // Excel sheet name limit is 31 chars; keep it short and deterministic.
        string name = $"FrameNr_{frameNr}";
        return name.Length <= 31 ? name : name[..31];
    }

    private static string EnsureXlsxExtension(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return path;
        return Path.ChangeExtension(path, ".xlsx");
    }

    private static void Bgr24ToBgr32TopDown(byte[] srcBgr24TopDown, byte[] dstBgr32TopDown, int w, int h)
    {
        int srcStride = w * 3;
        int dstStride = w * 4;
        for (int y = 0; y < h; y++)
        {
            int si = y * srcStride;
            int di = y * dstStride;
            for (int x = 0; x < w; x++)
            {
                dstBgr32TopDown[di++] = srcBgr24TopDown[si++]; // B
                dstBgr32TopDown[di++] = srcBgr24TopDown[si++]; // G
                dstBgr32TopDown[di++] = srcBgr24TopDown[si++]; // R
                dstBgr32TopDown[di++] = 0; // X
            }
        }
    }

    private readonly record struct FrameSet(byte[] AGray, byte[] BGray, byte[] DBgrTopDown);
}
