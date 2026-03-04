using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using SharpPcap;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
namespace VilsSharpX
{
    public partial class MainWindow : Window
    {
        private const double FpsEstimationWindowSec = 0.25;
        private const double FpsEmaAlpha = 0.30; // 0..1, higher = more responsive, lower = smoother
        private const double LiveSignalLostTimeoutSec = 0.625; // ~5 frames at 8fps

        private enum Pane
        {
            A = 0,
            B = 1,
            D = 2,
        }

        private enum LoadedSource
        {
            None = 0,
            Image,
            Pcap,
            Avi,
            Sequence,
            Scene
        }

        private enum ModeOfOperation
        {
            PlayerFromFiles = 0,
            AvtpLiveMonitor = 1,
        }

        // Default (Osram) resolution constants - used for protocol and as defaults
        private const int DefaultW = 320;
        private const int DefaultH = 80;
        private const int H_LVDS = 84;
        private const int META_LINES = 4; // bottom 4 (unused for now)

        // Selected LSM device type (determines resolution)
        private LsmDeviceType _currentDeviceType = LsmDeviceType.Osram20;

        // Current resolution (based on selected device type)
        private int _currentWidth = DefaultW;
        private int _currentHeight = DefaultH;

        // Loop playback flag for AVI/PCAP sources
        private volatile bool _loopPlayingEnabled = false;

        /// <summary>
        /// Gets the active frame width for the currently selected device type.
        /// </summary>
        private int GetCurrentWidth() => _currentWidth;

        /// <summary>
        /// Gets the active frame height for the currently selected device type.
        /// </summary>
        private int GetCurrentHeight() => _currentHeight;

        // Playback state management - delegated to PlaybackStateManager
        private readonly PlaybackStateManager _playback = new(FpsEstimationWindowSec, FpsEmaAlpha);

        // Live capture management - delegated to LiveCaptureManager  
        private LiveCaptureManager _liveCapture = null!;

        // Recording (AVI) - delegated to RecordingManager
        private RecordingManager _recordingManager = null!;

        private volatile int _bValueDelta;

        private volatile byte _diffThreshold;
        private byte[] _diffBgr = null!;

        // If >0, forces B[pixel_ID] to 0 (simulated dead pixel). pixel_ID is 1..(W*H_ACTIVE).
        private int _forcedDeadPixelId;

        private volatile bool _darkPixelCompensationEnabled = false;

        private volatile bool _zeroZeroIsWhite = false;

        // Live AVTP capture settings (Ethernet via SharpPcap)
        private bool _avtpLiveEnabled = true;
        private string? _avtpLiveDeviceHint;

        // AVTP TX MAC addresses
        private string _srcMac = "3C:CE:15:00:00:19";
        private string _dstMac = "01:00:5E:16:00:12";

        // AVTP header fields
        private int _ecuVariant = 0;
        private int _vlanId = 70;
        private int _vlanPriority = 5;
        private string _avtpEtherType = "0x22F0";
        private string _streamIdLastByte = "0x50";

        private ModeOfOperation _modeOfOperation = ModeOfOperation.AvtpLiveMonitor;

        // Fallback image / generator base
        private byte[] _pgmFrame = null!;

        // Always-available idle pattern so we can distinguish "no render" from black/loaded frames
        private byte[] _idleGradientFrame = null!;

        // No-signal background (mid gray) rendered under the overlay.
        private byte[] _noSignalGrayFrame = null!;
        private byte[] _noSignalGrayBgr = null!;

        // Optional LVDS source image (top-left 320x84). If loaded, B can be driven from this later.
        private byte[]? _lvdsFrame84;

        private LoadedSource _lastLoaded = LoadedSource.None;
        private string? _lastLoadedPcapPath;

        // Source players (extracted to separate classes)
        private SequencePlayer _sequencePlayer = null!;
        private ScenePlayer _scenePlayer = null!;
        private AviSourcePlayer _aviPlayer = null!;
        private SourceLoaderHelper _sourceLoader = null!;

        // Frame snapshot/report saver
        private FrameSnapshotSaver _snapshotSaver = null!;

        // Live NIC selector
        private readonly LiveNicSelector _nicSelector = new();

        // LVDS capture manager
        private LvdsLiveManager _lvdsManager = null!;

        // Nichia Ethernet capture (Aurix GETH → raw ethertype 0x88B5)
        private NichiaEthCapture? _nichiaEthCapture;

        private Frame? _latestA;
        private Frame? _latestB;
        private Frame? _latestD;

        // Snapshot used while paused so overlays/inspectors match the frozen image.
        private Frame? _pausedA;
        private Frame? _pausedB;
        private Frame? _pausedD;
        /// <summary>Sync-matched A that corresponds to _pausedB, frozen at pause time.</summary>
        private Frame? _pausedMatchedA;

        private readonly object _frameLock = new();

        // ─── Frame synchronization for diff comparison ─────────────────────
        // Ring buffer of recently produced A frames.  When B arrives from LVDS
        // with ECU round-trip delay, we find the A frame that was originally
        // sent and use it for a correct diff comparison.
        private const int SyncRingSize = 128;
        private readonly Frame?[] _syncRing = new Frame?[SyncRingSize];
        /// <summary>Cached per-pixel variance for each ring entry (set at push time).</summary>
        private readonly double[] _syncRingVarPerPx = new double[SyncRingSize];
        private volatile int _syncRingHead;
        /// <summary>
        /// The A frame that best matches the current _latestB for diff.
        /// Updated each time a new LVDS B frame arrives via HandleLvdsFrameReady.
        /// </summary>
        private Frame? _matchedAForDiff;
        /// <summary>NCC of the best match (1.0 = perfect, 0 = uncorrelated). NaN when not computed.</summary>
        private double _lastMatchNcc = double.NaN;

        // Zoom/pan manager (replaces individual _zoom/_pan fields)
        private readonly ZoomPanManager _zoomPan = new();

        // Pixel inspector for hover info
        private PixelInspector _pixelInspector = null!;

        // UI settings manager
        private UiSettingsManager _settingsManager = null!;

        private readonly DispatcherTimer _overlayTimerA;
        private readonly DispatcherTimer _overlayTimerB;
        private readonly DispatcherTimer _overlayTimerD;

        private DateTime _statusOverrideUntil = DateTime.MinValue;

        private bool _overlayPendingA;
        private bool _overlayPendingB;
        private bool _overlayPendingD;

        private bool _isUpdatingDiffThresholdText;

        private WriteableBitmap _wbA = null!;
        private WriteableBitmap _wbB = null!;
        private WriteableBitmap _wbD = null!;

        // --- AVTP Transmitter (managed by AvtpTransmitManager) ---
        private AvtpTransmitManager _txManager = null!;

        private void ShowSaveFeedback(string message, Brush color)
        {
            if (LblSaveFeedback != null)
            {
                LblSaveFeedback.Foreground = color;
                LblSaveFeedback.Text = message;
            }
        }

        private void HideSaveFeedback()
        {
            if (LblSaveFeedback != null)
            {
                LblSaveFeedback.Text = "";
            }
        }

        // Overlay renderer
        private readonly OverlayRenderer _overlayRenderer = new();

        public MainWindow()
        {
            // Initialize resolution-dependent objects with default (Osram) resolution
            InitializeResolutionDependentObjects();

            // XAML can trigger SelectionChanged/TextChanged during InitializeComponent.
            // Treat that phase like settings-load to avoid running app logic before controls/bitmaps are wired.
            _settingsManager.IsLoading = true;
            InitializeComponent();
            ImgA.Source = _wbA;
            ImgB.Source = _wbB;
            ImgD.Source = _wbD;
            _zoomPan.AttachToImages(ImgA, ImgB, ImgD);
            _overlayTimerA = MakeOverlayTimer(Pane.A);
            _overlayTimerB = MakeOverlayTimer(Pane.B);
            _overlayTimerD = MakeOverlayTimer(Pane.D);

            InitializeDefaultPatterns();

            if (TxtDiffThr != null) TxtDiffThr.Text = "0";

            _settingsManager.IsLoading = false;
        }

        /// <summary>
        /// Initializes or reinitializes all resolution-dependent objects based on _currentDeviceType.
        /// Must be called: 1) in constructor (before InitializeComponent), 2) when device type changes.
        /// </summary>
        private void InitializeResolutionDependentObjects()
        {
            _currentWidth = _currentDeviceType.GetActiveWidth();
            _currentHeight = _currentDeviceType.GetActiveHeight();

            int w = _currentWidth;
            int h = _currentHeight;

            // Frame buffers
            _diffBgr = new byte[w * h * 3];
            _pgmFrame = new byte[w * h];
            _idleGradientFrame = new byte[w * h];
            _noSignalGrayFrame = new byte[w * h];
            _noSignalGrayBgr = new byte[w * h * 3];

            // Bitmaps
            _wbA = BitmapUtils.MakeGray8(w, h);
            _wbB = BitmapUtils.MakeGray8(w, h);
            _wbD = BitmapUtils.MakeBgr24(w, h);

            // Helper classes that depend on resolution
            _liveCapture = new LiveCaptureManager(w, h, FpsEstimationWindowSec * 2.5, AppendDiagLog);
            _recordingManager = new RecordingManager(w, h);
            _sequencePlayer = new SequencePlayer(w, h);
            _scenePlayer = new ScenePlayer(w, h);
            _aviPlayer = new AviSourcePlayer(w, h, FpsEstimationWindowSec, FpsEmaAlpha);
            _sourceLoader = new SourceLoaderHelper(w, h, H_LVDS);
            _snapshotSaver = new FrameSnapshotSaver(w, h);
            _pixelInspector = new PixelInspector(w, h);
            _settingsManager = new UiSettingsManager(w, h);
            _txManager = new AvtpTransmitManager(w, h, AppendDiagLog);
            _lvdsManager = new LvdsLiveManager(_currentDeviceType, LiveSignalLostTimeoutSec, AppendDiagLog);
        }

        /// <summary>
        /// Reinitializes resolution-dependent objects after device type change.
        /// Called from CmbLsmDeviceType_SelectionChanged.
        /// </summary>
        private void ReinitializeForNewResolution()
        {
            // IMPORTANT: Dispose old managers that hold external resources before recreating them.
            // This prevents stale resources (pcap devices, sockets, files) from causing issues.
            try { _txManager?.Dispose(); } catch { /* ignore */ }
            try { _liveCapture?.Dispose(); } catch { /* ignore */ }
            try { _lvdsManager?.Dispose(); } catch { /* ignore */ }
            try { StopNichiaEthCapture(); } catch { /* ignore */ }
            try { _aviPlayer?.Dispose(); } catch { /* ignore */ }

            InitializeResolutionDependentObjects();
            InitializeDefaultPatterns();

            // Re-subscribe to LiveCaptureManager events (since we recreated the instance)
            if (_liveCapture != null)
                _liveCapture.OnFrameReady += (frame, meta) => Dispatcher.Invoke(() => HandleLiveFrameReady(frame, meta));

            // Re-subscribe to LVDS manager events
            if (_lvdsManager != null)
                _lvdsManager.OnFrameReady += (frame, meta) => Dispatcher.BeginInvoke(() => HandleLvdsFrameReady(frame, meta));

            // Update LVDS protocol info label
            UpdateLvdsProtocolLabel();

            // Rebind bitmaps to UI
            if (ImgA != null) ImgA.Source = _wbA;
            if (ImgB != null) ImgB.Source = _wbB;
            if (ImgD != null) ImgD.Source = _wbD;

            // Reset frame state
            lock (_frameLock)
            {
                _latestA = null;
                _latestB = null;
                _latestD = null;
                _pausedA = null;
                _pausedB = null;
                _pausedD = null;
                _pausedMatchedA = null;
            }
            ResetSyncState();

            RenderNoSignalFrames();
        }

        private void InitializeDefaultPatterns()
        {
            int w = _currentWidth;
            int h = _currentHeight;

            // Horizontal gradient (fallback)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    _idleGradientFrame[y * w + x] = (byte)(x * 255 / (w - 1));

            // No-signal pattern: flat mid-gray
            Array.Fill(_noSignalGrayFrame, (byte)0x80);
            Array.Fill(_noSignalGrayBgr, (byte)0x80);

            Buffer.BlockCopy(_idleGradientFrame, 0, _pgmFrame, 0, _pgmFrame.Length);
        }

        private bool ShouldShowNoSignalWhileRunning()
        {
            if (_playback.Cts == null) return false;
            if (!_playback.IsRunning) return false;
            if (_modeOfOperation != ModeOfOperation.AvtpLiveMonitor) return false;

            // If Nichia Ethernet capture is active and already has frames,
            // pane B is valid — proceed with rendering even if AVTP (pane A)
            // hasn't arrived yet.  Pane A will show gray; B and D render normally.
            if (_nichiaEthCapture != null
                && _nichiaEthCapture.IsCapturing
                && _nichiaEthCapture.FramesCompleted > 0)
                return false;

            // In AVTP Live mode, keep panes in "Signal not available" until first valid frame arrives.
            return !_liveCapture.HasAvtpFrame;
        }

        private void EnterWaitingForSignalState()
        {
            var prevFeed = GetActiveAvtpFeed();
            var lastAgeMs = _liveCapture.LastAvtpFrameUtc == DateTime.MinValue
                ? double.NaN
                : (DateTime.UtcNow - _liveCapture.LastAvtpFrameUtc).TotalMilliseconds;

            AppendDiagLog(
                $"[live] signal lost -> waiting | prevFeed={prevFeed} src={_liveCapture.LastRvfSrcLabel} " +
                $"ageMs={(double.IsNaN(lastAgeMs) ? "n/a" : lastAgeMs.ToString("F0", CultureInfo.InvariantCulture))} " +
                $"timeoutMs={LiveSignalLostTimeoutSec * 1000:F0} " +
                $"suppressMs={1000.ToString(CultureInfo.InvariantCulture)}");

            // Debounce: ignore late buffered live packets for a short time.
            _liveCapture.SuppressLiveInput(TimeSpan.FromSeconds(1.0));

            // Drop the last received frame and revert to the no-signal rendering path.
            // Reset reassembly so a fresh stream restart doesn't inherit seq/line state.
            _liveCapture.ResetAll();

            // Force the "Waiting for signal..." status to be refreshed.
            _playback.WasWaitingForSignal = false;

            lock (_frameLock)
            {
                _latestA = null;
                _latestB = null;
                _latestD = null;
                _pausedA = null;
                _pausedB = null;
                _pausedD = null;
                _pausedMatchedA = null;
            }
        }

        private void ApplyNoSignalUiState(bool noSignal)
        {
            if (NoSignalA != null) NoSignalA.Visibility = noSignal ? Visibility.Visible : Visibility.Collapsed;
            if (NoSignalB != null) NoSignalB.Visibility = noSignal ? Visibility.Visible : Visibility.Collapsed;
            if (NoSignalD != null) NoSignalD.Visibility = noSignal ? Visibility.Visible : Visibility.Collapsed;

            if (noSignal)
            {
                if (LblA != null) LblA.Text = "";
                if (LblB != null) LblB.Text = "";
                if (LblD != null) LblD.Text = "";
                if (LblDiffStats != null) LblDiffStats.Text = "";
            }
        }

        /// <summary>
        /// Sets button enabled/disabled state based on whether playback is running.
        /// When stopped: Load Files + Start enabled; Prev/Next/Record/Stop/Save/OpenFolder disabled.
        /// When running: Start(Pause) + Record/Stop/Save/OpenFolder enabled; Prev/Next disabled (only enabled when paused).
        /// When paused: Prev/Next enabled; Record disabled.
        /// </summary>
        private void ApplyButtonStates(bool isRunning, bool isPaused = false)
        {
            bool isAvtpLive = _modeOfOperation == ModeOfOperation.AvtpLiveMonitor;
            // Load Files is disabled while running OR when in AVTP Live mode (no file sources)
            if (BtnLoadFiles != null) BtnLoadFiles.IsEnabled = !isRunning && !isAvtpLive;
            if (BtnStart != null) BtnStart.IsEnabled = true; // always enabled (Start or Pause/Resume)
            if (BtnPrev != null) BtnPrev.IsEnabled = isRunning && isPaused;
            if (BtnNext != null) BtnNext.IsEnabled = isRunning && isPaused;
            if (BtnRecord != null) BtnRecord.IsEnabled = isRunning && !isPaused;
            if (BtnStop != null) BtnStop.IsEnabled = isRunning;
            if (BtnSave != null) BtnSave.IsEnabled = isRunning;
            if (BtnOpenSnapshots != null) BtnOpenSnapshots.IsEnabled = true; // always enabled
        }

        private void RenderNoSignalFrames()
        {
            BitmapUtils.Blit(_wbA, _noSignalGrayFrame, _currentWidth);
            BitmapUtils.Blit(_wbB, _noSignalGrayFrame, _currentWidth);
            BitmapUtils.Blit(_wbD, _noSignalGrayBgr, _currentWidth * 3);
        }

        private DispatcherTimer MakeOverlayTimer(Pane pane)
        {
            var t = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS while interacting
            };
            t.Tick += (_, __) => OverlayTimerTick(pane);
            return t;
        }

        private void OverlayTimerTick(Pane pane)
        {
            if (!_playback.IsPaused)
            {
                StopOverlayTimer(pane);
                return;
            }

            bool pending = pane switch
            {
                Pane.A => _overlayPendingA,
                Pane.B => _overlayPendingB,
                _ => _overlayPendingD,
            };

            if (!pending)
            {
                StopOverlayTimer(pane);
                return;
            }

            // consume pending
            switch (pane)
            {
                case Pane.A: _overlayPendingA = false; break;
                case Pane.B: _overlayPendingB = false; break;
                default: _overlayPendingD = false; break;
            }

            UpdateOverlay(pane);
        }

        private void StopOverlayTimer(Pane pane)
        {
            switch (pane)
            {
                case Pane.A:
                    _overlayPendingA = false;
                    if (_overlayTimerA.IsEnabled) _overlayTimerA.Stop();
                    break;
                case Pane.B:
                    _overlayPendingB = false;
                    if (_overlayTimerB.IsEnabled) _overlayTimerB.Stop();
                    break;
                default:
                    _overlayPendingD = false;
                    if (_overlayTimerD.IsEnabled) _overlayTimerD.Stop();
                    break;
            }
        }

        private void Img_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_playback.IsPaused) return;
            var pane = PaneFromSender(sender);
            RequestOverlayUpdate(pane);
        }

        private void RequestOverlayUpdate(Pane pane)
        {
            // Throttle overlay redraw during pan/zoom; do not debounce-cancel (that makes it appear "stuck").
            switch (pane)
            {
                case Pane.A:
                    _overlayPendingA = true;
                    if (!_overlayTimerA.IsEnabled) _overlayTimerA.Start();
                    break;
                case Pane.B:
                    _overlayPendingB = true;
                    if (!_overlayTimerB.IsEnabled) _overlayTimerB.Start();
                    break;
                default:
                    _overlayPendingD = true;
                    if (!_overlayTimerD.IsEnabled) _overlayTimerD.Start();
                    break;
            }
        }

        private (System.Windows.Controls.Image img, System.Windows.Controls.Canvas ovr, ScaleTransform zoom) GetPaneVisuals(Pane pane)
        {
            int idx = (int)pane;
            return pane switch
            {
                Pane.A => (ImgA, OvrA, _zoomPan.GetZoom(0)),
                Pane.B => (ImgB, OvrB, _zoomPan.GetZoom(1)),
                _ => (ImgD, OvrD, _zoomPan.GetZoom(2)),
            };
        }

        private Frame? GetDisplayedFrameForPane(Pane pane)
        {
            lock (_frameLock)
            {
                if (_playback.IsPaused)
                {
                    var a = _pausedA;
                    var b = _pausedB;
                    var d = _pausedD;

                    if (pane == Pane.B)
                    {
                        if (a == null && b == null) return null;
                        a ??= b;
                        b ??= a;
                        return (a != null && b != null) ? ApplyBPostProcessing(a, b) : null;
                    }

                    var f = pane switch
                    {
                        Pane.A => a,
                        _ => d,
                    };
                    return f;
                }

                var aLive = _latestA;
                var bLive = _latestB;
                var dLive = _latestD;

                // Fallback: if no composed Frame exists yet but we do have raw AVTP bytes,
                // construct a lightweight Frame so cursor hover can show values while live.
                if (aLive == null && _liveCapture.HasAvtpFrame)
                {
                    try
                    {
                        var copy = ImageUtils.Copy(_liveCapture.AvtpFrame);
                        aLive = new Frame(_currentWidth, _currentHeight, copy, _liveCapture.LastAvtpFrameUtc == DateTime.MinValue ? DateTime.UtcNow : _liveCapture.LastAvtpFrameUtc);
                    }
                    catch
                    {
                        // safe fallback: leave aLive null if allocation fails
                        aLive = null;
                    }
                }

                // Also construct a B fallback if missing (apply UI B delta to A or raw AVTP bytes).
                if (bLive == null && _liveCapture.HasAvtpFrame)
                {
                    try
                    {
                        if (aLive != null)
                        {
                            var bBytes = ApplyValueDelta(aLive.Data, _bValueDelta);
                            bLive = new Frame(_currentWidth, _currentHeight, bBytes, aLive.TimestampUtc);
                        }
                        else
                        {
                            var copyB = ImageUtils.Copy(_liveCapture.AvtpFrame);
                            var bBytes = ApplyValueDelta(copyB, _bValueDelta);
                            bLive = new Frame(_currentWidth, _currentHeight, bBytes, _liveCapture.LastAvtpFrameUtc == DateTime.MinValue ? DateTime.UtcNow : _liveCapture.LastAvtpFrameUtc);
                        }
                    }
                    catch
                    {
                        bLive = null;
                    }
                }

                if (pane == Pane.B)
                {
                    if (aLive == null && bLive == null) return null;
                    aLive ??= bLive;
                    bLive ??= aLive;
                    return (aLive != null && bLive != null) ? ApplyBPostProcessing(aLive, bLive) : null;
                }

                // If D is missing but A/B are available, synthesize D from A/B so the diff pane hover works.
                if (dLive == null)
                {
                    try
                    {
                        if (aLive != null && bLive != null)
                        {
                            dLive = AbsDiff(aLive, bLive);
                        }
                        else if (aLive == null && bLive != null)
                        {
                            var aFallback = new Frame(_currentWidth, _currentHeight, GetASourceBytes(), DateTime.UtcNow);
                            dLive = AbsDiff(aFallback, bLive);
                        }
                        else if (aLive != null && bLive == null)
                        {
                            var bFallbackBytes = ApplyValueDelta(aLive.Data, _bValueDelta);
                            var bFallback = new Frame(_currentWidth, _currentHeight, bFallbackBytes, aLive.TimestampUtc);
                            dLive = AbsDiff(aLive, bFallback);
                        }
                    }
                    catch
                    {
                        dLive = null;
                    }
                }

                return pane switch
                {
                    Pane.A => aLive,
                    _ => dLive,
                };
            }
        }

        private Frame ApplyBPostProcessing(Frame a, Frame b)
        {
            return DarkPixelCompensation.ApplyBPostProcessing(a, b, _currentWidth, _currentHeight, 
                _darkPixelCompensationEnabled, Volatile.Read(ref _forcedDeadPixelId));
        }

        private void ClearOverlay(Pane pane)
        {
            var (_, ovr, _) = GetPaneVisuals(pane);
            ovr.Children.Clear();
            ovr.Visibility = Visibility.Collapsed;
            StopOverlayTimer(pane);
        }

        private void UpdateOverlay(Pane pane)
        {
            var (img, ovr, zoom) = GetPaneVisuals(pane);
            if (img == null || ovr == null) return;

            if (!_playback.IsPaused || zoom.ScaleX < OverlayRenderer.MinZoom)
            {
                ClearOverlay(pane);
                return;
            }

            Frame? fBase;
            Frame? fA = null;
            Frame? fB = null;
            if (pane == Pane.D)
            {
                lock (_frameLock)
                {
                    fA = _pausedA ?? _latestA;
                    fB = _pausedB ?? _latestB;
                }

                // D is rendered from A and *post-processed* B (forced dead pixel + optional dark pixel compensation).
                if (fA != null && fB != null)
                    fB = ApplyBPostProcessing(fA, fB);
                fBase = fA;
            }
            else
            {
                fBase = GetDisplayedFrameForPane(pane);
            }

            if (fBase == null)
            {
                ClearOverlay(pane);
                return;
            }

            double aw = img.ActualWidth;
            double ah = img.ActualHeight;
            if (aw <= 1 || ah <= 1)
            {
                ClearOverlay(pane);
                return;
            }

            GeneralTransform imgToOvr;
            try { imgToOvr = img.TransformToVisual(ovr); }
            catch { ClearOverlay(pane); return; }

            var dpi = VisualTreeHelper.GetDpi(this);
            double pixelsPerDip = dpi.PixelsPerDip;

            if (pane == Pane.D && fA != null && fB != null)
            {
                _overlayRenderer.RenderDiffOverlay(ovr, img, fA, fB, zoom.ScaleX, imgToOvr, pixelsPerDip, _diffThreshold, _zeroZeroIsWhite);
            }
            else
            {
                _overlayRenderer.RenderGrayscaleOverlay(ovr, img, fBase, zoom.ScaleX, imgToOvr, pixelsPerDip);
            }
        }

        private void UpdateOverlaysAll()
        {
            UpdateOverlay(Pane.A);
            UpdateOverlay(Pane.B);
            UpdateOverlay(Pane.D);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Hook LiveCaptureManager -> update UI with frame info (frame storage is already done in the manager)
            _liveCapture.OnFrameReady += (frame, meta) => Dispatcher.Invoke(() => HandleLiveFrameReady(frame, meta));

            ShowIdleGradient();
            int w = GetCurrentWidth();
            int h = GetCurrentHeight();
            LblStatus.Text = $"Ready. Load an image (PGM/BMP/PNG; BMP/PNG are converted to Gray8 u8; will crop top-left to {w}×{h}) and press Start to begin rendering.";

            LoadUiSettings();

            // Startup should show "Signal not available".
            ApplyNoSignalUiState(noSignal: true);

            // Wire up LVDS manager frame ready event
            _lvdsManager.OnFrameReady += (frame, meta) => Dispatcher.BeginInvoke(() => HandleLvdsFrameReady(frame, meta));

            UpdateLvdsProtocolLabel();

            // Default button states: Load Files + Start enabled; others disabled
            ApplyButtonStates(false);
        }

        private void HandleLiveFrameReady(byte[] frame, FrameMeta meta)
        {
            // Keep status stable when stopped; ignore late frames during shutdown races.
            if (!_playback.IsRunning)
                return;

            // Always keep the sync ring populated so B frames arriving after resume
            // can find the correct matching A (CANoe keeps sending during pause).
            if (frame != null && frame.Length == _currentWidth * _currentHeight)
            {
                try
                {
                    var aLive = new Frame(_currentWidth, _currentHeight, frame,
                        _liveCapture.LastAvtpFrameUtc == DateTime.MinValue ? DateTime.UtcNow : _liveCapture.LastAvtpFrameUtc);
                    PushSyncFrame(aLive);

                    // Don't update display state while paused — panes are frozen.
                    if (!_playback.IsPaused)
                    {
                        lock (_frameLock)
                        {
                            _latestA = aLive;
                        }
                    }
                }
                catch
                {
                    // ignore frame copy issues in callback path
                }
            }

            _playback.IncrementCountAvtpIn();

            // AVTP/RVF always sends RvfProtocol.H lines (80) regardless of device active height.
            // Compare against that, not the device's active crop height.
            int rvfHeight = RvfProtocol.H;
            int displayHeight = GetCurrentHeight();
            bool incomplete = meta.linesWritten < rvfHeight;
            bool gap = meta.seqGaps > 0;
            if (incomplete) _playback.IncrementCountAvtpIncomplete();
            if (gap)
            {
                _playback.IncrementCountAvtpSeqGapFrames();
                _playback.AddSeqGaps(meta.seqGaps);
            }
            if (incomplete || gap) _playback.IncrementCountAvtpDropped();

            // Increment _countB for AVTP Live mode FPS tracking
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor)
                _playback.IncrementCountB();

            string src = GetActiveAvtpFeed() switch
            {
                LiveCaptureManager.Feed.EthernetAvtp => "Ethernet/AVTP",
                LiveCaptureManager.Feed.PcapReplay => "PCAP",
                _ => "?"
            };
            _liveCapture.LastRvfSrcLabel = src;

            // For display, clamp linesWritten to display height so Nichia shows "64/64" not "80/64".
            int displayLines = Math.Min(meta.linesWritten, displayHeight);
            LblStatus.Text = StatusFormatter.FormatAvtpRvfStatus(
                src, meta.frameId, meta.seq, displayLines, displayHeight, meta.seqGaps,
                _playback.CountAvtpDropped, _playback.CountAvtpSeqGapFrames,
                _playback.CountAvtpIncomplete, _playback.CountLateFramesSkipped);
        }

        private void LoadUiSettings()
        {
            var s = _settingsManager.Load();
            try
            {

                _bValueDelta = s.BDelta;
                _diffThreshold = (byte)s.Deadband;
                _zeroZeroIsWhite = s.ZeroZeroIsWhite;
                Volatile.Write(ref _forcedDeadPixelId, s.ForcedDeadPixelId);
                _darkPixelCompensationEnabled = s.DarkPixelCompensationEnabled;

                _avtpLiveEnabled = s.AvtpLiveEnabled;
                _avtpLiveDeviceHint = s.AvtpLiveDeviceHint;

                _srcMac = s.SrcMac ?? "3C:CE:15:00:00:19";
                _dstMac = s.DstMac ?? "01:00:5E:16:00:12";

                _ecuVariant = Math.Clamp(s.EcuVariant, 0, 14);
                _vlanId = Math.Clamp(s.VlanId, 0, 4095);
                _vlanPriority = Math.Clamp(s.VlanPriority, 0, 7);
                _avtpEtherType = s.AvtpEtherType ?? "0x22F0";
                _streamIdLastByte = s.StreamIdLastByte ?? "0x50";

                _modeOfOperation = s.ModeOfOperation == (int)ModeOfOperation.AvtpLiveMonitor
                    ? ModeOfOperation.AvtpLiveMonitor
                    : ModeOfOperation.PlayerFromFiles;

                _currentDeviceType = s.LsmDeviceType switch
                {
                    1 => LsmDeviceType.Osram205,
                    2 => LsmDeviceType.Nichia,
                    _ => LsmDeviceType.Osram20
                };

                // If saved device type differs from default Osram20 used in constructor,
                // reinitialize all resolution-dependent objects to match the saved resolution.
                if (_currentDeviceType != LsmDeviceType.Osram20)
                {
                    ReinitializeForNewResolution();
                }

                if (TxtFps != null) TxtFps.Text = s.Fps.ToString();
                if (TxtBDelta != null) TxtBDelta.Text = s.BDelta.ToString();
                if (TxtDiffThr != null) TxtDiffThr.Text = s.Deadband.ToString();
                if (ChkZeroZeroWhite != null) ChkZeroZeroWhite.IsChecked = s.ZeroZeroIsWhite;
                if (TxtDeadPixelId != null) TxtDeadPixelId.Text = s.ForcedDeadPixelId.ToString();
                if (ChkDarkPixelComp != null) ChkDarkPixelComp.IsChecked = s.DarkPixelCompensationEnabled;
                if (TxtSrcMac != null) TxtSrcMac.Text = _srcMac;
                if (TxtDstMac != null) TxtDstMac.Text = _dstMac;

                if (CmbModeOfOperation != null)
                {
                    CmbModeOfOperation.SelectedIndex = _modeOfOperation == ModeOfOperation.AvtpLiveMonitor ? 0 : 1;
                }

                if (CmbLsmDeviceType != null)
                {
                    CmbLsmDeviceType.SelectedIndex = (int)_currentDeviceType;
                }

                if (CmbEcuVariant != null)
                {
                    CmbEcuVariant.SelectedIndex = _ecuVariant;
                }

                if (CmbLvdsMode != null)
                {
                    CmbLvdsMode.SelectedIndex = Math.Clamp(s.LvdsMode, 0, 1);
                }

                if (TxtVlanId != null) TxtVlanId.Text = _vlanId.ToString();
                if (TxtVlanPriority != null) TxtVlanPriority.Text = _vlanPriority.ToString();
                if (TxtAvtpEtherType != null) TxtAvtpEtherType.Text = _avtpEtherType;
                if (TxtStreamIdLastByte != null) TxtStreamIdLastByte.Text = _streamIdLastByte;

                RefreshLiveNicList();
                UpdateLiveUiEnabledState();

                UpdateLvdsProtocolLabel();

                RenderAll();

                // Update status text with the correct (possibly reinitialized) resolution
                int w = GetCurrentWidth();
                int h = GetCurrentHeight();
                LblStatus.Text = $"Ready. Load an image (PGM/BMP/PNG; BMP/PNG are converted to Gray8 u8; will crop top-left to {w}×{h}) and press Start to begin rendering.";
            }
            finally
            {
                _settingsManager.FinishLoading();
            }
        }

        private void SaveUiSettings()
        {
            int fps = (TxtFps != null && int.TryParse(TxtFps.Text, out var f) && f > 0) ? f : 100;
            var s = UiSettingsManager.CreateFromState(
                fps, _bValueDelta, _diffThreshold, _zeroZeroIsWhite,
                Volatile.Read(ref _forcedDeadPixelId), _darkPixelCompensationEnabled,
                _avtpLiveEnabled, _avtpLiveDeviceHint, (int)_modeOfOperation,
                _srcMac, _dstMac, (int)_currentDeviceType,
                _ecuVariant, _vlanId, _vlanPriority, _avtpEtherType, _streamIdLastByte,
                null, CmbLvdsMode?.SelectedIndex ?? 0);
            _settingsManager.TrySave(s);
        }

        private void CmbModeOfOperation_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_settingsManager.IsLoading || !IsLoaded) return;

            var newMode = (CmbModeOfOperation?.SelectedIndex ?? 0) == 0
                ? ModeOfOperation.AvtpLiveMonitor
                : ModeOfOperation.PlayerFromFiles;

            if (newMode == _modeOfOperation) return;

            _modeOfOperation = newMode;
            SaveUiSettings();

            UpdateLiveUiEnabledState();

            // Mode switch is a big behavior change; reset run state and AVTP buffers.
            StopAll();
            _liveCapture.Reassembler.ResetAll();
            _liveCapture.ClearAvtpFrame();

            // Hide Loop Playing checkbox when switching to AVTP Live mode
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor)
            {
                if (ChkLoopPlaying != null) ChkLoopPlaying.Visibility = Visibility.Collapsed;
            }

            // Update button enabled states to reflect new mode (e.g. Load Files disabled in AVTP Live)
            ApplyButtonStates(isRunning: false);

            LblStatus.Text = _modeOfOperation == ModeOfOperation.AvtpLiveMonitor
                ? "Mode: AVTP Monitoring. Press Start to listen/capture live stream."
                : "Mode: AVTP Generator. Load a file and press Start.";
        }

        private void CmbLsmDeviceType_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_settingsManager.IsLoading || !IsLoaded) return;

            var newDeviceType = (CmbLsmDeviceType?.SelectedIndex ?? 0) switch
            {
                1 => LsmDeviceType.Osram205,
                2 => LsmDeviceType.Nichia,
                _ => LsmDeviceType.Osram20
            };

            if (newDeviceType == _currentDeviceType) return;

            _currentDeviceType = newDeviceType;
            SaveUiSettings();

            // Device type change affects resolution - reinitialize all resolution-dependent objects
            StopAll();
            ReinitializeForNewResolution();

            LblStatus.Text = $"Device Type: {_currentDeviceType.GetDisplayName()} ({GetCurrentWidth()}x{GetCurrentHeight()}). Load a file or start live capture.";
        }

        private void CmbEcuVariant_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_settingsManager.IsLoading || !IsLoaded) return;

            _ecuVariant = CmbEcuVariant?.SelectedIndex ?? 0;
            SaveUiSettings();
        }

        private void CmbLvdsMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_settingsManager.IsLoading || !IsLoaded) return;
            // LVDS Mode: 0 = Monitoring (current behavior), 1 = Generator (future)
            SaveUiSettings();
        }

        private void TxtAvtpHeader_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_settingsManager.IsLoading || !IsLoaded) return;

            if (TxtVlanId != null && int.TryParse(TxtVlanId.Text, out var vid))
                _vlanId = Math.Clamp(vid, 0, 4095);
            if (TxtVlanPriority != null && int.TryParse(TxtVlanPriority.Text, out var vpri))
                _vlanPriority = Math.Clamp(vpri, 0, 7);
            if (TxtAvtpEtherType != null)
                _avtpEtherType = TxtAvtpEtherType.Text?.Trim() ?? "0x22F0";
            if (TxtStreamIdLastByte != null)
                _streamIdLastByte = TxtStreamIdLastByte.Text?.Trim() ?? "0x50";

            SaveUiSettings();
        }

        // Convenience aliases for live capture feed - delegate to _liveCapture
        private bool TrySetActiveAvtpFeed(LiveCaptureManager.Feed feed) => _liveCapture.TrySetActiveFeed(feed);
        private LiveCaptureManager.Feed GetActiveAvtpFeed() => _liveCapture.ActiveFeed;

        private void RefreshLiveNicList() => _nicSelector.RefreshNicList(CmbLiveNic, _avtpLiveDeviceHint);

        private string? GetTxPcapDeviceNameOrNull() => _nicSelector.GetTxPcapDeviceNameOrNull(CmbLiveNic, _avtpLiveDeviceHint);

        private void UpdateLiveUiEnabledState() =>
            _nicSelector.UpdateLiveUiEnabledState(CmbLiveNic, _modeOfOperation == ModeOfOperation.AvtpLiveMonitor);

        private void BtnRefreshNics_Click(object sender, RoutedEventArgs e) => RefreshLiveNicList();

        // ── Nichia Ethernet Capture (Aurix GETH → pane B) ──────────────

        private void StartNichiaEthCapture()
        {
            try
            {
                // If already capturing via Ethernet, stop first
                StopNichiaEthCapture();

                // Use the same NIC as the AVTP live capture (pane A);
                // the user can change the NIC via the existing NIC dropdown.
                string? nicHint = _nicSelector.GetSelectedDeviceName(CmbLiveNic) ?? _avtpLiveDeviceHint;

                _nichiaEthCapture = NichiaEthCapture.Start(nicHint, AppendDiagLog);
                _nichiaEthCapture.OnFrameReady += (frame, meta) =>
                    Dispatcher.BeginInvoke(() => HandleLvdsFrameReady(frame, meta));

                AppendDiagLog("[nfe] Nichia Ethernet capture started");
            }
            catch (Exception ex)
            {
                AppendDiagLog($"[nfe] Ethernet capture error: {ex.Message}");
            }
        }

        private void StopNichiaEthCapture()
        {
            if (_nichiaEthCapture != null)
            {
                _nichiaEthCapture.Dispose();
                _nichiaEthCapture = null;
                AppendDiagLog("[nfe] Nichia Ethernet capture stopped");
            }
        }

        private void HandleLvdsFrameReady(byte[] frame, LvdsFrameMeta meta)
        {
            // Guard: reject stale callbacks that arrive via Dispatcher.BeginInvoke
            // after Stop has been pressed.
            // Without this, queued callbacks re-render the last frame over "No Signal".
            bool ethActive = _nichiaEthCapture != null && _nichiaEthCapture.IsCapturing;
            if (!ethActive)
                return;

            // Update LVDS stats labels
            LblLvdsFrameCount.Text = $"Frames: {meta.FrameId} ({meta.ValidLines}/{meta.LinesExpected} lines)";
            LblLvdsBytesReceived.Text = $"Bytes: {meta.TotalBytes:N0}";
            LblLvdsSyncLoss.Text = $"Sync_error: {meta.SyncLosses}  CRC_error: {meta.CrcErrors}  Parity_error: {meta.ParityErrors}";

            // FPS from Ethernet capture
            double fps = _nichiaEthCapture?.FpsEma ?? 0;
            LblLvdsFps.Text = $"FPS: {fps:F1}";

            // Status text is now shown only in Frame Statistics, no need to duplicate here

            // During pause: keep sync matching alive (ring buffer + _matchedAForDiff)
            // so B frames still find the correct A. But don't update _latestB or render.
            if (_playback.IsPaused)
            {
                _matchedAForDiff = FindBestMatchA(frame, _currentWidth * _currentHeight);
                return;
            }

            // Always store LVDS frame for pane B (used by RenderAll when playback is active)
            lock (_frameLock)
            {
                _latestB = new Frame(_currentWidth, _currentHeight, frame, DateTime.UtcNow);
            }

            // Find best-matching A frame for synchronized diff comparison.
            // B arrives with ECU round-trip delay; pixel-sampling picks the A
            // that was originally sent and passed through the ECU.
            _matchedAForDiff = FindBestMatchA(frame, _currentWidth * _currentHeight);

            // When the main playback loop is NOT running (user didn't press Start),
            // render LVDS frame directly on pane B (standalone LVDS capture mode).
            if (_playback.Cts == null)
            {
                RenderLvdsOnly(frame, meta);
            }
        }

        /// <summary>
        /// Renders LVDS frame on pane B (and optionally A/DIFF) when the main
        /// AVTP playback loop is not active. This allows standalone LVDS capture
        /// without needing to hit the Start button.
        /// </summary>
        private void RenderLvdsOnly(byte[] frame, LvdsFrameMeta meta)
        {
            int w = _currentWidth;
            int h = _currentHeight;

            // Pane B: LVDS frame
            if (frame.Length == w * h)
            {
                BitmapUtils.Blit(_wbB, frame, w);
            }
            else if (frame.Length > 0)
            {
                // Dimension mismatch — create a padded/cropped buffer
                var safeFrame = new byte[w * h];
                int copyLen = Math.Min(frame.Length, safeFrame.Length);
                Buffer.BlockCopy(frame, 0, safeFrame, 0, copyLen);
                BitmapUtils.Blit(_wbB, safeFrame, w);
            }

            // Pane A: show the loaded/generated source frame (gradient if nothing loaded)
            var aData = GetASourceBytes();
            BitmapUtils.Blit(_wbA, aData, w);

            // Pane D: |A − B| diff
            DiffRenderer.RenderCompareToBgr(_diffBgr, aData, frame.Length == w * h ? frame : _noSignalGrayFrame,
                w, h, _diffThreshold, _zeroZeroIsWhite,
                out var minDiff, out var maxDiff, out var meanDiff,
                out var maxAbsDiff, out var meanAbsDiff, out var aboveDeadband,
                out var totalDarkPixels);
            BitmapUtils.Blit(_wbD, _diffBgr, w * 3);

            if (LblDiffStats != null)
                LblDiffStats.Text = StatusFormatter.FormatDiffStats(maxDiff, minDiff, meanAbsDiff, aboveDeadband, totalDarkPixels, _lastMatchNcc);

            ApplyNoSignalUiState(noSignal: false);
        }

        private void UpdateLvdsProtocolLabel()
        {
            if (LblLvdsProtocol == null) return;
            var cfg = LvdsProtocol.GetUartConfig(_currentDeviceType);
            string parityStr = cfg.Parity == System.IO.Ports.Parity.None ? "N" :
                               cfg.Parity == System.IO.Ports.Parity.Odd ? "O" : "E";
            LblLvdsProtocol.Text = $"Protocol: {_currentDeviceType.GetDisplayName()}\n" +
                                   $"Baud: {cfg.BaudRate:N0} bps | {cfg.DataBits}{parityStr}1 | LSB-first\n" +
                                   $"Line: [0x5D][row][{cfg.FrameWidth}px][CRC{cfg.CrcLen*8}] = {cfg.LinePacketLen} B\n" +
                                   $"Frame: {cfg.ActiveHeight} lines → rezolution {cfg.FrameWidth}×{cfg.ActiveHeight}";
        }

        /// <summary>
        /// Resets panes to "Signal not available" and clears stored LVDS frame data.
        /// Called when Stop is pressed.
        /// </summary>
        private void ClearLvdsPanes()
        {
            lock (_frameLock)
            {
                _latestB = null;
            }
            _lvdsManager.ClearFrame();
            RenderNoSignalFrames();
            ApplyNoSignalUiState(noSignal: true);
        }

        // ── End LVDS ────────────────────────────────────────────────────────

        private void CmbLiveNic_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_settingsManager.IsLoading) return;
            _avtpLiveDeviceHint = _nicSelector.GetSelectedDeviceName(CmbLiveNic);
            SaveUiSettings();
        }

        private void BDelta_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (TxtBDelta != null && int.TryParse(TxtBDelta.Text, out var d))
                _bValueDelta = Math.Clamp(d, -255, 255);

            SaveUiSettings();

            // If not running (or paused), update display immediately.
            if (_playback.Cts == null || _playback.IsPaused)
            {
                // keep latestA/B/D consistent with new offset
                var a = _latestA ?? new Frame(_currentWidth, _currentHeight, GetASourceBytes(), DateTime.UtcNow);
                var bBytes = ApplyValueDelta(a.Data, _bValueDelta);
                var b = new Frame(_currentWidth, _currentHeight, bBytes, DateTime.UtcNow);
                lock (_frameLock)
                {
                    _latestA = a;
                    _latestB = b;
                    _latestD = AbsDiff(a, b); // keep Gray8 abs-diff buffer for any internal uses
                }
                RenderAll();
            }
        }

        private void TxtDiffThr_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_settingsManager.IsLoading || _isUpdatingDiffThresholdText) return;

            if (TxtDiffThr != null && int.TryParse(TxtDiffThr.Text, out var v))
                SetDiffThreshold(v, updateText: false);
        }

        private void BtnDiffThrUp_Click(object sender, RoutedEventArgs e) =>
            SetDiffThreshold(_diffThreshold + 1, updateText: true);

        private void BtnDiffThrDown_Click(object sender, RoutedEventArgs e) =>
            SetDiffThreshold(_diffThreshold - 1, updateText: true);

        private void SetDiffThreshold(int value, bool updateText)
        {
            byte clamped = (byte)Math.Clamp(value, 0, 255);
            if (_diffThreshold == clamped && !updateText) return;

            _diffThreshold = clamped;

            if (updateText && TxtDiffThr != null)
            {
                _isUpdatingDiffThresholdText = true;
                TxtDiffThr.Text = _diffThreshold.ToString();
                _isUpdatingDiffThresholdText = false;
            }

            SaveUiSettings();
            if (_playback.Cts == null || _playback.IsPaused) RenderAll();
        }

        private void TxtDeadPixelId_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            int id = 0;
            if (TxtDeadPixelId != null && int.TryParse(TxtDeadPixelId.Text, out var parsed))
                id = Math.Clamp(parsed, 0, _currentWidth * _currentHeight);

            Volatile.Write(ref _forcedDeadPixelId, id);

            SaveUiSettings();

            // If not running (or paused), update display immediately.
            if (_playback.Cts == null || _playback.IsPaused) RenderAll();
        }

        private void ChkDarkPixelComp_Changed(object sender, RoutedEventArgs e)
        {
            _darkPixelCompensationEnabled = ChkDarkPixelComp?.IsChecked == true;
            SaveUiSettings();
            RenderAll();
        }

        private void ChkZeroZeroWhite_Changed(object sender, RoutedEventArgs e)
        {
            _zeroZeroIsWhite = ChkZeroZeroWhite?.IsChecked == true;
            SaveUiSettings();
            RenderAll();
        }

        private void TxtMac_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_settingsManager.IsLoading) return;

            if (TxtSrcMac != null)
                _srcMac = TxtSrcMac.Text?.Trim() ?? "3C:CE:15:00:00:19";
            if (TxtDstMac != null)
                _dstMac = TxtDstMac.Text?.Trim() ?? "01:00:5E:16:00:12";

            SaveUiSettings();
        }
        private void ShowIdleGradient()
        {
            // Replaced idle gradient with explicit "no signal" UI.
            lock (_frameLock)
            {
                _latestA = null;
                _latestB = null;
                _latestD = null;
                _pausedA = null;
                _pausedB = null;
                _pausedD = null;
            }

            RenderNoSignalFrames();
            ApplyNoSignalUiState(noSignal: true);
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveUiSettings();
            if (_recordingManager.IsRecording) StopRecording();
            StopAll();
            try { StopNichiaEthCapture(); } catch { /* ignore */ }
            try { _lvdsManager?.Dispose(); } catch { /* ignore */ }
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtFps.Text, out var fps) || fps <= 0) fps = 100;

            if (!_playback.IsRunning)
            {
                Start(fps);
                if (BtnStart != null) BtnStart.Content = "Pause";
                ApplyButtonStates(isRunning: true, isPaused: false);
                return;
            }

            if (!_playback.IsPaused)
            {
                Pause();
            }
            else
            {
                Resume();
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            // If we are in Generator/Player: STOP => keep AVTP alive by sending BLACK
            if (_modeOfOperation == ModeOfOperation.PlayerFromFiles)
            {
                int fps = 100;
                _ = int.TryParse(TxtFps.Text, out fps);

                // Stop UI/loops as before
                StopAll();

                // StopAll() closes the shared pcap device handle (singleton) used
                // by captures AND the TX transmitter.  We MUST reinitialize the TX
                // to reopen a fresh handle before starting the black loop.
                {
                    ushort ethType = ParseHexUshort(_avtpEtherType, 0x22F0);
                    byte stIdByte = ParseHexByte(_streamIdLastByte, 0x50);
                    string? deviceHint = _nicSelector.GetSelectedDeviceName(CmbLiveNic) ?? _avtpLiveDeviceHint;
                    _txManager.Initialize(deviceHint, _srcMac, _dstMac,
                        _vlanId, _vlanPriority, ethType, stIdByte);
                }

                // Start BLACK TX loop — strict 0x00 for LSM keepalive.
                _txManager.StartBlackLoop(fps);

                LblStatus.Text = "Player STOP: sending BLACK AVTP (Signal not available).";
                return;
            }

            // AVTP Live: normal stop
            StopAll();
        }

        private void ChkLoopPlaying_Changed(object sender, RoutedEventArgs e)
        {
            _loopPlayingEnabled = ChkLoopPlaying?.IsChecked == true;
            _aviPlayer.LoopEnabled = _loopPlayingEnabled;
        }

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_recordingManager.IsRecording) StopRecording();
            else StartRecording();
        }

        private async void BtnSaveReport_Click(object sender, RoutedEventArgs e)
        {
            // Get current frames
            Frame? a;
            Frame? b;
            lock (_frameLock)
            {
                a = _playback.IsPaused ? _pausedA : _latestA;
                b = _playback.IsPaused ? _pausedB : _latestB;
            }

            if (a == null || b == null)
            {
                // Force a deterministic one-frame render to populate latest frames.
                RenderOneFrameNow();
                lock (_frameLock)
                {
                    a = _playback.IsPaused ? _pausedA : _latestA;
                    b = _playback.IsPaused ? _pausedB : _latestB;
                }
            }

            if (a == null || b == null)
            {
                MessageBox.Show("No frame available to report yet.", "Save report");
                HideSaveFeedback();
                return;
            }

            // Match Record behavior: apply B post-processing before generating diff/report.
            var bPost = ApplyBPostProcessing(a, b);
            int frameNr = GetCurrentFrameNumberHint();

            await _snapshotSaver.SaveAsync(a, bPost, _diffThreshold, _zeroZeroIsWhite, frameNr,
                LblSaveFeedback, LblStatus, ShowSaveFeedback, HideSaveFeedback);
        }

        private void BtnOpenSnapshots_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = RecordingManager.GetFrameSnapshotsOutputDirectory();
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open folder: {ex.Message}", "Open folder");
            }
        }

        private int GetCurrentFrameNumberHint()
        {
            return _lastLoaded switch
            {
                LoadedSource.Avi => _aviPlayer.CurrentIndex + 1,
                LoadedSource.Scene => _scenePlayer.CurrentIndex + 1,
                LoadedSource.Sequence => _sequencePlayer.CurrentIndex + 1,
                _ => 1
            };
        }

        private void StartRecording()
        {
            if (_playback.Cts == null || !_playback.IsRunning || _playback.IsPaused)
            {
                MessageBox.Show("Recording works while running (not paused). Press Start (and unpause) first.", "Record");
                return;
            }

            int fps = (int)Math.Clamp(_playback.TargetFps > 0 ? _playback.TargetFps : 30, 1, 1000);
            var (success, error, statusMessage) = _recordingManager.StartRecording(fps, _diffThreshold);

            if (success)
            {
                if (BtnRecord != null) BtnRecord.Content = "Stop Rec";
                LblStatus.Text = statusMessage ?? "Recording started.";
            }
            else
            {
                if (BtnRecord != null) BtnRecord.Content = "Record";
                MessageBox.Show($"Failed to start recording: {error}", "Record error");
            }
        }

        private void StopRecording()
        {
            LblStatus.Text = _recordingManager.StopRecording();
            if (BtnRecord != null) BtnRecord.Content = "Record";
        }

        private void Pause()
        {
            _playback.Pause();
            _playback.PauseGate.Reset();

            // Freeze the currently displayed frames so overlays match the frozen bitmap.
            lock (_frameLock)
            {
                _pausedA = _latestA ?? new Frame(_currentWidth, _currentHeight, GetASourceBytes(), DateTime.UtcNow);

                bool ethActive = _nichiaEthCapture != null
                                 && _nichiaEthCapture.IsCapturing
                                 && _nichiaEthCapture.FramesCompleted > 0;

                if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor && ethActive && _latestB != null)
                {
                    // Real ETH LVDS: freeze the actual B from Ethernet and use
                    // the sync-matched A for the diff so D reflects real ECU differences.
                    _pausedB = _latestB;
                    _pausedMatchedA = _matchedAForDiff ?? _pausedA;
                    _pausedD = AbsDiff(_pausedMatchedA, _pausedB);
                }
                else if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor)
                {
                    // Fallback (no ETH frames yet): mock B = A + delta
                    _pausedB = new Frame(_currentWidth, _currentHeight, ApplyValueDelta(_pausedA.Data, _bValueDelta), _pausedA.TimestampUtc);
                    _pausedMatchedA = _pausedA;
                    _pausedD = AbsDiff(_pausedA, _pausedB);
                }
                else
                {
                    _pausedB = _latestB ?? _pausedA;
                    _pausedMatchedA = _pausedA;
                    _pausedD = _latestD ?? AbsDiff(_pausedA, _pausedB);
                }
            }

            // Re-render once from the frozen snapshot to avoid any race with in-flight frame updates.
            RenderAll();

            if (BtnStart != null) BtnStart.Content = "Start";
            if (LblRunInfoA != null) LblRunInfoA.Text = "Paused";
            if (LblRunInfoB != null) LblRunInfoB.Text = "Paused";
            LblStatus.Text = "Paused.";

            ApplyButtonStates(isRunning: true, isPaused: true);
            UpdateOverlaysAll();
        }

        private void Resume()
        {
            _playback.Resume();
            _playback.PauseGate.Set();

            // Don't clear the sync ring on resume — it contains valid A frames
            // pushed during pause that correspond to B frames still in the ECU pipeline.
            _matchedAForDiff = null;

            lock (_frameLock)
            {
                _pausedA = null;
                _pausedB = null;
                _pausedD = null;
                _pausedMatchedA = null;
            }

            if (BtnStart != null) BtnStart.Content = "Pause";

            ApplyButtonStates(isRunning: true, isPaused: false);

            double shownFps = GetShownFps(avtpInFps: _playback.AvtpInFpsEma);
            bool isAviZero = _lastLoaded == LoadedSource.Avi && shownFps <= 0.0;
            if (LblRunInfoA != null)
                LblRunInfoA.Text = StatusFormatter.FormatRunInfoA(true, false, shownFps, isAviZero);
            if (LblRunInfoB != null)
                LblRunInfoB.Text = StatusFormatter.FormatRunInfoB(true, false, false, _playback.BFpsEma);

            LblStatus.Text = _playback.RunningStatusText ?? "Running.";

            ClearOverlay(Pane.A);
            ClearOverlay(Pane.B);
            ClearOverlay(Pane.D);
        }

        private void BtnLoadFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Load Files",
                Filter = "All supported (*.pgm;*.bmp;*.png;*.avi;*.pcap;*.pcapng;*.scene)|*.pgm;*.bmp;*.png;*.avi;*.pcap;*.pcapng;*.scene|Images (*.pgm;*.bmp;*.png)|*.pgm;*.bmp;*.png|AVI (*.avi)|*.avi|Captures (*.pcap;*.pcapng)|*.pcap;*.pcapng|Scenes (*.scene)|*.scene|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dlg.ShowDialog(this) != true)
                return;

            string path = dlg.FileName;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

            try
            {
                switch (ext)
                {
                    case ".pcap":
                    case ".pcapng":
                        LoadPcapPath(path);
                        break;

                    case ".avi":
                        LoadAvi(path);
                        break;

                    case ".scene":
                        LoadScene(path);
                        break;

                    case ".pgm":
                    case ".bmp":
                    case ".png":
                        LoadSingleImage(path);
                        break;

                    default:
                        MessageBox.Show($"Unsupported file type '{ext}'.", "Load error");
                        break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load file: {ex.Message}", "Load error");
            }
        }

        private void LoadPcapPath(string path)
        {
            PrepareForNewSource(clearAvtpFrame: false);
            _lastLoaded = LoadedSource.Pcap;
            _lastLoadedPcapPath = path;
            LblStatus.Text = SourceLoaderHelper.GetPcapStatusMessage();

            // Show Loop checkbox for PCAP files
            if (ChkLoopPlaying != null) ChkLoopPlaying.Visibility = Visibility.Visible;

            // Extract the first AVTP/RVF frame from the PCAP for preview on pane A
            var firstFrame = PcapAvtpRvfReplay.ExtractFirstFrame(path);
            if (firstFrame != null)
            {
                // AVTP frames are always 320×80; crop to current resolution if needed
                int w = _currentWidth;
                int h = _currentHeight;
                int avtpW = 320;

                if (w == avtpW && firstFrame.Length == w * h)
                {
                    _pgmFrame = firstFrame;
                }
                else
                {
                    // Linear copy: Nichia data is linearly packed in the first
                    // w*h bytes of the AVTP frame (CANoe linear padding convention).
                    var cropped = new byte[w * h];
                    int copyLen = Math.Min(w * h, firstFrame.Length);
                    Buffer.BlockCopy(firstFrame, 0, cropped, 0, copyLen);
                    _pgmFrame = cropped;
                }

                _lastLoaded = LoadedSource.Pcap;
                if (_playback.Cts == null || _playback.IsPaused) RenderOneFrameNow();
            }
        }

        private void LoadSingleImage(string path)
        {
            PrepareForNewSource(clearAvtpFrame: true);

            // Hide Loop checkbox for image files
            if (ChkLoopPlaying != null) ChkLoopPlaying.Visibility = Visibility.Collapsed;

            var result = _sourceLoader.LoadImage(path);
            _pgmFrame = result.Frame;
            _lvdsFrame84 = result.LvdsFrame;

            _lastLoaded = LoadedSource.Image;
            LblStatus.Text = result.StatusMessage;

            if (_playback.Cts == null || _playback.IsPaused) RenderOneFrameNow();
        }

        private void LoadAvi(string path)
        {
            PrepareForNewSource(clearAvtpFrame: true);
            _aviPlayer.LoopEnabled = _loopPlayingEnabled;
            _aviPlayer.Load(path);
            _lastLoaded = LoadedSource.Avi;
            LblStatus.Text = _aviPlayer.BuildStatusMessage();

            // Show Loop checkbox for AVI files
            if (ChkLoopPlaying != null) ChkLoopPlaying.Visibility = Visibility.Visible;

            if (_playback.Cts == null || _playback.IsPaused) RenderOneFrameNow();
        }

        private void PrepareForNewSource(bool clearAvtpFrame)
        {
            ClearAvi();
            _playback.ResetFpsEstimates();
            _scenePlayer.Clear();
            _lastLoadedPcapPath = null;
            if (clearAvtpFrame)
                _liveCapture.ClearAvtpFrame();
        }

        private void ClearAvi()
        {
            _aviPlayer.Close();
        }

        private void Start(int fps)
        {
            _txManager.StopBlackLoop();
            // Safety: if already running, stop first
            if (_playback.Cts != null)
                StopAll();

            // Init playback state and reset stats (includes CTS creation, running=true, paused=false)
            var ct = _playback.Start(fps);

            AppendDiagLog($"[start] mode={_modeOfOperation}, fps={fps}");

            // Reset runtime stats
            ApplyNoSignalUiState(noSignal: false);

            // Reset feed selection + reassembler state
            _liveCapture.ResetAll();
            

            // Default source label before first frame
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor)
            {
                if (_avtpLiveEnabled) _liveCapture.LastRvfSrcLabel = "Ethernet/AVTP";
                // Explicitly force the 'Waiting for signal...' state at first start
                EnterWaitingForSignalState();
            }
            else if (_lastLoaded == LoadedSource.Pcap)
            {
                _liveCapture.LastRvfSrcLabel = "PCAP";
            }

            // -------------------------------------------------
            // TX init (ONLY in Generator/Player mode)
            // -------------------------------------------------
            if (_modeOfOperation == ModeOfOperation.PlayerFromFiles)
            {
                ushort ethType = ParseHexUshort(_avtpEtherType, 0x22F0);
                byte stIdByte = ParseHexByte(_streamIdLastByte, 0x50);
                _txManager.Initialize(_avtpLiveDeviceHint, _srcMac, _dstMac,
                    _vlanId, _vlanPriority, ethType, stIdByte);
            }

            // -------------------------------------------------
            // Start loops
            // -------------------------------------------------
            if (_modeOfOperation == ModeOfOperation.PlayerFromFiles)
            {
                // Generator/Player:
                _ = Task.Run(() => GeneratorLoopAsync(fps, ct));
                _ = Task.Run(() => UiRefreshLoop(ct));

                LblStatus.Text = StatusFormatter.FormatPlayerRunning(fps, avtpEnabled: true);
                _playback.RunningStatusText = LblStatus.Text;
            }
            else
            {
                // AVTP Live Monitor:
                _ = Task.Run(() => UiRefreshLoop(ct));

                // Until the first frame arrives, show explicit waiting message.
                LblStatus.Text = StatusFormatter.FormatWaitingForSignal(GetDiagLogPath());

                _playback.WasWaitingForSignal = true;
            }

            // -------------------------------------------------
            // Auto start PCAP replay (ONLY in Player mode)
            // -------------------------------------------------
            if (_modeOfOperation == ModeOfOperation.PlayerFromFiles
                && _lastLoaded == LoadedSource.Pcap
                && !string.IsNullOrWhiteSpace(_lastLoadedPcapPath))
            {
                StartPcapReplay(_lastLoadedPcapPath);
            }

            // -------------------------------------------------
            // Start AVTP live source in AVTP Live mode
            // -------------------------------------------------
            bool allowLiveSources = _modeOfOperation == ModeOfOperation.AvtpLiveMonitor;
            if (allowLiveSources)
            {
                // Ethernet capture (Npcap)
                // NOTE: We always (re)start capture on Start in AVTP Live mode.
                // Reason: at app startup, the NIC selection / device hint may change after settings load,
                // and keeping an old capture instance can leave the UI stuck on the fallback image until Stop->Start.
                // Ensure we use the NIC currently selected in the UI (avoids slow/incorrect auto-pick).
                string? deviceHint = _nicSelector.GetSelectedDeviceName(CmbLiveNic) ?? _avtpLiveDeviceHint;

                // Persist the final hint so next Start uses the same interface.
                _avtpLiveDeviceHint = deviceHint;

                _liveCapture.StartEthernetCapture(deviceHint);
            }

            // Pane B real LVDS over Ethernet (Aurix GETH), managed by Start/Stop.
            // Use it for Nichia in both AVTP Live and Generator/Player modes.
            if (_currentDeviceType == LsmDeviceType.Nichia)
            {
                StartNichiaEthCapture();
            }
            
        }

        private void StartPcapReplay(string path)
        {
            _liveCapture.StartPcapReplay(
                path, 
                _playback.PauseGate,
                onComplete: () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_lastLoaded != LoadedSource.Pcap) return;

                        if (_loopPlayingEnabled && !string.IsNullOrWhiteSpace(_lastLoadedPcapPath))
                        {
                            // Loop: restart the PCAP replay from the beginning
                            StartPcapReplay(_lastLoadedPcapPath);
                        }
                        else
                        {
                            // No loop: stop
                            StopAll();
                        }
                    });
                },
                onError: msg =>
                {
                    Dispatcher.Invoke(() => { LblStatus.Text = "PCAP: error"; });
                });
        }

        private void StopRenderLoops()
        {
            _playback.Stop();

            if (LblRunInfoA != null)
                LblRunInfoA.Text = "";
            if (LblRunInfoB != null)
                LblRunInfoB.Text = "";

            ShowIdleGradient();
            LblStatus.Text = "Render stopped.";
        }

        private void StopAll()
        {
            if (_recordingManager.IsRecording) StopRecording();
            _playback.Resume();
            _playback.Stop();
            if (BtnStart != null) BtnStart.Content = "Start";

            lock (_frameLock)
            {
                _pausedA = null;
                _pausedB = null;
                _pausedD = null;
            }
            ResetSyncState();

            StopRenderLoops();

            // Clear AVTP stats labels
            if (LblAvtpInFps != null) LblAvtpInFps.Text = "";
            if (LblAvtpDropped != null) LblAvtpDropped.Text = "";

            // Stop all live capture sources
            _liveCapture.StopAll();
            StopNichiaEthCapture();

            // Ensure we don't remain paused after stopping.
            _playback.PauseGate.Set();

            // Stop should behave like a reset for file-backed sources.
            if (_lastLoaded == LoadedSource.Avi && _aviPlayer.IsLoaded)
            {
                _aviPlayer.Reset();
            }

            SaveUiSettings();

            if (_modeOfOperation == ModeOfOperation.PlayerFromFiles)
                LblStatus.Text = "Stopped.";
            else
                LblStatus.Text = StatusFormatter.FormatStoppedStatus(_liveCapture.LastRvfSrcLabel);

            ClearOverlay(Pane.A);
            ClearOverlay(Pane.B);
            ClearOverlay(Pane.D);

            // Restore button states: Load Files + Start enabled; others disabled
            ApplyButtonStates(false);
        }

        private static void AppendDiagLog(string message) => DiagnosticLogger.Log(message);
        private static string GetDiagLogPath() => DiagnosticLogger.LogPath;

        private void BtnLoadSeqA_Click(object sender, RoutedEventArgs e) => LoadSequenceImage(isA: true);
        private void BtnLoadSeqB_Click(object sender, RoutedEventArgs e) => LoadSequenceImage(isA: false);
        private void BtnSeqPrev_Click(object sender, RoutedEventArgs e) => StepSequence(-1);
        private void BtnSeqNext_Click(object sender, RoutedEventArgs e) => StepSequence(+1);

        private void LoadSequenceImage(bool isA)
        {
            ClearAvi();
            var dlg = new OpenFileDialog
            {
                Title = isA ? "Select Sequence A image" : "Select Sequence B image",
                Filter = "Images (*.pgm;*.bmp;*.png)|*.pgm;*.bmp;*.png|PGM (*.pgm)|*.pgm|BMP (*.bmp)|*.bmp|PNG (*.png)|*.png"
            };

            if (dlg.ShowDialog(this) != true)
                return;

            (int width, int height, byte[] data) img;
            try
            {
                img = ImageUtils.LoadImageAsGray8(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load image: {ex.Message}", "Load error");
                return;
            }

            if (img.width < _currentWidth || img.height < _currentHeight)
            {
                MessageBox.Show($"Expected at least {_currentWidth}x{_currentHeight}, got {img.width}x{img.height}.", "Size mismatch");
                return;
            }

            var cropped = ImageUtils.CropTopLeftGray8(img.data, img.width, img.height, _currentWidth, _currentHeight);

            if (isA)
            {
                _sequencePlayer.LoadA(dlg.FileName, cropped);
            }
            else
            {
                _sequencePlayer.LoadB(dlg.FileName, cropped);
            }

            // Switching sources: stop using previously replayed/received AVTP frame.
            _liveCapture.ClearAvtpFrame();
            _lastLoaded = LoadedSource.Sequence;
            _lastLoadedPcapPath = null;

            LblStatus.Text = _sequencePlayer.BuildStatusMessage();

            // If not running (or paused), update display immediately.
            if (_playback.Cts == null || _playback.IsPaused) RenderOneFrameNow();
        }

        private void StepSequence(int dir)
        {
            if (_lastLoaded == LoadedSource.Scene)
            {
                StepScene(dir);
                return;
            }

            if (_lastLoaded == LoadedSource.Avi)
            {
                StepAvi(dir);
                return;
            }

            if (!_sequencePlayer.HasAny)
            {
                LblStatus.Text = "Sequence: load Seq A and/or Seq B first.";
                return;
            }

            // Toggle between A and B.
            _sequencePlayer.Toggle();

            _liveCapture.ClearAvtpFrame();
            _lastLoaded = LoadedSource.Sequence;
            _lastLoadedPcapPath = null;

            LblStatus.Text = _sequencePlayer.BuildStatusMessage();

            // If not running (or paused), update display immediately.
            if (_playback.Cts == null || _playback.IsPaused) RenderOneFrameNow();
        }

        private void StepAvi(int dir)
        {
            if (!_aviPlayer.IsLoaded)
            {
                LblStatus.Text = "AVI: load an .avi first.";
                return;
            }

            LblStatus.Text = _aviPlayer.Step(dir);
            if (_playback.Cts == null || _playback.IsPaused) RenderOneFrameNow();
        }

        private void StepScene(int dir)
        {
            if (!_scenePlayer.IsLoaded)
            {
                LblStatus.Text = "Scene: load a .scene first.";
                return;
            }

            LblStatus.Text = _scenePlayer.Step(dir);
            if (_playback.Cts == null || _playback.IsPaused) RenderOneFrameNow();
        }

        private void LoadScene(string scenePath)
        {
            ClearAvi();
            _playback.ResetFpsEstimates();
            _liveCapture.ClearAvtpFrame();
            _lastLoadedPcapPath = null;

            // Hide Loop checkbox for scene files
            if (ChkLoopPlaying != null) ChkLoopPlaying.Visibility = Visibility.Collapsed;

            _scenePlayer.Load(scenePath);
            _lastLoaded = LoadedSource.Scene;

            LblStatus.Text = _scenePlayer.BuildStatusMessage();
            if (_playback.Cts == null || _playback.IsPaused) RenderOneFrameNow();
        }

        private byte[]? GetSequenceBytes() => _sequencePlayer.GetBytes();

        private void RenderOneFrameNow()
        {
            var now = DateTime.UtcNow;
            var aBytes = GetASourceBytes();
            var a = new Frame(_currentWidth, _currentHeight, aBytes, now);
            var bBytes = ApplyValueDelta(a.Data, _bValueDelta);
            var b = new Frame(_currentWidth, _currentHeight, bBytes, now);
            var d = AbsDiff(a, b);

            lock (_frameLock)
            {
                _latestA = a;
                _latestB = b;
                _latestD = d;

                // If we're paused, RenderAll uses the paused snapshots. Keep them in sync
                // so Prev/Next stepping updates the displayed image while staying "paused".
                if (_playback.IsPaused)
                {
                    _pausedA = a;
                    _pausedB = b;
                    _pausedD = d;
                    _pausedMatchedA = a;
                }
            }

            if (_playback.Cts != null)
            {
                // Playback is active — use full RenderAll which handles Live/paused/recording logic
                RenderAll();
            }
            else
            {
                // Not yet started — render preview on pane A only; B and D keep "Signal not available"
                BitmapUtils.Blit(_wbA, a.Data, a.Stride);
                if (NoSignalA != null) NoSignalA.Visibility = Visibility.Collapsed;
            }

            if (_playback.IsPaused)
                UpdateOverlaysAll();
        }

        // Choose source for A:
        // - depends on last loaded source (image vs PCAP)
        private byte[] GetASourceBytes()
        {
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor)
                return _liveCapture.HasAvtpFrame ? _liveCapture.AvtpFrame : _noSignalGrayFrame;

            return _lastLoaded switch
            {
                LoadedSource.Image => _pgmFrame,
                LoadedSource.Pcap => _liveCapture.HasAvtpFrame ? _liveCapture.AvtpFrame : _pgmFrame,
                LoadedSource.Avi => _aviPlayer.GetBytesAndUpdateIfNeeded(DateTime.UtcNow, _playback.IsPaused) ?? _idleGradientFrame,
                LoadedSource.Sequence => GetSequenceBytes() ?? _idleGradientFrame,
                LoadedSource.Scene => _scenePlayer.GetBytesAndUpdateIfNeeded(DateTime.UtcNow, _playback.IsPaused) ?? _idleGradientFrame,
                _ => _idleGradientFrame
            };
        }

        private async Task GeneratorLoopAsync(int fps, CancellationToken ct)
        {
            AppendDiagLog($"[generator] Entered GeneratorLoop, mode={_modeOfOperation}, fps={fps}");
            var period = TimeSpan.FromSeconds(1.0 / Math.Max(1, fps));
            var sw = Stopwatch.StartNew();
            var next = sw.Elapsed;
        
            while (!ct.IsCancellationRequested)
            {
                try { _playback.PauseGate.Wait(ct); }
                catch { break; }
        
                next += period;
        
                // A: either AVTP latest or PGM/AVI/Scene fallback (depending on what you loaded)
                var aBytes = GetASourceBytes();
                var a = new Frame(_currentWidth, _currentHeight, aBytes, DateTime.UtcNow);
                _playback.IncrementCountA();
                PushSyncFrame(a);
        
                // B: prefer real Nichia Ethernet LVDS when available; otherwise simulated LVDS (A + delta)
                Frame b;
                bool useRealEthB = false;
                if (_currentDeviceType == LsmDeviceType.Nichia
                    && _nichiaEthCapture != null
                    && _nichiaEthCapture.IsCapturing
                    && _nichiaEthCapture.FramesCompleted > 0)
                {
                    lock (_frameLock)
                    {
                        if (_latestB != null
                            && _latestB.Width == _currentWidth
                            && _latestB.Height == _currentHeight)
                        {
                            b = _latestB;
                            useRealEthB = true;
                        }
                        else
                        {
                            var bBytesFallback = ApplyValueDelta(a.Data, _bValueDelta);
                            b = new Frame(_currentWidth, _currentHeight, bBytesFallback, DateTime.UtcNow);
                        }
                    }
                }
                else
                {
                    var bBytes = ApplyValueDelta(a.Data, _bValueDelta);
                    b = new Frame(_currentWidth, _currentHeight, bBytes, DateTime.UtcNow);
                }

                if (!useRealEthB)
                    _playback.IncrementCountB();
        
                // D: diff — use frame-matched A when real ETH B is active
                var genMatchedA = _matchedAForDiff; // snapshot for thread safety
                Frame diffA = (useRealEthB && genMatchedA != null) ? genMatchedA : a;
                var d = AbsDiff(diffA, b);
        
                // -----------------------------
                // AVTP Ethernet TX (ONLY PlayerFromFiles)
                // -----------------------------
                if (_modeOfOperation == ModeOfOperation.PlayerFromFiles)
                {
                    try
                    {
                        await _txManager.SendFrameAsync(a.Data, ct);
                    }
                    catch (OperationCanceledException) { break; }
                }

                // Detect AVI end-of-file when loop is disabled → auto-stop
                if (_lastLoaded == LoadedSource.Avi && _aviPlayer.IsAtEnd)
                {
                    _ = Dispatcher.BeginInvoke(() => StopAll());
                    break;
                }
        
                // If pause was activated exactly during the iteration, do not publish frame
                if (_playback.IsPaused || !_playback.PauseGate.IsSet)
                {
                    if (_playback.IncrementLateFramesSkipped() == 1)
                        AppendDiagLog("[ui] generator skipped publish due to pause race (late frame)");
                    continue;
                }
        
                lock (_frameLock)
                {
                    _latestA = a;
                    if (!useRealEthB)
                        _latestB = b;
                    _latestD = d;
                }
                _playback.IncrementCountD();
        
                // pace
                var now = sw.Elapsed;
                var sleep = next - now;
                if (sleep > TimeSpan.Zero)
                {
                    try { Task.Delay(sleep, ct).Wait(ct); } catch { }
                }
                else
                {
                    next = now;
                }
            }
        }

        private void UiRefreshLoop(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var period = TimeSpan.FromMilliseconds(16);
            var next = sw.Elapsed;

            while (!ct.IsCancellationRequested)
            {
                try { _playback.PauseGate.Wait(ct); }
                catch { break; }

                next += period;
                try
                {
                    Dispatcher.Invoke(RenderAll);
                }
                catch
                {
                    // ignore shutdown races
                    break;
                }

                var now = sw.Elapsed;
                var sleep = next - now;
                if (sleep > TimeSpan.Zero)
                {
                    try { Task.Delay(sleep, ct).Wait(ct); } catch { }
                }
                else next = now;
            }
        }

        private void RenderAll()
        {
            // When stopped/startup, keep the panes in "Signal not available" state and
            // disable compare/dead-pixel processing.
            if (_playback.Cts == null)
            {
                RenderNoSignalFrames();
                ApplyNoSignalUiState(noSignal: true);
                return;
            }

            // AVTP Live: if CANoe (or the source) stops while we're still Running, clear the
            // last frame and fall back to the no-signal "Waiting for signal" UI.
            if (_playback.IsRunning
                && _modeOfOperation == ModeOfOperation.AvtpLiveMonitor
                && _liveCapture.HasAvtpFrame
                && _liveCapture.LastAvtpFrameUtc != DateTime.MinValue
                && (DateTime.UtcNow - _liveCapture.LastAvtpFrameUtc) > TimeSpan.FromSeconds(LiveSignalLostTimeoutSec))
            {
                EnterWaitingForSignalState();
            }

            // In AVTP Live mode, while waiting for the first frame, keep showing "Signal not available".
            if (ShouldShowNoSignalWhileRunning())
            {
                RenderNoSignalFrames();
                ApplyNoSignalUiState(noSignal: true);

                if (!_playback.WasWaitingForSignal && LblStatus != null)
                {
                    LblStatus.Text = StatusFormatter.FormatWaitingForSignal(GetDiagLogPath());
                    _playback.RunningStatusText = LblStatus.Text;
                }
                _playback.WasWaitingForSignal = true;

                UpdateFpsLabels();
                return;
            }

            _playback.WasWaitingForSignal = false;

            // Ensure A reflects newest source even if GeneratorLoop is stopped
            Frame a;
            Frame b;
            Frame d;
            lock (_frameLock)
            {
                if (_playback.IsPaused && _pausedA != null)
                {
                    a = _pausedA;
                    b = _pausedB ?? a;
                    d = _pausedD ?? AbsDiff(a, b);
                }
                else
                {
                    a = _latestA ?? new Frame(_currentWidth, _currentHeight, GetASourceBytes(), DateTime.UtcNow);
                    b = _latestB ?? a;
                    d = _latestD ?? AbsDiff(a, b);
                }
            }

            // In AVTP Live mode:
            //   - If LVDS capture is active and has a frame, use the real LVDS data for B
            //   - Otherwise, derive B from A using the UI delta (mock LVDS)
            // Snapshot the matched-A reference ONCE to avoid race with HandleLvdsFrameReady.
            // When paused, use the frozen _pausedMatchedA that was saved at pause time.
            var matchedA = _playback.IsPaused ? _pausedMatchedA : _matchedAForDiff;
            bool hasRealEthB = _nichiaEthCapture != null
                               && _nichiaEthCapture.IsCapturing
                               && _nichiaEthCapture.FramesCompleted > 0;

            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor && !_playback.IsPaused)
            {
                if (hasRealEthB)
                {
                    // B already comes from HandleLvdsFrameReady() via _latestB.
                    // Keep current b from frame cache (do not overwrite with mock data).
                }
                else if (_lvdsManager.IsCapturing && _lvdsManager.HasFrame)
                {
                    // Real LVDS frame for Pane B
                    var lvdsData = ImageUtils.Copy(_lvdsManager.LvdsFrame);
                    b = new Frame(_currentWidth, _currentHeight, lvdsData, _lvdsManager.LastFrameUtc);
                }
                else
                {
                    // Fallback: mock LVDS (A + delta)
                    b = new Frame(_currentWidth, _currentHeight, ApplyValueDelta(a.Data, _bValueDelta), a.TimestampUtc);
                }
            }

            // B post-processing (forced dead pixel + optional compensation)
            // Use the sync-matched A when available so dark-pixel detection
            // aligns with the same A reference used for the diff comparison.
            Frame aForPostProcess = (hasRealEthB && matchedA != null) ? matchedA : a;
            b = ApplyBPostProcessing(aForPostProcess, b);

            // For diff rendering, use the matched A frame (sync-corrected) when ETH B is active,
            // so the comparison reflects actual ECU processing differences, not animation timing.
            byte[] diffARef = (hasRealEthB && matchedA != null) ? matchedA.Data : a.Data;

            BitmapUtils.Blit(_wbA, a.Data, a.Stride);
            BitmapUtils.Blit(_wbB, b.Data, b.Stride);
            DiffRenderer.RenderCompareToBgr(_diffBgr, diffARef, b.Data, _currentWidth, _currentHeight, _diffThreshold,
                _zeroZeroIsWhite,
                out var minDiff, out var maxDiff, out var meanDiff,
                out var maxAbsDiff, out var meanAbsDiff, out var aboveDeadband,
                out var totalDarkPixels);
            BitmapUtils.Blit(_wbD, _diffBgr, _currentWidth * 3);

            // Record what we render (A/B in Gray8; D in Bgr24). Diff buffer is reused, so copy it.
            if (_recordingManager.IsRecording && !_playback.IsPaused && _playback.Cts != null)
            {
                var dCopy = new byte[_diffBgr.Length];
                Buffer.BlockCopy(_diffBgr, 0, dCopy, 0, dCopy.Length);
                _recordingManager.TryEnqueueFrame(a.Data, b.Data, dCopy);
            }

            if (LblDiffStats != null)
                LblDiffStats.Text = StatusFormatter.FormatDiffStats(maxDiff, minDiff, meanAbsDiff, aboveDeadband, totalDarkPixels, _lastMatchNcc);

            // Per-pane no-signal: when AVTP hasn't arrived but ETH pane B is valid,
            // show "Signal not available" only on pane A.
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor && !_liveCapture.HasAvtpFrame)
            {
                if (NoSignalA != null) NoSignalA.Visibility = Visibility.Visible;
                if (NoSignalB != null) NoSignalB.Visibility = Visibility.Collapsed;
                if (NoSignalD != null) NoSignalD.Visibility = Visibility.Collapsed;
            }
            else
            {
                ApplyNoSignalUiState(noSignal: false);
            }
            UpdateFpsLabels();
        }

        private void UpdateFpsLabels()
        {
            if (!_playback.TryUpdateFpsEstimates(out double fpsA, out double fpsB, out double fpsIn))
                return;

            bool noSignal = ShouldShowNoSignalWhileRunning();
            bool isRunning = _playback.Cts != null;
            bool isPaused = _playback.IsPaused;

            if (LblAvtpInFps != null) 
                LblAvtpInFps.Text = $"{_playback.AvtpInFpsEma:F1} fps";
            if (LblAvtpDropped != null) 
                LblAvtpDropped.Text = StatusFormatter.FormatAvtpDropped(
                    _playback.CountAvtpDropped, _playback.CountAvtpSeqGapFrames, 
                    _playback.CountAvtpIncomplete, _playback.SumAvtpSeqGaps);

            if (LblRunInfoA != null)
            {
                double shownFps = noSignal ? 0.0 : GetShownFps(_playback.AvtpInFpsEma);
                bool isAviZero = _lastLoaded == LoadedSource.Avi && shownFps <= 0.0;
                LblRunInfoA.Text = StatusFormatter.FormatRunInfoA(isRunning, isPaused, shownFps, isAviZero);

                // Update lateSkip in status
                if (isRunning && !isPaused && _playback.CountLateFramesSkipped > 0 && LblStatus != null)
                    LblStatus.Text = StatusFormatter.UpdateLateSkipInStatus(LblStatus.Text ?? "", _playback.CountLateFramesSkipped);
            }

            if (LblRunInfoB != null)
            {
                double paneBFps = 0.0;
                if (!noSignal)
                {
                    if (_nichiaEthCapture != null && _nichiaEthCapture.IsCapturing && _nichiaEthCapture.FpsEma > 0.0)
                        paneBFps = _nichiaEthCapture.FpsEma;
                    else if (_lvdsManager.IsCapturing && _lvdsManager.FpsEma > 0.0)
                        paneBFps = _lvdsManager.FpsEma;
                    else
                        paneBFps = _playback.BFpsEma;
                }

                LblRunInfoB.Text = StatusFormatter.FormatRunInfoB(isRunning, isPaused, noSignal, paneBFps);
            }
        }

        private double GetShownFps(double avtpInFps)
        {
            // In live-monitor mode, prefer the measured AVTP-in fps over the user-entered target fps.
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor && avtpInFps > 0.0)
                return avtpInFps;

            return _lastLoaded switch
            {
                LoadedSource.Pcap => avtpInFps,
                // For AVI playback, show ONLY the "source fps" (frame content changes/sec).
                // Do not fall back to the AVI header fps (often the fixed record fps like 100).
                LoadedSource.Avi => _aviPlayer.SourceFpsEma,
                _ => _playback.TargetFps
            };
        }

        private Frame AbsDiff(Frame a, Frame b) => ImageUtils.AbsDiff(a, b, _currentWidth, _currentHeight);
        private static byte[] ApplyValueDelta(byte[] src, int delta) => ImageUtils.ApplyValueDelta(src, delta);

        // ─── Frame synchronization helpers ──────────────────────────────────

        /// <summary>
        /// Stores an A frame in the sync ring buffer for later matching with B frames.
        /// </summary>
        private void PushSyncFrame(Frame a)
        {
            int idx = Interlocked.Increment(ref _syncRingHead) & (SyncRingSize - 1);
            _syncRing[idx] = a;

            // Pre-compute per-pixel variance for instant flat-frame lookups.
            var data = a.Data;
            int n = data.Length;
            long sum = 0, sumSq = 0;
            for (int j = 0; j < n; j++)
            {
                int v = data[j];
                sum += v;
                sumSq += (long)v * v;
            }
            _syncRingVarPerPx[idx] = ((double)n * sumSq - (double)sum * sum) / ((double)n * n);
        }

        /// <summary>
        /// Finds the A frame in the sync ring buffer that best matches the given
        /// B frame pixel data.  Uses Normalized Cross-Correlation (NCC) as the
        /// matching metric.  NCC is invariant to both additive and multiplicative
        /// brightness transforms (e.g. ECU thermal derating where B ≈ k·A + c),
        /// so it stays near 1.0 for the correct match regardless of derating level.
        ///
        /// NCC = cov(A,B) / (σA · σB), computed in one pass as:
        ///   (N·ΣAB − ΣA·ΣB) / √((N·ΣA² − (ΣA)²)(N·ΣB² − (ΣB)²))
        ///
        /// When B has very low variance (uniform frame, no bar/structure), NCC is
        /// numerically unstable (0/0).  In that case we skip the correlation and
        /// return the most recent A frame from the ring, which is always correct
        /// because when B has no structure, A has already transitioned to no
        /// structure as well (A leads B by the ECU round-trip latency).
        ///
        /// Cost: ~5.3 M int-ops per call at 256×64 × 128 entries — sub-ms.
        /// </summary>
        private Frame? FindBestMatchA(byte[] bData, int expectedLen)
        {
            if (expectedLen == 0) return null;

            // Pre-compute B statistics (constant across all A candidates).
            long bSum = 0;
            long bSumSq = 0;
            for (int j = 0; j < expectedLen; j++)
            {
                int b = bData[j];
                bSum += b;
                bSumSq += (long)b * b;
            }
            double bNVar = (double)expectedLen * bSumSq - (double)bSum * bSum;

            // ── Flat-B shortcut ──────────────────────────────────────────────
            // Per-pixel variance = bNVar / N².  When std-dev < 5 gray levels
            // the frame has no meaningful spatial structure (no bar/pattern).
            // NCC would degenerate to noise/noise → random results that may
            // pick an old A frame with a bar still in the ring.  Instead, use
            // the most recent A directly.
            const double FlatStdDevThreshold = 5.0;
            double bVarPerPixel = bNVar / ((double)expectedLen * expectedLen);
            if (bVarPerPixel < FlatStdDevThreshold * FlatStdDevThreshold)
            {
                // B is flat → find the most recent A that is ALSO flat.
                // This avoids picking a latest-A that already has the bar
                // (A leads B by ECU latency) when B hasn't shown it yet.
                _lastMatchNcc = 1.0; // no structure → trivially matched
                return GetMostRecentFlatSyncFrame(expectedLen, FlatStdDevThreshold * FlatStdDevThreshold);
            }

            // ── Full NCC matching for structured frames ──────────────────────
            Frame? best = null;
            double bestNcc = double.MinValue;

            for (int i = 0; i < SyncRingSize; i++)
            {
                var f = _syncRing[i];
                if (f == null || f.Data.Length != expectedLen) continue;

                var aData = f.Data;
                long aSum = 0;
                long aSumSq = 0;
                long abSum = 0;
                for (int j = 0; j < expectedLen; j++)
                {
                    int a = aData[j];
                    aSum += a;
                    aSumSq += (long)a * a;
                    abSum += (long)a * bData[j];
                }

                double aNVar = (double)expectedLen * aSumSq - (double)aSum * aSum;

                double ncc;
                if (aNVar < 1.0)
                {
                    // A is constant but B has structure → no meaningful correlation.
                    // Penalize so we prefer an A that also has structure.
                    ncc = -1.0;
                }
                else
                {
                    double covN = (double)expectedLen * abSum - (double)aSum * bSum;
                    ncc = covN / Math.Sqrt(aNVar * bNVar);
                }

                if (ncc > bestNcc)
                {
                    bestNcc = ncc;
                    best = f;
                }
            }

            // ── Quality gate ─────────────────────────────────────────────────
            // If the best NCC is still poor (< 0.5), the match is unreliable.
            // Fall back to the most recent A as a safe default.
            if (bestNcc < 0.5)
            {
                var recent = GetMostRecentSyncFrame(expectedLen);
                if (recent != null)
                {
                    _lastMatchNcc = bestNcc; // report actual NCC for diagnostics
                    return recent;
                }
            }

            _lastMatchNcc = (bestNcc > double.MinValue) ? bestNcc : double.NaN;
            return best;
        }

        /// <summary>
        /// Returns the most recently pushed A frame from the sync ring buffer.
        /// </summary>
        private Frame? GetMostRecentSyncFrame(int expectedLen)
        {
            int head = _syncRingHead;
            for (int k = 0; k < SyncRingSize; k++)
            {
                int idx = (head - k) & (SyncRingSize - 1);
                var f = _syncRing[idx];
                if (f != null && f.Data.Length == expectedLen)
                    return f;
            }
            return null;
        }

        /// <summary>
        /// Returns the most recently pushed A frame whose per-pixel variance
        /// is below <paramref name="varThreshold"/>, i.e. also a flat frame.
        /// Falls back to the most recent frame of any variance if no flat A
        /// is found (ring fully populated with structured frames).
        /// </summary>
        private Frame? GetMostRecentFlatSyncFrame(int expectedLen, double varThreshold)
        {
            int head = _syncRingHead;
            Frame? fallback = null;
            for (int k = 0; k < SyncRingSize; k++)
            {
                int idx = (head - k) & (SyncRingSize - 1);
                var f = _syncRing[idx];
                if (f == null || f.Data.Length != expectedLen) continue;
                fallback ??= f; // remember most recent regardless of variance
                if (_syncRingVarPerPx[idx] < varThreshold)
                    return f;
            }
            return fallback;
        }

        /// <summary>
        /// Resets the frame synchronization state (call on Start/Stop).
        /// </summary>
        private void ResetSyncState()
        {
            Array.Clear(_syncRing);
            Array.Clear(_syncRingVarPerPx);
            _syncRingHead = 0;
            _matchedAForDiff = null;
            _lastMatchNcc = double.NaN;
        }

        private void ImgA_MouseMove(object sender, MouseEventArgs e) => ShowPixelInfo(e, GetDisplayedFrameForPane(Pane.A), LblA);
        private void ImgB_MouseMove(object sender, MouseEventArgs e) => ShowPixelInfo(e, GetDisplayedFrameForPane(Pane.B), LblB);
        private void ImgD_MouseMove(object sender, MouseEventArgs e) => ShowPixelInfoDiff(e, LblD);

        private void ImgA_MouseLeave(object sender, MouseEventArgs e) => LblA.Text = "";
        private void ImgB_MouseLeave(object sender, MouseEventArgs e) => LblB.Text = "";
        private void ImgD_MouseLeave(object sender, MouseEventArgs e) => LblD.Text = "";

        private static Pane PaneFromSender(object sender)
        {
            if (sender is System.Windows.Controls.Image img)
            {
                return img.Name switch
                {
                    "ImgA" => Pane.A,
                    "ImgB" => Pane.B,
                    _ => Pane.D,
                };
            }
            return Pane.A;
        }

        private void Img_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not System.Windows.Controls.Image img) return;

            var pane = PaneFromSender(sender);
            var parent = img.Parent as IInputElement ?? img;

            if (_zoomPan.HandleMouseWheel((int)pane, e, parent))
            {
                e.Handled = true;
                if (_playback.IsPaused)
                    RequestOverlayUpdate(pane);
            }
        }

        private void Img_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.Image img) return;
            var pane = PaneFromSender(sender);

            if (_zoomPan.StartPan((int)pane, e, this, img))
            {
                e.Handled = true;
                if (e.ClickCount >= 2 && _playback.IsPaused)
                    UpdateOverlay(pane);
            }
        }

        private void Img_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_zoomPan.IsPanning) return;
            if (sender is System.Windows.Controls.Image img)
                _zoomPan.StopPan(img);
            e.Handled = true;
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);
            if (!_zoomPan.IsPanning) return;

            _zoomPan.UpdatePan(e, this);

            if (_playback.IsPaused)
                RequestOverlayUpdate((Pane)_zoomPan.PanningPaneIndex);
        }

        private void ShowPixelInfo(MouseEventArgs e, Frame? f, System.Windows.Controls.TextBlock lbl)
        {
            if (_playback.Cts == null || ShouldShowNoSignalWhileRunning()) { lbl.Text = ""; return; }
            if (f == null) { lbl.Text = ""; return; }

            var img = e.Source as System.Windows.Controls.Image;
            if (img == null) { lbl.Text = ""; return; }
            var pane = PaneFromSender(img);
            var (_, ovr, _) = GetPaneVisuals(pane);

            if (!_pixelInspector.TryGetPixelXY(e, f, img, ovr, out int x, out int y)) { lbl.Text = ""; return; }

            byte v = f.Data[y * f.Stride + x];
            lbl.Text = PixelInspector.FormatGrayscaleInfo(x, y, v, f.Width);
        }

        private void ShowPixelInfoDiff(MouseEventArgs e, System.Windows.Controls.TextBlock lbl)
        {
            if (_playback.Cts == null || ShouldShowNoSignalWhileRunning()) { lbl.Text = ""; return; }

            var a = GetDisplayedFrameForPane(Pane.A);
            var b = GetDisplayedFrameForPane(Pane.B);
            var refFrame = a ?? b;
            if (refFrame == null) { lbl.Text = ""; return; }

            var img = e.Source as System.Windows.Controls.Image;
            if (img == null) { lbl.Text = ""; return; }
            var pane = PaneFromSender(img);
            var (_, ovr, _) = GetPaneVisuals(pane);

            if (!_pixelInspector.TryGetPixelXY(e, refFrame, img, ovr, out int x, out int y)) { lbl.Text = ""; return; }

            int idx = (y * refFrame.Stride) + x;
            byte av = (a != null && idx < a.Data.Length) ? a.Data[idx] : (byte)0;
            byte bv = (b != null && idx < b.Data.Length) ? b.Data[idx] : (byte)0;
            lbl.Text = PixelInspector.FormatDiffInfo(x, y, av, bv, refFrame.Width);
        }

        /// <summary>
        /// Parses a hex string like "0x22F0" or "22F0" to ushort. Returns fallback on failure.
        /// </summary>
        private static ushort ParseHexUshort(string? text, ushort fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);
            return ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : fallback;
        }

        /// <summary>
        /// Parses a hex string like "0x50" or "50" to byte. Returns fallback on failure.
        /// </summary>
        private static byte ParseHexByte(string? text, byte fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);
            return byte.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : fallback;
        }
    }

}
