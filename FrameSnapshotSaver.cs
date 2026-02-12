using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Controls;

namespace VilsSharpX
{
    /// <summary>
    /// Handles saving frame snapshots (A/B/D PNGs + XLSX report) with UI feedback.
    /// </summary>
    public sealed class FrameSnapshotSaver
    {
        private readonly int _width;
        private readonly int _height;
        private CancellationTokenSource? _feedbackCts;

        public FrameSnapshotSaver(int width, int height)
        {
            _width = width;
            _height = height;
        }

        /// <summary>
        /// Saves A, B, D frames as PNG and generates an XLSX compare report.
        /// </summary>
        public async Task SaveAsync(
            Frame a,
            Frame b,
            byte diffThreshold,
            bool zeroZeroIsWhite,
            int frameNumber,
            TextBlock? lblFeedback,
            TextBlock? lblStatus,
            Action<string, Brush>? showFeedback,
            Action? hideFeedback)
        {
            // Cancel any previous pending feedback
            try { _feedbackCts?.Cancel(); } catch { }
            try { _feedbackCts?.Dispose(); } catch { }
            _feedbackCts = new CancellationTokenSource();
            var uiCt = _feedbackCts.Token;

            showFeedback?.Invoke("Saving current frame...", Brushes.DimGray);

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string outDir = RecordingManager.GetFrameSnapshotsOutputDirectory();
            var paths = RecordingManager.MakeUniqueSaveSetPaths(outDir, ts);

            try
            {
                if (lblStatus != null)
                    lblStatus.Text = $"Saving report + images… ({Path.GetFileName(paths.XlsxPath)})";

                var aBytes = (byte[])a.Data.Clone();
                var bBytes = (byte[])b.Data.Clone();
                byte thr = diffThreshold;

                // Create the D snapshot exactly like the UI render (B−A mapping to BGR24).
                var dBgr = new byte[_width * _height * 3];
                DiffRenderer.RenderCompareToBgr(dBgr, aBytes, bBytes, _width, _height, thr, zeroZeroIsWhite,
                    out _, out _, out _,
                    out _, out _, out _,
                    out _);

                await Task.Run(() =>
                {
                    AviTripletRecorder.SaveSingleFrameCompareXlsx(paths.XlsxPath, frameNumber, aBytes, bBytes, _width, _height, deviationThreshold: thr);

                    // Save 1:1 snapshots for all panes.
                    ImageUtils.SaveGray8Png(paths.APath, aBytes, _width, _height);
                    ImageUtils.SaveGray8Png(paths.BPath, bBytes, _width, _height);
                    ImageUtils.SaveBgr24Png(paths.DPath, dBgr, _width, _height);
                });

                // Brief delay before showing success
                try { await Task.Delay(1200, uiCt); } catch { }

                if (!uiCt.IsCancellationRequested)
                    showFeedback?.Invoke("Current frame saved!", Brushes.ForestGreen);

                // Clear after a few seconds
                try { await Task.Delay(2500, uiCt); } catch { }
                if (!uiCt.IsCancellationRequested)
                    hideFeedback?.Invoke();

                if (lblStatus != null)
                    lblStatus.Text = $"Saved: {paths.XlsxPath} (+ A/B/D PNG)";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to save XLSX: {ex.Message}", "Save report error");

                if (!uiCt.IsCancellationRequested)
                    showFeedback?.Invoke("Save failed.", Brushes.IndianRed);

                try { await Task.Delay(3000, uiCt); } catch { }
                if (!uiCt.IsCancellationRequested)
                    hideFeedback?.Invoke();
            }
        }

        public void Dispose()
        {
            try { _feedbackCts?.Cancel(); } catch { }
            try { _feedbackCts?.Dispose(); } catch { }
        }
    }
}
