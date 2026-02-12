using System;
using System.Diagnostics;
using System.Threading;

namespace VilsSharpX
{
    /// <summary>
    /// Manages playback state (running, paused, stopped) and runtime statistics.
    /// </summary>
    public class PlaybackStateManager
    {
        private readonly double _fpsEstimationWindowSec;
        private readonly double _fpsEmaAlpha;

        private CancellationTokenSource? _cts;
        private readonly ManualResetEventSlim _pauseGate = new(true);

        private volatile bool _isRunning;
        private volatile bool _isPaused;
        private int _targetFps;
        private string? _runningStatusText;

        // Stats counters
        private int _countA;
        private int _countB;
        private int _countD;
        private int _countAvtpIn;
        private int _countAvtpDropped;
        private int _countAvtpIncomplete;
        private int _countAvtpSeqGapFrames;
        private int _sumAvtpSeqGaps;
        private int _countLateFramesSkipped;

        // FPS estimation
        private Stopwatch _statSw = Stopwatch.StartNew();
        private double _avtpInFpsEma;
        private double _bFpsEma;
        private bool _wasWaitingForSignal;

        public PlaybackStateManager(double fpsEstimationWindowSec, double fpsEmaAlpha)
        {
            _fpsEstimationWindowSec = fpsEstimationWindowSec;
            _fpsEmaAlpha = fpsEmaAlpha;
        }

        // Properties
        public bool IsRunning => _isRunning;
        public bool IsPaused => _isPaused;
        public int TargetFps => _targetFps;
        public string? RunningStatusText { get => _runningStatusText; set => _runningStatusText = value; }
        public CancellationTokenSource? Cts => _cts;
        public ManualResetEventSlim PauseGate => _pauseGate;

        // Stats properties
        public int CountA => _countA;
        public int CountB => _countB;
        public int CountD => _countD;
        public int CountAvtpIn => _countAvtpIn;
        public int CountAvtpDropped => _countAvtpDropped;
        public int CountAvtpIncomplete => _countAvtpIncomplete;
        public int CountAvtpSeqGapFrames => _countAvtpSeqGapFrames;
        public int SumAvtpSeqGaps => _sumAvtpSeqGaps;
        public int CountLateFramesSkipped => Volatile.Read(ref _countLateFramesSkipped);
        public double AvtpInFpsEma => _avtpInFpsEma;
        public double BFpsEma => _bFpsEma;
        public bool WasWaitingForSignal { get => _wasWaitingForSignal; set => _wasWaitingForSignal = value; }
        public Stopwatch StatStopwatch => _statSw;

        /// <summary>
        /// Start playback with the specified target FPS.
        /// </summary>
        public CancellationToken Start(int fps)
        {
            _targetFps = fps;
            _cts = new CancellationTokenSource();
            _isRunning = true;
            _isPaused = false;
            _pauseGate.Set();
            ResetStats();
            return _cts.Token;
        }

        /// <summary>
        /// Resets all runtime statistics.
        /// </summary>
        public void ResetStats()
        {
            _countA = _countB = _countD = 0;
            _countAvtpIn = 0;
            _countAvtpDropped = 0;
            _countAvtpIncomplete = 0;
            _countAvtpSeqGapFrames = 0;
            _sumAvtpSeqGaps = 0;
            _countLateFramesSkipped = 0;
            _statSw.Restart();
            _avtpInFpsEma = 0.0;
            _bFpsEma = 0.0;
            _wasWaitingForSignal = false;
        }

        /// <summary>
        /// Pause playback (blocks the pause gate).
        /// </summary>
        public void Pause()
        {
            _isPaused = true;
            _pauseGate.Reset();
        }

        /// <summary>
        /// Resume playback (releases the pause gate).
        /// </summary>
        public void Resume()
        {
            _isPaused = false;
            _pauseGate.Set();
        }

        /// <summary>
        /// Stop playback completely.
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
            _isPaused = false;
            _isRunning = false;
            _runningStatusText = null;
            _countLateFramesSkipped = 0;
            _pauseGate.Set(); // Ensure gate is open after stop
            _targetFps = 0;
        }

        // Increment methods (thread-safe)
        public void IncrementCountA() => Interlocked.Increment(ref _countA);
        public void IncrementCountB() => Interlocked.Increment(ref _countB);
        public void IncrementCountD() => Interlocked.Increment(ref _countD);
        public void IncrementCountAvtpIn() => Interlocked.Increment(ref _countAvtpIn);
        public void IncrementCountAvtpDropped() => Interlocked.Increment(ref _countAvtpDropped);
        public void IncrementCountAvtpIncomplete() => Interlocked.Increment(ref _countAvtpIncomplete);
        public void IncrementCountAvtpSeqGapFrames() => Interlocked.Increment(ref _countAvtpSeqGapFrames);
        public void AddSeqGaps(int gaps) => Interlocked.Add(ref _sumAvtpSeqGaps, gaps);
        public int IncrementLateFramesSkipped() => Interlocked.Increment(ref _countLateFramesSkipped);
        
        /// <summary>
        /// Reset FPS estimates (used when switching sources).
        /// </summary>
        public void ResetFpsEstimates()
        {
            _avtpInFpsEma = 0.0;
            _bFpsEma = 0.0;
        }

        /// <summary>
        /// Updates FPS estimation if the window has elapsed. Returns true if updated.
        /// </summary>
        public bool TryUpdateFpsEstimates(out double fpsA, out double fpsB, out double fpsIn)
        {
            fpsA = 0; fpsB = 0; fpsIn = 0;

            if (_statSw.Elapsed.TotalSeconds < _fpsEstimationWindowSec)
                return false;

            var sec = _statSw.Elapsed.TotalSeconds;
            fpsA = Interlocked.Exchange(ref _countA, 0) / sec;
            fpsB = Interlocked.Exchange(ref _countB, 0) / sec;
            fpsIn = Interlocked.Exchange(ref _countAvtpIn, 0) / sec;
            _statSw.Restart();

            _avtpInFpsEma = ImageUtils.ApplyEma(_avtpInFpsEma, fpsIn, _fpsEmaAlpha);
            _bFpsEma = ImageUtils.ApplyEma(_bFpsEma, fpsB, _fpsEmaAlpha);

            return true;
        }

        /// <summary>
        /// Dispose resources.
        /// </summary>
        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _pauseGate.Dispose();
        }
    }
}
