using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VideoStreamPlayer;

/// <summary>
/// Renders pixel value overlays on zoomed panes.
/// </summary>
public sealed class OverlayRenderer
{
    public const double MinZoom = 8.0;
    public const int MaxLabels = 40000;
    public const double TextSizePx = 10.0;

    private readonly Typeface _typeface;
    private readonly SolidColorBrush _fgWhite;
    private readonly SolidColorBrush _fgBlack;

    public OverlayRenderer()
    {
        _typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _fgWhite = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
        _fgBlack = new SolidColorBrush(Color.FromArgb(230, 0, 0, 0));
        _fgWhite.Freeze();
        _fgBlack.Freeze();
    }

    /// <summary>
    /// Computes the step (skip factor) for overlay labels based on zoom level.
    /// </summary>
    public static int ComputeStep(double scale, double zoomScale, int width, int height)
    {
        double pixelOnScreen = scale * zoomScale;
        const double MinPixelForAll = 14.0;
        int step = (int)Math.Max(1.0, Math.Ceiling(MinPixelForAll / Math.Max(1e-6, pixelOnScreen)));
        while (((width + step - 1) / step) * ((height + step - 1) / step) > MaxLabels)
            step++;
        return step;
    }

    /// <summary>
    /// Checks if the zoom level is sufficient to show pixel labels (must fit in one pixel).
    /// </summary>
    public bool CanShowLabels(double pixelOnScreen, double pixelsPerDip, bool isDiffPane)
    {
        string sample = isDiffPane ? "+255" : "255";
        var sampleFt = new FormattedText(sample, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _typeface, TextSizePx, _fgWhite, pixelsPerDip);
        double sampleW = Math.Max(sampleFt.WidthIncludingTrailingWhitespace, 1.0);
        double sampleH = Math.Max(sampleFt.Height, 1.0);
        const double FitMargin = 1.15;
        return pixelOnScreen >= sampleW * FitMargin && pixelOnScreen >= sampleH * FitMargin;
    }

    /// <summary>
    /// Renders grayscale pixel value overlay for pane A or B.
    /// </summary>
    public void RenderGrayscaleOverlay(
        Canvas overlay,
        Image img,
        Frame frame,
        double zoomScale,
        GeneralTransform imgToOverlay,
        double pixelsPerDip)
    {
        double aw = img.ActualWidth;
        double ah = img.ActualHeight;
        double scale = Math.Min(aw / frame.Width, ah / frame.Height);
        double dw = frame.Width * scale;
        double dh = frame.Height * scale;
        double ox = (aw - dw) / 2.0;
        double oy = (ah - dh) / 2.0;
        double pixelOnScreen = scale * zoomScale;

        if (!CanShowLabels(pixelOnScreen, pixelsPerDip, isDiffPane: false))
        {
            ClearOverlay(overlay);
            return;
        }

        int step = 1;
        double fontSize = Math.Max(1.0, TextSizePx);

        var dg = new DrawingGroup();
        using (var dc = dg.Open())
        {
            var cacheW = new FormattedText[256];
            var cacheB = new FormattedText[256];
            var boundsW = new Rect[256];
            var boundsB = new Rect[256];
            var boundsWSet = new bool[256];
            var boundsBSet = new bool[256];

            FormattedText GetFt(byte v, bool white)
            {
                var arr = white ? cacheW : cacheB;
                var ft = arr[v];
                if (ft != null) return ft;
                ft = new FormattedText(v.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _typeface, fontSize, white ? _fgWhite : _fgBlack, pixelsPerDip);
                arr[v] = ft;
                return ft;
            }

            Rect GetBounds(byte v, bool white)
            {
                if (white)
                {
                    if (!boundsWSet[v])
                    {
                        boundsW[v] = GetFt(v, true).BuildGeometry(new Point(0, 0)).Bounds;
                        boundsWSet[v] = true;
                    }
                    return boundsW[v];
                }
                if (!boundsBSet[v])
                {
                    boundsB[v] = GetFt(v, false).BuildGeometry(new Point(0, 0)).Bounds;
                    boundsBSet[v] = true;
                }
                return boundsB[v];
            }

            int added = 0;
            for (int y = 0; y < frame.Height; y += step)
            {
                double imgCy = oy + (y + 0.5) * scale;
                for (int x = 0; x < frame.Width; x += step)
                {
                    double imgCx = ox + (x + 0.5) * scale;

                    Point p;
                    try { p = imgToOverlay.Transform(new Point(imgCx, imgCy)); }
                    catch { continue; }

                    byte v = frame.Data[y * frame.Stride + x];
                    bool white = v < 128;
                    var ft = GetFt(v, white);
                    var bounds = GetBounds(v, white);

                    double oxText = p.X - (bounds.X + bounds.Width * 0.5);
                    double oyText = p.Y - (bounds.Y + bounds.Height * 0.5);
                    dc.DrawText(ft, new Point(oxText, oyText));

                    if (++added >= MaxLabels) break;
                }
                if (added >= MaxLabels) break;
            }
        }
        dg.Freeze();

        ApplyDrawingToOverlay(overlay, dg, aw, ah);
    }

    /// <summary>
    /// Renders diff (B-A) overlay for pane D.
    /// </summary>
    public void RenderDiffOverlay(
        Canvas overlay,
        Image img,
        Frame frameA,
        Frame frameB,
        double zoomScale,
        GeneralTransform imgToOverlay,
        double pixelsPerDip,
        byte diffThreshold,
        bool zeroZeroIsWhite)
    {
        double aw = img.ActualWidth;
        double ah = img.ActualHeight;
        double scale = Math.Min(aw / frameA.Width, ah / frameA.Height);
        double dw = frameA.Width * scale;
        double dh = frameA.Height * scale;
        double ox = (aw - dw) / 2.0;
        double oy = (ah - dh) / 2.0;
        double pixelOnScreen = scale * zoomScale;

        if (!CanShowLabels(pixelOnScreen, pixelsPerDip, isDiffPane: true))
        {
            ClearOverlay(overlay);
            return;
        }

        int step = 1;
        double fontSize = Math.Max(1.0, TextSizePx);

        var dg = new DrawingGroup();
        using (var dc = dg.Open())
        {
            var cacheDiffW = new Dictionary<int, FormattedText>(512);
            var cacheDiffB = new Dictionary<int, FormattedText>(512);
            var boundsDiffW = new Dictionary<int, Rect>(512);
            var boundsDiffB = new Dictionary<int, Rect>(512);

            FormattedText GetDiffFt(int diff, bool white)
            {
                var dict = white ? cacheDiffW : cacheDiffB;
                if (dict.TryGetValue(diff, out var ft)) return ft;
                string text = diff.ToString("+0;-0;0", CultureInfo.InvariantCulture);
                ft = new FormattedText(text, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _typeface, fontSize, white ? _fgWhite : _fgBlack, pixelsPerDip);
                dict[diff] = ft;
                return ft;
            }

            Rect GetDiffBounds(int diff, bool white)
            {
                var dict = white ? boundsDiffW : boundsDiffB;
                if (!dict.TryGetValue(diff, out var b))
                {
                    b = GetDiffFt(diff, white).BuildGeometry(new Point(0, 0)).Bounds;
                    dict[diff] = b;
                }
                return b;
            }

            int added = 0;
            for (int y = 0; y < frameA.Height; y += step)
            {
                double imgCy = oy + (y + 0.5) * scale;
                for (int x = 0; x < frameA.Width; x += step)
                {
                    double imgCx = ox + (x + 0.5) * scale;

                    Point p;
                    try { p = imgToOverlay.Transform(new Point(imgCx, imgCy)); }
                    catch { continue; }

                    byte aPx = frameA.Data[y * frameA.Stride + x];
                    byte bPx = frameB.Data[y * frameB.Stride + x];
                    int diff = bPx - aPx;

                    DiffRenderer.ComparePixelToBgr(aPx, bPx, diffThreshold, zeroZeroIsWhite, out var bl, out var gg, out var rr);
                    double lum = (0.2126 * rr) + (0.7152 * gg) + (0.0722 * bl);
                    bool white = lum < 128.0;

                    var ft = GetDiffFt(diff, white);
                    var bounds = GetDiffBounds(diff, white);

                    double oxText = p.X - (bounds.X + bounds.Width * 0.5);
                    double oyText = p.Y - (bounds.Y + bounds.Height * 0.5);
                    dc.DrawText(ft, new Point(oxText, oyText));

                    if (++added >= MaxLabels) break;
                }
                if (added >= MaxLabels) break;
            }
        }
        dg.Freeze();

        ApplyDrawingToOverlay(overlay, dg, aw, ah);
    }

    /// <summary>
    /// Applies a DrawingGroup to a Canvas overlay.
    /// </summary>
    private static void ApplyDrawingToOverlay(Canvas overlay, DrawingGroup dg, double aw, double ah)
    {
        overlay.Visibility = Visibility.Visible;
        overlay.Children.Clear();
        overlay.Width = aw;
        overlay.Height = ah;

        var db = new DrawingBrush(dg)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            TileMode = TileMode.None,
            ViewboxUnits = BrushMappingMode.Absolute,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, aw, ah),
            Viewport = new Rect(0, 0, aw, ah)
        };
        db.Freeze();

        var rect = new Rectangle
        {
            Width = aw,
            Height = ah,
            Fill = db,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(rect, 0);
        Canvas.SetTop(rect, 0);
        overlay.Children.Add(rect);
    }

    /// <summary>
    /// Clears and hides the overlay.
    /// </summary>
    public static void ClearOverlay(Canvas overlay)
    {
        overlay.Children.Clear();
        overlay.Visibility = Visibility.Collapsed;
    }
}
