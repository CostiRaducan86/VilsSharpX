using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoStreamPlayer;

/// <summary>
/// Provides pixel coordinate mapping and info display for image panes.
/// </summary>
public sealed class PixelInspector
{
    private readonly int _width;
    private readonly int _height;

    public PixelInspector(int width, int height)
    {
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Tries to get the pixel X/Y coordinates from a mouse event.
    /// Uses visual transforms so hover mapping matches overlay mapping exactly.
    /// </summary>
    public bool TryGetPixelXY(MouseEventArgs e, Frame frame, Image img, Canvas overlay, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (frame == null || img == null || overlay == null)
            return false;

        Point pOvr = e.GetPosition(overlay);

        GeneralTransform ovrToImg;
        try { ovrToImg = overlay.TransformToVisual(img); }
        catch { return false; }

        Point pImg;
        try { pImg = ovrToImg.Transform(pOvr); }
        catch { return false; }

        double lx = pImg.X;
        double ly = pImg.Y;

        double aw = img.ActualWidth;
        double ah = img.ActualHeight;
        if (aw <= 1 || ah <= 1) return false;

        // Stretch=Uniform => image may be letterboxed
        double scale = Math.Min(aw / frame.Width, ah / frame.Height);
        double dw = frame.Width * scale;
        double dh = frame.Height * scale;
        double ox = (aw - dw) / 2.0;
        double oy = (ah - dh) / 2.0;

        double ix = lx - ox;
        double iy = ly - oy;
        if (ix < 0 || iy < 0 || ix >= dw || iy >= dh)
            return false;

        x = (int)(ix / scale);
        y = (int)(iy / scale);
        if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
            return false;

        return true;
    }

    /// <summary>
    /// Formats pixel info for grayscale panes (A or B).
    /// </summary>
    public static string FormatGrayscaleInfo(int x, int y, byte value, int width)
    {
        int pixelId = (y * width) + x + 1;
        return $"x={x} y={y} v={value} pixel_ID={pixelId}";
    }

    /// <summary>
    /// Formats pixel info for diff pane (D).
    /// </summary>
    public static string FormatDiffInfo(int x, int y, byte valueA, byte valueB, int width)
    {
        int diff = valueB - valueA;
        int absDiff = diff < 0 ? -diff : diff;
        int pixelId = (y * width) + x + 1;
        return $"x={x} y={y} A={valueA} B={valueB} diff(B−A)={diff} |diff|={absDiff} pixel_ID={pixelId}";
    }

    /// <summary>
    /// Shows pixel info on a TextBlock for a grayscale frame.
    /// </summary>
    public void ShowGrayscaleInfo(MouseEventArgs e, Frame? frame, Image img, Canvas overlay, TextBlock label)
    {
        if (frame == null)
        {
            label.Text = "";
            return;
        }

        if (!TryGetPixelXY(e, frame, img, overlay, out int x, out int y))
        {
            label.Text = "";
            return;
        }

        byte v = frame.Data[y * frame.Stride + x];
        label.Text = FormatGrayscaleInfo(x, y, v, frame.Width);
    }

    /// <summary>
    /// Shows pixel info on a TextBlock for the diff pane.
    /// </summary>
    public void ShowDiffInfo(MouseEventArgs e, Frame? frameA, Frame? frameB, Image img, Canvas overlay, TextBlock label)
    {
        var refFrame = frameA ?? frameB;
        if (refFrame == null)
        {
            label.Text = "";
            return;
        }

        if (!TryGetPixelXY(e, refFrame, img, overlay, out int x, out int y))
        {
            label.Text = "";
            return;
        }

        int idx = (y * refFrame.Stride) + x;
        byte av = (frameA != null && idx < frameA.Data.Length) ? frameA.Data[idx] : (byte)0;
        byte bv = (frameB != null && idx < frameB.Data.Length) ? frameB.Data[idx] : (byte)0;
        label.Text = FormatDiffInfo(x, y, av, bv, refFrame.Width);
    }
}
