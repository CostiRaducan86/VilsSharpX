using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VilsSharpX
{
    /// <summary>
    /// Utilities for creating and updating WriteableBitmap instances.
    /// </summary>
    public static class BitmapUtils
    {
        // Last content written to each bitmap; identical frames are skipped to save GPU/RDP updates.
        private static readonly ConditionalWeakTable<WriteableBitmap, byte[]> s_lastContent = [];

        public static WriteableBitmap MakeGray8(int w, int h) =>
            new(w, h, 96, 96, PixelFormats.Gray8, null);

        public static WriteableBitmap MakeBgr24(int w, int h) =>
            new(w, h, 96, 96, PixelFormats.Bgr24, null);

        /// <summary>
        /// Writes pixel data to a WriteableBitmap, skipping the write if the content is unchanged.
        /// Works for both Gray8 (stride=w) and Bgr24 (stride=w*3). Must be called on the UI thread.
        /// </summary>
        public static void Blit(WriteableBitmap wb, byte[] src, int stride)
        {
            int len = Math.Min(src.Length, stride * wb.PixelHeight);
            var span = src.AsSpan(0, len);
            if (s_lastContent.TryGetValue(wb, out var last) && last.Length == len && span.SequenceEqual(last))
                return;

            wb.Lock();
            try
            {
                wb.WritePixels(new Int32Rect(0, 0, wb.PixelWidth, wb.PixelHeight), src, stride, 0);
            }
            finally { wb.Unlock(); }

            if (last == null || last.Length != len)
            {
                last = new byte[len];
                s_lastContent.AddOrUpdate(wb, last);
            }
            span.CopyTo(last);
        }
    }
}
