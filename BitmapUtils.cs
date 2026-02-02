using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoStreamPlayer
{
    /// <summary>
    /// Utilities for creating and updating WriteableBitmap instances.
    /// </summary>
    public static class BitmapUtils
    {
        public static WriteableBitmap MakeGray8(int w, int h) =>
            new WriteableBitmap(w, h, 96, 96, PixelFormats.Gray8, null);

        public static WriteableBitmap MakeBgr24(int w, int h) =>
            new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);

        /// <summary>
        /// Writes pixel data to a WriteableBitmap.
        /// Works for both Gray8 (stride=w) and Bgr24 (stride=w*3).
        /// </summary>
        public static void Blit(WriteableBitmap wb, byte[] src, int stride)
        {
            wb.Lock();
            try
            {
                wb.WritePixels(new Int32Rect(0, 0, wb.PixelWidth, wb.PixelHeight), src, stride, 0);
            }
            finally { wb.Unlock(); }
        }
    }
}
