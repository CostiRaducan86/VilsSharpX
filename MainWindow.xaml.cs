using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using SharpPcap;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
namespace VideoStreamPlayer
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
        private const int W = 320;
        private const int H_ACTIVE = 80;
        private const int H_LVDS = 84;
        private const int META_LINES = 4; // bottom 4 (unused for now)

        // Playback state management - delegated to PlaybackStateManager
        private readonly PlaybackStateManager _playback = new(FpsEstimationWindowSec, FpsEmaAlpha);

        // Live capture management - delegated to LiveCaptureManager  
        private readonly LiveCaptureManager _liveCapture;

        // Recording (AVI) - delegated to RecordingManager
        private readonly RecordingManager _recordingManager = new(W, H_ACTIVE);

        private volatile int _bValueDelta;

        private volatile byte _diffThreshold;
        private readonly byte[] _diffBgr = new byte[W * H_ACTIVE * 3];

        // If >0, forces B[pixel_ID] to 0 (simulated dead pixel). pixel_ID is 1..(W*H_ACTIVE).
        private int _forcedDeadPixelId;

        private volatile bool _darkPixelCompensationEnabled = false;

        private volatile bool _zeroZeroIsWhite = false;

        private bool _isLoadingSettings;
        private readonly string _settingsPath = AppSettingsStore.GetSettingsPath();

        // Live AVTP capture settings (Ethernet via SharpPcap)
        private bool _avtpLiveEnabled = true;
        private string? _avtpLiveDeviceHint;
        private bool _avtpLiveUdpEnabled = true;

        private ModeOfOperation _modeOfOperation = ModeOfOperation.AvtpLiveMonitor;

        // Fallback image / generator base
        private byte[] _pgmFrame = new byte[W * H_ACTIVE];

        // Always-available idle pattern so we can distinguish "no render" from black/loaded frames
        private readonly byte[] _idleGradientFrame = new byte[W * H_ACTIVE];

        // No-signal background (mid gray) rendered under the overlay.
        private readonly byte[] _noSignalGrayFrame = new byte[W * H_ACTIVE];
        private readonly byte[] _noSignalGrayBgr = new byte[W * H_ACTIVE * 3];

        // Optional LVDS source image (top-left 320x84). If loaded, B can be driven from this later.
        private byte[]? _lvdsFrame84;

        private LoadedSource _lastLoaded = LoadedSource.None;
        private string? _lastLoadedPcapPath;

        // Source players (extracted to separate classes)
        private readonly SequencePlayer _sequencePlayer = new(W, H_ACTIVE);
        private readonly ScenePlayer _scenePlayer = new(W, H_ACTIVE);
        private readonly AviSourcePlayer _aviPlayer = new(W, H_ACTIVE, FpsEstimationWindowSec, FpsEmaAlpha);

        private Frame? _latestA;
        private Frame? _latestB;
        private Frame? _latestD;

        // Snapshot used while paused so overlays/inspectors match the frozen image.
        private Frame? _pausedA;
        private Frame? _pausedB;
        private Frame? _pausedD;

        private readonly object _frameLock = new();

        // Zoom/pan (per pane)
        private readonly ScaleTransform _zoomA = new(1.0, 1.0);
        private readonly TranslateTransform _panA = new(0.0, 0.0);
        private readonly ScaleTransform _zoomB = new(1.0, 1.0);
        private readonly TranslateTransform _panB = new(0.0, 0.0);
        private readonly ScaleTransform _zoomD = new(1.0, 1.0);
        private readonly TranslateTransform _panD = new(0.0, 0.0);

        private const double OverlayMinZoom = 8.0;
        private const int OverlayMaxLabels = 40000;
        private const double OverlayTextPx = 10.0; // on-screen size (Cassandra-like)

        private readonly DispatcherTimer _overlayTimerA;
        private readonly DispatcherTimer _overlayTimerB;
        private readonly DispatcherTimer _overlayTimerD;

        private bool _overlayPendingA;
        private bool _overlayPendingB;
        private bool _overlayPendingD;

        private int _overlayStepA;
        private int _overlayStepB;
        private int _overlayStepD;

        private bool _isPanning;
        private Pane _panningPane;
        private Point _panStart;
        private double _panStartX;
        private double _panStartY;

        private readonly WriteableBitmap _wbA = BitmapUtils.MakeGray8(W, H_ACTIVE);
        private readonly WriteableBitmap _wbB = BitmapUtils.MakeGray8(W, H_ACTIVE);
        private readonly WriteableBitmap _wbD = BitmapUtils.MakeBgr24(W, H_ACTIVE);

        private CancellationTokenSource? _saveFeedbackCts;

        // --- PlayerFromFiles: when STOP, keep AVTP alive by sending BLACK frames ---
        private CancellationTokenSource? _txBlackCts;
        private Task? _txBlackTask;
        private static readonly byte[] _black320x80 = new byte[320 * 80];

        private void StartBlackTxLoop(int fps)
        {
            StopBlackTxLoop();

            if (_tx == null) return;
            if (fps <= 0) fps = 100;

            _txBlackCts = new CancellationTokenSource();
            var ct = _txBlackCts.Token;

            _txBlackTask = Task.Run(async () =>
            {
                var period = TimeSpan.FromMilliseconds(1000.0 / fps);

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Send one BLACK frame (20 packets) in the same AVTP format
                        await _tx.SendFrame320x80Async(_black320x80, ct);
                        await Task.Delay(period, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        AppendUdpLog($"[avtp-tx] BLACK loop error: {ex.GetType().Name}: {ex.Message}");
                        break;
                    }
                }
            }, ct);

            AppendUdpLog($"[avtp-tx] BLACK loop started @ {fps} fps");
        }

        private void StopBlackTxLoop()
        {
            try
            {
                _txBlackCts?.Cancel();
                _txBlackCts?.Dispose();
            }
            catch { }
            finally
            {
                _txBlackCts = null;
                _txBlackTask = null;
            }
        }

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

        private AvtpRvfTransmitter? _tx;

        public MainWindow()
        {
            // Initialize LiveCaptureManager before anything else
            _liveCapture = new LiveCaptureManager(W, H_ACTIVE, FpsEstimationWindowSec * 2.5, AppendUdpLog);

            // XAML can trigger SelectionChanged/TextChanged during InitializeComponent.
            // Treat that phase like settings-load to avoid running app logic before controls/bitmaps are wired.
            _isLoadingSettings = true;
            InitializeComponent();
            ImgA.Source = _wbA;
            ImgB.Source = _wbB;
            ImgD.Source = _wbD;
            AttachZoomPanTransforms();
            _overlayTimerA = MakeOverlayTimer(Pane.A);
            _overlayTimerB = MakeOverlayTimer(Pane.B);
            _overlayTimerD = MakeOverlayTimer(Pane.D);

            // default pattern: horizontal gradient (fallback)
            for (int y = 0; y < H_ACTIVE; y++)
                for (int x = 0; x < W; x++)
                    _idleGradientFrame[y * W + x] = (byte)(x * 255 / (W - 1));

            // no-signal pattern: flat mid-gray
            for (int i = 0; i < _noSignalGrayFrame.Length; i++)
                _noSignalGrayFrame[i] = 0x80;
            for (int i = 0; i < _noSignalGrayBgr.Length; i++)
                _noSignalGrayBgr[i] = 0x80;

            Buffer.BlockCopy(_idleGradientFrame, 0, _pgmFrame, 0, _pgmFrame.Length);

            if (LblDiffThr != null) LblDiffThr.Text = "0";

            _isLoadingSettings = false;
        }

        private bool ShouldShowNoSignalWhileRunning()
        {
            if (_playback.Cts == null) return false;
            if (!_playback.IsRunning) return false;
            if (_modeOfOperation != ModeOfOperation.AvtpLiveMonitor) return false;

            // In AVTP Live mode, keep panes in "Signal not available" until first valid frame arrives.
            return !_liveCapture.HasAvtpFrame;
        }

        private void EnterWaitingForSignalState()
        {
            var prevFeed = GetActiveAvtpFeed();
            var lastAgeMs = _liveCapture.LastAvtpFrameUtc == DateTime.MinValue
                ? double.NaN
                : (DateTime.UtcNow - _liveCapture.LastAvtpFrameUtc).TotalMilliseconds;

            AppendUdpLog(
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

        private void RenderNoSignalFrames()
        {
            BitmapUtils.Blit(_wbA, _noSignalGrayFrame, W);
            BitmapUtils.Blit(_wbB, _noSignalGrayFrame, W);
            BitmapUtils.Blit(_wbD, _noSignalGrayBgr, W * 3);
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

        private void AttachZoomPanTransforms()
        {
            var tgA = new TransformGroup { Children = new TransformCollection { _zoomA, _panA } };
            var tgB = new TransformGroup { Children = new TransformCollection { _zoomB, _panB } };
            var tgD = new TransformGroup { Children = new TransformCollection { _zoomD, _panD } };

            ImgA.RenderTransform = tgA;
            ImgB.RenderTransform = tgB;
            ImgD.RenderTransform = tgD;

            // Overlay is rendered in screen space (no transform) and we compute positions using zoom/pan.
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
            return pane switch
            {
                Pane.A => (ImgA, OvrA, _zoomA),
                Pane.B => (ImgB, OvrB, _zoomB),
                _ => (ImgD, OvrD, _zoomD),
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
                        aLive = new Frame(W, H_ACTIVE, copy, _liveCapture.LastAvtpFrameUtc == DateTime.MinValue ? DateTime.UtcNow : _liveCapture.LastAvtpFrameUtc);
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
                            bLive = new Frame(W, H_ACTIVE, bBytes, aLive.TimestampUtc);
                        }
                        else
                        {
                            var copyB = ImageUtils.Copy(_liveCapture.AvtpFrame);
                            var bBytes = ApplyValueDelta(copyB, _bValueDelta);
                            bLive = new Frame(W, H_ACTIVE, bBytes, _liveCapture.LastAvtpFrameUtc == DateTime.MinValue ? DateTime.UtcNow : _liveCapture.LastAvtpFrameUtc);
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
                            var aFallback = new Frame(W, H_ACTIVE, GetASourceBytes(), DateTime.UtcNow);
                            dLive = AbsDiff(aFallback, bLive);
                        }
                        else if (aLive != null && bLive == null)
                        {
                            var bFallbackBytes = ApplyValueDelta(aLive.Data, _bValueDelta);
                            var bFallback = new Frame(W, H_ACTIVE, bFallbackBytes, aLive.TimestampUtc);
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
            return DarkPixelCompensation.ApplyBPostProcessing(a, b, W, H_ACTIVE, 
                _darkPixelCompensationEnabled, Volatile.Read(ref _forcedDeadPixelId));
        }

        private void ClearOverlay(Pane pane)
        {
            var (_, ovr, _) = GetPaneVisuals(pane);
            ovr.Children.Clear();
            ovr.Visibility = Visibility.Collapsed;

            StopOverlayTimer(pane);

            switch (pane)
            {
                case Pane.A: _overlayStepA = 0; break;
                case Pane.B: _overlayStepB = 0; break;
                default: _overlayStepD = 0; break;
            }
        }

        private static int ComputeOverlayStep(double scale, double zoomScale, int width, int height)
        {
            double pixelOnScreen = scale * zoomScale;
            const double MinPixelForAll = 14.0;
            int step = (int)Math.Max(1.0, Math.Ceiling(MinPixelForAll / Math.Max(1e-6, pixelOnScreen)));
            while (((width + step - 1) / step) * ((height + step - 1) / step) > OverlayMaxLabels)
                step++;
            return step;
        }

        private bool ShouldRegenerateOverlay(Pane pane)
        {
            if (!_playback.IsPaused) return false;
            var (img, ovr, zoom) = GetPaneVisuals(pane);
            if (img == null || ovr == null) return false;
            if (zoom.ScaleX < OverlayMinZoom) return false;

            // If overlay is hidden, we need to create it.
            if (ovr.Visibility != Visibility.Visible) return true;

            Frame? fBase;
            if (pane == Pane.D)
            {
                lock (_frameLock)
                {
                    // While paused, D must use the same frozen A/B snapshot used by render + hover.
                    fBase = _pausedA ?? _latestA;
                }
            }
            else
            {
                fBase = GetDisplayedFrameForPane(pane);
            }
            if (fBase == null) return true;

            double aw = img.ActualWidth;
            double ah = img.ActualHeight;
            if (aw <= 1 || ah <= 1) return true;

            double scale = Math.Min(aw / fBase.Width, ah / fBase.Height);
            int stepNow = ComputeOverlayStep(scale, zoom.ScaleX, fBase.Width, fBase.Height);
            int stepPrev = pane switch
            {
                Pane.A => _overlayStepA,
                Pane.B => _overlayStepB,
                _ => _overlayStepD,
            };
            return stepNow != stepPrev;
        }

        private void UpdateOverlay(Pane pane)
        {
            var (img, ovr, zoom) = GetPaneVisuals(pane);
            if (img == null || ovr == null) return;

            if (!_playback.IsPaused || zoom.ScaleX < OverlayMinZoom)
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
                // Keep overlay labels consistent with the actual rendered diff.
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

            // Stretch=Uniform => image may be letterboxed. Compute displayed rect.
            double scale = Math.Min(aw / fBase.Width, ah / fBase.Height);
            double dw = fBase.Width * scale;
            double dh = fBase.Height * scale;
            double ox = (aw - dw) / 2.0;
            double oy = (ah - dh) / 2.0;

            // Cassandra-like mode:
            // - Fixed on-screen text size
            // - Show only when it fits in ONE pixel (step=1)
            // - Text is drawn in screen space and positioned using zoom/pan (no drift)
            int step = 1;
            double fontSize = Math.Max(1.0, OverlayTextPx); // constant on-screen size

            // Pixel size on screen (DIPs)
            double pixelOnScreen = scale * zoom.ScaleX;

            // Map points from image local coords into overlay local coords.
            // This automatically accounts for RenderTransform (zoom/pan) AND layout offsets
            // (e.g., the header row above the image inside the parent Grid).
            GeneralTransform imgToOvr;
            try
            {
                imgToOvr = img.TransformToVisual(ovr);
            }
            catch
            {
                ClearOverlay(pane);
                return;
            }

            ovr.Visibility = Visibility.Visible;
            ovr.Children.Clear();

            // Ensure the overlay surface uses the same coordinate space as the image cell.
            ovr.Width = aw;
            ovr.Height = ah;

            // Render overlay as a single vector drawing for performance.
            var dg = new DrawingGroup();
            using (var dc = dg.Open())
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                double pixelsPerDip = dpi.PixelsPerDip;

                var typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                var fgWhite = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
                var fgBlack = new SolidColorBrush(Color.FromArgb(230, 0, 0, 0));
                fgWhite.Freeze();
                fgBlack.Freeze();

                // Decide visibility based on whether the max-width label fits inside ONE pixel on-screen.
                string sample = pane == Pane.D ? "+255" : "255";
                var sampleFt = new FormattedText(sample, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, fontSize, fgWhite, pixelsPerDip);
                double sampleW = Math.Max(sampleFt.WidthIncludingTrailingWhitespace, 1.0);
                double sampleH = Math.Max(sampleFt.Height, 1.0);
                const double FitMargin = 1.15;
                if (pixelOnScreen < sampleW * FitMargin || pixelOnScreen < sampleH * FitMargin)
                {
                    // Too small -> hide entirely.
                    ClearOverlay(pane);
                    return;
                }

                static bool IsDarkBgr(byte bl, byte g, byte r)
                {
                    double y = (0.2126 * r) + (0.7152 * g) + (0.0722 * bl);
                    return y < 128.0;
                }

                static bool IsDarkGray(byte v) => v < 128;

                // Cache FormattedText for current font size to reduce allocations.
                var cacheGrayW = new FormattedText[256];
                var cacheGrayB = new FormattedText[256];
                var cacheDiffW = new Dictionary<int, FormattedText>(512);
                var cacheDiffB = new Dictionary<int, FormattedText>(512);

                var boundsGrayW = new Rect[256];
                var boundsGrayB = new Rect[256];
                var boundsGrayWSet = new bool[256];
                var boundsGrayBSet = new bool[256];
                var boundsDiffW = new Dictionary<int, Rect>(512);
                var boundsDiffB = new Dictionary<int, Rect>(512);

                FormattedText GetGrayFt(byte v, bool white)
                {
                    var arr = white ? cacheGrayW : cacheGrayB;
                    var ft = arr[v];
                    if (ft != null) return ft;
                    ft = new FormattedText(v.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, fontSize, white ? fgWhite : fgBlack, pixelsPerDip);
                    arr[v] = ft;
                    return ft;
                }

                Rect GetGrayBounds(byte v, bool white)
                {
                    if (white)
                    {
                        if (!boundsGrayWSet[v])
                        {
                            boundsGrayW[v] = GetGrayFt(v, true).BuildGeometry(new Point(0, 0)).Bounds;
                            boundsGrayWSet[v] = true;
                        }
                        return boundsGrayW[v];
                    }

                    if (!boundsGrayBSet[v])
                    {
                        boundsGrayB[v] = GetGrayFt(v, false).BuildGeometry(new Point(0, 0)).Bounds;
                        boundsGrayBSet[v] = true;
                    }
                    return boundsGrayB[v];
                }

                FormattedText GetDiffFt(int diff, bool white)
                {
                    var dict = white ? cacheDiffW : cacheDiffB;
                    if (dict.TryGetValue(diff, out var ft)) return ft;
                    string text = diff.ToString("+0;-0;0", CultureInfo.InvariantCulture);
                    ft = new FormattedText(text, CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, fontSize, white ? fgWhite : fgBlack, pixelsPerDip);
                    dict[diff] = ft;
                    return ft;
                }

                Rect GetDiffBounds(int diff, bool white)
                {
                    var dict = white ? boundsDiffW : boundsDiffB;
                    if (!dict.TryGetValue(diff, out var b))
                    {
                        b = GetDiffFt(diff, white).BuildGeometry(new Point(0, 0)).Bounds;
                        dict[diff] = b;
                    }
                    return b;
                }

                int added = 0;
                for (int y = 0; y < fBase.Height; y += step)
                {
                    double imgCy = oy + (y + 0.5) * scale;
                    for (int x = 0; x < fBase.Width; x += step)
                    {
                        double imgCx = ox + (x + 0.5) * scale;

                        // Transform from image-local coords into overlay-local coords.
                        Point p;
                        try
                        {
                            p = imgToOvr.Transform(new Point(imgCx, imgCy));
                        }
                        catch
                        {
                            continue;
                        }
                        double cx = p.X;
                        double cy = p.Y;

                        FormattedText ft;
                        Rect bounds;
                        if (pane == Pane.D && fA != null && fB != null)
                        {
                            byte aPx = fA.Data[y * fA.Stride + x];
                            byte bPx = fB.Data[y * fB.Stride + x];
                            int diff = bPx - aPx;

                            DiffRenderer.ComparePixelToBgr(aPx, bPx, _diffThreshold, _zeroZeroIsWhite, out var bl, out var gg, out var rr);
                            bool white = IsDarkBgr(bl, gg, rr);
                            ft = GetDiffFt(diff, white);
                            bounds = GetDiffBounds(diff, white);
                        }
                        else
                        {
                            byte v = fBase.Data[y * fBase.Stride + x];
                            bool white = IsDarkGray(v);
                            ft = GetGrayFt(v, white);
                            bounds = GetGrayBounds(v, white);
                        }

                        // Center based on actual glyph geometry bounds (fixes baseline/overhang bias).
                        double oxText = cx - (bounds.X + (bounds.Width * 0.5));
                        double oyText = cy - (bounds.Y + (bounds.Height * 0.5));
                        dc.DrawText(ft, new Point(oxText, oyText));

                        if (++added >= OverlayMaxLabels) break;
                    }
                    if (added >= OverlayMaxLabels) break;
                }
            }
            dg.Freeze();

            // Render via a DrawingBrush with an explicit absolute Viewbox/Viewport so the
            // brush coordinate system is stable (0..aw, 0..ah) regardless of drawing bounds.
            var db = new DrawingBrush(dg)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                TileMode = TileMode.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, aw, ah),
                Viewport = new Rect(0, 0, aw, ah)
            };
            db.Freeze();

            var rect = new Rectangle
            {
                Width = aw,
                Height = ah,
                Fill = db,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            System.Windows.Controls.Canvas.SetLeft(rect, 0);
            System.Windows.Controls.Canvas.SetTop(rect, 0);
            ovr.Children.Add(rect);
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
            _liveCapture.OnFrameReady += (frame, meta) =>
            {
                // Runs on receiver thread -> marshal to UI
                Dispatcher.Invoke(() =>
                {
                    // Keep status stable when stopped; ignore late frames during shutdown races.
                    if (!_playback.IsRunning)
                        return;

                    _playback.IncrementCountAvtpIn();

                    bool incomplete = meta.linesWritten != H_ACTIVE;
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
                    {
                        _playback.IncrementCountB();
                    }

                    int lateSkip = _playback.CountLateFramesSkipped;
                    string late = lateSkip > 0 ? $" | lateSkip={lateSkip}" : "";

                    string src = GetActiveAvtpFeed() switch
                    {
                        LiveCaptureManager.Feed.UdpRvf => "UDP/RVFU",
                        LiveCaptureManager.Feed.EthernetAvtp => "Ethernet/AVTP",
                        LiveCaptureManager.Feed.PcapReplay => "PCAP",
                        _ => "?"
                    };

                    _liveCapture.LastRvfSrcLabel = src;

                    LblStatus.Text = $"AVTP RVF ({src}): frameId={meta.frameId} seq={meta.seq} lines={meta.linesWritten}/80 gaps={meta.seqGaps} | dropped={_playback.CountAvtpDropped} (gapFrames={_playback.CountAvtpSeqGapFrames}, incomplete={_playback.CountAvtpIncomplete}){late}";
                });
            };

            ShowIdleGradient();
            LblStatus.Text = $"Ready. Load an image (PGM/BMP/PNG; BMP/PNG are converted to Gray8 u8; will crop top-left to 320×80) and press Start to begin rendering. UDP listens on {IPAddress.Any}:{RvfProtocol.DefaultPort} when started.";

            LoadUiSettings();

            // Startup should show "Signal not available".
            ApplyNoSignalUiState(noSignal: true);
        }

        private void LoadUiSettings()
        {
            _isLoadingSettings = true;
            try
            {
                var s = AppSettingsStore.LoadOrDefault(_settingsPath);
                s.Fps = Math.Clamp(s.Fps, 1, 1000);
                s.BDelta = Math.Clamp(s.BDelta, -255, 255);
                s.Deadband = Math.Clamp(s.Deadband, 0, 255);
                s.ForcedDeadPixelId = Math.Clamp(s.ForcedDeadPixelId, 0, W * H_ACTIVE);

                _bValueDelta = s.BDelta;
                _diffThreshold = (byte)s.Deadband;
                _zeroZeroIsWhite = s.ZeroZeroIsWhite;
                Volatile.Write(ref _forcedDeadPixelId, s.ForcedDeadPixelId);
                _darkPixelCompensationEnabled = s.DarkPixelCompensationEnabled;

                _avtpLiveEnabled = s.AvtpLiveEnabled;
                _avtpLiveDeviceHint = s.AvtpLiveDeviceHint;
                _avtpLiveUdpEnabled = s.AvtpLiveUdpEnabled;

                _modeOfOperation = s.ModeOfOperation == (int)ModeOfOperation.AvtpLiveMonitor
                    ? ModeOfOperation.AvtpLiveMonitor
                    : ModeOfOperation.PlayerFromFiles;

                if (TxtFps != null) TxtFps.Text = s.Fps.ToString();
                if (TxtBDelta != null) TxtBDelta.Text = s.BDelta.ToString();
                if (SldDiffThr != null) SldDiffThr.Value = s.Deadband;
                if (LblDiffThr != null) LblDiffThr.Text = s.Deadband.ToString();
                if (ChkZeroZeroWhite != null) ChkZeroZeroWhite.IsChecked = s.ZeroZeroIsWhite;
                if (TxtDeadPixelId != null) TxtDeadPixelId.Text = s.ForcedDeadPixelId.ToString();
                if (ChkDarkPixelComp != null) ChkDarkPixelComp.IsChecked = s.DarkPixelCompensationEnabled;

                if (CmbModeOfOperation != null)
                {
                    CmbModeOfOperation.SelectedIndex = _modeOfOperation == ModeOfOperation.AvtpLiveMonitor ? 0 : 1;
                }
                RefreshLiveNicList();
                UpdateLiveUiEnabledState();

                RenderAll();
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        private void SaveUiSettings()
        {
            if (_isLoadingSettings) return;
            try
            {
                var s = new AppSettings
                {
                    Fps = (TxtFps != null && int.TryParse(TxtFps.Text, out var fps) && fps > 0) ? fps : 100,
                    BDelta = _bValueDelta,
                    Deadband = _diffThreshold,
                    ZeroZeroIsWhite = _zeroZeroIsWhite,
                    ForcedDeadPixelId = Volatile.Read(ref _forcedDeadPixelId),
                    DarkPixelCompensationEnabled = _darkPixelCompensationEnabled,
                    AvtpLiveEnabled = _avtpLiveEnabled,
                    AvtpLiveDeviceHint = _avtpLiveDeviceHint,
                    AvtpLiveUdpEnabled = _avtpLiveUdpEnabled,
                    ModeOfOperation = (int)_modeOfOperation
                };
                AppSettingsStore.Save(s, _settingsPath);
            }
            catch
            {
                // ignore settings I/O errors
            }
        }

        private void CmbModeOfOperation_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isLoadingSettings || !IsLoaded) return;

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

            LblStatus.Text = _modeOfOperation == ModeOfOperation.AvtpLiveMonitor
                ? "Mode: AVTP Live (Monitoring). Press Start to listen/capture live stream."
                : "Mode: Generator/Player (Files). Load a file and press Start.";
        }

        // Convenience aliases for live capture feed - delegate to _liveCapture
        private bool TrySetActiveAvtpFeed(LiveCaptureManager.Feed feed) => _liveCapture.TrySetActiveFeed(feed);
        private LiveCaptureManager.Feed GetActiveAvtpFeed() => _liveCapture.ActiveFeed;

        private sealed class LiveNicItem
        {
            public required string Display;
            public required string? DeviceName;
            public override string ToString() => Display;
        }

        private void RefreshLiveNicList()
        {
            if (CmbLiveNic == null) return;

            CmbLiveNic.Items.Clear();
            CmbLiveNic.Items.Add(new LiveNicItem { Display = "<Auto>", DeviceName = null });

            var devs = AvtpLiveCapture.ListDevicesSafe();
            foreach (var d in devs)
            {
                string name = d.Name ?? string.Empty;
                string desc = d.Description ?? string.Empty;
                string display = NetworkInterfaceUtils.DescribeCaptureDeviceForUi(name, desc);
                CmbLiveNic.Items.Add(new LiveNicItem { Display = display, DeviceName = name });
            }

            int idx = 0;
            if (!string.IsNullOrWhiteSpace(_avtpLiveDeviceHint))
            {
                for (int i = 1; i < CmbLiveNic.Items.Count; i++)
                {
                    if (CmbLiveNic.Items[i] is LiveNicItem item
                        && string.Equals(item.DeviceName, _avtpLiveDeviceHint, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }
            }
            CmbLiveNic.SelectedIndex = idx;
        }

        private string? GetTxPcapDeviceNameOrNull()
        {
            // 1) if the user explicitly selected from the combo (CmbLiveNic), use that NPF name
            try
            {
                if (CmbLiveNic?.SelectedItem is LiveNicItem item && !string.IsNullOrWhiteSpace(item.DeviceName))
                    return item.DeviceName;
            }
            catch { /* ignore */ }
        
            // 2) fallback: try to find by hint (description/MAC) in the pcap device list
            string hint = _avtpLiveDeviceHint ?? string.Empty;
        
            try
            {
                var devs = CaptureDeviceList.Instance
                    .OfType<SharpPcap.LibPcap.LibPcapLiveDevice>()
                    .ToList();
        
                // a) match by hint in Name/Description
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    var hit = devs.FirstOrDefault(d =>
                        (!string.IsNullOrWhiteSpace(d.Description) && d.Description.Contains(hint, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(d.Name) && d.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)));
        
                    if (hit != null) return hit.Name;
                }
        
                // b) alternative fallback: first non-loopback card
                var first = devs.FirstOrDefault(d => !NetworkInterfaceUtils.LooksLikeLoopbackDevice(d.Name, d.Description));
                return first?.Name;
            }
            catch
            {
                return null;
            }
        }

        private void UpdateLiveUiEnabledState()
        {
            bool isLive = _modeOfOperation == ModeOfOperation.AvtpLiveMonitor;
            if (CmbLiveNic != null) CmbLiveNic.IsEnabled = isLive;
        }

        private void BtnRefreshNics_Click(object sender, RoutedEventArgs e) => RefreshLiveNicList();

        private void CmbLiveNic_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isLoadingSettings) return;
            if (CmbLiveNic?.SelectedItem is LiveNicItem item)
            {
                _avtpLiveDeviceHint = item.DeviceName;
                SaveUiSettings();
            }
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
                var a = _latestA ?? new Frame(W, H_ACTIVE, GetASourceBytes(), DateTime.UtcNow);
                var bBytes = ApplyValueDelta(a.Data, _bValueDelta);
                var b = new Frame(W, H_ACTIVE, bBytes, DateTime.UtcNow);
                lock (_frameLock)
                {
                    _latestA = a;
                    _latestB = b;
                    _latestD = AbsDiff(a, b); // keep Gray8 abs-diff buffer for any internal uses
                }
                RenderAll();
            }
        }

        private void SldDiffThr_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _diffThreshold = (byte)Math.Clamp((int)Math.Round(e.NewValue), 0, 255);
            if (LblDiffThr != null) LblDiffThr.Text = _diffThreshold.ToString();
            SaveUiSettings();
            if (_playback.Cts == null || _playback.IsPaused) RenderAll();
        }

        private void TxtDeadPixelId_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            int id = 0;
            if (TxtDeadPixelId != null && int.TryParse(TxtDeadPixelId.Text, out var parsed))
                id = Math.Clamp(parsed, 0, W * H_ACTIVE);

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
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtFps.Text, out var fps) || fps <= 0) fps = 100;

            if (!_playback.IsRunning)
            {
                Start(fps);
                if (BtnStart != null) BtnStart.Content = "Pause";
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

                // Start BLACK TX loop (do NOT stop transmitter)
                StartBlackTxLoop(fps);

                LblStatus.Text = "Player STOP: sending BLACK AVTP (Signal not available).";
                return;
            }

            // AVTP Live: normal stop
            StopAll();
        }

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_recordingManager.IsRecording) StopRecording();
            else StartRecording();
        }

        private async void BtnSaveReport_Click(object sender, RoutedEventArgs e)
        {
            // UI feedback (cancel any previous pending status transitions)
            try { _saveFeedbackCts?.Cancel(); } catch { }
            try { _saveFeedbackCts?.Dispose(); } catch { }
            _saveFeedbackCts = new CancellationTokenSource();
            var uiCt = _saveFeedbackCts.Token;

            ShowSaveFeedback("Saving current frame...", Brushes.DimGray);

            // Save a compare report for the CURRENTLY DISPLAYED frame (works best while paused + stepping).
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
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string outDir = RecordingManager.GetFrameSnapshotsOutputDirectory();

            var paths = RecordingManager.MakeUniqueSaveSetPaths(outDir, ts);

            try
            {
                LblStatus.Text = $"Saving report + images… ({System.IO.Path.GetFileName(paths.XlsxPath)})";

                var aBytes = (byte[])a.Data.Clone();
                var bBytes = (byte[])bPost.Data.Clone();
                byte thr = _diffThreshold;

                // Create the D snapshot exactly like the UI render (B−A mapping to BGR24).
                var dBgr = new byte[W * H_ACTIVE * 3];
                DiffRenderer.RenderCompareToBgr(dBgr, aBytes, bBytes, W, H_ACTIVE, thr, _zeroZeroIsWhite,
                    out _, out _, out _,
                    out _, out _, out _,
                    out _);

                await Task.Run(() =>
                {
                    AviTripletRecorder.SaveSingleFrameCompareXlsx(paths.XlsxPath, frameNr, aBytes, bBytes, W, H_ACTIVE, deviationThreshold: thr);

                    // Save 1:1 snapshots for all panes.
                    ImageUtils.SaveGray8Png(paths.APath, aBytes, W, H_ACTIVE);
                    ImageUtils.SaveGray8Png(paths.BPath, bBytes, W, H_ACTIVE);
                    ImageUtils.SaveBgr24Png(paths.DPath, dBgr, W, H_ACTIVE);
                });

                // Keep the "Saving..." message visible for a short moment, then switch to "Saved!".
                try { await Task.Delay(1200, uiCt); } catch { /* ignore */ }

                if (!uiCt.IsCancellationRequested)
                    ShowSaveFeedback("Current frame saved!", Brushes.ForestGreen);

                // Optionally clear after a few seconds.
                try { await Task.Delay(2500, uiCt); } catch { /* ignore */ }
                if (!uiCt.IsCancellationRequested) HideSaveFeedback();

                LblStatus.Text = $"Saved: {paths.XlsxPath} (+ A/B/D PNG)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save XLSX: {ex.Message}", "Save report error");

                if (!uiCt.IsCancellationRequested)
                    ShowSaveFeedback("Save failed.", Brushes.IndianRed);

                try { await Task.Delay(3000, uiCt); } catch { /* ignore */ }
                if (!uiCt.IsCancellationRequested) HideSaveFeedback();
            }
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
                _pausedA = _latestA ?? new Frame(W, H_ACTIVE, GetASourceBytes(), DateTime.UtcNow);

                if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor)
                {
                    // In AVTP Live mode, B is derived from A using the UI delta; D is derived from (B-A).
                    _pausedB = new Frame(W, H_ACTIVE, ApplyValueDelta(_pausedA.Data, _bValueDelta), _pausedA.TimestampUtc);
                    _pausedD = AbsDiff(_pausedA, _pausedB);
                }
                else
                {
                    _pausedB = _latestB ?? _pausedA;
                    _pausedD = _latestD ?? AbsDiff(_pausedA, _pausedB);
                }
            }

            // Re-render once from the frozen snapshot to avoid any race with in-flight frame updates.
            RenderAll();

            if (BtnStart != null) BtnStart.Content = "Start";
            if (LblRunInfoA != null) LblRunInfoA.Text = "Paused";
            if (LblRunInfoB != null) LblRunInfoB.Text = "Paused";
            LblStatus.Text = "Paused.";

            UpdateOverlaysAll();
        }

        private void Resume()
        {
            _playback.Resume();
            _playback.PauseGate.Set();

            lock (_frameLock)
            {
                _pausedA = null;
                _pausedB = null;
                _pausedD = null;
            }

            if (BtnStart != null) BtnStart.Content = "Pause";
            if (LblRunInfoA != null)
            {
                double shownFps = GetShownFps(avtpInFps: _playback.AvtpInFpsEma);
                LblRunInfoA.Text = shownFps > 0 ? $"Running @: {shownFps:F1} fps" : "Running";
            }
            if (LblRunInfoB != null)
            {
                if (_playback.BFpsEma <= 0.0)
                    LblRunInfoB.Text = "Running";
                else
                    LblRunInfoB.Text = $"Running @: {_playback.BFpsEma:F1} fps";
            }
            LblStatus.Text = _playback.RunningStatusText ?? "Running.";

            ClearOverlay(Pane.A);
            ClearOverlay(Pane.B);
            ClearOverlay(Pane.D);
        }

        private void BtnLoad_Click(object sender, RoutedEventArgs e) => BtnLoadFiles_Click(sender, e);

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
            ClearAvi();
            _playback.ResetFpsEstimates();
            _lastLoaded = LoadedSource.Pcap;
            _lastLoadedPcapPath = path;
            _scenePlayer.Clear();
            LblStatus.Text = "PCAP loaded. Press Start to begin replay.";
        }

        private void LoadSingleImage(string path)
        {
            ClearAvi();
            _playback.ResetFpsEstimates();
            // Switching sources: drop any previously replayed/received AVTP frame
            _liveCapture.ClearAvtpFrame();

            (int width, int height, byte[] data) img = ImageUtils.LoadImageAsGray8(path);

            if (img.width < W || img.height < H_ACTIVE)
                throw new InvalidOperationException($"Expected at least {W}x{H_ACTIVE}, got {img.width}x{img.height}.");

            // Always crop A to 320x80 from top-left (x=0,y=0)
            _pgmFrame = ImageUtils.CropTopLeftGray8(img.data, img.width, img.height, W, H_ACTIVE);

            // Keep an optional 320x84 buffer (also top-left) for future LVDS usage.
            _lvdsFrame84 = img.height >= H_LVDS ? ImageUtils.CropTopLeftGray8(img.data, img.width, img.height, W, H_LVDS) : null;

            _lastLoaded = LoadedSource.Image;
            _lastLoadedPcapPath = null;
            _scenePlayer.Clear();

            LblStatus.Text = "Image loaded (PGM Gray8 or BMP/PNG→Gray8 u8). Press Start to begin rendering.";

            if (_playback.Cts == null || _playback.IsPaused) RenderOneFrameNow();
        }

        private void LoadAvi(string path)
        {
            ClearAvi();

            // Switching sources: stop using previously replayed/received AVTP frame
            _liveCapture.ClearAvtpFrame();

            _aviPlayer.Load(path);

            _lastLoaded = LoadedSource.Avi;
            _lastLoadedPcapPath = null;
            _scenePlayer.Clear();

            LblStatus.Text = _aviPlayer.BuildStatusMessage();

            if (_playback.Cts == null || _playback.IsPaused) RenderOneFrameNow();
        }

        private void ClearAvi()
        {
            _aviPlayer.Close();
        }

        private void Start(int fps)
        {
            StopBlackTxLoop();
            // Safety: if already running, stop first
            if (_playback.Cts != null)
                StopAll();

            // Init playback state and reset stats (includes CTS creation, running=true, paused=false)
            var ct = _playback.Start(fps);

            AppendUdpLog($"[start] mode={_modeOfOperation}, fps={fps}");

            // Reset runtime stats
            ApplyNoSignalUiState(noSignal: false);

            // Reset feed selection + reassembler state
            _liveCapture.ResetAll();
            

            // Default source label before first frame
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor)
            {
                if (_avtpLiveEnabled) _liveCapture.LastRvfSrcLabel = "Ethernet/AVTP";
                else if (_avtpLiveUdpEnabled) _liveCapture.LastRvfSrcLabel = "UDP/RVFU";
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
            try
            {
                if (_modeOfOperation == ModeOfOperation.PlayerFromFiles)
                {
                    var devName = _avtpLiveDeviceHint; // use exactly the device selected in the NIC dropdown
                    if (!string.IsNullOrWhiteSpace(devName))
                    {
                        _tx = new AvtpRvfTransmitter(devName);
                        AppendUdpLog($"[avtp-tx] TX ready on {devName}");
                    }
                    else
                    {
                        AppendUdpLog("[avtp-tx] TX disabled: no TX device selected/found.");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendUdpLog($"[avtp-tx] TX init ERROR: {ex.GetType().Name}: {ex.Message}");
                try { _tx?.Dispose(); } catch { }
                _tx = null;
            }

            // -------------------------------------------------
            // Start loops
            // -------------------------------------------------
            if (_modeOfOperation == ModeOfOperation.PlayerFromFiles)
            {
                // Generator/Player:
                _ = Task.Run(() => GeneratorLoopAsync(fps, ct));
                _ = Task.Run(() => UiRefreshLoop(ct));

                LblStatus.Text = $"Running Player @ {fps} fps (AVTP TX enabled)";
            }
            else
            {
                // AVTP Live Monitor:
                _ = Task.Run(() => UiRefreshLoop(ct));

                // Until the first frame arrives, show explicit waiting message.
                LblStatus.Text =
                    $"Waiting for signal... (0.0 fps) (Mode=AVTP Live). Ethernet/AVTP capture best-effort" +
                    (_avtpLiveUdpEnabled ? $"; UDP/RVFU on 0.0.0.0:{RvfProtocol.DefaultPort}" : "") +
                    $". (log: {GetUdpLogPath()})";

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
            // Start live sources ONLY in AVTP Live mode
            // -------------------------------------------------
            bool allowLiveSources = _modeOfOperation == ModeOfOperation.AvtpLiveMonitor;
            if (allowLiveSources)
            {
                // Ethernet capture (Npcap)
                // NOTE: We always (re)start capture on Start in AVTP Live mode.
                // Reason: at app startup, the NIC selection / device hint may change after settings load,
                // and keeping an old capture instance can leave the UI stuck on the fallback image until Stop->Start.
                if (_avtpLiveEnabled)
                {
                    // Ensure we use the NIC currently selected in the UI (avoids slow/incorrect auto-pick).
                    string? deviceHint = _avtpLiveDeviceHint;
                    if (CmbLiveNic?.SelectedItem is LiveNicItem sel && !string.IsNullOrWhiteSpace(sel.DeviceName))
                        deviceHint = sel.DeviceName;

                    // Persist the final hint so next Start uses the same interface.
                    _avtpLiveDeviceHint = deviceHint;

                    _liveCapture.StartEthernetCapture(deviceHint);
                }

                // Optional UDP/RVFU
                if (_avtpLiveUdpEnabled)
                {
                    _liveCapture.StartUdpReceiver(RvfProtocol.DefaultPort);
                }
            }
            
        }

        private void StartPcapReplay(string path)
        {
            _liveCapture.StartPcapReplay(
                path, 
                _playback.PauseGate,
                onComplete: () =>
                {
                    // Auto-stop after replay: show idle gradient and reset UI.
                    Dispatcher.Invoke(() =>
                    {
                        if (_lastLoaded == LoadedSource.Pcap)
                            StopAll();
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

            StopRenderLoops();

            // Clear AVTP stats labels
            if (LblAvtpInFps != null) LblAvtpInFps.Text = "";
            if (LblAvtpDropped != null) LblAvtpDropped.Text = "";

            // Stop all live capture sources
            _liveCapture.StopAll();

            // Ensure we don't remain paused after stopping.
            _playback.PauseGate.Set();

            // Stop should behave like a reset for file-backed sources.
            if (_lastLoaded == LoadedSource.Avi && _aviPlayer.IsLoaded)
            {
                _aviPlayer.Reset();
            }

            SaveUiSettings();

            LblStatus.Text = $"AVTP RVF ({_liveCapture.LastRvfSrcLabel}): Stopped.";

            ClearOverlay(Pane.A);
            ClearOverlay(Pane.B);
            ClearOverlay(Pane.D);
        }

        private static void AppendUdpLog(string message) => DiagnosticLogger.Log(message);
        private static string GetUdpLogPath() => DiagnosticLogger.LogPath;

        private async void BtnPlayPcap_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select PCAP/PCAPNG",
                Filter = "Capture files (*.pcap;*.pcapng)|*.pcap;*.pcapng|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dlg.ShowDialog(this) != true)
                return;

            _lastLoaded = LoadedSource.Pcap;
            _lastLoadedPcapPath = dlg.FileName;
            LblStatus.Text = "PCAP loaded. Press Start to begin replay.";

            // Keep signature async for WPF handler; no await needed anymore.
            await Task.CompletedTask;
        }

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

            if (img.width < W || img.height < H_ACTIVE)
            {
                MessageBox.Show($"Expected at least {W}x{H_ACTIVE}, got {img.width}x{img.height}.", "Size mismatch");
                return;
            }

            var cropped = ImageUtils.CropTopLeftGray8(img.data, img.width, img.height, W, H_ACTIVE);

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

            // Switching sources: stop using previously replayed/received AVTP frame
            _liveCapture.ClearAvtpFrame();

            _scenePlayer.Load(scenePath);

            _lastLoaded = LoadedSource.Scene;
            _lastLoadedPcapPath = null;

            LblStatus.Text = _scenePlayer.BuildStatusMessage();
            if (_playback.Cts == null || _playback.IsPaused) RenderOneFrameNow();
        }

        private byte[]? GetSequenceBytes() => _sequencePlayer.GetBytes();

        private void RenderOneFrameNow()
        {
            var now = DateTime.UtcNow;
            var aBytes = GetASourceBytes();
            var a = new Frame(W, H_ACTIVE, aBytes, now);
            var bBytes = ApplyValueDelta(a.Data, _bValueDelta);
            var b = new Frame(W, H_ACTIVE, bBytes, now);
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
                }
            }
            RenderAll();

            if (_playback.IsPaused)
                UpdateOverlaysAll();
        }

        // Choose source for A:
        // - depends on last loaded source (image vs PCAP)
        private byte[] GetASourceBytes()
        {
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor)
                return _liveCapture.HasAvtpFrame ? _liveCapture.AvtpFrame : _idleGradientFrame;

            return _lastLoaded switch
            {
                LoadedSource.Image => _pgmFrame,
                LoadedSource.Pcap => _liveCapture.HasAvtpFrame ? _liveCapture.AvtpFrame : _idleGradientFrame,
                LoadedSource.Avi => _aviPlayer.GetBytesAndUpdateIfNeeded(DateTime.UtcNow, _playback.IsPaused) ?? _idleGradientFrame,
                LoadedSource.Sequence => GetSequenceBytes() ?? _idleGradientFrame,
                LoadedSource.Scene => _scenePlayer.GetBytesAndUpdateIfNeeded(DateTime.UtcNow, _playback.IsPaused) ?? _idleGradientFrame,
                _ => _idleGradientFrame
            };
        }

        private async Task GeneratorLoopAsync(int fps, CancellationToken ct)
        {
            AppendUdpLog($"[generator] Entered GeneratorLoop, mode={_modeOfOperation}, fps={fps}");
            var period = TimeSpan.FromSeconds(1.0 / Math.Max(1, fps));
            var sw = Stopwatch.StartNew();
            var next = sw.Elapsed;
        
            int txErrOnce = 0;
            int txNoDevOnce = 0;
        
            while (!ct.IsCancellationRequested)
            {
                try { _playback.PauseGate.Wait(ct); }
                catch { break; }
        
                next += period;
        
                // A: either UDP latest or PGM/AVI/Scene fallback (depending on what you loaded)
                var aBytes = GetASourceBytes();
                var a = new Frame(W, H_ACTIVE, aBytes, DateTime.UtcNow);
                _playback.IncrementCountA();
        
                // B: simulated LVDS = A with brightness delta
                var bBytes = ApplyValueDelta(a.Data, _bValueDelta);
                var b = new Frame(W, H_ACTIVE, bBytes, DateTime.UtcNow);
                _playback.IncrementCountB();
        
                // D: diff
                var d = AbsDiff(a, b);
        
                // -----------------------------
                // AVTP Ethernet TX (ONLY PlayerFromFiles)
                // -----------------------------
                if (_modeOfOperation == ModeOfOperation.PlayerFromFiles)
                {
                    if (_tx != null)
                    {
                        try
                        {
                            // IMPORTANT: send frame "A" (320x80 Gray8)
                            await _tx.SendFrame320x80Async(a.Data, ct);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            // log only once to avoid spamming
                            if (Interlocked.Exchange(ref txErrOnce, 1) == 0)
                                AppendUdpLog($"[avtp-tx] SEND ERROR (first): {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                    else
                    {
                        if (Interlocked.Exchange(ref txNoDevOnce, 1) == 0)
                            AppendUdpLog("[avtp-tx] TX is NULL -> nothing will be sent (select NIC and press Start).");
                    }
                }
        
                // If pause was activated exactly during the iteration, do not publish frame
                if (_playback.IsPaused || !_playback.PauseGate.IsSet)
                {
                    if (_playback.IncrementLateFramesSkipped() == 1)
                        AppendUdpLog("[ui] generator skipped publish due to pause race (late frame)");
                    continue;
                }
        
                lock (_frameLock)
                {
                    _latestA = a;
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
                    LblStatus.Text =
                        $"Waiting for signal... (0.0 fps) (Mode=AVTP Live). Ethernet/AVTP capture best-effort" +
                        (_avtpLiveUdpEnabled ? $"; UDP/RVFU on 0.0.0.0:{RvfProtocol.DefaultPort}" : "") +
                        $". (log: {GetUdpLogPath()})";
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
                    a = _latestA ?? new Frame(W, H_ACTIVE, GetASourceBytes(), DateTime.UtcNow);
                    b = _latestB ?? a;
                    d = _latestD ?? AbsDiff(a, b);
                }
            }

            // In AVTP Live mode, B is derived from A using the UI delta, D is derived from (B-A).
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor && !_playback.IsPaused)
            {
                b = new Frame(W, H_ACTIVE, ApplyValueDelta(a.Data, _bValueDelta), a.TimestampUtc);
                d = AbsDiff(a, b);
            }

            // B post-processing (forced dead pixel + optional compensation)
            b = ApplyBPostProcessing(a, b);

            BitmapUtils.Blit(_wbA, a.Data, a.Stride);
            BitmapUtils.Blit(_wbB, b.Data, b.Stride);
            DiffRenderer.RenderCompareToBgr(_diffBgr, a.Data, b.Data, W, H_ACTIVE, _diffThreshold,
                _zeroZeroIsWhite,
                out var minDiff, out var maxDiff, out var meanDiff,
                out var maxAbsDiff, out var meanAbsDiff, out var aboveDeadband,
                out var totalDarkPixels);
            BitmapUtils.Blit(_wbD, _diffBgr, W * 3);

            // Record what we render (A/B in Gray8; D in Bgr24). Diff buffer is reused, so copy it.
            if (_recordingManager.IsRecording && !_playback.IsPaused && _playback.Cts != null)
            {
                var dCopy = new byte[_diffBgr.Length];
                Buffer.BlockCopy(_diffBgr, 0, dCopy, 0, dCopy.Length);
                _recordingManager.TryEnqueueFrame(a.Data, b.Data, dCopy);
            }

            if (LblDiffStats != null)
                LblDiffStats.Text = $"COMPARE (B−A): max_positive_dev={Math.Max(0, maxDiff)}  max_negative_dev={Math.Min(0, minDiff)}  total_pixels_dev={aboveDeadband}  total_dark_pixels={totalDarkPixels}";

            ApplyNoSignalUiState(noSignal: false);
            UpdateFpsLabels();
        }

        private void UpdateFpsLabels()
        {
            if (!_playback.TryUpdateFpsEstimates(out double fpsA, out double fpsB, out double fpsIn))
                return;

            bool noSignalWhileRunning = ShouldShowNoSignalWhileRunning();

            // Note: LblA/LblB/LblD are reserved for cursor x/y/v info.
            string avtpDrop = $"{_playback.CountAvtpDropped} (gapFrames={_playback.CountAvtpSeqGapFrames}, incomplete={_playback.CountAvtpIncomplete}, gaps={_playback.SumAvtpSeqGaps})";

            // Keep these updated for potential future use (row is hidden in XAML).
            if (LblAvtpInFps != null) LblAvtpInFps.Text = $"{_playback.AvtpInFpsEma:F1} fps";
            if (LblAvtpDropped != null) LblAvtpDropped.Text = avtpDrop;

            if (LblRunInfoA != null)
            {
                if (_playback.Cts != null)
                {
                    if (_playback.IsPaused)
                    {
                        LblRunInfoA.Text = "Paused";
                    }
                    else
                    {
                        double shownFps = noSignalWhileRunning ? 0.0 : GetShownFps(avtpInFps: _playback.AvtpInFpsEma);
                        if (_lastLoaded == LoadedSource.Avi && shownFps <= 0.0)
                            LblRunInfoA.Text = "Running";
                        else
                            LblRunInfoA.Text = $"Running @: {shownFps:F1} fps";

                        // Only when >0, keep a visible hint in the status line too.
                        int lateSkip = _playback.CountLateFramesSkipped;
                        if (lateSkip > 0 && LblStatus != null)
                        {
                            const string tag = "lateSkip=";
                            string s = LblStatus.Text ?? string.Empty;
                            int idx = s.IndexOf(tag, StringComparison.Ordinal);
                            if (idx >= 0)
                            {
                                int start = idx + tag.Length;
                                int end = start;
                                while (end < s.Length && char.IsDigit(s[end])) end++;
                                LblStatus.Text = s.Substring(0, start)
                                               + lateSkip.ToString(CultureInfo.InvariantCulture)
                                               + s.Substring(end);
                            }
                            else
                            {
                                LblStatus.Text = string.IsNullOrWhiteSpace(s)
                                    ? $"lateSkip={lateSkip}"
                                    : $"{s} | lateSkip={lateSkip}";
                            }
                        }
                    }
                }
                else LblRunInfoA.Text = "";
            }

            if (LblRunInfoB != null)
            {
                if (_playback.Cts != null)
                {
                    if (_playback.IsPaused)
                    {
                        LblRunInfoB.Text = "Paused";
                    }
                    else
                    {
                        if (noSignalWhileRunning)
                        {
                            LblRunInfoB.Text = $"Running @: {0.0:F1} fps";
                        }
                        else if (_playback.BFpsEma <= 0.0)
                            LblRunInfoB.Text = "Running";
                        else
                            LblRunInfoB.Text = $"Running @: {_playback.BFpsEma:F1} fps";
                    }
                }
                else LblRunInfoB.Text = "";
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

        private static Frame AbsDiff(Frame a, Frame b) => ImageUtils.AbsDiff(a, b, W, H_ACTIVE);
        private static byte[] ApplyValueDelta(byte[] src, int delta) => ImageUtils.ApplyValueDelta(src, delta);

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

        private (ScaleTransform zoom, TranslateTransform pan) GetPaneTransforms(Pane pane) => pane switch
        {
            Pane.A => (_zoomA, _panA),
            Pane.B => (_zoomB, _panB),
            _ => (_zoomD, _panD),
        };

        private void ResetZoomPan(Pane pane)
        {
            var (zoom, pan) = GetPaneTransforms(pane);
            zoom.ScaleX = zoom.ScaleY = 1.0;
            pan.X = pan.Y = 0.0;
        }

        private void Img_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (sender is not System.Windows.Controls.Image img) return;

            var pane = PaneFromSender(sender);
            var (zoom, pan) = GetPaneTransforms(pane);

            double oldScale = zoom.ScaleX;
            double factor = e.Delta > 0 ? 1.15 : (1.0 / 1.15);
            double newScale = Math.Clamp(oldScale * factor, 1.0, 40.0);
            if (Math.Abs(newScale - oldScale) < 1e-9) return;

            // Zoom around mouse: compute anchor in parent coords, convert to local via inverse transform,
            // then update pan so that the same local point stays under the cursor.
            var parent = img.Parent as IInputElement;
            Point pParent = parent != null ? e.GetPosition(parent) : e.GetPosition(img);

            double localX = (pParent.X - pan.X) / oldScale;
            double localY = (pParent.Y - pan.Y) / oldScale;

            pan.X = pParent.X - (localX * newScale);
            pan.Y = pParent.Y - (localY * newScale);
            zoom.ScaleX = zoom.ScaleY = newScale;

            // If zoom reset back to 1, also reset pan.
            if (Math.Abs(newScale - 1.0) < 1e-6)
            {
                pan.X = pan.Y = 0.0;
            }

            e.Handled = true;

            // Overlay positions depend on zoom/pan (screen-space overlay), so update on zoom.
            if (_playback.IsPaused)
                RequestOverlayUpdate(pane);
        }

        private void Img_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.Image img) return;
            var pane = PaneFromSender(sender);

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.ClickCount >= 2)
            {
                ResetZoomPan(pane);
                if (_playback.IsPaused) UpdateOverlay(pane);
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

            var (_, pan) = GetPaneTransforms(pane);
            _isPanning = true;
            _panningPane = pane;
            _panStart = e.GetPosition(this);
            _panStartX = pan.X;
            _panStartY = pan.Y;
            img.CaptureMouse();
            e.Handled = true;
        }

        private void Img_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            _isPanning = false;
            if (sender is System.Windows.Controls.Image img)
                img.ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);
            if (!_isPanning) return;

            var (_, pan) = GetPaneTransforms(_panningPane);
            Point cur = e.GetPosition(this);
            Vector d = cur - _panStart;
            pan.X = _panStartX + d.X;
            pan.Y = _panStartY + d.Y;

            if (_playback.IsPaused)
                RequestOverlayUpdate(_panningPane);
        }

        private void ShowPixelInfo(MouseEventArgs e, Frame? f, System.Windows.Controls.TextBlock lbl)
        {
            // Do not show pixel info when signal is not available (idle/no-signal UI).
            if (_playback.Cts == null || ShouldShowNoSignalWhileRunning()) { lbl.Text = ""; return; }
            if (f == null) { lbl.Text = ""; return; }
            if (!TryGetPixelXYFromMouse(e, f, out int x, out int y)) { lbl.Text = ""; return; }

            byte v = f.Data[y * f.Stride + x];
            int pixelId = (y * f.Width) + x + 1;
            lbl.Text = $"x={x} y={y} v={v} pixel_ID={pixelId}";
        }

        private void ShowPixelInfoDiff(MouseEventArgs e, System.Windows.Controls.TextBlock lbl)
        {
            // Pane D displays deviation in BGR derived from A vs post-processed B.
            // For hover, show signed diff (B−A) plus A/B values at that pixel.
            // If signal not available (no-signal UI), don't show info.
            if (_playback.Cts == null || ShouldShowNoSignalWhileRunning()) { lbl.Text = ""; return; }

            var a = GetDisplayedFrameForPane(Pane.A);
            var b = GetDisplayedFrameForPane(Pane.B); // includes post-processing
            var refFrame = a ?? b;
            if (refFrame == null) { lbl.Text = ""; return; }

            if (!TryGetPixelXYFromMouse(e, refFrame, out int x, out int y)) { lbl.Text = ""; return; }

            int idx = (y * refFrame.Stride) + x;
            byte av = (a != null && idx < a.Data.Length) ? a.Data[idx] : (byte)0;
            byte bv = (b != null && idx < b.Data.Length) ? b.Data[idx] : (byte)0;
            int diff = bv - av;
            int ad = diff < 0 ? -diff : diff;
            int pixelId = (y * refFrame.Width) + x + 1;

            lbl.Text = $"x={x} y={y} A={av} B={bv} diff(B−A)={diff} |diff|={ad} pixel_ID={pixelId}";
        }

        private bool TryGetPixelXYFromMouse(MouseEventArgs e, Frame f, out int x, out int y)
        {
            x = 0;
            y = 0;

            var img = (System.Windows.Controls.Image)e.Source;
            var pane = PaneFromSender(img);
            var (_, ovr, _) = GetPaneVisuals(pane);

            // Use visual transforms so hover mapping matches overlay mapping exactly.
            Point pOvr = e.GetPosition(ovr);
            GeneralTransform ovrToImg;
            try
            {
                ovrToImg = ovr.TransformToVisual(img);
            }
            catch
            {
                return false;
            }

            Point pImg;
            try
            {
                pImg = ovrToImg.Transform(pOvr);
            }
            catch
            {
                return false;
            }

            double lx = pImg.X;
            double ly = pImg.Y;

            double aw = img.ActualWidth;
            double ah = img.ActualHeight;
            if (aw <= 1 || ah <= 1) return false;

            // Stretch=Uniform => image may be letterboxed. Compute displayed rect.
            double scale = Math.Min(aw / f.Width, ah / f.Height);
            double dw = f.Width * scale;
            double dh = f.Height * scale;
            double ox = (aw - dw) / 2.0;
            double oy = (ah - dh) / 2.0;

            double ix = lx - ox;
            double iy = ly - oy;
            if (ix < 0 || iy < 0 || ix >= dw || iy >= dh)
                return false;

            x = (int)(ix / scale);
            y = (int)(iy / scale);
            if (x < 0 || y < 0 || x >= f.Width || y >= f.Height)
                return false;

            return true;
        }
    }

}
