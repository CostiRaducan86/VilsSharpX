using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VilsSharpX;

/// <summary>
/// Utility methods for loading, cropping, and saving grayscale images.
/// </summary>
public static class ImageUtils
{
    /// <summary>
    /// Loads an image file (PGM, BMP, or PNG) and returns its data as Gray8.
    /// </summary>
    /// <param name="path">Path to the image file.</param>
    /// <returns>Tuple containing width, height, and grayscale pixel data.</returns>
    /// <exception cref="NotSupportedException">If the image format is not supported.</exception>
    public static (int width, int height, byte[] data) LoadImageAsGray8(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        
        if (ext == ".pgm")
        {
            var img = PgmLoader.Load(path);
            return (img.Width, img.Height, img.Data);
        }

        if (ext == ".bmp" || ext == ".png")
        {
            // Use WPF decoder so we don't depend on System.Drawing / GDI+.
            var uri = new Uri(path, UriKind.Absolute);
            var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource src = decoder.Frames[0];

            if (src.Format != PixelFormats.Gray8)
            {
                src = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
                src.Freeze();
            }

            int w = src.PixelWidth;
            int h = src.PixelHeight;
            int stride = w; // Gray8 => 1 byte/pixel
            var data = new byte[stride * h];
            src.CopyPixels(data, stride, 0);
            return (w, h, data);
        }

        throw new NotSupportedException($"Unsupported image format '{ext}'. Supported: .pgm, .bmp, .png");
    }

    /// <summary>
    /// Crops the top-left region of a grayscale image.
    /// </summary>
    /// <param name="src">Source image data (row-major, 1 byte per pixel).</param>
    /// <param name="srcW">Source width.</param>
    /// <param name="srcH">Source height.</param>
    /// <param name="cropW">Crop width.</param>
    /// <param name="cropH">Crop height.</param>
    /// <returns>Cropped image data.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If crop dimensions are invalid.</exception>
    /// <exception cref="ArgumentException">If source is smaller than crop region.</exception>
    public static byte[] CropTopLeftGray8(byte[] src, int srcW, int srcH, int cropW, int cropH)
    {
        if (cropW <= 0 || cropH <= 0) throw new ArgumentOutOfRangeException(nameof(cropW));
        if (srcW < cropW || srcH < cropH) throw new ArgumentException("Source smaller than crop.");

        var dst = new byte[cropW * cropH];
        for (int y = 0; y < cropH; y++)
        {
            Buffer.BlockCopy(src, y * srcW, dst, y * cropW, cropW);
        }
        return dst;
    }

    /// <summary>
    /// Saves a Gray8 image as a PNG file.
    /// </summary>
    /// <param name="path">Output file path.</param>
    /// <param name="grayTopDown">Grayscale pixel data (top-down, row-major).</param>
    /// <param name="w">Image width.</param>
    /// <param name="h">Image height.</param>
    public static void SaveGray8Png(string path, byte[] grayTopDown, int w, int h)
    {
        int stride = w;
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, grayTopDown, stride);
        src.Freeze();

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        enc.Save(fs);
    }

    /// <summary>
    /// Saves a BGR24 image as a PNG file.
    /// </summary>
    /// <param name="path">Output file path.</param>
    /// <param name="bgrTopDown">BGR24 pixel data (top-down, row-major, 3 bytes per pixel).</param>
    /// <param name="w">Image width.</param>
    /// <param name="h">Image height.</param>
    public static void SaveBgr24Png(string path, byte[] bgrTopDown, int w, int h)
    {
        int stride = w * 3;
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, bgrTopDown, stride);
        src.Freeze();

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        enc.Save(fs);
    }

    /// <summary>
    /// Creates a copy of a byte array.
    /// </summary>
    /// <param name="src">Source array.</param>
    /// <returns>A new array with the same contents.</returns>
    public static byte[] Copy(byte[] src)
    {
        var dst = new byte[src.Length];
        Buffer.BlockCopy(src, 0, dst, 0, src.Length);
        return dst;
    }

    /// <summary>
    /// Applies a brightness delta to all pixels in a grayscale image.
    /// </summary>
    /// <param name="src">Source image data.</param>
    /// <param name="delta">Value to add to each pixel (clamped to 0-255).</param>
    /// <returns>New array with adjusted pixel values.</returns>
    public static byte[] ApplyValueDelta(byte[] src, int delta)
    {
        if (delta == 0) return src;
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            int v = src[i] + delta;
            if (v < 0) v = 0;
            else if (v > 255) v = 255;
            dst[i] = (byte)v;
        }
        return dst;
    }

    /// <summary>
    /// Computes the absolute difference between two frames.
    /// </summary>
    /// <param name="a">First frame.</param>
    /// <param name="b">Second frame.</param>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <returns>New frame containing |a-b| for each pixel.</returns>
    public static Frame AbsDiff(Frame a, Frame b, int width, int height)
    {
        var outData = new byte[width * height];
        for (int i = 0; i < outData.Length; i++)
        {
            int v = a.Data[i] - b.Data[i];
            if (v < 0) v = -v;
            outData[i] = (byte)v;
        }
        return new Frame(width, height, outData, DateTime.UtcNow);
    }

    /// <summary>
    /// Applies exponential moving average smoothing.
    /// </summary>
    /// <param name="prev">Previous EMA value.</param>
    /// <param name="value">New sample value.</param>
    /// <param name="alpha">Smoothing factor (0-1). Higher = more responsive.</param>
    /// <returns>Updated EMA value.</returns>
    public static double ApplyEma(double prev, double value, double alpha)
    {
        if (value < 0) value = 0;
        if (alpha <= 0) return prev;
        if (alpha >= 1) return value;
        return (prev <= 0) ? value : (prev + alpha * (value - prev));
    }
}
