using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SharpPcap;
using System.Text.RegularExpressions;
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

        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private volatile bool _isPaused;
        private readonly ManualResetEventSlim _pauseGate = new(true);
        private string? _runningStatusText;

        // Recording (AVI)
        private AviTripletRecorder? _recorder;
        private bool _isRecording;
        private int _recordDropped;

        private volatile int _bValueDelta;

        private volatile byte _diffThreshold;
        private readonly byte[] _diffBgr = new byte[W * H_ACTIVE * 3];

        // If >0, forces B[pixel_ID] to 0 (simulated dead pixel). pixel_ID is 1..(W*H_ACTIVE).
        private int _forcedDeadPixelId;

        private volatile bool _darkPixelCompensationEnabled = false;

        private static readonly (int dx, int dy, int pct)[] DarkCompOffsets =
        [
            // Less aggressive compensation (Cassandra-style)
            // Cross (distance 1) => +15%
            (-1, 0, 15),
            (1, 0, 15),
            (0, -1, 15),
            (0, 1, 15),

            // Diagonals (distance 1) => +10%
            (-1, -1, 10),
            (-1, 1, 10),
            (1, -1, 10),
            (1, 1, 10),

            // Cross (distance 2) => +5%
            (-2, 0, 5),
            (2, 0, 5),
            (0, -2, 5),
            (0, 2, 5),
        ];

        private volatile bool _zeroZeroIsWhite = false;

        private bool _isLoadingSettings;
        private readonly string _settingsPath = AppSettingsStore.GetSettingsPath();

        // Live AVTP capture settings (Ethernet via SharpPcap)
        private bool _avtpLiveEnabled = true;
        private string? _avtpLiveDeviceHint;
        private bool _avtpLiveUdpEnabled = false;

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

        // Sequence mode: toggle A between two loaded images.
        private byte[]? _seqA;
        private byte[]? _seqB;
        private int _seqIndex; // 0 => A, 1 => B
        private string? _seqPathA;
        private string? _seqPathB;

        private sealed class SceneItem
        {
            public required string Path;
            public required byte[] Data;
            public required int DelayMs;
        }

        private List<SceneItem>? _sceneItems;
        private int _sceneIndex;
        private DateTime _sceneNextSwitchUtc;
        private string? _scenePath;
        private bool _sceneLoopEnabled = true;

        // AVI source (uncompressed indexed AVI)
        private AviUncompressedVideoReader? _avi;
        private int _aviIndex;
        private DateTime _aviNextSwitchUtc;
        private string? _aviPath;
        private bool _aviLoopEnabled = true;
        private byte[]? _aviCurrentFrame;

        // AVI "source fps" estimation (counts content changes/sec; good proxy for original AVTP-in fps
        // when the AVI was recorded at a fixed render fps with repeated frames).
        private byte[]? _aviPrevFrameForFps;
        private DateTime _aviFpsWindowStartUtc;
        private int _aviChangesInWindow;
        private double _aviSourceFps;
        private double _aviSourceFpsEma;

        // PCAP/AVTP-in fps smoothing
        private double _avtpInFpsEma;

        // Pane B measured fps smoothing
        private double _bFpsEma;

        // NEW: latest AVTP frame coming from UDP (320x80). If never received, it stays zero.
        private byte[] _avtpFrame = new byte[W * H_ACTIVE];
        private volatile bool _hasAvtpFrame = false;

        private Frame? _latestA;
        private Frame? _latestB;
        private CancellationTokenSource? _pcapCts;
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

        private readonly WriteableBitmap _wbA = MakeGray8Bitmap(W, H_ACTIVE);
        private readonly WriteableBitmap _wbB = MakeGray8Bitmap(W, H_ACTIVE);
        private readonly WriteableBitmap _wbD = MakeBgr24Bitmap(W, H_ACTIVE);

        private int _countA, _countB, _countD;
        private int _countAvtpIn;
        private int _countAvtpDropped;
        private int _countAvtpIncomplete;
        private int _countAvtpSeqGapFrames;
        private int _sumAvtpSeqGaps;
        private int _countLateFramesSkipped;
        private Stopwatch _statSw = Stopwatch.StartNew();

        private CancellationTokenSource? _saveFeedbackCts;

        private bool _wasWaitingForSignal;

        // AVTP/RVF live: track last frame arrival to detect signal loss while still Running.
        // (e.g. CANoe stops streaming: we want to clear the last frame and go back to "Signal not available".)
        private DateTime _lastAvtpFrameUtc = DateTime.MinValue;
        private static readonly TimeSpan LiveSignalLostTimeout = TimeSpan.FromSeconds(FpsEstimationWindowSec * 2.5);

        // When a live signal is lost, some capture stacks can still deliver a few late buffered packets.
        // Suppress accepting new live chunks for a short window to avoid flicker:
        // "Signal not available" -> last frame -> "Signal not available".
        private DateTime _suppressLiveUntilUtc = DateTime.MinValue;

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



        private bool IsLiveInputSuppressed()
        {
            if (_modeOfOperation != ModeOfOperation.AvtpLiveMonitor) return false;
            return DateTime.UtcNow < _suppressLiveUntilUtc;
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

        private int _targetFps;

        private CancellationTokenSource? _udpCts;
        private RvfUdpReceiver? _udp;
        private readonly RvfReassembler _rvf = new();

        private readonly object _rvfPushLock = new();

        private AvtpLiveCapture? _avtpLive;

        private AvtpRvfTransmitter? _tx;

        private enum AvtpFeed
        {
            None = 0,
            UdpRvf = 1,
            EthernetAvtp = 2,
            PcapReplay = 3,
        }

        // 0 => none (first valid source wins per Start)
        private int _activeAvtpFeed = (int)AvtpFeed.None;

        private string _lastRvfSrcLabel = "Ethernet/AVTP";

        public MainWindow()
        {
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
            if (_cts == null) return false;
            if (!_isRunning) return false;
            if (_modeOfOperation != ModeOfOperation.AvtpLiveMonitor) return false;

            // In AVTP Live mode, keep panes in "Signal not available" until first valid frame arrives.
            return !_hasAvtpFrame;
        }

        private void EnterWaitingForSignalState()
        {
            var prevFeed = GetActiveAvtpFeed();
            var lastAgeMs = _lastAvtpFrameUtc == DateTime.MinValue
                ? double.NaN
                : (DateTime.UtcNow - _lastAvtpFrameUtc).TotalMilliseconds;

            AppendUdpLog(
                $"[live] signal lost -> waiting | prevFeed={prevFeed} src={_lastRvfSrcLabel} " +
                $"ageMs={(double.IsNaN(lastAgeMs) ? "n/a" : lastAgeMs.ToString("F0", CultureInfo.InvariantCulture))} " +
                $"timeoutMs={LiveSignalLostTimeout.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} " +
                $"suppressMs={1000.ToString(CultureInfo.InvariantCulture)}");

            // Drop the last received frame and revert to the no-signal rendering path.
            _hasAvtpFrame = false;
            _lastAvtpFrameUtc = DateTime.MinValue;

            // Debounce: ignore late buffered live packets for a short time.
            _suppressLiveUntilUtc = DateTime.UtcNow.Add(TimeSpan.FromSeconds(1.0));

            // Force the "Waiting for signal..." status to be refreshed.
            _wasWaitingForSignal = false;

            // Reset reassembly so a fresh stream restart doesn't inherit seq/line state.
            try { _rvf.ResetAll(); } catch { }

            // Allow the next real source to become active again.
            Volatile.Write(ref _activeAvtpFeed, (int)AvtpFeed.None);

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
            Blit(_wbA, _noSignalGrayFrame, W);
            Blit(_wbB, _noSignalGrayFrame, W);
            BlitBgr24(_wbD, _noSignalGrayBgr, W * 3);
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
            if (!_isPaused)
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
            if (!_isPaused) return;
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
                if (_isPaused)
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

                if (pane == Pane.B)
                {
                    if (aLive == null && bLive == null) return null;
                    aLive ??= bLive;
                    bLive ??= aLive;
                    return (aLive != null && bLive != null) ? ApplyBPostProcessing(aLive, bLive) : null;
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
            bool comp = _darkPixelCompensationEnabled;
            int forcedId = Volatile.Read(ref _forcedDeadPixelId);

            if (!comp && forcedId <= 0) return b;

            byte[] baseB;
            if (forcedId > 0)
            {
                baseB = new byte[b.Data.Length];
                Buffer.BlockCopy(b.Data, 0, baseB, 0, baseB.Length);

                int idx = forcedId - 1;
                if (idx >= 0 && idx < baseB.Length) baseB[idx] = 0;
            }
            else
            {
                // no forced pixel; use the current B buffer as base
                baseB = b.Data;
            }

            if (!comp)
            {
                // Only the forced pixel feature is active.
                return forcedId > 0 ? new Frame(b.Width, b.Height, baseB, b.TimestampUtc) : b;
            }

            // Apply dark pixel compensation (Cassandra-style small neighborhood)
            byte[]? outData = null;
            int n = W * H_ACTIVE;
            for (int i = 0; i < n && i < a.Data.Length && i < baseB.Length; i++)
            {
                // Dark pixel: A has signal but ECU output is forced to 0
                if (a.Data[i] == 0 || baseB[i] != 0) continue;

                outData ??= Copy(baseB);

                int y = i / W;
                int x = i - (y * W);
                foreach (var (dx, dy, pct) in DarkCompOffsets)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if ((uint)nx >= (uint)W || (uint)ny >= (uint)H_ACTIVE) continue;

                    int ni = (ny * W) + nx;
                    byte v = baseB[ni];
                    if (v == 0) continue;

                    int boosted = (v * (100 + pct) + 50) / 100;
                    if (boosted > 255) boosted = 255;
                    if (boosted > outData[ni]) outData[ni] = (byte)boosted;
                }
            }

            if (outData == null)
            {
                // No dark pixels detected; but forced pixel may still be active.
                return forcedId > 0 ? new Frame(b.Width, b.Height, baseB, b.TimestampUtc) : b;
            }

            return new Frame(b.Width, b.Height, outData, b.TimestampUtc);
        }

        private static byte[] Copy(byte[] src)
        {
            var dst = new byte[src.Length];
            Buffer.BlockCopy(src, 0, dst, 0, src.Length);
            return dst;
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
            if (!_isPaused) return false;
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

            if (!_isPaused || zoom.ScaleX < OverlayMinZoom)
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

                            ComparePixelToBgr(aPx, bPx, _diffThreshold, _zeroZeroIsWhite, out var bl, out var gg, out var rr);
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
            // Hook RVF reassembler -> update latest AVTP frame
            _rvf.OnFrameReady += (frame, meta) =>
            {
                // Runs on receiver thread -> marshal to UI
                Dispatcher.Invoke(() =>
                {
                    // Keep status stable when stopped; ignore late frames during shutdown races.
                    if (!_isRunning)
                        return;

                    // Immediately after signal-loss we suppress late buffered frames to avoid flicker.
                    if (IsLiveInputSuppressed())
                        return;

                    Interlocked.Increment(ref _countAvtpIn);

                    bool incomplete = meta.linesWritten != H_ACTIVE;
                    bool gap = meta.seqGaps > 0;
                    if (incomplete) Interlocked.Increment(ref _countAvtpIncomplete);
                    if (gap)
                    {
                        Interlocked.Increment(ref _countAvtpSeqGapFrames);
                        Interlocked.Add(ref _sumAvtpSeqGaps, meta.seqGaps);
                    }
                    if (incomplete || gap) Interlocked.Increment(ref _countAvtpDropped);

                    // store latest frame
                    // Live streams may come as 320x80 (expected) OR 320x84 (bottom 4 lines are metadata).
                    // We always display/calculate on the active 320x80 area.
                    byte[]? useFrame = null;
                    if (frame.Length == W * H_ACTIVE)
                    {
                        useFrame = frame;
                    }
                    else if (frame.Length == W * (H_ACTIVE + 4))
                    {
                        useFrame = new byte[W * H_ACTIVE];
                        Buffer.BlockCopy(frame, 0, useFrame, 0, useFrame.Length);
                    }

                    if (useFrame != null)
                    {
                        _avtpFrame = useFrame;       // replace buffer (simple & safe)
                        _hasAvtpFrame = true;
                        _lastAvtpFrameUtc = DateTime.UtcNow;
                        // Increment _countB for AVTP Live mode FPS tracking
                        if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor)
                        {
                            Interlocked.Increment(ref _countB);
                        }
                    }

                    int lateSkip = Volatile.Read(ref _countLateFramesSkipped);
                    string late = lateSkip > 0 ? $" | lateSkip={lateSkip}" : "";

                    string src = GetActiveAvtpFeed() switch
                    {
                        AvtpFeed.UdpRvf => "UDP/RVFU",
                        AvtpFeed.EthernetAvtp => "Ethernet/AVTP",
                        AvtpFeed.PcapReplay => "PCAP",
                        _ => "?"
                    };

                    _lastRvfSrcLabel = src;

                    LblStatus.Text = $"AVTP RVF ({src}): frameId={meta.frameId} seq={meta.seq} lines={meta.linesWritten}/80 gaps={meta.seqGaps} | dropped={_countAvtpDropped} (gapFrames={_countAvtpSeqGapFrames}, incomplete={_countAvtpIncomplete}){late}";
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
            _rvf.ResetAll();
            _hasAvtpFrame = false;

            LblStatus.Text = _modeOfOperation == ModeOfOperation.AvtpLiveMonitor
                ? "Mode: AVTP Live (Monitoring). Press Start to listen/capture live stream."
                : "Mode: Generator/Player (Files). Load a file and press Start.";
        }

        private bool TrySetActiveAvtpFeed(AvtpFeed feed)
        {
            int f = Volatile.Read(ref _activeAvtpFeed);
            if (f == (int)feed) return true;
            if (f != (int)AvtpFeed.None) return false;

            return Interlocked.CompareExchange(ref _activeAvtpFeed, (int)feed, (int)AvtpFeed.None) == (int)AvtpFeed.None;
        }

        private AvtpFeed GetActiveAvtpFeed() => (AvtpFeed)Volatile.Read(ref _activeAvtpFeed);

        private sealed class LiveNicItem
        {
            public required string Display;
            public required string? DeviceName;
            public override string ToString() => Display;
        }

        private static string? TryExtractGuidFromPcapDeviceName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            // Typical Npcap device: \\Device\\NPF_{E493C20C-D117-4FDD-86F9-4405B80C085C}
            int open = name.IndexOf('{');
            int close = name.IndexOf('}', open + 1);
            if (open < 0 || close < 0 || close <= open) return null;

            string guid = name.Substring(open + 1, close - open - 1);
            return guid.Length >= 32 ? guid : null;
        }

        private static NetworkInterface? TryFindNetworkInterfaceByGuid(string? guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (string.Equals(ni.Id, guid, StringComparison.OrdinalIgnoreCase))
                        return ni;
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static string DescribeCaptureDeviceForUi(string? pcapName, string? pcapDesc)
        {
            string name = pcapName ?? string.Empty;
            string desc = pcapDesc ?? string.Empty;

            string? guid = TryExtractGuidFromPcapDeviceName(name);
            var ni = TryFindNetworkInterfaceByGuid(guid);
            if (ni == null)
            {
                return string.IsNullOrWhiteSpace(desc) ? name : $"{desc}  ({name})";
            }

            string ips = string.Empty;
            try
            {
                var ipProps = ni.GetIPProperties();
                var v4 = new List<string>();
                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua?.Address != null && ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        v4.Add(ua.Address.ToString());
                }
                if (v4.Count > 0) ips = string.Join(",", v4);
            }
            catch
            {
                // ignore
            }

            string up = ni.OperationalStatus == OperationalStatus.Up ? "Up" : ni.OperationalStatus.ToString();
            string ipPart = string.IsNullOrWhiteSpace(ips) ? string.Empty : $" {ips}";

            string mac = string.Empty;
            try
            {
                var pa = ni.GetPhysicalAddress();
                if (pa != null)
                {
                    var b = pa.GetAddressBytes();
                    if (b.Length == 6) mac = string.Join("-", b.Select(x => x.ToString("X2")));
                }
            }
            catch { /* ignore */ }

            string macPart = string.IsNullOrWhiteSpace(mac) ? string.Empty : $" {mac}";

            // Keep it compact; the raw NPF_{GUID} is still visible at the end.
            return $"{ni.Name} [{up}]{ipPart}{macPart} — {desc}  ({name})";
        }

        private static string GetAvtpBpfFilter()
            => "ether proto 0x22f0 or (vlan and ether proto 0x22f0) or (vlan and vlan and ether proto 0x22f0)";

        private static bool LooksLikeLoopbackDevice(string? name, string? desc)
        {
            var n = name ?? string.Empty;
            var d = desc ?? string.Empty;
            return n.Contains("NPF_Loopback", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Loopback", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEthernetAvtp22F0(byte[] data)
        {
            // Ethernet II: dst(6) src(6) type(2)
            if (data == null || data.Length < 14) return false;

            static ushort ReadU16BE(byte[] b, int offset)
                => (ushort)((b[offset] << 8) | b[offset + 1]);

            ushort type = ReadU16BE(data, 12);
            if (type == 0x22F0) return true;

            // VLAN tags: 0x8100 (802.1Q), 0x88A8 (802.1ad), 0x9100 (common QinQ)
            if (type == 0x8100 || type == 0x88A8 || type == 0x9100)
            {
                if (data.Length < 18) return false;
                ushort inner = ReadU16BE(data, 16);
                if (inner == 0x22F0) return true;

                // QinQ: VLAN-in-VLAN
                if ((inner == 0x8100 || inner == 0x88A8 || inner == 0x9100) && data.Length >= 22)
                {
                    ushort inner2 = ReadU16BE(data, 20);
                    return inner2 == 0x22F0;
                }
            }

            return false;
        }

        private static int ProbeSingleDeviceForAvtp(ICaptureDevice dev, int durationMs)
        {
            // Best-effort: some devices may fail to open or capture.
            int count = 0;
            void OnArrival(object s, PacketCapture e)
            {
                try
                {
                    var raw = e.GetPacket();
                    var data = raw?.Data;
                    if (data == null || data.Length == 0) return;

                    // Count only real Ethernet AVTP frames (ethertype 0x22F0), not random loopback/UDP noise.
                    if (IsEthernetAvtp22F0(data))
                        Interlocked.Increment(ref count);
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                dev.OnPacketArrival += OnArrival;

                // Promiscuous, small timeout.
                dev.Open(DeviceModes.Promiscuous, 250);
                try { dev.Filter = GetAvtpBpfFilter(); } catch { /* ignore */ }

                dev.StartCapture();
                Thread.Sleep(durationMs);
            }
            finally
            {
                try { dev.StopCapture(); } catch { }
                try { dev.Close(); } catch { }
                try { dev.OnPacketArrival -= OnArrival; } catch { }
            }

            return count;
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
                string display = DescribeCaptureDeviceForUi(name, desc);
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
                var first = devs.FirstOrDefault(d => !LooksLikeLoopbackDevice(d.Name, d.Description));
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

        private void BtnRefreshNics_Click(object sender, RoutedEventArgs e)
        {
            RefreshLiveNicList();
        }


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
            if (_cts == null || _isPaused)
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
            if (_cts == null || _isPaused) RenderAll();
        }

        private void TxtDeadPixelId_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            int id = 0;
            if (TxtDeadPixelId != null && int.TryParse(TxtDeadPixelId.Text, out var parsed))
                id = Math.Clamp(parsed, 0, W * H_ACTIVE);

            Volatile.Write(ref _forcedDeadPixelId, id);

            SaveUiSettings();

            // If not running (or paused), update display immediately.
            if (_cts == null || _isPaused) RenderAll();
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
            if (_isRecording) StopRecording();
            StopAll();
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtFps.Text, out var fps) || fps <= 0) fps = 100;

            if (!_isRunning)
            {
                _pauseGate.Set();
                _isPaused = false;
                Start(fps);
                _isRunning = true;
                if (BtnStart != null) BtnStart.Content = "Pause";
                return;
            }

            if (!_isPaused)
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
            if (_isRecording) StopRecording();
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
                a = _isPaused ? _pausedA : _latestA;
                b = _isPaused ? _pausedB : _latestB;
            }

            if (a == null || b == null)
            {
                // Force a deterministic one-frame render to populate latest frames.
                RenderOneFrameNow();
                lock (_frameLock)
                {
                    a = _isPaused ? _pausedA : _latestA;
                    b = _isPaused ? _pausedB : _latestB;
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
            string outDir = GetFrameSnapshotsOutputDirectory();

            var paths = MakeUniqueSaveSetPaths(outDir, ts);

            try
            {
                LblStatus.Text = $"Saving report + images… ({System.IO.Path.GetFileName(paths.XlsxPath)})";

                var aBytes = (byte[])a.Data.Clone();
                var bBytes = (byte[])bPost.Data.Clone();
                byte thr = _diffThreshold;

                // Create the D snapshot exactly like the UI render (B−A mapping to BGR24).
                var dBgr = new byte[W * H_ACTIVE * 3];
                RenderCompareToBgr(dBgr, aBytes, bBytes, W, H_ACTIVE, thr, _zeroZeroIsWhite,
                    out _, out _, out _,
                    out _, out _, out _,
                    out _);

                await Task.Run(() =>
                {
                    AviTripletRecorder.SaveSingleFrameCompareXlsx(paths.XlsxPath, frameNr, aBytes, bBytes, W, H_ACTIVE, deviationThreshold: thr);

                    // Save 1:1 snapshots for all panes.
                    SaveGray8Png(paths.APath, aBytes, W, H_ACTIVE);
                    SaveGray8Png(paths.BPath, bBytes, W, H_ACTIVE);
                    SaveBgr24Png(paths.DPath, dBgr, W, H_ACTIVE);
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
                string dir = GetFrameSnapshotsOutputDirectory();
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

        private static (string APath, string BPath, string DPath, string XlsxPath) MakeUniqueSaveSetPaths(string outDir, string ts)
        {
            // Keep the same naming template as Record:
            //   <ts>_AVTP.*, <ts>_LVDS.*, <ts>_Compare.*
            // but ensure the whole set is unique (Save might be pressed multiple times within one second).
            for (int i = 0; i <= 999; i++)
            {
                string suffix = i == 0 ? string.Empty : $"_{i:000}";
                string stem = ts + suffix;

                string a = System.IO.Path.Combine(outDir, $"{stem}_AVTP.png");
                string b = System.IO.Path.Combine(outDir, $"{stem}_LVDS.png");
                string d = System.IO.Path.Combine(outDir, $"{stem}_Compare.png");
                string x = System.IO.Path.Combine(outDir, $"{stem}_Compare.xlsx");

                if (!File.Exists(a) && !File.Exists(b) && !File.Exists(d) && !File.Exists(x))
                    return (a, b, d, x);
            }

            // Fallback: very unlikely; just return the base names.
            return (
                System.IO.Path.Combine(outDir, $"{ts}_AVTP.png"),
                System.IO.Path.Combine(outDir, $"{ts}_LVDS.png"),
                System.IO.Path.Combine(outDir, $"{ts}_Compare.png"),
                System.IO.Path.Combine(outDir, $"{ts}_Compare.xlsx"));
        }

        private static void SaveGray8Png(string path, byte[] grayTopDown, int w, int h)
        {
            int stride = w;
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, grayTopDown, stride);
            src.Freeze();

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            enc.Save(fs);
        }

        private static void SaveBgr24Png(string path, byte[] bgrTopDown, int w, int h)
        {
            int stride = w * 3;
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, bgrTopDown, stride);
            src.Freeze();

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            enc.Save(fs);
        }

        private int GetCurrentFrameNumberHint()
        {
            return _lastLoaded switch
            {
                LoadedSource.Avi => _aviIndex + 1,
                LoadedSource.Scene => _sceneIndex + 1,
                LoadedSource.Sequence => _seqIndex + 1,
                _ => 1
            };
        }

        private string GetCurrentSourceTag()
        {
            return _lastLoaded switch
            {
                LoadedSource.Avi => $"AVI_Frame{_aviIndex + 1}",
                LoadedSource.Scene => $"Scene_Step{_sceneIndex + 1}",
                LoadedSource.Sequence => $"Seq_{(_seqIndex == 0 ? "A" : "B")}",
                LoadedSource.Pcap => "PCAP",
                LoadedSource.Image => "Image",
                _ => "Frame"
            };
        }

        private void StartRecording()
        {
            if (_cts == null || !_isRunning || _isPaused)
            {
                MessageBox.Show("Recording works while running (not paused). Press Start (and unpause) first.", "Record");
                return;
            }

            int fps = (int)Math.Clamp(_targetFps > 0 ? _targetFps : 30, 1, 1000);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string outDir = GetVideoRecordOutputDirectory();

            string pathA = MakeUniquePath(global::System.IO.Path.Combine(outDir, $"{ts}_AVTP.avi"));
            string pathB = MakeUniquePath(global::System.IO.Path.Combine(outDir, $"{ts}_LVDS.avi"));
            string pathD = MakeUniquePath(global::System.IO.Path.Combine(outDir, $"{ts}_Compare.avi"));
            string pathXlsx = global::System.IO.Path.ChangeExtension(pathD, ".xlsx");

            try
            {
                _recorder?.Dispose();
                _recorder = new AviTripletRecorder(pathA, pathB, pathD, W, H_ACTIVE, fps, compareCsvPath: pathXlsx, compareDeadband: _diffThreshold);
                _recordDropped = 0;
                _isRecording = true;
                if (BtnRecord != null) BtnRecord.Content = "Stop Rec";
                LblStatus.Text = $"Recording AVI to: {outDir}  ({ts}_AVTP/LVDS/Compare) @ {fps} fps";
            }
            catch (Exception ex)
            {
                try { _recorder?.Dispose(); } catch { }
                _recorder = null;
                _isRecording = false;
                if (BtnRecord != null) BtnRecord.Content = "Record";
                MessageBox.Show($"Failed to start recording: {ex.Message}", "Record error");
            }
        }

        private static string GetVideoRecordOutputDirectory()
        {
            // Requested location: \docs\outputs\videoRecords (under repo/project root)
            // Try to find a parent folder that contains "docs".
            string? root = FindRepoRootWithDocs(AppContext.BaseDirectory)
                           ?? FindRepoRootWithDocs(Directory.GetCurrentDirectory());

            string baseDir = root ?? Directory.GetCurrentDirectory();
            string outDir = global::System.IO.Path.Combine(baseDir, "docs", "outputs", "videoRecords");
            Directory.CreateDirectory(outDir);
            return outDir;
        }

        private static string GetFrameSnapshotsOutputDirectory()
        {
            // Requested location: \docs\outputs\frameSnapshots (under repo/project root)
            // Try to find a parent folder that contains "docs".
            string? root = FindRepoRootWithDocs(AppContext.BaseDirectory)
                           ?? FindRepoRootWithDocs(Directory.GetCurrentDirectory());

            string baseDir = root ?? Directory.GetCurrentDirectory();
            string outDir = global::System.IO.Path.Combine(baseDir, "docs", "outputs", "frameSnapshots");
            Directory.CreateDirectory(outDir);
            return outDir;
        }

        private static string? FindRepoRootWithDocs(string startPath)
        {
            try
            {
                var dir = new DirectoryInfo(startPath);
                if (dir.Exists == false)
                    dir = new DirectoryInfo(global::System.IO.Path.GetDirectoryName(startPath) ?? startPath);

                for (int i = 0; i < 8 && dir != null; i++)
                {
                    var docs = new DirectoryInfo(global::System.IO.Path.Combine(dir.FullName, "docs"));
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

        private static string MakeUniquePath(string desiredPath)
        {
            if (!File.Exists(desiredPath))
                return desiredPath;

            string dir = global::System.IO.Path.GetDirectoryName(desiredPath) ?? "";
            string name = global::System.IO.Path.GetFileNameWithoutExtension(desiredPath);
            string ext = global::System.IO.Path.GetExtension(desiredPath);

            for (int i = 1; i <= 999; i++)
            {
                string p = global::System.IO.Path.Combine(dir, $"{name}_{i:000}{ext}");
                if (!File.Exists(p))
                    return p;
            }

            return desiredPath;
        }

        private void StopRecording()
        {
            _isRecording = false;
            try { _recorder?.Dispose(); } catch { }
            _recorder = null;

            if (BtnRecord != null) BtnRecord.Content = "Record";

            LblStatus.Text = _recordDropped > 0
                ? $"Recording stopped. Dropped frames (queue full): {_recordDropped}"
                : "Recording stopped.";
        }

        private void Pause()
        {
            _isPaused = true;
            _pauseGate.Reset();

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
            LblStatus.Text = "Paused.";

            UpdateOverlaysAll();
        }

        private void Resume()
        {
            _isPaused = false;
            _pauseGate.Set();

            lock (_frameLock)
            {
                _pausedA = null;
                _pausedB = null;
                _pausedD = null;
            }

            if (BtnStart != null) BtnStart.Content = "Pause";
            if (LblRunInfoA != null)
            {
                double shownFps = GetShownFps(avtpInFps: _avtpInFpsEma);
                LblRunInfoA.Text = shownFps > 0 ? $"Running @: {shownFps:F1} fps" : "Running";
            }
            LblStatus.Text = _runningStatusText ?? "Running.";

            ClearOverlay(Pane.A);
            ClearOverlay(Pane.B);
            ClearOverlay(Pane.D);
        }

        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            // legacy handler (kept for compatibility) -> delegate to the unified loader
            BtnLoadFiles_Click(sender, e);
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
            ClearAvi();
            _avtpInFpsEma = 0.0;
            _lastLoaded = LoadedSource.Pcap;
            _lastLoadedPcapPath = path;
            _sceneItems = null;
            _scenePath = null;
            LblStatus.Text = "PCAP loaded. Press Start to begin replay.";
        }

        private void LoadSingleImage(string path)
        {
            ClearAvi();
            _avtpInFpsEma = 0.0;
            // Switching sources: drop any previously replayed/received AVTP frame
            _hasAvtpFrame = false;

            (int width, int height, byte[] data) img = LoadImageAsGray8(path);

            if (img.width < W || img.height < H_ACTIVE)
                throw new InvalidOperationException($"Expected at least {W}x{H_ACTIVE}, got {img.width}x{img.height}.");

            // Always crop A to 320x80 from top-left (x=0,y=0)
            _pgmFrame = CropTopLeftGray8(img.data, img.width, img.height, W, H_ACTIVE);

            // Keep an optional 320x84 buffer (also top-left) for future LVDS usage.
            _lvdsFrame84 = img.height >= H_LVDS ? CropTopLeftGray8(img.data, img.width, img.height, W, H_LVDS) : null;

            _lastLoaded = LoadedSource.Image;
            _lastLoadedPcapPath = null;
            _sceneItems = null;
            _scenePath = null;

            LblStatus.Text = "Image loaded (PGM Gray8 or BMP/PNG→Gray8 u8). Press Start to begin rendering.";

            if (_cts == null || _isPaused) RenderOneFrameNow();
        }

        private void LoadAvi(string path)
        {
            ClearAvi();

            // Switching sources: stop using previously replayed/received AVTP frame
            _hasAvtpFrame = false;

            _avi = AviUncompressedVideoReader.Open(path);
            _aviPath = path;
            _aviIndex = 0;
            _aviCurrentFrame = _avi.ReadFrameAsGray8TopDown(_aviIndex, W, H_ACTIVE);
            _aviNextSwitchUtc = DateTime.UtcNow.AddMilliseconds(_avi.FrameDurationMs);

            _aviPrevFrameForFps = _aviCurrentFrame;
            _aviFpsWindowStartUtc = DateTime.UtcNow;
            _aviChangesInWindow = 0;
            _aviSourceFps = 0.0;
            _aviSourceFpsEma = 0.0;

            _lastLoaded = LoadedSource.Avi;
            _lastLoadedPcapPath = null;
            _sceneItems = null;
            _scenePath = null;

            string name = System.IO.Path.GetFileName(path);
            LblStatus.Text = $"AVI loaded: '{name}' ({_avi.Width}x{_avi.Height}, {(_avi.BitsPerPixel)}bpp, frames={_avi.FrameCount}, frameMs={_avi.FrameDurationMs:F1}). Press Start to play; Prev/Next steps frames.";

            if (_cts == null || _isPaused) RenderOneFrameNow();
        }

        private void ClearAvi()
        {
            try { _avi?.Dispose(); } catch { }
            _avi = null;
            _aviPath = null;
            _aviIndex = 0;
            _aviNextSwitchUtc = DateTime.MinValue;
            _aviCurrentFrame = null;

            _aviPrevFrameForFps = null;
            _aviFpsWindowStartUtc = DateTime.MinValue;
            _aviChangesInWindow = 0;
            _aviSourceFps = 0.0;
            _aviSourceFpsEma = 0.0;
        }

        private static (int width, int height, byte[] data) LoadImageAsGray8(string path)
        {
            string ext = global::System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".pgm")
            {
                var img = Pgm.Load(path);
                return (img.Width, img.Height, img.Data);
            }

            if (ext == ".bmp" || ext == ".png")
            {
                // Use WPF decoder so we don't depend on System.Drawing / GDI+.
                var uri = new Uri(path, UriKind.Absolute);
                var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource src = decoder.Frames[0];

                if (src.Format != PixelFormats.Gray8)
                {
                    src = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
                    src.Freeze();
                }

                int w = src.PixelWidth;
                int h = src.PixelHeight;
                int stride = w; // Gray8 => 1 byte/pixel
                var data = new byte[stride * h];
                src.CopyPixels(data, stride, 0);
                return (w, h, data);
            }

            throw new NotSupportedException($"Unsupported image format '{ext}'. Supported: .pgm, .bmp, .png");
        }

        private static byte[] CropTopLeftGray8(byte[] src, int srcW, int srcH, int cropW, int cropH)
        {
            if (cropW <= 0 || cropH <= 0) throw new ArgumentOutOfRangeException(nameof(cropW));
            if (srcW < cropW || srcH < cropH) throw new ArgumentException("Source smaller than crop.");

            var dst = new byte[cropW * cropH];
            for (int y = 0; y < cropH; y++)
            {
                Buffer.BlockCopy(src, y * srcW, dst, y * cropW, cropW);
            }
            return dst;
        }

        private void Start(int fps)
        {
            StopBlackTxLoop();
            // Safety: if already running, stop first
            if (_cts != null)
                StopAll();

            _targetFps = fps;

            // -----------------------------
            // Init run state
            // -----------------------------
            _cts = new CancellationTokenSource();
            _isRunning = true;
            _isPaused = false;
            _pauseGate.Set();

            AppendUdpLog($"[start] mode={_modeOfOperation}, fps={fps}");

            // Reset runtime stats
            ApplyNoSignalUiState(noSignal: false);
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

            // Reset feed selection + reassembler state
            Volatile.Write(ref _activeAvtpFeed, (int)AvtpFeed.None);
            _rvf.ResetAll();
            _hasAvtpFrame = false;
            _lastAvtpFrameUtc = DateTime.MinValue;

            // Default source label before first frame
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor)
            {
                if (_avtpLiveEnabled) _lastRvfSrcLabel = "Ethernet/AVTP";
                else if (_avtpLiveUdpEnabled) _lastRvfSrcLabel = "UDP/RVFU";
                // Explicitly force the 'Waiting for signal...' state at first start
                EnterWaitingForSignalState();
            }
            else if (_lastLoaded == LoadedSource.Pcap)
            {
                _lastRvfSrcLabel = "PCAP";
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
                _ = Task.Run(() => GeneratorLoopAsync(fps, _cts.Token));
                _ = Task.Run(() => UiRefreshLoop(_cts.Token));

                LblStatus.Text = $"Running Player @ {fps} fps (AVTP TX enabled)";
            }
            else
            {
                // AVTP Live Monitor:
                _ = Task.Run(() => UiRefreshLoop(_cts.Token));

                // Until the first frame arrives, show explicit waiting message.
                LblStatus.Text =
                    $"Waiting for signal... (0.0 fps) (Mode=AVTP Live). Ethernet/AVTP capture best-effort" +
                    (_avtpLiveUdpEnabled ? $"; UDP/RVFU on 0.0.0.0:{RvfProtocol.DefaultPort}" : "") +
                    $". (log: {GetUdpLogPath()})";

                _wasWaitingForSignal = true;
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
                    try
                    {
                        // Ensure we use the NIC currently selected in the UI (avoids slow/incorrect auto-pick).
                        string? deviceHint = _avtpLiveDeviceHint;
                        if (CmbLiveNic?.SelectedItem is LiveNicItem sel && !string.IsNullOrWhiteSpace(sel.DeviceName))
                            deviceHint = sel.DeviceName;

                        // Persist the final hint so next Start uses the same interface.
                        _avtpLiveDeviceHint = deviceHint;

                        try { _avtpLive?.Dispose(); } catch { }
                        _avtpLive = null;
                        var devs = AvtpLiveCapture.ListDevicesSafe();
                        if (devs.Count > 0)
                        {
                            AppendUdpLog("[avtp-live] devices:");
                            for (int i = 0; i < devs.Count; i++)
                                AppendUdpLog($"  [{i}] {DescribeCaptureDeviceForUi(devs[i].Name, devs[i].Description)}");
                        }

                        _avtpLive = AvtpLiveCapture.Start(
                            deviceHint: deviceHint,
                            log: msg => AppendUdpLog(msg),
                            onChunk: chunk =>
                            {
                                if (IsLiveInputSuppressed())
                                    return;

                                if (!TrySetActiveAvtpFeed(AvtpFeed.EthernetAvtp))
                                    return;

                                lock (_rvfPushLock)
                                {
                                    _rvf.Push(chunk);
                                }
                            });
                    }
                    catch (Exception ex)
                    {
                        AppendUdpLog($"[avtp-live] start failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Optional UDP/RVFU
                if (_avtpLiveUdpEnabled)
                {
                    if (_udpCts == null)
                    {
                        _udpCts = new CancellationTokenSource();
                        _udp = new RvfUdpReceiver(RvfProtocol.DefaultPort);

                        AppendUdpLog($"UDP start on 0.0.0.0:{RvfProtocol.DefaultPort}");

                        _udp.OnChunk += c =>
                        {
                            if (IsLiveInputSuppressed())
                                return;

                            if (!TrySetActiveAvtpFeed(AvtpFeed.UdpRvf))
                                return;

                            lock (_rvfPushLock)
                            {
                                _rvf.Push(c);
                            }
                        };

                        _ = Task.Run(() => _udp!.RunAsync(_udpCts.Token));
                    }
                }
            }
            
        }

        private void StartPcapReplay(string path)
        {
            // Cancel any existing PCAP replay
            if (_pcapCts != null)
            {
                try { _pcapCts.Cancel(); } catch { }
                _pcapCts.Dispose();
                _pcapCts = null;
            }

            _pcapCts = new CancellationTokenSource();
            var ct = _pcapCts.Token;

            // Ensure we start from a clean reassembly state when replaying.
            _rvf.ResetAll();
            _hasAvtpFrame = false;
            _lastAvtpFrameUtc = DateTime.MinValue;

            Volatile.Write(ref _activeAvtpFeed, (int)AvtpFeed.PcapReplay);

            AppendUdpLog($"[pcap] replay start: {path}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await PcapAvtpRvfReplay.ReplayAsync(
                        path,
                        chunk =>
                        {
                            if (IsLiveInputSuppressed())
                                return;

                            if (!TrySetActiveAvtpFeed(AvtpFeed.PcapReplay))
                                return;

                            lock (_rvfPushLock)
                            {
                                _rvf.Push(chunk);
                            }
                        },
                        msg => AppendUdpLog(msg),
                        ct,
                        speed: 1.0,
                        pauseGate: _pauseGate);

                    if (!ct.IsCancellationRequested)
                    {
                        AppendUdpLog("[pcap] replay done");

                        // Auto-stop after replay: show idle gradient and reset UI.
                        Dispatcher.Invoke(() =>
                        {
                            if (_lastLoaded == LoadedSource.Pcap)
                                StopAll();
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    Dispatcher.Invoke(() => { LblStatus.Text = "PCAP: cancelled"; });
                    AppendUdpLog("[pcap] replay cancelled");
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => { LblStatus.Text = "PCAP: error"; });
                    AppendUdpLog($"[pcap] replay error: {ex}");
                }
            }, ct);
        }

        private void StopRenderLoops()
        {
            _cts?.Cancel();
            _cts = null;

            _targetFps = 0;
            if (LblRunInfoA != null)
                LblRunInfoA.Text = "";
            if (LblRunInfoB != null)
                LblRunInfoB.Text = "";

            _countA = _countB = _countD = 0;
            _statSw.Restart();
            _bFpsEma = 0.0;
            _wasWaitingForSignal = false;

            ShowIdleGradient();
            LblStatus.Text = "Render stopped.";
        }

        private void StopAll()
        {
            if (_isRecording) StopRecording();
            _isPaused = false;
            _isRunning = false;
            _runningStatusText = null;
            if (BtnStart != null) BtnStart.Content = "Start";

            _countLateFramesSkipped = 0;

            lock (_frameLock)
            {
                _pausedA = null;
                _pausedB = null;
                _pausedD = null;
            }

            StopRenderLoops();

            _countAvtpIn = 0;
            _countAvtpDropped = 0;
            _countAvtpIncomplete = 0;
            _countAvtpSeqGapFrames = 0;
            _sumAvtpSeqGaps = 0;
            _avtpInFpsEma = 0.0;
            if (LblAvtpInFps != null) LblAvtpInFps.Text = "";
            if (LblAvtpDropped != null) LblAvtpDropped.Text = "";

            if (_pcapCts != null)
            {
                try { _pcapCts.Cancel(); } catch { }
                _pcapCts.Dispose();
                _pcapCts = null;
            }

            if (_udpCts != null)
            {
                _udpCts.Cancel();
                _udpCts.Dispose();
                _udpCts = null;

                _udp?.Dispose();
                _udp = null;
            }

            if (_avtpLive != null)
            {
                try { _avtpLive.Dispose(); } catch { }
                _avtpLive = null;
            }

            Volatile.Write(ref _activeAvtpFeed, (int)AvtpFeed.None);

            // Ensure we don't remain paused after stopping.
            _pauseGate.Set();

            // Stop should behave like a reset for file-backed sources.
            if (_lastLoaded == LoadedSource.Avi && _avi != null)
            {
                try
                {
                    _aviIndex = 0;
                    _aviCurrentFrame = _avi.ReadFrameAsGray8TopDown(_aviIndex, W, H_ACTIVE);
                    _aviNextSwitchUtc = DateTime.UtcNow.AddMilliseconds(_avi.FrameDurationMs);
                }
                catch
                {
                    // ignore AVI reset errors on stop
                }
            }

            SaveUiSettings();

            LblStatus.Text = $"AVTP RVF ({_lastRvfSrcLabel}): Stopped.";

            ClearOverlay(Pane.A);
            ClearOverlay(Pane.B);
            ClearOverlay(Pane.D);
        }

        private static readonly object _udpLogLock = new();

        private static string GetUdpLogPath()
            => System.IO.Path.Combine(AppContext.BaseDirectory, "udp_rx.log");

        private static void AppendUdpLog(string message)
        {
            try
            {
                lock (_udpLogLock)
                {
                    File.AppendAllText(GetUdpLogPath(), $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
                }
            }
            catch
            {
                // ignore logging errors
            }
        }

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

        private void BtnLoadSeqA_Click(object sender, RoutedEventArgs e)
        {
            LoadSequenceImage(isA: true);
        }

        private void BtnLoadSeqB_Click(object sender, RoutedEventArgs e)
        {
            LoadSequenceImage(isA: false);
        }

        private void BtnSeqPrev_Click(object sender, RoutedEventArgs e)
        {
            StepSequence(-1);
        }

        private void BtnSeqNext_Click(object sender, RoutedEventArgs e)
        {
            StepSequence(+1);
        }

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
                img = LoadImageAsGray8(dlg.FileName);
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

            var cropped = CropTopLeftGray8(img.data, img.width, img.height, W, H_ACTIVE);

            if (isA)
            {
                _seqA = cropped;
                _seqPathA = dlg.FileName;
                _seqIndex = 0;
            }
            else
            {
                _seqB = cropped;
                _seqPathB = dlg.FileName;
                _seqIndex = 1;
            }

            // Switching sources: stop using previously replayed/received AVTP frame.
            _hasAvtpFrame = false;
            _lastLoaded = LoadedSource.Sequence;
            _lastLoadedPcapPath = null;

            LblStatus.Text = BuildSequenceStatus();

            // If not running (or paused), update display immediately.
            if (_cts == null || _isPaused) RenderOneFrameNow();
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

            if (_seqA == null && _seqB == null)
            {
                LblStatus.Text = "Sequence: load Seq A and/or Seq B first.";
                return;
            }

            // Toggle between A and B.
            _seqIndex = 1 - _seqIndex;

            _hasAvtpFrame = false;
            _lastLoaded = LoadedSource.Sequence;
            _lastLoadedPcapPath = null;

            LblStatus.Text = BuildSequenceStatus();

            // If not running (or paused), update display immediately.
            if (_cts == null || _isPaused) RenderOneFrameNow();
        }

        private void StepAvi(int dir)
        {
            var avi = _avi;
            if (avi == null)
            {
                LblStatus.Text = "AVI: load an .avi first.";
                return;
            }

            int n = avi.FrameCount;
            if (n <= 0)
            {
                LblStatus.Text = "AVI: no frames.";
                return;
            }

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
            _aviCurrentFrame = avi.ReadFrameAsGray8TopDown(_aviIndex, W, H_ACTIVE);
            _aviNextSwitchUtc = DateTime.UtcNow.AddMilliseconds(avi.FrameDurationMs);

            // Manual stepping should not pollute the rate estimate; reset the window.
            _aviPrevFrameForFps = _aviCurrentFrame;
            _aviFpsWindowStartUtc = DateTime.UtcNow;
            _aviChangesInWindow = 0;
            _aviSourceFps = 0.0;
            _aviSourceFpsEma = 0.0;

            string name = _aviPath != null ? System.IO.Path.GetFileName(_aviPath) : "<avi>";
            LblStatus.Text = $"AVI '{name}': frame {_aviIndex + 1}/{n} (loop={(_aviLoopEnabled ? "ON" : "OFF")}).";
            if (_cts == null || _isPaused) RenderOneFrameNow();
        }

        private void StepScene(int dir)
        {
            var items = _sceneItems;
            if (items == null || items.Count == 0)
            {
                LblStatus.Text = "Scene: load a .scene first.";
                return;
            }

            lock (_frameLock)
            {
                int n = items.Count;
                _sceneIndex = (_sceneIndex + (dir < 0 ? -1 : 1)) % n;
                if (_sceneIndex < 0) _sceneIndex += n;
                _sceneNextSwitchUtc = DateTime.UtcNow.AddMilliseconds(items[_sceneIndex].DelayMs);
            }

            LblStatus.Text = BuildSceneStatus();
            if (_cts == null || _isPaused) RenderOneFrameNow();
        }

        private static List<string> SplitTopLevelObjects(string text)
        {
            // Extract top-level '{ ... }' blocks. This is a tiny brace-level parser, good enough for this scene format.
            var blocks = new List<string>();
            int depth = 0;
            int start = -1;
            bool inString = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    // toggle string state (scene files don't escape quotes typically)
                    inString = !inString;
                    continue;
                }
                if (inString) continue;

                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        blocks.Add(text.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
            return blocks;
        }

        private static (List<(string path, int delayMs)> steps, bool loop) ParseSimpleScene(string text)
        {
            // Minimal scene format:
            // - optional globals:
            //     - delayMs = 500
            //     - loop = true|false
            // - steps can be provided as:
            //     - img1 = file.bmp, img2 = file.pgm, ...
            //     - step1 = file.bmp, step2 = file.pgm, ...
            //     - or one image path per line
            // - optional per-step delay:
            //     - delayMs1 = 500, delayMs2 = 1000, ...
            // - comments: lines starting with //, #, ;
            var stepByIndex = new Dictionary<int, string>();
            var delayByIndex = new Dictionary<int, int>();
            var loosePaths = new List<string>();
            int defaultDelayMs = 500;
            bool loop = true;

            using var sr = new StringReader(text);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#') || line.StartsWith(';'))
                    continue;

                int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIdx >= 0) line = line.Substring(0, commentIdx).Trim();
                if (line.Length == 0) continue;

                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
                        value = value.Substring(1, value.Length - 2);

                    if (key.Equals("delayMs", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value, out var d) && d > 0) defaultDelayMs = d;
                        continue;
                    }
                    if (key.Equals("loop", StringComparison.OrdinalIgnoreCase))
                    {
                        loop = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    // delayMsN (per-step)
                    var mDelayN = Regex.Match(key, @"^delayMs(\d+)$", RegexOptions.IgnoreCase);
                    if (mDelayN.Success && int.TryParse(mDelayN.Groups[1].Value, out var delayIndex))
                    {
                        if (int.TryParse(value, out var dN) && dN > 0)
                            delayByIndex[delayIndex] = dN;
                        continue;
                    }

                    // step/img indices (img1/img2/step1/step2/...)
                    var mStep = Regex.Match(key, @"^(img|image|step|frame)(\d+)$", RegexOptions.IgnoreCase);
                    if (mStep.Success && int.TryParse(mStep.Groups[2].Value, out var stepIndex))
                    {
                        if (!string.IsNullOrWhiteSpace(value))
                            stepByIndex[stepIndex] = value;
                        continue;
                    }

                    // Allow img (no index) as an append
                    if (key.Equals("img", StringComparison.OrdinalIgnoreCase) || key.Equals("image", StringComparison.OrdinalIgnoreCase) || key.Equals("step", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(value))
                            loosePaths.Add(value);
                        continue;
                    }
                }

                // Treat remaining non-empty lines as image paths.
                if (line.StartsWith('"') && line.EndsWith('"') && line.Length >= 2)
                    line = line.Substring(1, line.Length - 2);

                loosePaths.Add(line);
            }

            // Build ordered step list.
            var steps = new List<(string path, int delayMs)>();

            if (stepByIndex.Count > 0)
            {
                foreach (var idx in stepByIndex.Keys.OrderBy(i => i))
                {
                    string p = stepByIndex[idx];
                    int d = delayByIndex.TryGetValue(idx, out var dd) ? dd : defaultDelayMs;
                    steps.Add((p, d));
                }
            }

            // Append any loose paths (keeps file order)
            foreach (var p in loosePaths)
            {
                steps.Add((p, defaultDelayMs));
            }

            return (steps, loop);
        }

        private void LoadScene(string scenePath)
        {
            ClearAvi();
            _avtpInFpsEma = 0.0;
            string text = File.ReadAllText(scenePath);

            // Switching sources: stop using previously replayed/received AVTP frame
            _hasAvtpFrame = false;

            // 1) Prefer minimal scene format (paths + delayMs + loop)
            var (steps, simpleLoop) = ParseSimpleScene(text);
            var items = new List<SceneItem>();
            if (steps.Count > 0)
            {
                foreach (var (p, stepDelayMs) in steps)
                {
                    string resolved = p;
                    if (!System.IO.Path.IsPathRooted(resolved))
                        resolved = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scenePath) ?? string.Empty, resolved);

                    var img = LoadImageAsGray8(resolved);
                    if (img.width < W || img.height < H_ACTIVE)
                        throw new InvalidOperationException($"Scene item '{resolved}' expected at least {W}x{H_ACTIVE}, got {img.width}x{img.height}.");

                    var cropped = CropTopLeftGray8(img.data, img.width, img.height, W, H_ACTIVE);
                    items.Add(new SceneItem { Path = resolved, Data = cropped, DelayMs = stepDelayMs });
                }
            }
            else
            {
                // 2) Backward-compatible legacy format fallback (object blocks with filename=...)
                int defaultDelayMs = 500;

                // Optional global loop flag (default true)
                bool loop = true;
                var mLoop = Regex.Match(text, @"\bloop\s*=\s*(true|false)", RegexOptions.IgnoreCase);
                if (mLoop.Success)
                    loop = string.Equals(mLoop.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);

                // Optional global delayMs (first match wins)
                var mDelay = Regex.Match(text, @"\bdelayMs\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                if (mDelay.Success && int.TryParse(mDelay.Groups[1].Value, out var parsedDelay) && parsedDelay > 0)
                    defaultDelayMs = parsedDelay;

                var blocks = SplitTopLevelObjects(text);
                foreach (var block in blocks)
                {
                    var mFile = Regex.Match(block, @"\bfilename\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    if (!mFile.Success) continue;

                    string rawPath = mFile.Groups[1].Value.Trim();
                    string resolved = rawPath;
                    if (!System.IO.Path.IsPathRooted(resolved))
                        resolved = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scenePath) ?? "", resolved);

                    int delayMs = defaultDelayMs;
                    var mItemDelay = Regex.Match(block, @"\bdelayMs\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                    if (mItemDelay.Success && int.TryParse(mItemDelay.Groups[1].Value, out var itemDelay) && itemDelay > 0)
                        delayMs = itemDelay;

                    var img = LoadImageAsGray8(resolved);
                    if (img.width < W || img.height < H_ACTIVE)
                        throw new InvalidOperationException($"Scene item '{resolved}' expected at least {W}x{H_ACTIVE}, got {img.width}x{img.height}.");

                    var cropped = CropTopLeftGray8(img.data, img.width, img.height, W, H_ACTIVE);
                    items.Add(new SceneItem { Path = resolved, Data = cropped, DelayMs = delayMs });
                }

                if (items.Count == 0)
                    throw new InvalidOperationException("No valid scene items found. Expected either: (a) one image path per line, or (b) legacy blocks containing filename=\"...\".");

                simpleLoop = loop;
            }

            // Switching sources: drop any previously replayed/received AVTP frame
            _hasAvtpFrame = false;
            _lastLoaded = LoadedSource.Scene;
            _lastLoadedPcapPath = null;

            _sceneItems = items;
            _scenePath = scenePath;
            _sceneLoopEnabled = simpleLoop;

            lock (_frameLock)
            {
                _sceneIndex = 0;
                _sceneNextSwitchUtc = DateTime.UtcNow.AddMilliseconds(items[0].DelayMs);
            }

            LblStatus.Text = BuildSceneStatus();
            if (_cts == null || _isPaused) RenderOneFrameNow();
        }

        private string BuildSceneStatus()
        {
            var items = _sceneItems;
            if (items == null || items.Count == 0) return "Scene mode.";
            string curName;
            int delay;
            bool loop;
            lock (_frameLock)
            {
                curName = System.IO.Path.GetFileName(items[_sceneIndex].Path);
                delay = items[_sceneIndex].DelayMs;
                loop = _sceneLoopEnabled;
            }
            string sceneName = _scenePath != null ? System.IO.Path.GetFileName(_scenePath) : "<scene>";
            string loopText = loop ? "loop=ON" : "loop=OFF";
            return $"Scene '{sceneName}' loaded ({items.Count} items, {loopText}). Current={curName}, delay={delay}ms. Press Start to play; Prev/Next steps manually.";
        }

        private byte[]? GetSceneBytesAndUpdateIfNeeded(DateTime nowUtc)
        {
            var items = _sceneItems;
            if (items == null || items.Count == 0) return null;

            lock (_frameLock)
            {
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

        private string BuildSequenceStatus()
        {
            string a = _seqPathA != null ? System.IO.Path.GetFileName(_seqPathA) : "<not set>";
            string b = _seqPathB != null ? System.IO.Path.GetFileName(_seqPathB) : "<not set>";
            string cur = _seqIndex == 0 ? "A" : "B";
            return $"Sequence mode (A={a}, B={b}), current={cur}. Use Prev/Next to toggle.";
        }

        private byte[]? GetSequenceBytes()
        {
            return _seqIndex == 0 ? (_seqA ?? _seqB) : (_seqB ?? _seqA);
        }

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
                if (_isPaused)
                {
                    _pausedA = a;
                    _pausedB = b;
                    _pausedD = d;
                }
            }
            RenderAll();

            if (_isPaused)
                UpdateOverlaysAll();
        }

        // Choose source for A:
        // - depends on last loaded source (image vs PCAP)
        private byte[] GetASourceBytes()
        {
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor)
                return _hasAvtpFrame ? _avtpFrame : _idleGradientFrame;

            return _lastLoaded switch
            {
                LoadedSource.Image => _pgmFrame,
                LoadedSource.Pcap => _hasAvtpFrame ? _avtpFrame : _idleGradientFrame,
                LoadedSource.Avi => GetAviBytesAndUpdateIfNeeded(DateTime.UtcNow) ?? _idleGradientFrame,
                LoadedSource.Sequence => GetSequenceBytes() ?? _idleGradientFrame,
                LoadedSource.Scene => GetSceneBytesAndUpdateIfNeeded(DateTime.UtcNow) ?? _idleGradientFrame,
                _ => _idleGradientFrame
            };
        }

        private byte[]? GetAviBytesAndUpdateIfNeeded(DateTime nowUtc)
        {
            var avi = _avi;
            if (avi == null) return null;

            // If we're paused, GeneratorLoop isn't running (pause gate blocks), and RenderAll uses snapshots.
            // Only update when explicitly asked to render a new frame (Prev/Next or manual refresh).
            if (_isPaused)
                return _aviCurrentFrame;

            if (_aviCurrentFrame == null)
            {
                _aviIndex = 0;
                _aviCurrentFrame = avi.ReadFrameAsGray8TopDown(_aviIndex, W, H_ACTIVE);
                _aviNextSwitchUtc = nowUtc.AddMilliseconds(avi.FrameDurationMs);

                _aviPrevFrameForFps = _aviCurrentFrame;
                _aviFpsWindowStartUtc = nowUtc;
                _aviChangesInWindow = 0;
                _aviSourceFps = 0.0;
                return _aviCurrentFrame;
            }

            if (nowUtc < _aviNextSwitchUtc)
                return _aviCurrentFrame;

            int n = avi.FrameCount;
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
                var nextFrame = avi.ReadFrameAsGray8TopDown(_aviIndex, W, H_ACTIVE);
                UpdateAviSourceFps(nextFrame, nowUtc);
                _aviCurrentFrame = nextFrame;
                _aviNextSwitchUtc = _aviNextSwitchUtc.AddMilliseconds(avi.FrameDurationMs);

                // Prevent runaway drift if we were paused/blocked.
                if ((_aviNextSwitchUtc - nowUtc).TotalMilliseconds < -5000)
                    _aviNextSwitchUtc = nowUtc.AddMilliseconds(avi.FrameDurationMs);
            }

            return _aviCurrentFrame;
        }

        private void UpdateAviSourceFps(byte[] nextFrame, DateTime nowUtc)
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
            if (sec >= FpsEstimationWindowSec)
            {
                _aviSourceFps = _aviChangesInWindow / Math.Max(1e-6, sec);
                _aviSourceFpsEma = ApplyEma(_aviSourceFpsEma, _aviSourceFps, FpsEmaAlpha);
                _aviFpsWindowStartUtc = nowUtc;
                _aviChangesInWindow = 0;
            }
        }

        private static double ApplyEma(double prev, double value, double alpha)
        {
            if (value < 0) value = 0;
            if (alpha <= 0) return prev;
            if (alpha >= 1) return value;
            return (prev <= 0) ? value : (prev + alpha * (value - prev));
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
                try { _pauseGate.Wait(ct); }
                catch { break; }
        
                next += period;
        
                // A: either UDP latest or PGM/AVI/Scene fallback (depending on what you loaded)
                var aBytes = GetASourceBytes();
                var a = new Frame(W, H_ACTIVE, aBytes, DateTime.UtcNow);
                Interlocked.Increment(ref _countA);
        
                // B: simulated LVDS = A with brightness delta
                var bBytes = ApplyValueDelta(a.Data, _bValueDelta);
                var b = new Frame(W, H_ACTIVE, bBytes, DateTime.UtcNow);
                Interlocked.Increment(ref _countB);
        
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
                if (_isPaused || !_pauseGate.IsSet)
                {
                    if (Interlocked.Increment(ref _countLateFramesSkipped) == 1)
                        AppendUdpLog("[ui] generator skipped publish due to pause race (late frame)");
                    continue;
                }
        
                lock (_frameLock)
                {
                    _latestA = a;
                    _latestB = b;
                    _latestD = d;
                }
                Interlocked.Increment(ref _countD);
        
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
                try { _pauseGate.Wait(ct); }
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
            if (_cts == null)
            {
                RenderNoSignalFrames();
                ApplyNoSignalUiState(noSignal: true);
                return;
            }

            // AVTP Live: if CANoe (or the source) stops while we're still Running, clear the
            // last frame and fall back to the no-signal "Waiting for signal" UI.
            if (_isRunning
                && _modeOfOperation == ModeOfOperation.AvtpLiveMonitor
                && _hasAvtpFrame
                && _lastAvtpFrameUtc != DateTime.MinValue
                && (DateTime.UtcNow - _lastAvtpFrameUtc) > LiveSignalLostTimeout)
            {
                EnterWaitingForSignalState();
            }

            // In AVTP Live mode, while waiting for the first frame, keep showing "Signal not available".
            if (ShouldShowNoSignalWhileRunning())
            {
                RenderNoSignalFrames();
                ApplyNoSignalUiState(noSignal: true);

                if (!_wasWaitingForSignal && LblStatus != null)
                {
                    LblStatus.Text =
                        $"Waiting for signal... (0.0 fps) (Mode=AVTP Live). Ethernet/AVTP capture best-effort" +
                        (_avtpLiveUdpEnabled ? $"; UDP/RVFU on 0.0.0.0:{RvfProtocol.DefaultPort}" : "") +
                        $". (log: {GetUdpLogPath()})";
                    _runningStatusText = LblStatus.Text;
                }
                _wasWaitingForSignal = true;

                UpdateFpsLabels();
                return;
            }

            _wasWaitingForSignal = false;

            // Ensure A reflects newest source even if GeneratorLoop is stopped
            Frame a;
            Frame b;
            Frame d;
            lock (_frameLock)
            {
                if (_isPaused && _pausedA != null)
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

            
            // In AVTP Live mode, B is not coming from a file; it is derived from A using the UI delta,
            // and D is derived from (B-A). This keeps behavior identical between Live and Player modes.
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor && !_isPaused)
            {
                b = new Frame(W, H_ACTIVE, ApplyValueDelta(a.Data, _bValueDelta), a.TimestampUtc);
                d = AbsDiff(a, b);
            }

            // B post-processing (forced dead pixel + optional compensation)
            b = ApplyBPostProcessing(a, b);

            Blit(_wbA, a.Data, a.Stride);
            Blit(_wbB, b.Data, b.Stride);
            RenderCompareToBgr(_diffBgr, a.Data, b.Data, W, H_ACTIVE, _diffThreshold,
                _zeroZeroIsWhite,
                out var minDiff, out var maxDiff, out var meanDiff,
                out var maxAbsDiff, out var meanAbsDiff, out var aboveDeadband,
                out var totalDarkPixels);
            BlitBgr24(_wbD, _diffBgr, W * 3);

            // Record what we render (A/B in Gray8; D in Bgr24). Diff buffer is reused, so copy it.
            if (_isRecording && _recorder != null && !_isPaused && _cts != null)
            {
                var dCopy = new byte[_diffBgr.Length];
                Buffer.BlockCopy(_diffBgr, 0, dCopy, 0, dCopy.Length);
                if (!_recorder.TryEnqueue(a.Data, b.Data, dCopy))
                    Interlocked.Increment(ref _recordDropped);
            }

            if (LblDiffStats != null)
                LblDiffStats.Text = $"COMPARE (B−A): max_positive_dev={Math.Max(0, maxDiff)}  max_negative_dev={Math.Min(0, minDiff)}  total_pixels_dev={aboveDeadband}  total_dark_pixels={totalDarkPixels}";

            ApplyNoSignalUiState(noSignal: false);
            UpdateFpsLabels();
        }

        private void UpdateFpsLabels()
        {
            if (_statSw.Elapsed.TotalSeconds >= FpsEstimationWindowSec)
            {
                bool noSignalWhileRunning = ShouldShowNoSignalWhileRunning();

                var sec = _statSw.Elapsed.TotalSeconds;
                var fa = Interlocked.Exchange(ref _countA, 0) / sec;
                var fb = Interlocked.Exchange(ref _countB, 0) / sec;
                var fd = Interlocked.Exchange(ref _countD, 0) / sec;
                var fin = Interlocked.Exchange(ref _countAvtpIn, 0) / sec;
                _statSw.Restart();

                _avtpInFpsEma = ApplyEma(_avtpInFpsEma, fin, FpsEmaAlpha);
                _bFpsEma = ApplyEma(_bFpsEma, fb, FpsEmaAlpha);

                // Note: LblA/LblB/LblD are reserved for cursor x/y/v info.
                string avtpDrop = $"{_countAvtpDropped} (gapFrames={_countAvtpSeqGapFrames}, incomplete={_countAvtpIncomplete}, gaps={_sumAvtpSeqGaps})";

                // Keep these updated for potential future use (row is hidden in XAML).
                if (LblAvtpInFps != null) LblAvtpInFps.Text = $"{_avtpInFpsEma:F1} fps";
                if (LblAvtpDropped != null) LblAvtpDropped.Text = avtpDrop;

                if (LblRunInfoA != null)
                {
                    if (_cts != null)
                    {
                        if (_isPaused)
                        {
                            LblRunInfoA.Text = "Paused";
                        }
                        else
                        {
                            double shownFps = noSignalWhileRunning ? 0.0 : GetShownFps(avtpInFps: _avtpInFpsEma);
                            if (_lastLoaded == LoadedSource.Avi && shownFps <= 0.0)
                                LblRunInfoA.Text = "Running";
                            else
                                LblRunInfoA.Text = $"Running @: {shownFps:F1} fps";

                            // Only when >0, keep a visible hint in the status line too.
                            int lateSkip = Volatile.Read(ref _countLateFramesSkipped);
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
                    if (_cts != null)
                    {
                        if (_isPaused)
                        {
                            LblRunInfoB.Text = "Paused";
                        }
                        else
                        {
                            if (noSignalWhileRunning)
                            {
                                LblRunInfoB.Text = $"Running @: {0.0:F1} fps";
                            }
                            else if (_bFpsEma <= 0.0)
                                LblRunInfoB.Text = "Running";
                            else
                                LblRunInfoB.Text = $"Running @: {_bFpsEma:F1} fps";
                        }
                    }
                    else LblRunInfoB.Text = "";
                }
            }
        }



        private double GetShownFps(double avtpInFps)
        {
            // In live-monitor mode, prefer the measured AVTP-in fps over the user-entered target fps.
            // This keeps the top-right "Running @" label consistent with the bottom "AVTP in" readout.
            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor && avtpInFps > 0.0)
                return avtpInFps;

            return _lastLoaded switch
            {
                LoadedSource.Pcap => avtpInFps,
                // For AVI playback, show ONLY the "source fps" (frame content changes/sec).
                // Do not fall back to the AVI header fps (often the fixed record fps like 100).
                LoadedSource.Avi => _aviSourceFpsEma,
                _ => _targetFps
            };
        }

        private static Frame MockLvdsFromActive(Frame active80)
        {
            var outData = new byte[W * H_LVDS];

            // top 80 = active
            Buffer.BlockCopy(active80.Data, 0, outData, 0, W * H_ACTIVE);

            // bottom 4 = metadata (simple stripes)
            for (int y = H_ACTIVE; y < H_LVDS; y++)
            {
                int row = y * W;
                for (int x = 0; x < W; x++)
                {
                    outData[row + x] = (byte)(((x / 8) % 2 == 0) ? 0x20 : 0xE0);
                }
            }

            return new Frame(W, H_LVDS, outData, active80.TimestampUtc);
        }

        private static Frame CropLvdsToActive(Frame lvds84)
        {
            // metadata is bottom 4 → keep only first 80 lines
            var outData = new byte[W * H_ACTIVE];
            Buffer.BlockCopy(lvds84.Data, 0, outData, 0, W * H_ACTIVE);
            return new Frame(W, H_ACTIVE, outData, lvds84.TimestampUtc);
        }

        private static Frame AbsDiff(Frame a, Frame b)
        {
            var outData = new byte[W * H_ACTIVE];
            for (int i = 0; i < outData.Length; i++)
            {
                int v = a.Data[i] - b.Data[i];
                if (v < 0) v = -v;
                outData[i] = (byte)v;
            }
            return new Frame(W, H_ACTIVE, outData, DateTime.UtcNow);
        }

        private static WriteableBitmap MakeGray8Bitmap(int w, int h) =>
            new WriteableBitmap(w, h, 96, 96, PixelFormats.Gray8, null);

        private static WriteableBitmap MakeBgr24Bitmap(int w, int h) =>
            new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);

        private static void Blit(WriteableBitmap wb, byte[] src, int stride)
        {
            wb.Lock();
            try
            {
                wb.WritePixels(new Int32Rect(0, 0, wb.PixelWidth, wb.PixelHeight), src, stride, 0);
            }
            finally { wb.Unlock(); }
        }

        private static void BlitBgr24(WriteableBitmap wb, byte[] src, int stride)
        {
            wb.Lock();
            try
            {
                wb.WritePixels(new Int32Rect(0, 0, wb.PixelWidth, wb.PixelHeight), src, stride, 0);
            }
            finally { wb.Unlock(); }
        }

        private static byte[] ApplyValueDelta(byte[] src, int delta)
        {
            if (delta == 0) return src;
            var dst = new byte[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                int v = src[i] + delta;
                if (v < 0) v = 0;
                else if (v > 255) v = 255;
                dst[i] = (byte)v;
            }
            return dst;
        }

        private static void RenderCompareToBgr(byte[] dstBgr, byte[] aGray, byte[] bGray, int w, int h, byte deadband,
            bool zeroZeroIsWhite,
            out int minDiff, out int maxDiff, out double meanDiff,
            out int maxAbsDiff, out double meanAbsDiff, out int aboveDeadband,
            out int totalDarkPixels)
        {
            long sumAbs = 0;
            long sumDiff = 0;
            int maxAbs = 0;
            int above = 0;
            int dark = 0;
            int minD = int.MaxValue;
            int maxD = int.MinValue;

            int p = 0;
            int n = w * h;
            for (int i = 0; i < n; i++)
            {
                byte a = aGray[i];
                byte b = bGray[i];
                // Deviation is ECU output minus input: B - A
                int diff = b - a;
                int ad = diff < 0 ? -diff : diff;
                sumAbs += ad;
                sumDiff += diff;
                if (diff < minD) minD = diff;
                if (diff > maxD) maxD = diff;
                if (ad > maxAbs) maxAbs = ad;
                if (ad > deadband) above++;
                if (a > 0 && b == 0) dark++;

                ComparePixelToBgr(a, b, deadband, zeroZeroIsWhite, out var bl, out var gg, out var rr);
                dstBgr[p++] = bl;
                dstBgr[p++] = gg;
                dstBgr[p++] = rr;
            }

            if (n <= 0)
            {
                minDiff = 0;
                maxDiff = 0;
                meanDiff = 0.0;
                maxAbsDiff = 0;
                meanAbsDiff = 0.0;
                aboveDeadband = 0;
                totalDarkPixels = 0;
                return;
            }

            minDiff = minD == int.MaxValue ? 0 : minD;
            maxDiff = maxD == int.MinValue ? 0 : maxD;
            meanDiff = (double)sumDiff / n;
            maxAbsDiff = maxAbs;
            meanAbsDiff = (double)sumAbs / n;
            aboveDeadband = above;
            totalDarkPixels = dark;
        }

        private static void ComparePixelToBgr(byte a, byte b, byte deadband, bool zeroZeroIsWhite, out byte bl, out byte g, out byte r)
        {
            const int VisualStep = 12; // increase for more visible banding
            const int VisualMax = 128; // scale typical diffs (~70..100) to stronger colors

            // Deviation is ECU output minus input: B - A
            int diff = b - a;
            int ad = diff < 0 ? -diff : diff;

            // Optional special case: black A (0) == black B (0) -> white D (255)
            if (zeroZeroIsWhite && a == 0 && b == 0)
            {
                bl = 255;
                g = 255;
                r = 255;
                return;
            }

            // Dark pixel detection: A has signal, but B is forced to 0.
            // Make it visually obvious regardless of threshold.
            if (a > 0 && b == 0)
            {
                bl = 255;
                g = 0;
                r = 255;
                return;
            }

            // Deadband equality -> green
            if (ad <= deadband)
            {
                bl = 0;
                g = 255;
                r = 0;
                return;
            }

            // Make small differences more visible by quantizing the magnitude used for coloring.
            int adVis = ((ad + (VisualStep / 2)) / VisualStep) * VisualStep;
            if (adVis > 255) adVis = 255;
            double t = Math.Min(1.0, adVis / (double)VisualMax); // 0..1

            if (diff > 0)
            {
                // B > A: green -> yellow -> red
                // segment1: t 0..0.5 => r 0->255, g=255
                // segment2: t 0.5..1 => r=255, g 255->0
                if (t <= 0.5)
                {
                    r = (byte)Math.Round((t / 0.5) * 255.0);
                    g = 255;
                }
                else
                {
                    r = 255;
                    g = (byte)Math.Round((1.0 - (t - 0.5) / 0.5) * 255.0);
                }
                bl = 0;
                return;
            }

            // B < A: green -> turquoise -> blue -> white
            if (t <= 1.0 / 3.0)
            {
                // green -> turquoise: b 0->255, g=255
                double u = t / (1.0 / 3.0);
                r = 0;
                g = 255;
                bl = (byte)Math.Round(u * 255.0);
            }
            else if (t <= 2.0 / 3.0)
            {
                // turquoise -> blue: g 255->0, b=255
                double u = (t - 1.0 / 3.0) / (1.0 / 3.0);
                r = 0;
                g = (byte)Math.Round((1.0 - u) * 255.0);
                bl = 255;
            }
            else
            {
                // blue -> white: r 0->255, g 0->255, b=255
                double u = (t - 2.0 / 3.0) / (1.0 / 3.0);
                r = (byte)Math.Round(u * 255.0);
                g = (byte)Math.Round(u * 255.0);
                bl = 255;
            }
        }


        // pixel inspectors
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
            if (_isPaused)
                RequestOverlayUpdate(pane);
        }

        private void Img_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.Image img) return;
            var pane = PaneFromSender(sender);

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.ClickCount >= 2)
            {
                ResetZoomPan(pane);
                if (_isPaused) UpdateOverlay(pane);
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

            if (_isPaused)
                RequestOverlayUpdate(_panningPane);
        }

        private void ShowPixelInfo(MouseEventArgs e, Frame? f, System.Windows.Controls.TextBlock lbl)
        {
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

    public sealed class Frame
    {
        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }
        public byte[] Data { get; }
        public DateTime TimestampUtc { get; }

        public Frame(int w, int h, byte[] data, DateTime tsUtc)
        {
            Width = w;
            Height = h;
            Stride = w;
            Data = (byte[])data.Clone(); // safe; later we can optimize with pooling
            TimestampUtc = tsUtc;
        }
    }

    public sealed class PgmImage
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    public static class Pgm
    {
        public static PgmImage Load(string path)
        {
            var text = File.ReadAllText(path);
            var tokens = Tokenize(text);

            int idx = 0;
            string magic = tokens[idx++];

            int width = int.Parse(tokens[idx++]);
            int height = int.Parse(tokens[idx++]);
            int max = int.Parse(tokens[idx++]);

            if (max != 255)
                throw new InvalidDataException($"Unsupported max value: {max}");

            var data = new byte[width * height];

            if (magic == "P2")
            {
                // ASCII
                for (int i = 0; i < data.Length; i++)
                    data[i] = byte.Parse(tokens[idx++]);
            }
            else if (magic == "P5")
            {
                // Binary (simple scanner: find start of pixel bytes)
                byte[] bytes = File.ReadAllBytes(path);

                int pos = 0;
                int newlines = 0;
                while (pos < bytes.Length && newlines < 4)
                {
                    if (bytes[pos] == '\n') newlines++;
                    pos++;
                }

                Buffer.BlockCopy(bytes, pos, data, 0, data.Length);
            }
            else
            {
                throw new InvalidDataException($"Unsupported PGM format: {magic}");
            }

            return new PgmImage
            {
                Width = width,
                Height = height,
                Data = data
            };
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            using var sr = new StringReader(text);

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                tokens.AddRange(line.Split(
                    new[] { ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries));
            }
            return tokens;
        }
    }
}