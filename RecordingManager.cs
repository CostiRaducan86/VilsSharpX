using System;
using System.Globalization;
using System.IO;

namespace VideoStreamPlayer
{
    /// <summary>
    /// Manages video recording and frame snapshot saving operations.
    /// </summary>
    public class RecordingManager
    {
        private readonly int _width;
        private readonly int _height;

        private AviTripletRecorder? _recorder;
        private bool _isRecording;
        private int _recordDropped;

        public bool IsRecording => _isRecording;
        public int DroppedFrames => _recordDropped;
        public AviTripletRecorder? Recorder => _recorder;

        public RecordingManager(int width, int height)
        {
            _width = width;
            _height = height;
        }

        /// <summary>
        /// Starts recording to AVI files.
        /// </summary>
        public (bool success, string? error, string? statusMessage) StartRecording(int fps, byte diffThreshold)
        {
            if (fps <= 0) fps = 30;

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string outDir = GetVideoRecordOutputDirectory();

            string pathA = MakeUniquePath(Path.Combine(outDir, $"{ts}_AVTP.avi"));
            string pathB = MakeUniquePath(Path.Combine(outDir, $"{ts}_LVDS.avi"));
            string pathD = MakeUniquePath(Path.Combine(outDir, $"{ts}_Compare.avi"));
            string pathXlsx = Path.ChangeExtension(pathD, ".xlsx");

            try
            {
                _recorder?.Dispose();
                _recorder = new AviTripletRecorder(pathA, pathB, pathD, _width, _height, fps, 
                    compareCsvPath: pathXlsx, compareDeadband: diffThreshold);
                _recordDropped = 0;
                _isRecording = true;
                return (true, null, $"Recording AVI to: {outDir}  ({ts}_AVTP/LVDS/Compare) @ {fps} fps");
            }
            catch (Exception ex)
            {
                try { _recorder?.Dispose(); } catch { }
                _recorder = null;
                _isRecording = false;
                return (false, ex.Message, null);
            }
        }

        /// <summary>
        /// Stops the current recording.
        /// </summary>
        public string StopRecording()
        {
            _isRecording = false;
            double actualFps = 0;
            if (_recorder != null)
            {
                try
                {
                    actualFps = _recorder.ActualFps;
                }
                catch { }
            }
            try { _recorder?.Dispose(); } catch { }

            // After Dispose, ActualFps is updated and AVI headers are patched.
            if (_recorder != null && actualFps <= 0)
            {
                try { actualFps = _recorder.ActualFps; } catch { }
            }
            _recorder = null;

            string fpsInfo = actualFps > 0 ? $" Actual fps: {actualFps:F1}" : "";
            return _recordDropped > 0
                ? $"Recording stopped. Dropped frames (queue full): {_recordDropped}.{fpsInfo}"
                : $"Recording stopped.{fpsInfo}";
        }

        /// <summary>
        /// Tries to enqueue a frame for recording. Returns false if queue is full.
        /// </summary>
        public bool TryEnqueueFrame(byte[] aData, byte[] bData, byte[] dBgr)
        {
            if (!_isRecording || _recorder == null) return false;

            if (!_recorder.TryEnqueue(aData, bData, dBgr))
            {
                _recordDropped++;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Gets the output directory for video recordings.
        /// </summary>
        public static string GetVideoRecordOutputDirectory()
        {
            string? root = FindRepoRootWithDocs(AppContext.BaseDirectory)
                           ?? FindRepoRootWithDocs(Directory.GetCurrentDirectory());

            string baseDir = root ?? Directory.GetCurrentDirectory();
            string outDir = Path.Combine(baseDir, "docs", "outputs", "videoRecords");
            Directory.CreateDirectory(outDir);
            return outDir;
        }

        /// <summary>
        /// Gets the output directory for frame snapshots.
        /// </summary>
        public static string GetFrameSnapshotsOutputDirectory()
        {
            string? root = FindRepoRootWithDocs(AppContext.BaseDirectory)
                           ?? FindRepoRootWithDocs(Directory.GetCurrentDirectory());

            string baseDir = root ?? Directory.GetCurrentDirectory();
            string outDir = Path.Combine(baseDir, "docs", "outputs", "frameSnapshots");
            Directory.CreateDirectory(outDir);
            return outDir;
        }

        /// <summary>
        /// Finds the repository root containing a 'docs' folder.
        /// </summary>
        public static string? FindRepoRootWithDocs(string startPath)
        {
            try
            {
                var dir = new DirectoryInfo(startPath);
                if (!dir.Exists)
                    dir = new DirectoryInfo(Path.GetDirectoryName(startPath) ?? startPath);

                for (int i = 0; i < 8 && dir != null; i++)
                {
                    var docs = new DirectoryInfo(Path.Combine(dir.FullName, "docs"));
                    if (docs.Exists)
                        return dir.FullName;

                    dir = dir.Parent;
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }

        /// <summary>
        /// Creates a unique file path by appending a counter if the file already exists.
        /// </summary>
        public static string MakeUniquePath(string desiredPath)
        {
            if (!File.Exists(desiredPath))
                return desiredPath;

            string dir = Path.GetDirectoryName(desiredPath) ?? "";
            string name = Path.GetFileNameWithoutExtension(desiredPath);
            string ext = Path.GetExtension(desiredPath);

            for (int i = 1; i <= 999; i++)
            {
                string p = Path.Combine(dir, $"{name}_{i:000}{ext}");
                if (!File.Exists(p))
                    return p;
            }

            return desiredPath;
        }

        /// <summary>
        /// Creates unique paths for a complete save set (A, B, D images + XLSX report).
        /// </summary>
        public static (string APath, string BPath, string DPath, string XlsxPath) MakeUniqueSaveSetPaths(string outDir, string ts)
        {
            for (int i = 0; i <= 999; i++)
            {
                string suffix = i == 0 ? string.Empty : $"_{i:000}";
                string stem = ts + suffix;

                string a = Path.Combine(outDir, $"{stem}_AVTP.png");
                string b = Path.Combine(outDir, $"{stem}_LVDS.png");
                string d = Path.Combine(outDir, $"{stem}_Compare.png");
                string x = Path.Combine(outDir, $"{stem}_Compare.xlsx");

                if (!File.Exists(a) && !File.Exists(b) && !File.Exists(d) && !File.Exists(x))
                    return (a, b, d, x);
            }

            // Fallback
            return (
                Path.Combine(outDir, $"{ts}_AVTP.png"),
                Path.Combine(outDir, $"{ts}_LVDS.png"),
                Path.Combine(outDir, $"{ts}_Compare.png"),
                Path.Combine(outDir, $"{ts}_Compare.xlsx"));
        }
    }
}
