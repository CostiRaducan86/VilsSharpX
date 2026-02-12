using System;
using System.Threading;
using System.Threading.Tasks;

namespace VilsSharpX
{
    /// <summary>
    /// Manages live capture sources: Ethernet/AVTP and PCAP replay.
    /// </summary>
    public class LiveCaptureManager : IDisposable
    {
        public enum Feed
        {
            None = 0,
            EthernetAvtp = 2,
            PcapReplay = 3,
        }

    private readonly RvfReassembler _rvf;
    private readonly object _rvfPushLock = new();
    private readonly Action<string> _log;

    private AvtpLiveCapture? _avtpLive;
    private CancellationTokenSource? _pcapCts;

    private int _activeFeed = (int)Feed.None;

    // Display resolution (may differ from AVTP protocol resolution for Nichia)
    private readonly int _displayWidth;
    private readonly int _displayHeight;

    // AVTP frame buffer – always at display resolution (cropped from 320×80 if needed)
    private byte[] _avtpFrame;
    private bool _hasAvtpFrame;
    private DateTime _lastAvtpFrameUtc = DateTime.MinValue;

    // Signal loss detection
    private DateTime _suppressLiveUntilUtc = DateTime.MinValue;
    private readonly TimeSpan _liveSignalLostTimeout;

    private string _lastRvfSrcLabel = "?";

    public LiveCaptureManager(int width, int height, double signalLostTimeoutSec, Action<string> log)
    {
        _displayWidth = width;
        _displayHeight = height;

        // AVTP protocol always uses 320×80 regardless of display device.
        // The reassembler must match the chunk dimensions produced by AvtpLiveCapture.
        _avtpFrame = new byte[width * height];
        _rvf = new RvfReassembler(RvfReassembler.W, RvfReassembler.H);
        _liveSignalLostTimeout = TimeSpan.FromSeconds(signalLostTimeoutSec);
        _log = log ?? (_ => { });

        // Wire up reassembler frame ready event
        _rvf.OnFrameReady += OnRvfFrameReady;
    }

        // Events
        public event Action<byte[], FrameMeta>? OnFrameReady;

        // Properties
        public RvfReassembler Reassembler => _rvf;
        public byte[] AvtpFrame => _avtpFrame;
        public bool HasAvtpFrame => Volatile.Read(ref _hasAvtpFrame);
        public DateTime LastAvtpFrameUtc => _lastAvtpFrameUtc;
        public string LastRvfSrcLabel { get => _lastRvfSrcLabel; set => _lastRvfSrcLabel = value; }
        public Feed ActiveFeed => (Feed)Volatile.Read(ref _activeFeed);

        /// <summary>
        /// Try to set the active feed. Returns true if successful.
        /// </summary>
        public bool TrySetActiveFeed(Feed feed)
        {
            int f = Volatile.Read(ref _activeFeed);
            if (f == (int)feed) return true;
            if (f != (int)Feed.None) return false;
            return Interlocked.CompareExchange(ref _activeFeed, (int)feed, (int)Feed.None) == (int)Feed.None;
        }

        /// <summary>
        /// Reset the active feed to None.
        /// </summary>
        public void ResetActiveFeed()
        {
            Volatile.Write(ref _activeFeed, (int)Feed.None);
        }

        /// <summary>
        /// Check if live input should be suppressed (after signal loss).
        /// </summary>
        public bool IsLiveInputSuppressed()
        {
            return DateTime.UtcNow < _suppressLiveUntilUtc;
        }

        /// <summary>
        /// Suppress live input for a short period (after signal loss).
        /// </summary>
        public void SuppressLiveInput(TimeSpan duration)
        {
            _suppressLiveUntilUtc = DateTime.UtcNow.Add(duration);
        }

        /// <summary>
        /// Check if signal has been lost (no frame for timeout period).
        /// </summary>
        public bool IsSignalLost()
        {
            return HasAvtpFrame
                && _lastAvtpFrameUtc != DateTime.MinValue
                && (DateTime.UtcNow - _lastAvtpFrameUtc) > _liveSignalLostTimeout;
        }

        /// <summary>
        /// Clear the AVTP frame state (for signal loss handling).
        /// </summary>
        public void ClearAvtpFrame()
        {
            Volatile.Write(ref _hasAvtpFrame, false);
            _lastAvtpFrameUtc = DateTime.MinValue;
        }

        /// <summary>
        /// Reset all capture state.
        /// </summary>
        public void ResetAll()
        {
            ResetActiveFeed();
            _rvf.ResetAll();
            ClearAvtpFrame();
        }

        /// <summary>
        /// Start Ethernet/AVTP capture.
        /// </summary>
        public void StartEthernetCapture(string? deviceHint)
        {
            try
            {
                StopEthernetCapture();

                var devs = AvtpLiveCapture.ListDevicesSafe();
                if (devs.Count > 0)
                {
                    _log("[avtp-live] devices:");
                    for (int i = 0; i < devs.Count; i++)
                        _log($"  [{i}] {NetworkInterfaceUtils.DescribeCaptureDeviceForUi(devs[i].Name, devs[i].Description)}");
                }

                _avtpLive = AvtpLiveCapture.Start(
                    deviceHint: deviceHint,
                    log: _log,
                    onChunk: chunk =>
                    {
                        if (IsLiveInputSuppressed()) return;
                        if (!TrySetActiveFeed(Feed.EthernetAvtp)) return;
                        PushChunk(chunk);
                    });

                _lastRvfSrcLabel = "Ethernet/AVTP";
            }
            catch (Exception ex)
            {
                _log($"[avtp-live] start failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop Ethernet capture.
        /// </summary>
        public void StopEthernetCapture()
        {
            try { _avtpLive?.Dispose(); } catch { }
            _avtpLive = null;
        }

        /// <summary>
        /// Start PCAP replay.
        /// </summary>
        public void StartPcapReplay(string path, ManualResetEventSlim pauseGate, Action? onComplete = null, Action<string>? onError = null)
        {
            StopPcapReplay();

            _pcapCts = new CancellationTokenSource();
            var ct = _pcapCts.Token;

            ResetAll();
            Volatile.Write(ref _activeFeed, (int)Feed.PcapReplay);

            _log($"[pcap] replay start: {path}");
            _lastRvfSrcLabel = "PCAP";

            _ = Task.Run(async () =>
            {
                try
                {
                    await PcapAvtpRvfReplay.ReplayAsync(
                        path,
                        chunk =>
                        {
                            if (IsLiveInputSuppressed()) return;
                            if (!TrySetActiveFeed(Feed.PcapReplay)) return;
                            PushChunk(chunk);
                        },
                        _log,
                        ct,
                        speed: 1.0,
                        pauseGate: pauseGate);

                    if (!ct.IsCancellationRequested)
                    {
                        _log("[pcap] replay done");
                        onComplete?.Invoke();
                    }
                }
                catch (OperationCanceledException)
                {
                    _log("[pcap] replay cancelled");
                }
                catch (Exception ex)
                {
                    _log($"[pcap] replay error: {ex}");
                    onError?.Invoke(ex.Message);
                }
            }, ct);
        }

        /// <summary>
        /// Stop PCAP replay.
        /// </summary>
        public void StopPcapReplay()
        {
            try { _pcapCts?.Cancel(); } catch { }
            try { _pcapCts?.Dispose(); } catch { }
            _pcapCts = null;
        }

        /// <summary>
        /// Stop all capture sources.
        /// </summary>
        public void StopAll()
        {
            StopPcapReplay();
            StopEthernetCapture();
        }

        private void PushChunk(RvfChunk chunk)
        {
            lock (_rvfPushLock)
            {
                _rvf.Push(chunk);
            }
        }

        private void OnRvfFrameReady(byte[] frame, FrameMeta meta)
        {
            // AVTP protocol frame is always 320×80 (25,600 bytes).
            // For Nichia (256×64), the image data is linearly packed in the first
            // displayW*displayH bytes of the AVTP buffer (CANoe linear padding convention).
            int avtpW = RvfReassembler.W;
            int avtpH = RvfReassembler.H;
            byte[] displayFrame;

            if (_displayWidth == avtpW && _displayHeight == avtpH)
            {
                displayFrame = frame;
            }
            else
            {
                // Linear copy: first displayW*displayH bytes contain the image
                // with stride=displayWidth (not grid-aligned to 320-byte rows).
                int displayBytes = _displayWidth * _displayHeight;
                displayFrame = new byte[displayBytes];
                int copyLen = Math.Min(displayBytes, frame.Length);
                Buffer.BlockCopy(frame, 0, displayFrame, 0, copyLen);
            }

            // Store the cropped frame in our buffer
            if (displayFrame.Length <= _avtpFrame.Length)
            {
                Buffer.BlockCopy(displayFrame, 0, _avtpFrame, 0, displayFrame.Length);
                Volatile.Write(ref _hasAvtpFrame, true);
                _lastAvtpFrameUtc = DateTime.UtcNow;
            }

            // Forward to subscribers (at display resolution)
            OnFrameReady?.Invoke(displayFrame, meta);
        }

        public void Dispose()
        {
            StopAll();
            _rvf.OnFrameReady -= OnRvfFrameReady;
        }
    }
}
