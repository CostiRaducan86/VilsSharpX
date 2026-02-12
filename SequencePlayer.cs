namespace VilsSharpX
{
    /// <summary>
    /// Manages sequence (A/B toggle) playback state.
    /// </summary>
    public class SequencePlayer
    {
        private readonly int _targetWidth;
        private readonly int _targetHeight;

        private byte[]? _seqA;
        private byte[]? _seqB;
        private int _seqIndex; // 0 => A, 1 => B
        private string? _seqPathA;
        private string? _seqPathB;

        public SequencePlayer(int targetWidth, int targetHeight)
        {
            _targetWidth = targetWidth;
            _targetHeight = targetHeight;
        }

        public bool IsLoaded => _seqA != null || _seqB != null;
        public bool HasAny => _seqA != null || _seqB != null;
        public int CurrentIndex => _seqIndex;
        public string? PathA => _seqPathA;
        public string? PathB => _seqPathB;

        public void LoadA(string path)
        {
            var img = ImageUtils.LoadImageAsGray8(path);
            if (img.width < _targetWidth || img.height < _targetHeight)
                throw new System.InvalidOperationException($"Expected at least {_targetWidth}x{_targetHeight}, got {img.width}x{img.height}.");

            _seqA = ImageUtils.CropTopLeftGray8(img.data, img.width, img.height, _targetWidth, _targetHeight);
            _seqPathA = path;
            _seqIndex = 0;
        }

        /// <summary>
        /// Load A with pre-cropped data.
        /// </summary>
        public void LoadA(string path, byte[] croppedData)
        {
            _seqA = croppedData;
            _seqPathA = path;
            _seqIndex = 0;
        }

        public void LoadB(string path)
        {
            var img = ImageUtils.LoadImageAsGray8(path);
            if (img.width < _targetWidth || img.height < _targetHeight)
                throw new System.InvalidOperationException($"Expected at least {_targetWidth}x{_targetHeight}, got {img.width}x{img.height}.");

            _seqB = ImageUtils.CropTopLeftGray8(img.data, img.width, img.height, _targetWidth, _targetHeight);
            _seqPathB = path;
            _seqIndex = 1;
        }

        /// <summary>
        /// Load B with pre-cropped data.
        /// </summary>
        public void LoadB(string path, byte[] croppedData)
        {
            _seqB = croppedData;
            _seqPathB = path;
            _seqIndex = 1;
        }

        public void Clear()
        {
            _seqA = null;
            _seqB = null;
            _seqPathA = null;
            _seqPathB = null;
            _seqIndex = 0;
        }

        /// <summary>
        /// Toggles between A and B.
        /// </summary>
        /// <returns>Status message for display, or null if nothing loaded.</returns>
        public string? Toggle()
        {
            if (_seqA == null && _seqB == null)
                return null;

            _seqIndex = 1 - _seqIndex;
            return BuildStatusMessage();
        }

        /// <summary>
        /// Gets the current sequence frame bytes.
        /// </summary>
        public byte[]? GetBytes()
        {
            return _seqIndex == 0 ? (_seqA ?? _seqB) : (_seqB ?? _seqA);
        }

        public string BuildStatusMessage()
        {
            string a = _seqPathA != null ? System.IO.Path.GetFileName(_seqPathA) : "<not set>";
            string b = _seqPathB != null ? System.IO.Path.GetFileName(_seqPathB) : "<not set>";
            string cur = _seqIndex == 0 ? "A" : "B";
            return $"Sequence mode (AVTP={a}, LVDS={b}), current={cur}. Use Prev/Next to toggle.";
        }
    }
}
