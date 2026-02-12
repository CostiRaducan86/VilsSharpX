using System;
using System.Collections.Generic;

namespace VilsSharpX
{
    /// <summary>
    /// Manages scene playback state and frame retrieval.
    /// </summary>
    public class ScenePlayer
    {
        private readonly int _targetWidth;
        private readonly int _targetHeight;
        private readonly object _lock = new();

        private List<SceneItem>? _items;
        private string? _scenePath;
        private int _sceneIndex;
        private DateTime _sceneNextSwitchUtc;
        private bool _sceneLoopEnabled = true;

        public ScenePlayer(int targetWidth, int targetHeight)
        {
            _targetWidth = targetWidth;
            _targetHeight = targetHeight;
        }

        public bool IsLoaded => _items != null && _items.Count > 0;
        public string? Path => _scenePath;
        public int CurrentIndex { get { lock (_lock) return _sceneIndex; } }
        public int ItemCount => _items?.Count ?? 0;
        public bool LoopEnabled { get => _sceneLoopEnabled; set => _sceneLoopEnabled = value; }
        public List<SceneItem>? Items => _items;

        public void Load(string scenePath)
        {
            var (items, simpleLoop) = SceneLoader.Load(scenePath, _targetWidth, _targetHeight);

            lock (_lock)
            {
                _items = items;
                _scenePath = scenePath;
                _sceneLoopEnabled = simpleLoop;
                _sceneIndex = 0;
                _sceneNextSwitchUtc = DateTime.UtcNow.AddMilliseconds(items[0].DelayMs);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _items = null;
                _scenePath = null;
                _sceneIndex = 0;
            }
        }

        /// <summary>
        /// Steps to next/previous scene item manually.
        /// </summary>
        /// <returns>Status message for display.</returns>
        public string Step(int dir)
        {
            var items = _items;
            if (items == null || items.Count == 0)
                return "Scene: load a .scene first.";

            lock (_lock)
            {
                int n = items.Count;
                _sceneIndex = (_sceneIndex + (dir < 0 ? -1 : 1)) % n;
                if (_sceneIndex < 0) _sceneIndex += n;
                _sceneNextSwitchUtc = DateTime.UtcNow.AddMilliseconds(items[_sceneIndex].DelayMs);
            }

            return BuildStatusMessage();
        }

        /// <summary>
        /// Gets the current scene frame bytes, advancing automatically if needed based on timing.
        /// </summary>
        /// <param name="nowUtc">Current UTC time for timing calculations.</param>
        /// <param name="isPaused">If true, just return current frame without advancing.</param>
        public byte[]? GetBytesAndUpdateIfNeeded(DateTime nowUtc, bool isPaused)
        {
            var items = _items;
            if (items == null || items.Count == 0) return null;

            lock (_lock)
            {
                // When paused, just return current frame.
                if (isPaused)
                    return items[_sceneIndex].Data;

                if (items.Count > 1)
                {
                    while (nowUtc >= _sceneNextSwitchUtc)
                    {
                        if (!_sceneLoopEnabled && _sceneIndex >= items.Count - 1)
                        {
                            // Non-looping: hold last frame.
                            _sceneNextSwitchUtc = DateTime.MaxValue;
                            break;
                        }

                        _sceneIndex = (_sceneIndex + 1) % items.Count;
                        _sceneNextSwitchUtc = _sceneNextSwitchUtc.AddMilliseconds(items[_sceneIndex].DelayMs);

                        // If delays are small and we were paused or lagging, ensure we don't drift forever.
                        if ((_sceneNextSwitchUtc - nowUtc).TotalMilliseconds < -5000)
                            _sceneNextSwitchUtc = nowUtc.AddMilliseconds(items[_sceneIndex].DelayMs);
                    }
                }
                return items[_sceneIndex].Data;
            }
        }

        public string BuildStatusMessage()
        {
            var items = _items;
            if (items == null || items.Count == 0) return "Scene mode.";

            string curName;
            int delay;
            bool loop;
            lock (_lock)
            {
                curName = System.IO.Path.GetFileName(items[_sceneIndex].Path);
                delay = items[_sceneIndex].DelayMs;
                loop = _sceneLoopEnabled;
            }
            string sceneName = _scenePath != null ? System.IO.Path.GetFileName(_scenePath) : "<scene>";
            string loopText = loop ? "loop=ON" : "loop=OFF";
            return $"Scene '{sceneName}' loaded ({items.Count} items, {loopText}). Current={curName}, delay={delay}ms. Press Start to play; Prev/Next steps manually.";
        }
    }
}
