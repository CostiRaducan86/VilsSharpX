using System;

namespace VilsSharpX
{
    /// <summary>
    /// Manages AVI file playback state and frame retrieval.
    /// </summary>
    public class AviSourcePlayer : IDisposable
    {
        private readonly int _targetWidth;
        private readonly int _targetHeight;
        private readonly double _fpsEstimationWindowSec;
        private readonly double _fpsEmaAlpha;

        private AviUncompressedVideoReader? _avi;
        private string? _aviPath;
        private int _aviIndex;
        private DateTime _aviNextSwitchUtc;
        private bool _aviLoopEnabled = false;
        private byte[]? _aviCurrentFrame;

        // AVI "source fps" estimation
        private byte[]? _aviPrevFrameForFps;
        private DateTime _aviFpsWindowStartUtc;
        private int _aviChangesInWindow;
        private double _aviSourceFps;
        private double _aviSourceFpsEma;

        public AviSourcePlayer(int targetWidth, int targetHeight, double fpsEstimationWindowSec = 0.25, double fpsEmaAlpha = 0.30)
        {
            _targetWidth = targetWidth;
            _targetHeight = targetHeight;
            _fpsEstimationWindowSec = fpsEstimationWindowSec;
            _fpsEmaAlpha = fpsEmaAlpha;
        }

        public bool IsLoaded => _avi != null;
        public string? Path => _aviPath;
        public int CurrentIndex => _aviIndex;
        public int FrameCount => _avi?.FrameCount ?? 0;
        public double FrameDurationMs => _avi?.FrameDurationMs ?? 0;
        public bool LoopEnabled { get => _aviLoopEnabled; set => _aviLoopEnabled = value; }
        public double SourceFpsEma => _aviSourceFpsEma;
        public byte[]? CurrentFrame => _aviCurrentFrame;

        /// <summary>
        /// Returns true when the AVI has reached its last frame and looping is disabled.
        /// </summary>
        public bool IsAtEnd => !_aviLoopEnabled && _avi != null && _avi.FrameCount > 0 && _aviIndex >= _avi.FrameCount - 1;

        public void Load(string path)
        {
            Close();
            _avi = AviUncompressedVideoReader.Open(path);
            _aviPath = path;
            _aviIndex = 0;
            _aviCurrentFrame = _avi.ReadFrameAsGray8TopDown(_aviIndex, _targetWidth, _targetHeight);
            _aviNextSwitchUtc = DateTime.UtcNow.AddMilliseconds(_avi.FrameDurationMs);

            _aviPrevFrameForFps = _aviCurrentFrame;
            _aviFpsWindowStartUtc = DateTime.UtcNow;
            _aviChangesInWindow = 0;
            _aviSourceFps = 0.0;
            _aviSourceFpsEma = 0.0;
        }

        public void Close()
        {
            try { _avi?.Dispose(); } catch { }
            _avi = null;
            _aviPath = null;
            _aviIndex = 0;
            _aviCurrentFrame = null;
            _aviPrevFrameForFps = null;
            _aviFpsWindowStartUtc = DateTime.MinValue;
            _aviChangesInWindow = 0;
            _aviSourceFps = 0.0;
            _aviSourceFpsEma = 0.0;
        }

        public void Reset()
        {
            if (_avi == null) return;
            _aviIndex = 0;
            _aviCurrentFrame = _avi.ReadFrameAsGray8TopDown(_aviIndex, _targetWidth, _targetHeight);
            _aviNextSwitchUtc = DateTime.UtcNow.AddMilliseconds(_avi.FrameDurationMs);
        }

        /// <summary>
        /// Steps to next/previous frame manually.
        /// </summary>
        /// <returns>Status message for display.</returns>
        public string Step(int dir)
        {
            if (_avi == null)
                return "AVI: no file loaded.";

            int n = _avi.FrameCount;
            if (n <= 0)
                return "AVI: no frames.";

            int next = _aviIndex + (dir < 0 ? -1 : 1);
            if (_aviLoopEnabled)
            {
                next %= n;
                if (next < 0) next += n;
            }
            else
            {
                if (next < 0) next = 0;
                if (next >= n) next = n - 1;
            }

            _aviIndex = next;
            _aviCurrentFrame = _avi.ReadFrameAsGray8TopDown(_aviIndex, _targetWidth, _targetHeight);
            _aviNextSwitchUtc = DateTime.UtcNow.AddMilliseconds(_avi.FrameDurationMs);

            // Manual stepping should not pollute the rate estimate; reset the window.
            _aviPrevFrameForFps = _aviCurrentFrame;
            _aviFpsWindowStartUtc = DateTime.UtcNow;
            _aviChangesInWindow = 0;
            _aviSourceFps = 0.0;
            _aviSourceFpsEma = 0.0;

            string name = _aviPath != null ? System.IO.Path.GetFileName(_aviPath) : "<avi>";
            return $"AVI '{name}': frame {_aviIndex + 1}/{n} (loop={(_aviLoopEnabled ? "ON" : "OFF")}).";
        }

        /// <summary>
        /// Gets the current frame bytes, advancing automatically if needed based on timing.
        /// </summary>
        public byte[]? GetBytesAndUpdateIfNeeded(DateTime nowUtc, bool isPaused)
        {
            if (_avi == null) return null;

            // If paused, just return current frame without advancing.
            if (isPaused)
                return _aviCurrentFrame;

            if (_aviCurrentFrame == null)
            {
                _aviIndex = 0;
                _aviCurrentFrame = _avi.ReadFrameAsGray8TopDown(_aviIndex, _targetWidth, _targetHeight);
                _aviNextSwitchUtc = nowUtc.AddMilliseconds(_avi.FrameDurationMs);

                _aviPrevFrameForFps = _aviCurrentFrame;
                _aviFpsWindowStartUtc = nowUtc;
                _aviChangesInWindow = 0;
                _aviSourceFps = 0.0;
                return _aviCurrentFrame;
            }

            if (nowUtc < _aviNextSwitchUtc)
                return _aviCurrentFrame;

            int n = _avi.FrameCount;
            if (n <= 0) return _aviCurrentFrame;

            // Advance based on AVI timing, independent of generator fps.
            while (nowUtc >= _aviNextSwitchUtc)
            {
                if (!_aviLoopEnabled && _aviIndex >= n - 1)
                {
                    _aviNextSwitchUtc = DateTime.MaxValue;
                    break;
                }

                _aviIndex = (_aviIndex + 1) % n;
                var nextFrame = _avi.ReadFrameAsGray8TopDown(_aviIndex, _targetWidth, _targetHeight);
                UpdateSourceFps(nextFrame, nowUtc);
                _aviCurrentFrame = nextFrame;
                _aviNextSwitchUtc = _aviNextSwitchUtc.AddMilliseconds(_avi.FrameDurationMs);

                // Prevent runaway drift if we were paused/blocked.
                if ((_aviNextSwitchUtc - nowUtc).TotalMilliseconds < -5000)
                    _aviNextSwitchUtc = nowUtc.AddMilliseconds(_avi.FrameDurationMs);
            }

            return _aviCurrentFrame;
        }

        private void UpdateSourceFps(byte[] nextFrame, DateTime nowUtc)
        {
            if (_aviFpsWindowStartUtc == DateTime.MinValue)
                _aviFpsWindowStartUtc = nowUtc;

            var prev = _aviPrevFrameForFps;
            if (prev != null && prev.Length == nextFrame.Length)
            {
                if (!nextFrame.AsSpan().SequenceEqual(prev))
                    _aviChangesInWindow++;
            }

            _aviPrevFrameForFps = nextFrame;

            double sec = (nowUtc - _aviFpsWindowStartUtc).TotalSeconds;
            if (sec >= _fpsEstimationWindowSec)
            {
                _aviSourceFps = _aviChangesInWindow / Math.Max(1e-6, sec);
                _aviSourceFpsEma = ImageUtils.ApplyEma(_aviSourceFpsEma, _aviSourceFps, _fpsEmaAlpha);
                _aviFpsWindowStartUtc = nowUtc;
                _aviChangesInWindow = 0;
            }
        }

        public string BuildStatusMessage()
        {
            if (_avi == null) return "AVI: no file loaded.";
            string name = _aviPath != null ? System.IO.Path.GetFileName(_aviPath) : "<avi>";
            return $"AVI loaded: '{name}' ({_avi.Width}x{_avi.Height}, {_avi.BitsPerPixel}bpp, frames={_avi.FrameCount}, frameMs={_avi.FrameDurationMs:F1}). Press Start to play; Prev/Next steps frames.";
        }

        public void Dispose()
        {
            Close();
        }
    }
}
