using System;

namespace VideoStreamPlayer;

/// <summary>
/// Renders difference (comparison) between two grayscale frames to a color-coded BGR24 image.
/// </summary>
public static class DiffRenderer
{
    /// <summary>
    /// Visual quantization step for making small differences more visible.
    /// </summary>
    private const int VisualStep = 12;

    /// <summary>
    /// Maximum visual range for color scaling.
    /// </summary>
    private const int VisualMax = 128;

    /// <summary>
    /// Renders the comparison between two grayscale images to a BGR24 buffer.
    /// Green = within deadband, Yellow/Red = B > A, Turquoise/Blue/White = B &lt; A, Magenta = dark pixel.
    /// </summary>
    /// <param name="dstBgr">Destination BGR24 buffer (must be w*h*3 bytes).</param>
    /// <param name="aGray">Source A grayscale data.</param>
    /// <param name="bGray">Source B grayscale data (ECU output).</param>
    /// <param name="w">Image width.</param>
    /// <param name="h">Image height.</param>
    /// <param name="deadband">Threshold below which differences are considered equal (green).</param>
    /// <param name="zeroZeroIsWhite">If true, render A=0,B=0 as white instead of green.</param>
    /// <param name="minDiff">Output: minimum B-A difference.</param>
    /// <param name="maxDiff">Output: maximum B-A difference.</param>
    /// <param name="meanDiff">Output: mean B-A difference.</param>
    /// <param name="maxAbsDiff">Output: maximum absolute difference.</param>
    /// <param name="meanAbsDiff">Output: mean absolute difference.</param>
    /// <param name="aboveDeadband">Output: count of pixels with |diff| > deadband.</param>
    /// <param name="totalDarkPixels">Output: count of dark pixels (A>0, B=0).</param>
    public static void RenderCompareToBgr(byte[] dstBgr, byte[] aGray, byte[] bGray, int w, int h, byte deadband,
        bool zeroZeroIsWhite,
        out int minDiff, out int maxDiff, out double meanDiff,
        out int maxAbsDiff, out double meanAbsDiff, out int aboveDeadband,
        out int totalDarkPixels)
    {
        long sumAbs = 0;
        long sumDiff = 0;
        int maxAbs = 0;
        int above = 0;
        int dark = 0;
        int minD = int.MaxValue;
        int maxD = int.MinValue;

        int p = 0;
        int n = w * h;
        for (int i = 0; i < n; i++)
        {
            byte a = aGray[i];
            byte b = bGray[i];
            // Deviation is ECU output minus input: B - A
            int diff = b - a;
            int ad = diff < 0 ? -diff : diff;
            sumAbs += ad;
            sumDiff += diff;
            if (diff < minD) minD = diff;
            if (diff > maxD) maxD = diff;
            if (ad > maxAbs) maxAbs = ad;
            if (ad > deadband) above++;
            if (a > 0 && b == 0) dark++;

            ComparePixelToBgr(a, b, deadband, zeroZeroIsWhite, out var bl, out var gg, out var rr);
            dstBgr[p++] = bl;
            dstBgr[p++] = gg;
            dstBgr[p++] = rr;
        }

        if (n <= 0)
        {
            minDiff = 0;
            maxDiff = 0;
            meanDiff = 0.0;
            maxAbsDiff = 0;
            meanAbsDiff = 0.0;
            aboveDeadband = 0;
            totalDarkPixels = 0;
            return;
        }

        minDiff = minD == int.MaxValue ? 0 : minD;
        maxDiff = maxD == int.MinValue ? 0 : maxD;
        meanDiff = (double)sumDiff / n;
        maxAbsDiff = maxAbs;
        meanAbsDiff = (double)sumAbs / n;
        aboveDeadband = above;
        totalDarkPixels = dark;
    }

    /// <summary>
    /// Computes the BGR color for a single comparison pixel.
    /// </summary>
    /// <param name="a">A (input) pixel value.</param>
    /// <param name="b">B (ECU output) pixel value.</param>
    /// <param name="deadband">Threshold for equality.</param>
    /// <param name="zeroZeroIsWhite">Render A=0,B=0 as white.</param>
    /// <param name="bl">Output blue component.</param>
    /// <param name="g">Output green component.</param>
    /// <param name="r">Output red component.</param>
    public static void ComparePixelToBgr(byte a, byte b, byte deadband, bool zeroZeroIsWhite, out byte bl, out byte g, out byte r)
    {
        // Deviation is ECU output minus input: B - A
        int diff = b - a;
        int ad = diff < 0 ? -diff : diff;

        // Optional special case: black A (0) == black B (0) -> white D (255)
        if (zeroZeroIsWhite && a == 0 && b == 0)
        {
            bl = 255;
            g = 255;
            r = 255;
            return;
        }

        // Dark pixel detection: A has signal, but B is forced to 0.
        // Make it visually obvious regardless of threshold.
        if (a > 0 && b == 0)
        {
            bl = 255;
            g = 0;
            r = 255;
            return;
        }

        // Deadband equality -> green
        if (ad <= deadband)
        {
            bl = 0;
            g = 255;
            r = 0;
            return;
        }

        // Make small differences more visible by quantizing the magnitude used for coloring.
        int adVis = ((ad + (VisualStep / 2)) / VisualStep) * VisualStep;
        if (adVis > 255) adVis = 255;
        double t = Math.Min(1.0, adVis / (double)VisualMax); // 0..1

        if (diff > 0)
        {
            // B > A: green -> yellow -> red
            // segment1: t 0..0.5 => r 0->255, g=255
            // segment2: t 0.5..1 => r=255, g 255->0
            if (t <= 0.5)
            {
                r = (byte)Math.Round((t / 0.5) * 255.0);
                g = 255;
            }
            else
            {
                r = 255;
                g = (byte)Math.Round((1.0 - (t - 0.5) / 0.5) * 255.0);
            }
            bl = 0;
            return;
        }

        // B < A: green -> turquoise -> blue -> white
        if (t <= 1.0 / 3.0)
        {
            // green -> turquoise: b 0->255, g=255
            double u = t / (1.0 / 3.0);
            r = 0;
            g = 255;
            bl = (byte)Math.Round(u * 255.0);
        }
        else if (t <= 2.0 / 3.0)
        {
            // turquoise -> blue: g 255->0, b=255
            double u = (t - 1.0 / 3.0) / (1.0 / 3.0);
            r = 0;
            g = (byte)Math.Round((1.0 - u) * 255.0);
            bl = 255;
        }
        else
        {
            // blue -> white: r 0->255, g 0->255, b=255
            double u = (t - 2.0 / 3.0) / (1.0 / 3.0);
            r = (byte)Math.Round(u * 255.0);
            g = (byte)Math.Round(u * 255.0);
            bl = 255;
        }
    }
}
