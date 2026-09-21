using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SharpPcap;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VilsSharpX.DefectPixel;
namespace VilsSharpX
{
    public partial class MainWindow : Window
    {
        private const int WmNcHitTest = 0x0084;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;

        private const double FpsEstimationWindowSec = 0.25;
        private const double FpsEmaAlpha = 0.30; // 0..1, higher = more responsive, lower = smoother
        private const double LiveSignalLostTimeoutSec = 0.625; // ~5 frames at 8fps
        private const double BaslerFallbackDisplayFps = 50.0;
        private const double NichiaLsmFpsDisplayOffset = 0.1; // display-only compensation to match LVDS monitor cadence

        private enum Pane
        {
            A = 0,
            B = 1,
            C = 3,
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

        /// <summary>Comparison mode: 0=LVDS-AVTP (default), 1=LSM-LVDS, 2=LSM-AVTP</summary>
        private volatile int _comparisonMode = 0;

        // Live AVTP capture settings (Ethernet via SharpPcap)
        private bool _avtpLiveEnabled = true;
        private string? _avtpLiveDeviceHint;

        // Automation REST API settings (persisted; currently configured via settings.json only).
        private bool _apiAllowRemote = false;
        private bool _apiEnableHttps = false;
        private string _apiBindAddress = "127.0.0.1";
        private int _apiPort = Api.ApiHost.DefaultPort;
        private string _apiKey = string.Empty;
        private string[] _apiAllowedCidrs = [];

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
        private int _controlMode = 0; // 0 = ECU, 1 = Direct control
        private int _canUartMode = 0; // 0 = ECU↔LSM, 1 = ECU↔SmartVisio↔LSM, 2 = SmartVisio↔LSM

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

        // Ethernet capture from SmartVisio Box GETH → raw ethertype 0x88B5 (pane B)
        private NichiaEthCapture? _nichiaEthCapture;
        private OsramEthCapture? _osramEthCapture;
        // Basler USB3 camera capture (pane C)
        private BaslerCameraCapture? _baslerCapture;
        // Pylon Open/Close block for seconds and a USB3 camera allows one owner at
        // a time, so every camera lifecycle call is serialized on this chain.
        private Task _baslerWork = Task.CompletedTask;
        private int _baslerStartGeneration;
        private LsmCanDiagCapture? _canDiagCapture;
        private readonly LsmCanDiagStore _canDiagStore = new(0);
        private OsramDefectStore? _osramDefectStore;
        private OsramDefectControlWindow? _osramDefectControlWindow;
        private CommunicationFaultControlWindow? _communicationFaultControlWindow;
        private readonly CommunicationFaultState _communicationFaultState = new();
        private NichiaDefectStore? _nichiaDefectStore;
        private NichiaDefectControlWindow? _nichiaDefectControlWindow;
        private readonly ObservableCollection<CanDiagRowView> _canDiagRows = [];
        private readonly ConcurrentQueue<LsmCanDiagRecord> _pendingRawCanRecords = new();
        private int _canDiagCurrentPage = 1;
        private int _canDiagTotalPages = 1;
        private DateTime _canDiagLastRefresh = DateTime.MinValue;
        private static readonly TimeSpan CanUartMonitoringGrayDuration = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan CanUartMonitoringGreenDuration = TimeSpan.FromMilliseconds(500);
        private static readonly Color StatusLedRunningGreen = Color.FromRgb(0x81, 0xC7, 0x84);
        private static readonly Color StatusLedRunningGreenStroke = Color.FromRgb(0x4C, 0xAF, 0x50);
        private DateTime _canUartLedPhaseStartUtc = DateTime.UtcNow;
        private bool _canUartLedPhaseGreen;
        private volatile bool _canDiagRecording;  // starts false — user presses Record to begin
        private bool _canDiagTraceLoaded;
        private DateTime _canRecordSessionStart = DateTime.MinValue; // filters stale Dispatcher-queued records
        private DispatcherTimer? _canDiagRetryTimer; // resends START if CD stays 0
        private DispatcherTimer? _canDiagWatchdogTimer; // auto-heals long-run silent recording stalls
        private static readonly TimeSpan CanDiagRefreshInterval = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan CanDiagStallTimeout = TimeSpan.FromSeconds(6);
        private DateTime _canDiagLastRecordUtc = DateTime.MinValue;
        private DateTime _canDisplayTimestampUtc = DateTime.MinValue;
        private uint _canPreviousSourceTimestamp;
        private bool _canHasPreviousSourceTimestamp;
        private int _canNextDisplaySequence;
        private bool _canDiagSessionHadTraffic;
        private int _canDiagConsecutiveRestarts;
        private bool _canDiagWatchdogRecovering;
        private int _canDiagUiRefreshRequested;
        private int _canDiagStopRequested;
        private string _canSortColumn = "Nr";     // column header text used for sorting
        private bool _canSortAscending = true;     // true = ascending, false = descending
        private GridViewColumnHeader? _canLastSortHeader;  // last clicked header for glyph tracking

        /// <summary>
        /// Formats comparison stats label based on active comparison mode and diff statistics.
        /// </summary>
        private string FormatComparisonStats(int maxDiff, int minDiff, double meanAbsDiff, int aboveDeadband, int totalDarkPixels)
        {
            string modeLabel = ComparisonModeLabels[Math.Clamp(_comparisonMode, 0, ComparisonModeLabels.Length - 1)];
            int brightPixels = GetInjectedBrightPixelCount();
            int detectedFlickers = GetDetectedFlickerCount();
            // Capture the latest stats so the automation API can read them off-thread.
            StoreComparisonStats(maxDiff, minDiff, meanAbsDiff, aboveDeadband, totalDarkPixels, brightPixels, detectedFlickers);

            LblDiffMode.Text = $"[{modeLabel}]";
            LblMaxPositiveDev.Text = $"max_pos_dev={Math.Max(0, maxDiff)}";
            LblMaxNegativeDev.Text = $"max_neg_dev={Math.Min(0, minDiff)}";
            LblAverageDev.Text = $"avg_dev={meanAbsDiff:F0}";
            LblTotalPixelsDev.Text = $"total_pixels_dev={aboveDeadband}";
            LblDarkPixels.Text = $"dark_pixels={totalDarkPixels}";
            LblBrightPixels.Text = $"bright_pixels={brightPixels}";
            LblFlickers.Text = $"flickers={detectedFlickers}";
            return string.Empty;
        }

        private void ResetComparisonStatsDisplay()
        {
            string modeLabel = ComparisonModeLabels[Math.Clamp(_comparisonMode, 0, ComparisonModeLabels.Length - 1)];
            LblDiffMode.Text = $"[{modeLabel}]";
            LblMaxPositiveDev.Text = "max_pos_dev=0";
            LblMaxNegativeDev.Text = "max_neg_dev=0";
            LblAverageDev.Text = "avg_dev=0";
            LblTotalPixelsDev.Text = "total_pixels_dev=0";
            LblDarkPixels.Text = "dark_pixels=0";
            LblBrightPixels.Text = "bright_pixels=0";
            LblFlickers.Text = "flickers=0";
        }

        /// <summary>
        /// Number of injected "bright" pixels currently active (OSRAM Stuck + Nichia Bright).
        /// Returns 0 unless injection is enabled for the respective store, so the label only
        /// reflects pixels that are actually being simulated/injected.
        /// </summary>
        private int GetInjectedBrightPixelCount()
        {
            int count = 0;

            var osramStore = _osramDefectStore;
            if (osramStore != null && osramStore.InjectionEnabled)
            {
                foreach (var d in osramStore.GetActiveDefects())
                {
                    if (d.DefectType == OsramDefectType.Stuck)
                        count++;
                }
            }

            var nichiaStore = _nichiaDefectStore;
            if (nichiaStore != null && nichiaStore.InjectionEnabled)
            {
                foreach (var d in nichiaStore.GetActiveDefects())
                {
                    if (d.DefectType == NichiaDefectType.Bright)
                        count++;
                }
            }

            return count;
        }

        private Frame? _latestA;
        private Frame? _latestB;
        private Frame? _latestC;
        private Frame? _latestD;

        /// <summary>UTC time when the last LVDS/Ethernet frame arrived (for signal-lost detection).</summary>
        private DateTime _lastLvdsFrameUtc = DateTime.MinValue;
        /// <summary>Persistent flag: true when LVDS signal timed out, cleared when a new LVDS frame arrives.</summary>
        private bool _lvdsSignalLost;

        /// <summary>UTC time when the last Basler camera frame arrived (for signal-lost detection).</summary>
        private DateTime _lastBaslerFrameUtc = DateTime.MinValue;
        /// <summary>Persistent flag: true when Basler signal timed out (no trigger from ECU), cleared on new frame.</summary>
        private bool _baslerSignalLost;
        /// <summary>After the first LVDS frame, camera display is released in LVDS order.</summary>
        private bool _cameraDisplaySyncEnabled;
        private int _lvdsCameraDisplayCredits;
        private byte[]? _pendingCameraFrame;
        private int _pendingCameraWidth;
        private int _pendingCameraHeight;

        // ─── Pane C displayed-FPS tracking (sliding window counter, immune to UI jitter) ───
        private readonly Stopwatch _baslerDispFpsSw = Stopwatch.StartNew();
        private long _baslerDispWindowStartTicks;
        private int _baslerDispWindowFrames;
        private double _baslerDispFps;

        // ─── Run-info UI throttle (make AVTP/LSM cadence similar to LVDS label updates) ───
        private readonly Stopwatch _runInfoUiSw = Stopwatch.StartNew();
        private long _runInfoALastUpdateTicks;
        private long _runInfoCLastUpdateTicks;
        private const double RunInfoUiUpdatePeriodSec = 1.0;

        /// <summary>Reusable buffer for downscaled camera frame (sized to _currentWidth*_currentHeight).</summary>
        private byte[]? _downscaledCameraFrame;

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
        /// <summary>LVDS frames received since the current synchronization session started.</summary>
        private int _lvdsFramesSinceSyncReset;
        /// <summary>Comparison starts after the startup pipeline has had time to settle.</summary>
        private bool _lvdsComparisonReady;
        private const int LvdsComparisonWarmupFrames = 4;

        // Zoom/pan manager (replaces individual _zoom/_pan fields)
        private readonly ZoomPanManager _zoomPan = new();

        // UI settings manager
        private UiSettingsManager _settingsManager = null!;

        private readonly DispatcherTimer _overlayTimerA;
        private readonly DispatcherTimer _overlayTimerB;
        private readonly DispatcherTimer _overlayTimerC;
        private readonly DispatcherTimer _overlayTimerD;
        private readonly DispatcherTimer _deviceModeSyncTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        private readonly DateTime _statusOverrideUntil = DateTime.MinValue;

        private bool _overlayPendingA;
        private bool _overlayPendingB;
        private bool _overlayPendingC;
        private bool _overlayPendingD;

        private bool _isUpdatingDiffThresholdText;
        private CancellationTokenSource? _recordingFeedbackCts;

        // ─── Fullscreen pane toggle ────────────────────────────────────────
        private Pane? _fullscreenPane;
        private int _fsOrigRow, _fsOrigCol, _fsOrigRowSpan, _fsOrigColSpan;

        private WriteableBitmap _wbA = null!;
        private WriteableBitmap _wbB = null!;
        private WriteableBitmap _wbC = null!;
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
            _flickerInjection.InjectionCompleted += FlickerInjectionCompleted;
            _flickerDetector.StatusChanged += FlickerDetectorStatusChanged;
            InitializeCanDiagMonitor();
            ImgA.Source = _wbA;
            ImgB.Source = _wbB;
            ImgC.Source = _wbC;
            ImgD.Source = _wbD;
            _zoomPan.AttachToImages(ImgA, ImgB, ImgD, ImgC);
            _overlayTimerA = MakeOverlayTimer(Pane.A);
            _overlayTimerB = MakeOverlayTimer(Pane.B);
            _overlayTimerC = MakeOverlayTimer(Pane.C);
            _overlayTimerD = MakeOverlayTimer(Pane.D);
            _deviceModeSyncTimer.Tick += (_, _) => _ = TrySyncDeviceModeToAurixAsync("periodic");

            InitializeDefaultPatterns();

            if (TxtDiffThr != null) TxtDiffThr.Text = "0";

            _settingsManager.IsLoading = false;

            // Apply hardware constraints after settings are loaded
            ApplyModeConstraints();
            ResetComparisonStatsDisplay();
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
            // Pane C bitmap is sized to camera resolution, not LSM active area.
            // Initialised with a 1×1 placeholder; resized on first Basler frame.
            _wbC ??= BitmapUtils.MakeGray8(1, 1);
            _wbD = BitmapUtils.MakeBgr24(w, h);

            // Helper classes that depend on resolution
            _liveCapture = new LiveCaptureManager(w, h, FpsEstimationWindowSec * 2.5, AppendDiagLog);
            _recordingManager = new RecordingManager(w, h);
            _sequencePlayer = new SequencePlayer(w, h);
            _scenePlayer = new ScenePlayer(w, h);
            _aviPlayer = new AviSourcePlayer(w, h, FpsEstimationWindowSec, FpsEmaAlpha);
            _sourceLoader = new SourceLoaderHelper(w, h, H_LVDS);
            _snapshotSaver = new FrameSnapshotSaver(w, h);
            _settingsManager = new UiSettingsManager(w, h);
            _txManager = new AvtpTransmitManager(w, h, AppendDiagLog);
            ApplyCommunicationFaultState();
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
            try { StopNichiaEthCapture(); } catch { /* ignore */ }
            try { StopOsramEthCapture(); } catch { /* ignore */ }
            try { _aviPlayer?.Dispose(); } catch { /* ignore */ }

            InitializeResolutionDependentObjects();
            InitializeDefaultPatterns();

            // Re-subscribe to LiveCaptureManager events (since we recreated the instance)
            if (_liveCapture != null)
                _liveCapture.OnFrameReady += (frame, meta) => Dispatcher.Invoke(() => HandleLiveFrameReady(frame, meta));

            // Rebind bitmaps to UI
            if (ImgA != null) ImgA.Source = _wbA;
            if (ImgB != null) ImgB.Source = _wbB;
            if (ImgC != null) ImgC.Source = _wbC;
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
            UpdateLvdsProtocolLabel();
        }

        private void InitializeDefaultPatterns()
        {
            // Default generator frame: strict black until a source is loaded.
            Array.Fill(_idleGradientFrame, (byte)0x00);

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

            // If Ethernet capture (Nichia or Osram) is active and already has frames,
            // pane B is valid — proceed with rendering even if AVTP (pane A)
            // hasn't arrived yet.  Pane A will show gray; B and D render normally.
            if (HasRecentLvdsFrame())
                return false;

            // In AVTP Live mode, keep panes in "Signal not available" until first valid frame arrives.
            return !_liveCapture.HasAvtpFrame;
        }

        private bool HasRecentLvdsFrame()
        {
            if (_lvdsSignalLost || _lastLvdsFrameUtc == DateTime.MinValue)
                return false;

            bool captureActive = (_nichiaEthCapture?.IsCapturing == true)
                              || (_osramEthCapture?.IsCapturing == true);
            return captureActive
                && (DateTime.UtcNow - _lastLvdsFrameUtc) <= TimeSpan.FromSeconds(LiveSignalLostTimeoutSec);
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
            ResetSyncState();
            _lastLvdsFrameUtc = DateTime.MinValue;
            _lvdsSignalLost = true;
            _baslerCapture?.UseFreeRunFallback();
            ResetCameraDisplaySync();
            _cameraDisplaySyncEnabled = false;
            ResetLvdsStatusForNewSession();
            _lastBaslerFrameUtc = DateTime.MinValue;
            _baslerSignalLost = false;
            _baslerDispWindowFrames = 0;
            _baslerDispFps = 0;
            _baslerDispWindowStartTicks = _baslerDispFpsSw.ElapsedTicks;
        }

        private void ApplyNoSignalUiState(bool noSignal)
        {
            if (NoSignalA != null) NoSignalA.Visibility = noSignal ? Visibility.Visible : Visibility.Collapsed;
            if (NoSignalB != null) NoSignalB.Visibility = noSignal ? Visibility.Visible : Visibility.Collapsed;
            if (NoSignalC != null) NoSignalC.Visibility = noSignal ? Visibility.Visible : Visibility.Collapsed;
            if (NoSignalD != null) NoSignalD.Visibility = noSignal ? Visibility.Visible : Visibility.Collapsed;

            if (noSignal)
            {
                if (LblA != null) LblA.Text = "";
                if (LblB != null) LblB.Text = "";
                if (LblC != null) LblC.Text = "";
                if (LblD != null) LblD.Text = "";
                ResetComparisonStatsDisplay();
                if (LblRunInfoC != null) LblRunInfoC.Text = "";
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
            if (ChkLoopPlaying != null)
            {
                bool canLoop = !isAvtpLive && _lastLoaded == LoadedSource.Avi;
                ChkLoopPlaying.Visibility = isAvtpLive ? Visibility.Collapsed : Visibility.Visible;
                ChkLoopPlaying.IsEnabled = canLoop;
                if (!canLoop && ChkLoopPlaying.IsChecked == true)
                    ChkLoopPlaying.IsChecked = false;
            }
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
                Pane.C => _overlayPendingC,
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
                case Pane.C: _overlayPendingC = false; break;
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
                case Pane.C:
                    _overlayPendingC = false;
                    if (_overlayTimerC.IsEnabled) _overlayTimerC.Stop();
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
                case Pane.C:
                    _overlayPendingC = true;
                    if (!_overlayTimerC.IsEnabled) _overlayTimerC.Start();
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
                Pane.A => (ImgA, OvrA, _zoomPan.GetZoom(0)),
                Pane.B => (ImgB, OvrB, _zoomPan.GetZoom(1)),
                Pane.C => (ImgC, OvrC, _zoomPan.GetZoom(3)),
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
                        Pane.C => _latestC,
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

                // Select correct frames based on active comparison mode
                int cmpMode = _comparisonMode;
                if (cmpMode == 1 || cmpMode == 2)
                {
                    // Camera comparison modes: create Frame from downscaled buffer
                    Frame? cameraFrame = null;
                    var dsBuf = _downscaledCameraFrame;
                    if (dsBuf != null && dsBuf.Length == _currentWidth * _currentHeight)
                        cameraFrame = new Frame(_currentWidth, _currentHeight, dsBuf, DateTime.UtcNow);

                    if (cmpMode == 1)
                    {
                        // LSM-LVDS: reference = LVDS (B), measured = camera
                        fA = fB;
                        fB = cameraFrame ?? fA;
                    }
                    else
                    {
                        // LSM-AVTP: reference = AVTP (A), measured = camera
                        fB = cameraFrame ?? fA;
                    }
                }
                else
                {
                    // LVDS-AVTP: apply post-processing to B
                    if (fA != null && fB != null)
                        fB = ApplyBPostProcessing(fA, fB);
                }
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
            double fontScale = _fullscreenPane != null ? 2.0 : 1.0;

            if (pane == Pane.D && fA != null && fB != null)
            {
                byte overlayZeroThr = (byte)(_comparisonMode > 0 ? 5 : 0);
                _overlayRenderer.RenderDiffOverlay(ovr, img, fA, fB, zoom.ScaleX, imgToOvr, pixelsPerDip, _diffThreshold, _zeroZeroIsWhite, fontScale, overlayZeroThr);
            }
            else
            {
                _overlayRenderer.RenderGrayscaleOverlay(ovr, img, fBase, zoom.ScaleX, imgToOvr, pixelsPerDip, fontScale);
            }
        }

        private void UpdateOverlaysAll()
        {
            UpdateOverlay(Pane.A);
            UpdateOverlay(Pane.B);
            UpdateOverlay(Pane.C);
            UpdateOverlay(Pane.D);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Hook LiveCaptureManager -> update UI with frame info (frame storage is already done in the manager)
            _liveCapture.OnFrameReady += (frame, meta) => Dispatcher.Invoke(() => HandleLiveFrameReady(frame, meta));

            ShowIdleGradient();
            int w = GetCurrentWidth();
            int h = GetCurrentHeight();
            LblStatus.Text = $"Load an image (PGM/BMP/PNG are converted to Gray8 u8; will crop top-left to {w}×{h}). Press Start to begin rendering.";

            LoadUiSettings();

            if (_modeOfOperation == ModeOfOperation.PlayerFromFiles
                && !_communicationFaultState.AvtpFaultEnabled)
            {
                StartBlackKeepalive();
            }

            // Startup should show "Signal not available".
            ApplyNoSignalUiState(noSignal: true);
            UpdateLvdsProtocolLabel();

            // Default button states: Load Files + Start enabled; others disabled
            ApplyButtonStates(false);

            // Send device-mode command to ECU at app startup so the firmware
            // immediately matches the persisted device type from settings.
            _ = TrySyncDeviceModeToAurixAsync("startup");
            _ = TrySyncAdapterModeToAurixAsync("startup");

            // Keep SmartVisio Box aligned even after board reset/run while WPF stays open.
            _deviceModeSyncTimer.Start();

            // Open the camera up front so the first Start renders pane C at once.
            StartBaslerCapture(useFreeRunFallback: true);
        }

        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(MainWindowWndProc);
        }

        private static IntPtr MainWindowWndProc(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (message == WmNcHitTest)
            {
                int hitTest = DefWindowProc(hwnd, message, wParam, lParam).ToInt32();
                if (hitTest is HtLeft or HtRight or HtTop or HtTopLeft or HtTopRight
                    or HtBottom or HtBottomLeft or HtBottomRight)
                {
                    handled = true;
                    return new IntPtr(1); // HTCLIENT: keep the native Maximize button, block border resizing.
                }
            }

            return IntPtr.Zero;
        }

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static partial IntPtr DefWindowProc(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Tries to sync the current WPF device type to SmartVisio Box with short retries.
        /// Startup can race NIC enumeration, so we retry for a brief window.
        /// </summary>
        private async Task TrySyncDeviceModeToAurixAsync(string reason)
        {
            const int maxAttempts = 5;
            const int delayMs = 250;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (_communicationFaultState.LvdsFaultEnabled)
                        return;

                    if (_communicationFaultState.CanUartFaultEnabled)
                        return;

                    string? txDev = GetTxPcapDeviceNameOrNull();
                    if (!string.IsNullOrWhiteSpace(txDev))
                    {
                        DeviceModeCommand.SendDeviceMode(txDev, _currentDeviceType, AppendDiagLog);
                        AppendDiagLog($"[cmd] Device-mode sync ({reason}) done on attempt {attempt}/{maxAttempts}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppendDiagLog($"[cmd] Device-mode sync ({reason}) failed on attempt {attempt}/{maxAttempts}: {ex.Message}");
                }

                if (attempt < maxAttempts)
                    await Task.Delay(delayMs);
            }

            AppendDiagLog($"[cmd] Device-mode sync ({reason}) not sent: no usable NIC");
        }

        /// <summary>
        /// Tries to sync the current WPF control and CAN-UART modes to SmartVisio Box.
        /// Startup can race NIC enumeration, so retry for a brief window.
        /// </summary>
        private async Task TrySyncAdapterModeToAurixAsync(string reason)
        {
            const int maxAttempts = 5;
            const int delayMs = 250;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (_communicationFaultState.LvdsFaultEnabled)
                        return;

                    if (_communicationFaultState.CanUartFaultEnabled)
                        return;

                    string? txDev = GetTxPcapDeviceNameOrNull();
                    if (!string.IsNullOrWhiteSpace(txDev))
                    {
                        AdapterModeCommand.SendAdapterMode(txDev, _controlMode, _canUartMode, AppendDiagLog);
                        AppendDiagLog($"[cmd] Adapter-mode sync ({reason}) done on attempt {attempt}/{maxAttempts}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppendDiagLog($"[cmd] Adapter-mode sync ({reason}) failed on attempt {attempt}/{maxAttempts}: {ex.Message}");
                }

                if (attempt < maxAttempts)
                    await Task.Delay(delayMs);
            }

            AppendDiagLog($"[cmd] Adapter-mode sync ({reason}) not sent: no usable NIC");
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
                    bool sourceChanged;
                    lock (_frameLock)
                        sourceChanged = _latestA != null && IsAbruptFrameTransition(_latestA.Data, frame);

                    if (sourceChanged)
                        ResetSyncState();

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
            bool incomplete = meta.LinesWritten < rvfHeight;
            bool gap = meta.SeqGaps > 0;
            if (incomplete) _playback.IncrementCountAvtpIncomplete();
            if (gap)
            {
                _playback.IncrementCountAvtpSeqGapFrames();
                _playback.AddSeqGaps(meta.SeqGaps);
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
            int displayLines = Math.Min(meta.LinesWritten, displayHeight);
            // Keep previous wording and keep only dropped counter (without extra parentheses details)
            LblStatus.Text = StatusFormatter.FormatAvtpRvfStatus(
                src, meta.FrameId, meta.Seq, displayLines, displayHeight, meta.SeqGaps,
                _playback.CountAvtpDropped);
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
                _apiAllowRemote = s.ApiAllowRemote;
                _apiEnableHttps = s.ApiEnableHttps;
                _apiBindAddress = string.IsNullOrWhiteSpace(s.ApiBindAddress) ? "127.0.0.1" : s.ApiBindAddress.Trim();
                _apiPort = Math.Clamp(s.ApiPort, 1, 65535);
                _apiKey = s.ApiKey ?? string.Empty;
                _apiAllowedCidrs = s.ApiAllowedCidrs ?? [];

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

                _controlMode = Math.Clamp(s.ControlMode, 0, 1);
                if (CmbControlMode != null)
                {
                    CmbControlMode.SelectedIndex = _controlMode;
                }

                // Start every application session in the direct ECU <-> LSM mode.
                // Do not restore a persisted mode that could leave the ECU in failsafe.
                _canUartMode = 0;
                if (CmbCanUartMode != null)
                {
                    CmbCanUartMode.SelectedIndex = _canUartMode;
                }

                ApplyDeviceTypeConstraints();
                ApplyModeConstraints();

                if (TxtVlanId != null) TxtVlanId.Text = _vlanId.ToString();
                if (TxtVlanPriority != null) TxtVlanPriority.Text = _vlanPriority.ToString();
                if (TxtAvtpEtherType != null) TxtAvtpEtherType.Text = _avtpEtherType;
                if (TxtStreamIdLastByte != null) TxtStreamIdLastByte.Text = _streamIdLastByte;

                RefreshLiveNicList();
                UpdateLiveUiEnabledState();
                UpdateLvdsProtocolLabel();

                // Start the automation API only after persisted settings were loaded.
                StartAutomationApi();

                RenderAll();

                // Update status text with the correct (possibly reinitialized) resolution
                int w = GetCurrentWidth();
                int h = GetCurrentHeight();
                LblStatus.Text = $"Load an image (PGM/BMP/PNG are converted to Gray8 u8; will crop top-left to {w}×{h}). Press Start to begin rendering.";
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
                CmbLvdsMode?.SelectedIndex ?? 0,
                _controlMode, _canUartMode,
                _apiAllowRemote, _apiEnableHttps, _apiBindAddress, _apiPort, _apiKey, _apiAllowedCidrs);
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
            SendAvtpSourceFilterCommand();

            UpdateLiveUiEnabledState();

            UpdateCommunicationFaultAvailability();

            // Mode switch is a big behavior change; reset run state and AVTP buffers.
            StopAll();
            _liveCapture.Reassembler.ResetAll();
            _liveCapture.ClearAvtpFrame();

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

            if (_communicationFaultState.LvdsFaultEnabled)
            {
                if (_communicationFaultControlWindow != null)
                    _communicationFaultControlWindow.ClearLvdsFaultForExternalChange();
                else
                {
                    _communicationFaultState.LvdsFaultEnabled = false;
                    ApplyLvdsFaultState();
                }
            }
            ClearActiveCanUartFaultForExternalChange();

            _currentDeviceType = newDeviceType;
            ApplyDeviceTypeConstraints();
            SaveUiSettings();

            // Device type change affects resolution - reinitialize all resolution-dependent objects
            StopAll();
            ReinitializeForNewResolution();
            UpdateLvdsProtocolLabel();

            // Send device-mode command to ECU firmware via Ethernet
            try
            {
                string? txDev = GetTxPcapDeviceNameOrNull();
                if (string.IsNullOrWhiteSpace(txDev))
                {
                    AppendDiagLog("[cmd] ⚠ No NIC available — device-mode command NOT sent to Aurix. Please select a network interface.");
                }
                else
                {
                    AppendDiagLog($"[cmd] Selected NIC for command transmission: {txDev}");
                    DeviceModeCommand.SendDeviceMode(txDev, _currentDeviceType, AppendDiagLog);
                }
            }
            catch (Exception ex) { AppendDiagLog($"[cmd] Exception: {ex.Message}"); }

            LblStatus.Text = $"Device Type: {_currentDeviceType.GetDisplayName()} ({GetCurrentWidth()}x{GetCurrentHeight()}). Load a file or start live capture.";
        }

        private void UpdateLvdsProtocolLabel()
        {
            if (LblLvdsProtocol == null) return;

            if (_currentDeviceType == LsmDeviceType.Nichia)
            {
                LblLvdsProtocol.Text = "Protocol: Nichia\n" +
                                       "Baud: 12,500,000 bps | 8N1 | LSB-first\n" +
                                       "Line: [0x5D][row+parity][256px][CRC16] = 260 B\n" +
                                       "Frame: 64 lines = resolution 256×64";
            }
            else
            {
                LblLvdsProtocol.Text = $"Protocol: {_currentDeviceType.GetDisplayName()}\n" +
                                       "Baud: 20,000,000 bps | 8O1 | LSB-first\n" +
                                       "Frame: [0x80,0xA5,0xAA,0x55][25600px][CRC32] = 25608 B\n" +
                                       "Frame: 80 lines = resolution 320×80";
            }
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

        private void CmbControlMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_settingsManager.IsLoading || !IsLoaded) return;
            _controlMode = CmbControlMode?.SelectedIndex ?? 0;
            ClearActiveLvdsFaultForExternalChange();
            ClearActiveCanUartFaultForExternalChange();
            ApplyModeConstraints();
            SaveUiSettings();
            SendAdapterModeCommand();
            UpdateCommunicationFaultAvailability();
        }

        private void CmbCanUartMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_settingsManager.IsLoading || !IsLoaded) return;
            _canUartMode = CmbCanUartMode?.SelectedIndex ?? 0;
            _canUartLedPhaseStartUtc = DateTime.UtcNow;
            _canUartLedPhaseGreen = false;
            if (_canUartMode == 0 && _canDiagRecording)
                StopCanUartRecordingInternal();
            ClearActiveLvdsFaultForExternalChange();
            ClearActiveCanUartFaultForExternalChange();
            SaveUiSettings();
            SendAdapterModeCommand();
            UpdateCanDiagRecordingButtons();
            UpdateCommunicationFaultAvailability();
            UpdateCanDiagStatusText();
        }

        private void ApplyDeviceTypeConstraints()
        {
            bool isNichia = _currentDeviceType == LsmDeviceType.Nichia;

            if (MenuOsramDefectControl != null)
                MenuOsramDefectControl.IsEnabled = !isNichia;
            if (MenuNichiaDefectControl != null)
                MenuNichiaDefectControl.IsEnabled = isNichia;
        }

        private void ClearActiveLvdsFaultForExternalChange()
        {
            if (!_communicationFaultState.LvdsFaultEnabled)
                return;

            if (_communicationFaultControlWindow != null)
                _communicationFaultControlWindow.ClearLvdsFaultForExternalChange();
            else
            {
                _communicationFaultState.LvdsFaultEnabled = false;
                ApplyLvdsFaultState();
            }
        }

        private void ClearActiveCanUartFaultForExternalChange()
        {
            if (!_communicationFaultState.CanUartFaultEnabled)
                return;

            if (_communicationFaultControlWindow != null)
                _communicationFaultControlWindow.ClearCanUartFaultForExternalChange();
            else
            {
                _communicationFaultState.CanUartFaultEnabled = false;
                ApplyCanUartFaultState();
            }
        }

        /// <summary>
        /// Enforces the hardware constraints between Control Mode, CAN UART Mode and LVDS Mode.
        /// </summary>
        private void ApplyModeConstraints()
        {
            bool isDirect = _controlMode == 1;

            if (CmbLvdsMode != null && CmbLvdsMode.Items.Count >= 2)
            {
                ((System.Windows.Controls.ComboBoxItem)CmbLvdsMode.Items[0]).IsEnabled = !isDirect;
                ((System.Windows.Controls.ComboBoxItem)CmbLvdsMode.Items[1]).IsEnabled = isDirect;

                int requiredLvdsMode = isDirect ? 1 : 0;
                if (CmbLvdsMode.SelectedIndex != requiredLvdsMode)
                    CmbLvdsMode.SelectedIndex = requiredLvdsMode;
            }

            if (CmbCanUartMode == null || CmbCanUartMode.Items.Count < 3)
            {
                UpdateCanDiagRecordingButtons();
                return;
            }

            ((System.Windows.Controls.ComboBoxItem)CmbCanUartMode.Items[0]).IsEnabled = !isDirect;
            ((System.Windows.Controls.ComboBoxItem)CmbCanUartMode.Items[1]).IsEnabled = !isDirect;
            ((System.Windows.Controls.ComboBoxItem)CmbCanUartMode.Items[2]).IsEnabled = isDirect;

            int requiredCanUartMode = isDirect ? 2 : Math.Clamp(_canUartMode, 0, 1);
            if (CmbCanUartMode.SelectedIndex != requiredCanUartMode)
            {
                CmbCanUartMode.SelectedIndex = requiredCanUartMode;
                _canUartMode = requiredCanUartMode;
            }

            UpdateCanDiagRecordingButtons();
        }

        /// <summary>
        /// Sends the current adapter mode (control + CAN UART) to the SmartVisio Box via Ethernet.
        /// </summary>
        private void SendAdapterModeCommand()
        {
            try
            {
                string? txDev = GetTxPcapDeviceNameOrNull();
                if (!string.IsNullOrWhiteSpace(txDev))
                {
                    if (_communicationFaultState.LvdsFaultEnabled ||
                        _communicationFaultState.CanUartFaultEnabled)
                    {
                        AppendDiagLog("[cmd] Adapter-mode sync skipped while a firmware communication fault is active");
                        return;
                    }

                    AdapterModeCommand.SendAdapterMode(txDev, _controlMode, _canUartMode, AppendDiagLog);
                    SendAvtpSourceFilterCommand();
                }
                else
                    AppendDiagLog("[cmd] No NIC selected — adapter-mode command not sent");
            }
            catch (Exception ex) { AppendDiagLog($"[cmd] {ex.Message}"); }
        }

        /// <summary>
        /// Tells the SmartVisio Box which AVTP source to reassemble. While the app
        /// generates the stream in Direct control, a second source on the same
        /// multicast address (CANoe) would interleave its packets into the same
        /// frame and corrupt it, so the box is locked onto our source MAC.
        /// </summary>
        private void SendAvtpSourceFilterCommand()
        {
            try
            {
                string? txDev = GetTxPcapDeviceNameOrNull();
                if (string.IsNullOrWhiteSpace(txDev))
                    return;

                bool lockOnGenerator = _controlMode == 1
                    && _modeOfOperation == ModeOfOperation.PlayerFromFiles;

                AvtpSourceFilterCommand.Send(txDev, lockOnGenerator ? _srcMac : null, AppendDiagLog);
            }
            catch (Exception ex) { AppendDiagLog($"[cmd] {ex.Message}"); }
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

        private void RefreshLiveNicList() => LiveNicSelector.RefreshNicList(CmbLiveNic, _avtpLiveDeviceHint);

        private string? GetTxPcapDeviceNameOrNull() => LiveNicSelector.GetTxPcapDeviceNameOrNull(CmbLiveNic, _avtpLiveDeviceHint);

        private void UpdateLiveUiEnabledState() =>
            LiveNicSelector.UpdateLiveUiEnabledState(CmbLiveNic);

        private void BtnRefreshNics_Click(object sender, RoutedEventArgs e) => RefreshLiveNicList();

        // ── LVDS Ethernet Capture (SmartVisio Box GETH → pane B) ──────────────

        private void StartNichiaEthCapture()
        {
            try
            {
                StopNichiaEthCapture();
                string? nicHint = LiveNicSelector.GetSelectedDeviceName(CmbLiveNic) ?? _avtpLiveDeviceHint;
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

        private void StartOsramEthCapture()
        {
            try
            {
                StopOsramEthCapture();
                string? nicHint = LiveNicSelector.GetSelectedDeviceName(CmbLiveNic) ?? _avtpLiveDeviceHint;
                _osramEthCapture = OsramEthCapture.Start(nicHint, AppendDiagLog);
                _osramEthCapture.OnFrameReady += (frame, meta) =>
                    Dispatcher.BeginInvoke(() => HandleLvdsFrameReady(frame, meta));
                AppendDiagLog("[ofe] Osram Ethernet capture started");
            }
            catch (Exception ex)
            {
                AppendDiagLog($"[ofe] Ethernet capture error: {ex.Message}");
            }
        }

        private void StopOsramEthCapture()
        {
            if (_osramEthCapture != null)
            {
                _osramEthCapture.Dispose();
                _osramEthCapture = null;
                AppendDiagLog("[ofe] Osram Ethernet capture stopped");
            }
        }

        // ─── Basler camera capture (pane C) ────────────────────────────────

        private void StartBaslerCapture(bool useFreeRunFallback = false)
        {
            var existing = _baslerCapture;
            if (existing != null && existing.IsCapturing)
            {
                // The camera outlives a session stop; reopening the pylon device
                // would leave pane C on "Signal not available" for a second or more.
                if (useFreeRunFallback)
                    QueueBaslerWork(() => existing.UseFreeRunFallback());
                return;
            }

            var previous = DetachBaslerCapture();
            int generation = _baslerStartGeneration;

            QueueBaslerWork(() =>
            {
                previous?.Dispose();

                BaslerCameraCapture capture;
                try
                {
                    capture = BaslerCameraCapture.Start(AppendDiagLog);
                    if (useFreeRunFallback)
                        capture.UseFreeRunFallback();
                }
                catch (Exception ex)
                {
                    AppendDiagLog($"[basler] Camera capture error: {ex.Message}");
                    return;
                }

                Dispatcher.BeginInvoke(() =>
                {
                    // A Stop or a newer Start ran while the camera was opening.
                    if (generation != _baslerStartGeneration)
                    {
                        QueueBaslerWork(capture.Dispose);
                        return;
                    }

                    capture.OnFrameReady += (frame, w, h) =>
                        Dispatcher.BeginInvoke(() => HandleBaslerFrameReady(frame, w, h));
                    _baslerCapture = capture;
                    _lastBaslerFrameUtc = DateTime.UtcNow;
                    AppendDiagLog("[basler] Basler camera capture started");
                });
            });
        }

        private void StopBaslerCapture()
        {
            var previous = DetachBaslerCapture();
            if (previous == null)
                return;

            QueueBaslerWork(() =>
            {
                previous.Dispose();
                AppendDiagLog("[basler] Basler camera capture stopped");
            });
        }

        /// <summary>UI thread only: detaches the camera and invalidates a pending open.</summary>
        private BaslerCameraCapture? DetachBaslerCapture()
        {
            _baslerStartGeneration++;
            var previous = _baslerCapture;
            _baslerCapture = null;
            return previous;
        }

        /// <summary>UI thread only: queues camera work off the dispatcher, in call order.</summary>
        private void QueueBaslerWork(Action work) =>
            _baslerWork = _baslerWork.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception ex) { AppendDiagLog($"[basler] Camera lifecycle error: {ex.Message}"); }
            }, TaskScheduler.Default);

        /// <summary>
        /// Drops the pane C image. The bitmap survives a Stop, so without this the
        /// previous session's camera picture stays on screen while the camera opens.
        /// </summary>
        private void ClearPaneCImage()
        {
            lock (_frameLock)
            {
                _latestC = null;
            }
            _downscaledCameraFrame = null;
            _wbC = BitmapUtils.MakeGray8(1, 1);
            if (ImgC != null) ImgC.Source = _wbC;
        }

        private void HandleBaslerFrameReady(byte[] frame, int w, int h)
        {
            // While paused or stopped, keep the frozen frame — don't update pane C.
            if (_playback.IsPaused || _playback.Cts == null) return;

            // Basler may continue receiving valid hardware-triggered frames from
            // the Aurix fallback timer after LVDS has stopped. LVDS loss affects
            // pane B/D only; pane C remains an independent camera signal.
            _lastBaslerFrameUtc = DateTime.UtcNow;
            _baslerSignalLost = false;

            if (!_flickerDetectionEnabled
                || !_flickerInjection.TryGetFrame(frame, w, h, out byte[] displayFrame))
                displayFrame = frame;

            if (_flickerDetectionEnabled)
            {
                // Flicker detection is camera-only and must not wait for an LVDS display credit.
                byte[] detectionFrame = FrameDownscaler.DownscaleBlockAverage(
                    displayFrame, w, h, _currentWidth, _currentHeight);
                FlickerDetectionStatus previousFlickerStatus = _flickerDetector.Snapshot.Status;
                _flickerDetector.ProcessFrame(detectionFrame, _currentWidth, _currentHeight);
                FlickerDetectionStatus currentFlickerStatus = _flickerDetector.Snapshot.Status;
                if (currentFlickerStatus == FlickerDetectionStatus.Candidate)
                {
                    // Keep the strongest frame of the excursion, not merely the first one.
                    int deviatedPixels = _flickerDetector.Snapshot.DeviatedPixelCount;
                    if (previousFlickerStatus != FlickerDetectionStatus.Candidate
                        || _flickerCandidateC == null
                        || deviatedPixels > _flickerCandidatePeakPixels)
                    {
                        _flickerCandidatePeakPixels = deviatedPixels;
                        _flickerCandidateC = new Frame(
                            w, h, (byte[])displayFrame.Clone(), DateTime.UtcNow);
                    }
                }
                else if (previousFlickerStatus == FlickerDetectionStatus.Candidate)
                {
                    ClearFlickerCandidateFrames();
                }
            }

            if (_cameraDisplaySyncEnabled)
            {
                if (_lvdsCameraDisplayCredits <= 0)
                {
                    // Keep the newest camera frame until its LVDS counterpart
                    // has reached pane B through Ethernet reassembly.
                    _pendingCameraFrame = (byte[])displayFrame.Clone();
                    _pendingCameraWidth = w;
                    _pendingCameraHeight = h;
                    return;
                }

                _lvdsCameraDisplayCredits--;
            }

            RenderBaslerFrameReady(displayFrame, w, h);
        }

        private void RenderBaslerFrameReady(byte[] frame, int w, int h)
        {

            // Displayed-FPS: count frames in a 1-second sliding window (stable, immune to UI jitter)
            _baslerDispWindowFrames++;
            long nowTicks = _baslerDispFpsSw.ElapsedTicks;
            double windowSec = (nowTicks - _baslerDispWindowStartTicks) / (double)Stopwatch.Frequency;
            if (windowSec >= 1.0)
            {
                _baslerDispFps = _baslerDispWindowFrames / windowSec;
                _baslerDispWindowStartTicks = nowTicks;
                _baslerDispWindowFrames = 0;
            }

            // Resize _wbC if the camera resolution changed
            if (_wbC.PixelWidth != w || _wbC.PixelHeight != h)
            {
                _wbC = BitmapUtils.MakeGray8(w, h);
                ImgC.Source = _wbC;
                AppendDiagLog($"[basler] Pane C bitmap resized to {w}×{h}");
            }

            _latestC = new Frame(w, h, frame, DateTime.UtcNow);

            // Simulate injected "bright" (Stuck) pixels directly on the camera frame so the
            // user gets a visual confirmation on pane C (and, in LSM comparison modes, on
            // pane D via the downscaled buffer). Only active when injection is enabled.
            PaintInjectedBrightPixelsOnCamera(frame, w, h);

            BitmapUtils.Blit(_wbC, frame, w);

            // Pre-compute downscaled camera frame for comparison modes (avoids redundant work in RenderAll)
            if (_comparisonMode > 0)
            {
                _downscaledCameraFrame = FrameDownscaler.DownscaleBlockAverage(
                    frame, w, h, _currentWidth, _currentHeight, _downscaledCameraFrame);
            }

            // Enqueue pane C frame for recording
            if (_recordingManager.IsRecording)
                _recordingManager.TryEnqueueFrameC(frame);

            // Show live image, hide "Signal not available" overlay
            if (NoSignalC != null) NoSignalC.Visibility = Visibility.Collapsed;
        }

        private void ReleasePendingCameraFrameAfterLvds()
        {
            if (_pendingCameraFrame == null || _lvdsCameraDisplayCredits <= 0)
                return;

            var frame = _pendingCameraFrame;
            int w = _pendingCameraWidth;
            int h = _pendingCameraHeight;
            _pendingCameraFrame = null;
            _lvdsCameraDisplayCredits--;

            RenderBaslerFrameReady(frame, w, h);
        }

        private void ResetCameraDisplaySync()
        {
            _cameraDisplaySyncEnabled = true;
            _lvdsCameraDisplayCredits = 0;
            _pendingCameraFrame = null;
            _pendingCameraWidth = 0;
            _pendingCameraHeight = 0;
        }

        /// <summary>
        /// Minimum on-screen size (in native camera pixels) of a simulated bright pixel,
        /// so a single LED pixel remains clearly visible on the higher-resolution camera
        /// frame even when the block-average ratio is small.
        /// </summary>
        private const int InjectedBrightPixelMinSize = 6;

        /// <summary>
        /// Paints injected "bright" defect pixels (OSRAM Stuck + Nichia Bright) as white (255)
        /// blocks onto the native-resolution camera frame. Each LVDS pixel (0..width-1 ×
        /// 0..height-1) is mapped to the corresponding camera block (block-average geometry)
        /// and drawn at least <see cref="InjectedBrightPixelMinSize"/> pixels wide/high so it
        /// stays visible. No-op unless injection is enabled for the respective store.
        /// </summary>
        private void PaintInjectedBrightPixelsOnCamera(byte[] frame, int camW, int camH)
        {
            int dstW = _currentWidth;
            int dstH = _currentHeight;
            if (dstW <= 0 || dstH <= 0 || camW <= 0 || camH <= 0)
                return;
            if (frame.Length < camW * camH)
                return;

            double blockW = (double)camW / dstW;
            double blockH = (double)camH / dstH;

            var osramStore = _osramDefectStore;
            if (osramStore != null && osramStore.InjectionEnabled)
            {
                foreach (var d in osramStore.GetActiveDefects())
                {
                    if (d.DefectType == OsramDefectType.Stuck)
                        PaintInjectedBrightBlock(frame, camW, camH, d.X, d.Y, dstW, dstH, blockW, blockH);
                }
            }

            var nichiaStore = _nichiaDefectStore;
            if (nichiaStore != null && nichiaStore.InjectionEnabled)
            {
                foreach (var d in nichiaStore.GetActiveDefects())
                {
                    if (d.DefectType == NichiaDefectType.Bright)
                        PaintInjectedBrightBlock(frame, camW, camH, d.X, d.Y, dstW, dstH, blockW, blockH);
                }
            }
        }

        /// <summary>Paints a single injected bright pixel block; shared by OSRAM and Nichia.</summary>
        private static void PaintInjectedBrightBlock(byte[] frame, int camW, int camH, int x, int y, int dstW, int dstH, double blockW, double blockH)
        {
            if (x < 0 || x >= dstW || y < 0 || y >= dstH)
                return;

            // Map the LVDS pixel to its camera block (same geometry as the downscaler).
            int sx0 = (int)(x * blockW);
            int sy0 = (int)(y * blockH);
            int sx1 = (int)((x + 1) * blockW);
            int sy1 = (int)((y + 1) * blockH);

            // Enforce a minimum visible size (grow the block symmetrically if needed).
            if (sx1 - sx0 < InjectedBrightPixelMinSize)
            {
                int cx = (sx0 + sx1) / 2;
                sx0 = cx - InjectedBrightPixelMinSize / 2;
                sx1 = sx0 + InjectedBrightPixelMinSize;
            }
            if (sy1 - sy0 < InjectedBrightPixelMinSize)
            {
                int cy = (sy0 + sy1) / 2;
                sy0 = cy - InjectedBrightPixelMinSize / 2;
                sy1 = sy0 + InjectedBrightPixelMinSize;
            }

            // Clamp to camera bounds.
            if (sx0 < 0) sx0 = 0;
            if (sy0 < 0) sy0 = 0;
            if (sx1 > camW) sx1 = camW;
            if (sy1 > camH) sy1 = camH;

            for (int sy = sy0; sy < sy1; sy++)
            {
                int row = sy * camW;
                for (int sx = sx0; sx < sx1; sx++)
                    frame[row + sx] = 0xFF;
            }
        }

        private DispatcherTimer? _canDiagStatusTimer;
        private DispatcherTimer? _canDiagUiTimer;

        private void InitializeCanDiagMonitor()
        {
            if (LvCanDiag != null)
                LvCanDiag.ItemsSource = _canDiagRows;

            if (LblCanPageInfo != null)
                LblCanPageInfo.Text = "Page: 1 / 1";

            _canDiagStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _canDiagStatusTimer.Tick += (_, _) => UpdateCanDiagStatusText();
            _canDiagStatusTimer.Start();

            _canDiagUiTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = CanDiagRefreshInterval
            };
            _canDiagUiTimer.Tick += (_, _) => RefreshCanDiagUiIfNeeded();
            _canDiagUiTimer.Start();

            _canDiagWatchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

            // Refresh pane C when the camera display FPS becomes available.
            UpdateBaslerRunInfoLabel();
            _canDiagWatchdogTimer.Tick += (_, _) => CanDiagWatchdogTick();
            _canDiagWatchdogTimer.Start();

            // Recording starts as false — user must press Record
            UpdateCanDiagRecordingButtons();
            UpdateCanDiagStatusText();

            // Start Ethernet listener for diagnostic packets immediately.
            // CAN capture is independent of the main Start/Stop cycle so the
            // user can Record/Stop on the CAN monitor at any time.
            StartCanDiagCapture();

            InitializeOsramDefectInjection();
        }

        private void InitializeOsramDefectInjection()
        {
            _osramDefectStore ??= new OsramDefectStore();
            EnsureOsramDefectControlWindow();
            _nichiaDefectStore ??= new NichiaDefectStore();
            EnsureNichiaDefectControlWindow();
        }

        private void EnsureOsramDefectControlWindow()
        {
            if (_osramDefectControlWindow != null)
                return;

            if (_osramDefectStore == null)
                return;

            _osramDefectControlWindow = new OsramDefectControlWindow(_osramDefectStore);
            _osramDefectControlWindow.DefectStateChanged += SendOsramDefectListToAurix;
            _osramDefectControlWindow.Closed += (_, _) => _osramDefectControlWindow = null;
        }

        /// <summary>
        /// Pushes the current OSRAM defect table to the Aurix firmware over Ethernet.
        /// Sent only on state changes (enable/disable/add/remove) from the control window.
        /// The actual ELEDERP/ELEDERS injection into the CAN-UART stream runs in Aurix.
        /// </summary>
        private void SendOsramDefectListToAurix()
        {
            if (_osramDefectStore == null)
                return;

            try
            {
                string? txDev = GetTxPcapDeviceNameOrNull();
                if (string.IsNullOrWhiteSpace(txDev))
                {
                    AppendDiagLog("[cmd] No NIC selected — SET_DEFECT_LIST not sent");
                    return;
                }

                bool enable = _osramDefectStore.InjectionEnabled;
                var defects = _osramDefectStore.GetActiveDefects();
                SetDefectListCommand.Send(txDev, enable, defects, AppendDiagLog);
            }
            catch (Exception ex)
            {
                AppendDiagLog($"[cmd] SET_DEFECT_LIST error: {ex.Message}");
            }
        }

        private void EnsureNichiaDefectControlWindow()
        {
            if (_nichiaDefectControlWindow != null)
                return;

            if (_nichiaDefectStore == null)
                return;

            _nichiaDefectControlWindow = new NichiaDefectControlWindow(_nichiaDefectStore);
            _nichiaDefectControlWindow.DefectStateChanged += SendNichiaDefectListToAurix;
            _nichiaDefectControlWindow.Closed += (_, _) => _nichiaDefectControlWindow = null;
        }

        /// <summary>
        /// Pushes the current Nichia defect table to the SmartVisio Box firmware over Ethernet.
        /// Sent only on state changes (enable/disable/add/remove) from the control window.
        /// </summary>
        private void SendNichiaDefectListToAurix()
        {
            if (_nichiaDefectStore == null)
                return;

            try
            {
                string? txDev = GetTxPcapDeviceNameOrNull();
                if (string.IsNullOrWhiteSpace(txDev))
                {
                    AppendDiagLog("[cmd] No NIC selected — SET_DEFECT_LIST (Nichia) not sent");
                    return;
                }

                bool enable = _nichiaDefectStore.InjectionEnabled;
                var defects = _nichiaDefectStore.GetActiveDefects();
                SetDefectListCommand.SendNichia(txDev, enable, defects, AppendDiagLog);
            }
            catch (Exception ex)
            {
                AppendDiagLog($"[cmd] SET_DEFECT_LIST (Nichia) error: {ex.Message}");
            }
        }

        private void StartCanDiagCapture()
        {
            try
            {
                StopCanDiagCapture();
                string? nicHint = LiveNicSelector.GetSelectedDeviceName(CmbLiveNic) ?? _avtpLiveDeviceHint;
                _canDiagCapture = LsmCanDiagCapture.Start(nicHint, AppendDiagLog);
                // NOTE: OSRAM defect injection is NOT applied to the displayed trace here.
                // The C# side only DEFINES defects (via OsramDefectControlWindow); the actual
                // ELEDERP/ELEDERS injection into the CAN-UART stream is performed in the Aurix
                // firmware (LSM -> SmartVisio Box -> ECU). Records are shown unmodified.
                _canDiagCapture.OnRecordReady += HandleCanDiagRecord;
                AppendDiagLog("[can] diagnostic capture started");
                UpdateCanDiagStatusText();
            }
            catch (Exception ex)
            {
                AppendDiagLog($"[can] capture error: {ex.Message}");
                UpdateCanDiagStatusText();
            }
        }

        private void StopCanDiagCapture()
        {
            if (_canDiagCapture != null)
            {
                _canDiagCapture.Dispose();
                _canDiagCapture = null;
                AppendDiagLog("[can] diagnostic capture stopped");
            }

            UpdateCanDiagStatusText();
        }

        private void HandleCanDiagRecord(LsmCanDiagRecord record)
        {
            if (_canDiagCapture == null || !_canDiagCapture.IsCapturing)
                return;

            if (!_canDiagRecording)
                return;

            _canDiagLastRecordUtc = DateTime.UtcNow;
            _canDiagSessionHadTraffic = true;
            _canDiagConsecutiveRestarts = 0;
            _canDiagWatchdogRecovering = false;

            // Discard stale records from Dispatcher queue that were enqueued
            // before the current Record session started (fixes duplicate Seqs
            // leaking from a previous recording after Clear + Record).
            if (record.ReceivedUtc < _canRecordSessionStart)
                return;

            if (!_canHasPreviousSourceTimestamp)
            {
                _canDisplayTimestampUtc = record.ReceivedUtc;
                _canHasPreviousSourceTimestamp = true;
            }
            else
            {
                uint deltaUs = unchecked(record.SourceTimestamp - _canPreviousSourceTimestamp);
                _canDisplayTimestampUtc = _canDisplayTimestampUtc.AddTicks((long)deltaUs * 10L);
            }

            _canPreviousSourceTimestamp = record.SourceTimestamp;
            record = record.WithDisplayTiming(_canDisplayTimestampUtc, _canNextDisplaySequence++);
            _canDiagStore.Append(record);
            _pendingRawCanRecords.Enqueue(record);
            Interlocked.Exchange(ref _canDiagUiRefreshRequested, 1);
            // Auto-stop recording when store is full to prevent
            // oldest records from being silently discarded.
            if (_canDiagStore.IsFull)
            {
                if (Interlocked.Exchange(ref _canDiagStopRequested, 1) == 0)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_canDiagRecording)
                            StopCanUartRecordingInternal();
                    });
                }
            }
        }

        private void RefreshCanDiagUiIfNeeded()
        {
            if (Interlocked.Exchange(ref _canDiagUiRefreshRequested, 0) == 0
                && _pendingRawCanRecords.IsEmpty)
                return;

            _canDiagLastRefresh = DateTime.UtcNow;
            FlushPendingRawCanLines();
            RefreshCanDiagView();
        }

        private void RefreshCanDiagView()
        {
            int pageSize = CurrentCanPageSize;
            int totalRecords = _canDiagStore.Count;
            _canDiagTotalPages = Math.Max(1, (int)Math.Ceiling(totalRecords / (double)pageSize));
            _canDiagCurrentPage = Math.Max(1, Math.Min(_canDiagCurrentPage, _canDiagTotalPages));

            // The default monitor order is chronological, so page 1 remains stable while
            // later pages are populated as the recording continues.
            var snapshot = _canSortColumn == "Nr"
                ? _canDiagStore.SnapshotOldestFirst((_canDiagCurrentPage - 1) * pageSize, pageSize)
                : _canDiagStore.SnapshotNewestFirst(0);
            var filtered = snapshot
                .Where(MatchesCanDiagFilter)
                .Select(CanDiagRowView.FromRecord)
                .ToList();

            IEnumerable<CanDiagRowView> ordered = _canSortColumn switch
            {
                "Time (us)" => filtered
                    .OrderBy(row => row.RawDisplayTimestampUtc)
                    .ThenBy(row => row.RawSequence),
                "Name" => filtered.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
                "Address" => filtered.OrderBy(row => row.RawAddress),
                "MemoryType" => filtered.OrderBy(row => row.MemoryType, StringComparer.OrdinalIgnoreCase),
                "Device" => filtered.OrderBy(row => row.Device, StringComparer.OrdinalIgnoreCase),
                "R/W" => filtered.OrderBy(row => row.Operation, StringComparer.OrdinalIgnoreCase),
                "Value" => filtered.OrderBy(row => row.Value, StringComparer.OrdinalIgnoreCase),
                "Error" => filtered.OrderBy(row => row.Error, StringComparer.OrdinalIgnoreCase),
                _ => filtered.OrderBy(row => row.RawSequence),  // "Nr" default
            };

            if (!_canSortAscending)
                ordered = ordered.Reverse();

            List<CanDiagRowView> pageItems;
            if (_canSortColumn == "Nr")
                pageItems = [.. ordered];
            else
                pageItems = [.. ordered
                    .Skip((_canDiagCurrentPage - 1) * pageSize)
                    .Take(pageSize)];

            _canDiagRows.Clear();

            foreach (var row in pageItems)
                _canDiagRows.Add(row);

            if (LblCanPageInfo != null)
                LblCanPageInfo.Text = $"Page: {_canDiagCurrentPage} / {_canDiagTotalPages}";

            bool hasMessages = totalRecords > 0;
            if (BtnCanPrevPage != null)
                BtnCanPrevPage.IsEnabled = hasMessages && _canDiagCurrentPage > 1;

            if (BtnCanNextPage != null)
                BtnCanNextPage.IsEnabled = hasMessages && _canDiagCurrentPage < _canDiagTotalPages;

            UpdateCanDiagStatusText();
        }

        private bool MatchesCanDiagFilter(LsmCanDiagRecord record)
        {
            // All filter combos removed — show all records.
            return true;
        }

        private void UpdateCanDiagStatusText()
        {
            if (CanUartStateLed == null)
                return;

            if (_canUartMode == 0)
            {
                SetCanUartLed(System.Windows.Media.Brushes.Gray, "CAN/UART offline in ECU↔LSM mode");
                return;
            }

            if (_canDiagRecording)
            {
                SetCanUartLed(System.Windows.Media.Brushes.Red, "CAN/UART recording");
                return;
            }

            if (_canDiagCapture?.IsCapturing != true)
            {
                SetCanUartLed(System.Windows.Media.Brushes.Gray, "CAN/UART offline");
                return;
            }

            TimeSpan phaseDuration = _canUartLedPhaseGreen
                ? CanUartMonitoringGreenDuration
                : CanUartMonitoringGrayDuration;
            if (DateTime.UtcNow - _canUartLedPhaseStartUtc >= phaseDuration)
            {
                _canUartLedPhaseGreen = !_canUartLedPhaseGreen;
                _canUartLedPhaseStartUtc = DateTime.UtcNow;
            }

            SetCanUartLed(_canUartLedPhaseGreen
                    ? new SolidColorBrush(StatusLedRunningGreen)
                    : System.Windows.Media.Brushes.Gray,
                "CAN/UART monitoring");
        }

        private void SetCanUartLed(System.Windows.Media.Brush brush, string toolTip)
        {
            CanUartStateLed.Fill = brush;
            CanUartStateLed.ToolTip = toolTip;
        }

        private static string GetSelectedComboContent(System.Windows.Controls.ComboBox? combo)
        {
            if (combo?.SelectedItem is System.Windows.Controls.ComboBoxItem item)
                return item.Content?.ToString() ?? string.Empty;

            return string.Empty;
        }

        private void CmbCanFilters_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_settingsManager.IsLoading || !IsLoaded)
                return;

            _canDiagCurrentPage = 1;
            RefreshCanDiagView();
        }

        private void LvCanDiag_ColumnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader header || header.Column == null)
                return;

            // Strip any existing sort glyph from the header text
            string? headerText = header.Column.Header?.ToString()?.TrimEnd(' ', '▲', '▼', '△', '▽');
            if (string.IsNullOrEmpty(headerText)) return;

            // Toggle direction if same column, otherwise default to ascending
            if (string.Equals(headerText, _canSortColumn, StringComparison.Ordinal))
                _canSortAscending = !_canSortAscending;
            else
            {
                _canSortColumn = headerText;
                _canSortAscending = true;
            }

            // Update sort indicator glyph on column headers
            UpdateCanSortGlyph(header);

            _canDiagCurrentPage = 1;
            RefreshCanDiagView();
        }

        private void UpdateCanSortGlyph(GridViewColumnHeader? activeHeader)
        {
            // Remove glyph from previous header
            if (_canLastSortHeader != null)
                _canLastSortHeader.Column.Header = _canLastSortHeader.Column.Header?.ToString()?.TrimEnd(' ', '▲', '▼', '△', '▽');

            if (activeHeader?.Column == null) return;

            string baseText = activeHeader.Column.Header?.ToString()?.TrimEnd(' ', '▲', '▼', '△', '▽') ?? "";
            activeHeader.Column.Header = baseText + (_canSortAscending ? " △" : " ▽");
            _canLastSortHeader = activeHeader;
        }

        private bool _canMonitorExpanded;
        private const double CanDiagRowHeight = 18.0;
        private const double CanDiagLayoutReserve = 4.0;
        private const int CanDiagCompactRowCorrection = 1;
        private const int CanDiagExpandedRowCorrection = 2;
        private bool _canDiagSizeRefreshPending;

        private int CurrentCanPageSize
        {
            get
            {
                if (LvCanDiag == null || LvCanDiag.ActualHeight <= 0)
                    return 15;

                double headerHeight = FindVisualChild<GridViewColumnHeader>(LvCanDiag)?.ActualHeight ?? 0.0;
                // Do not use ScrollViewer.ViewportHeight here. With vertical scrolling
                // disabled, WPF can report a one-row viewport even when the ListView is
                // much taller, which collapses pagination to one record per page.
                double availableRowsHeight = LvCanDiag.ActualHeight
                    - headerHeight
                    - SystemParameters.HorizontalScrollBarHeight
                    - CanDiagLayoutReserve;
                int fittedRows = (int)Math.Floor(availableRowsHeight / CanDiagRowHeight);
                int correction = _canMonitorExpanded
                    ? CanDiagExpandedRowCorrection
                    : CanDiagCompactRowCorrection;
                return Math.Max(1, fittedRows - correction);
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                    return match;

                var nested = FindVisualChild<T>(child);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private void LvCanDiag_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_canDiagSizeRefreshPending)
                return;

            _canDiagSizeRefreshPending = true;
            Dispatcher.BeginInvoke(() =>
            {
                _canDiagSizeRefreshPending = false;
                RefreshCanDiagView();
            }, DispatcherPriority.Loaded);
        }

        private void BtnCanExpandCollapse_Click(object sender, RoutedEventArgs e)
        {
            _canMonitorExpanded = !_canMonitorExpanded;

            if (_canMonitorExpanded)
            {
                // Expand: CAN monitor takes full right column, hide LVDS Info + Status
                SidebarCan.SetValue(Grid.RowSpanProperty, 5);
                SidebarInfo.Visibility = Visibility.Collapsed;

                // Update icon: arrows pointing inward (collapse)
                PathExpandTop.Data = Geometry.Parse("M 4,2 L 8,6 L 12,2");
                PathExpandBottom.Data = Geometry.Parse("M 4,14 L 8,10 L 12,14");
                BtnCanExpandCollapse.ToolTip = "Collapse CAN Monitor";
            }
            else
            {
                // Collapse: restore original layout
                SidebarCan.SetValue(Grid.RowSpanProperty, 2);
                SidebarInfo.Visibility = Visibility.Visible;

                // Update icon: arrows pointing outward (expand)
                PathExpandTop.Data = Geometry.Parse("M 4,6 L 8,2 L 12,6");
                PathExpandBottom.Data = Geometry.Parse("M 4,10 L 8,14 L 12,10");
                BtnCanExpandCollapse.ToolTip = "Expand CAN Monitor";
            }

            // Recalculate page with new page size
            _canDiagCurrentPage = 1;
            RefreshCanDiagView();
        }

        private void BtnCanClear_Click(object sender, RoutedEventArgs e)
        {
            ClearCanUartInternal();
        }

        private void ClearCanUartInternal()
        {
            _canDiagStore.Clear();
            ResetCanDisplayTiming();
            _canDiagTraceLoaded = false;
            _rawCanLines.Clear();
            while (_pendingRawCanRecords.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _canDiagUiRefreshRequested, 0);

            if (TblRawCan != null)
                TblRawCan.Text = string.Empty;

            _canDiagCurrentPage = 1;
            // Reset capture counters so Rx/CD/OS restart from 0
            _canDiagCapture?.ResetCounters();
            UpdateCanDiagRecordingButtons();
            RefreshCanDiagView();
        }

        /// <summary>
        /// Ensures the CAN diagnostic Ethernet listener is active.
        /// Called from Record so capture works even if it was never started or was disposed.
        /// </summary>
        private void EnsureCanDiagCapture()
        {
            if (_canDiagCapture is not null && _canDiagCapture.IsCapturing)
                return;
            StartCanDiagCapture();
        }

        private void BtnCanRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_canDiagRecording)
                StopCanUartRecordingInternal();
            else
                StartCanUartRecordingInternal();
        }
        private void StartCanUartRecordingInternal()
        {
            // Ensure the Ethernet listener is running (independent of main Start/Stop)
            EnsureCanDiagCapture();

            // Mark session start BEFORE sending START — any records with ReceivedUtc
            // earlier than this are stale leftovers from the Dispatcher queue.
            _canRecordSessionStart = DateTime.UtcNow;
            _canDiagLastRecordUtc = _canRecordSessionStart;
            _canDiagSessionHadTraffic = false;
            _canDiagConsecutiveRestarts = 0;
            _canDiagWatchdogRecovering = false;

            // Tell SmartVisio Box to start diagnostic sniffing.
            // Firmware always resets on START (no 0→1 guard), so this works
            // even if g_diagSniffEnabled was already 1 from a previous session.
            SendDiagSniffStart();

            // Reset capture counters so Rx/CD/OS restart from 0
            _canDiagCapture?.ResetCounters();

            // Start a fresh recording session: clear previous data
            _canDiagStore.Clear();
            ResetCanDisplayTiming();
            _canDiagTraceLoaded = false;
            _rawCanLines.Clear();
            while (_pendingRawCanRecords.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _canDiagUiRefreshRequested, 0);

            if (TblRawCan != null)
                TblRawCan.Text = string.Empty;

            _canDiagCurrentPage = 1;
            _canDiagRecording = true;
            _canUartLedPhaseStartUtc = DateTime.UtcNow;
            _canUartLedPhaseGreen = false;
            Interlocked.Exchange(ref _canDiagStopRequested, 0);

            UpdateCanDiagRecordingButtons();
            RefreshCanDiagView();

            // Start a retry timer: if CD stays 0 after 2 seconds, resend START.
            StartDiagRetryTimer();
        }

        private void ResetCanDisplayTiming()
        {
            _canDisplayTimestampUtc = DateTime.MinValue;
            _canPreviousSourceTimestamp = 0;
            _canHasPreviousSourceTimestamp = false;
            _canNextDisplaySequence = 0;
        }

        /// <summary>Sends DiagSniff START to SmartVisio Box (3× broadcast for reliability).</summary>
        private void SendDiagSniffStart()
        {
            try
            {
                string? txDev = GetTxPcapDeviceNameOrNull();
                if (!string.IsNullOrWhiteSpace(txDev))
                    DiagSniffCommand.Send(txDev, start: true, AppendDiagLog);
            }
            catch (Exception ex) { AppendDiagLog($"[cmd] DiagSniff start: {ex.Message}"); }
        }

        /// <summary>
        /// Periodically resends START until CD packets are received or recording stops.
        /// Handles transient firmware/network issues that cause the initial START to be lost.
        /// </summary>
        private void StartDiagRetryTimer()
        {
            StopDiagRetryTimer();
            _canDiagRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _canDiagRetryTimer.Tick += DiagRetryTimer_Tick;
            _canDiagRetryTimer.Start();
        }

        private void StopDiagRetryTimer()
        {
            if (_canDiagRetryTimer != null)
            {
                _canDiagRetryTimer.Stop();
                _canDiagRetryTimer = null;
            }
        }

        private void DiagRetryTimer_Tick(object? sender, EventArgs e)
        {
            // Stop retrying if recording stopped or CD packets are flowing
            if (!_canDiagRecording)
            {
                StopDiagRetryTimer();
                return;
            }

            long cd = _canDiagCapture?.DiagMagicMatches ?? 0;
            if (cd > 0)
            {
                StopDiagRetryTimer();
                AppendDiagLog("[cmd] DiagSniff retry: CD packets received, stopping retries");
                return;
            }

            // CD still 0 — resend START
            AppendDiagLog("[cmd] DiagSniff retry: CD:0 — resending START");
            SendDiagSniffStart();
        }

        private void CanDiagWatchdogTick()
        {
            if (!_canDiagRecording)
                return;

            if (_canDiagWatchdogRecovering)
                return;

            // Only recover sessions that had traffic and then went silent.
            if (!_canDiagSessionHadTraffic)
                return;

            // Fresh data is still flowing.
            if (_canDiagLastRecordUtc != DateTime.MinValue
                && (DateTime.UtcNow - _canDiagLastRecordUtc) <= CanDiagStallTimeout)
                return;

            // If the user just pressed Record, give startup/retry path time before watchdog recovery.
            if (_canDiagLastRecordUtc == DateTime.MinValue
                || (DateTime.UtcNow - _canDiagLastRecordUtc) <= TimeSpan.FromSeconds(2))
                return;

            RecoverCanDiagRecording();
        }

        private void RecoverCanDiagRecording()
        {
            try
            {
                _canDiagWatchdogRecovering = true;
                _canDiagConsecutiveRestarts++;

                AppendDiagLog($"[can-watchdog] no records for {CanDiagStallTimeout.TotalSeconds:F0}s while recording; restarting capture (attempt {_canDiagConsecutiveRestarts})");

                StopCanDiagCapture();
                StartCanDiagCapture();

                _canRecordSessionStart = DateTime.UtcNow;
                _canDiagLastRecordUtc = _canRecordSessionStart;
                _canDiagSessionHadTraffic = false;
                _canDiagCapture?.ResetCounters();

                // Re-arm firmware sniff mode after reopening capture.
                SendDiagSniffStart();
                StartDiagRetryTimer();
            }
            catch (Exception ex)
            {
                AppendDiagLog($"[can-watchdog] recovery failed: {ex.Message}");
            }
            finally
            {
                _canDiagWatchdogRecovering = false;
            }
        }

        private void StopCanUartRecordingInternal()
        {
            _canDiagRecording = false;
            Interlocked.Exchange(ref _canDiagStopRequested, 0);
            _canDiagWatchdogRecovering = false;
            _canDiagSessionHadTraffic = false;
            _canDiagConsecutiveRestarts = 0;

            StopDiagRetryTimer();
            UpdateCanDiagRecordingButtons();

            // Tell SmartVisio Box to stop diagnostic sniffing
            try
            {
                string? txDev = GetTxPcapDeviceNameOrNull();
                if (!string.IsNullOrWhiteSpace(txDev))
                    DiagSniffCommand.Send(txDev, start: false, AppendDiagLog);
            }
            catch (Exception ex)
            {
                AppendDiagLog($"[cmd] DiagSniff stop: {ex.Message}");
            }
        }

        private void UpdateCanDiagRecordingButtons()
        {
            if (BtnCanRecord != null)
            {
                bool canRecordInCurrentMode = _canUartMode != 0;
                BtnCanRecord.IsEnabled = canRecordInCurrentMode;
                BtnCanRecord.Content = _canDiagRecording ? "⏹ Stop" : "⏺ Record";
                BtnCanRecord.Background = _canDiagRecording
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0x33, 0x33))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF6, 0xF6, 0xF6));
                BtnCanRecord.Foreground = _canDiagRecording
                    ? System.Windows.Media.Brushes.White
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44));
                BtnCanRecord.ToolTip = canRecordInCurrentMode
                    ? null
                    : "CAN-UART recording is available only in ECU↔SmartVisio↔LSM or SmartVisio↔LSM mode.";
            }

            if (BtnCanReplay != null)
                BtnCanReplay.IsEnabled = _controlMode == 1 &&
                    !_canDiagRecording &&
                    _canDiagTraceLoaded;
        }

        private void BtnCanReplay_Click(object sender, RoutedEventArgs e)
        {
            if (_controlMode != 1 || _canDiagRecording)
                return;

            try
            {
                string? txDev = GetTxPcapDeviceNameOrNull();
                if (string.IsNullOrWhiteSpace(txDev))
                {
                    MessageBox.Show("No Ethernet adapter selected.", "Replay", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!_canDiagTraceLoaded)
                    return;

                var trace = _canDiagStore.SnapshotOldestFirst(0, _canDiagStore.Count);
                if (_currentDeviceType == LsmDeviceType.Nichia)
                {
                    NichiaStartupSequenceCommand.Send(txDev, trace, AppendDiagLog);
                    MessageBox.Show("The NICHIA start-up sequence was uploaded to AURIX.",
                        "Replay", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    OsramStartupSequenceCommand.Send(txDev, trace, AppendDiagLog);
                    MessageBox.Show("The OSRAM start-up sequence was uploaded to AURIX.",
                    "Replay", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                AppendDiagLog($"[cmd] Startup replay failed: {ex.Message}");
                MessageBox.Show(ex.Message, "Replay failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SyncCanUartPageJumpText()
        {
            if (TxtCanPageJump == null)
                return;
        
            int page = Math.Max(1, _canDiagCurrentPage);
            string text = page.ToString();
        
            if (TxtCanPageJump.Text != text)
                TxtCanPageJump.Text = text;
        }

        private void BtnCanPrevPage_Click(object sender, RoutedEventArgs e)
        {
            PreviousCanUartPageInternal();
        }
        private void PreviousCanUartPageInternal()
        {
            if (_canDiagCurrentPage <= 1)
                return;

            _canDiagCurrentPage--;
            RefreshCanDiagView();
            SyncCanUartPageJumpText();
        }

        private void BtnCanNextPage_Click(object sender, RoutedEventArgs e)
        {
            NextCanUartPageInternal();
        }

        private void NextCanUartPageInternal()
        {
            if (_canDiagCurrentPage >= _canDiagTotalPages)
                return;

            _canDiagCurrentPage++;
            RefreshCanDiagView();
            SyncCanUartPageJumpText();
        }

        private void TxtCanPageJump_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter)
                return;

            if (int.TryParse(TxtCanPageJump?.Text, out int page))
            {
                SetCanUartPageInternal(page);
            }

            TxtCanPageJump?.SelectAll();
            e.Handled = true;
        }

        private void SetCanUartPageInternal(int page)
        {
            if (page < 1 || page > _canDiagTotalPages)
                return;

            _canDiagCurrentPage = page;
            RefreshCanDiagView();
            SyncCanUartPageJumpText();
        }

        // ── CAN/UART Trace: Save (.rply) ───────────────────────────────────────

        private static string GetCanUartTracesDirectory()
        {
            string? root = RecordingManager.FindRepoRootWithDocs(AppContext.BaseDirectory)
                           ?? RecordingManager.FindRepoRootWithDocs(Directory.GetCurrentDirectory());
            string baseDir = root ?? Directory.GetCurrentDirectory();
            string outDir = System.IO.Path.Combine(baseDir, "docs", "outputs", "canUartTraces");
            Directory.CreateDirectory(outDir);
            return outDir;
        }

        private void BtnCanSaveTrace_Click(object sender, RoutedEventArgs e)
        {
            var snapshot = _canDiagStore.SnapshotNewestFirst(0);
            if (snapshot.Count == 0)
            {
                MessageBox.Show("No records to save.", "Save Trace", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string dir = GetCanUartTracesDirectory();
            string defaultName = $"trace_{DateTime.Now:yyyyMMdd_HHmmss}.rply";

            var dlg = new SaveFileDialog
            {
                Title = "Save UART/CAN trace",
                Filter = "Replay trace (*.rply)|*.rply",
                InitialDirectory = dir,
                FileName = defaultName,
            };

            if (dlg.ShowDialog(this) != true)
                return;

            try
            {
                // Write oldest-first (chronological order)
                var ordered = snapshot.OrderBy(r => r.Sequence).ToList();
                using var sw = new StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8);
                sw.WriteLine($"// VilsSharpX CAN/UART trace – {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sw.WriteLine($"// Records: {ordered.Count}");
                sw.WriteLine("// Seq;Timestamp;Device;Op;Address;Value;Status;RspDelayUs;IfDelayUs;RawHex");

                foreach (var r in ordered)
                {
                    string ts = r.ReceivedUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    string hex = r.RawLength > 0
                        ? BitConverter.ToString(r.RawPayload, 0, Math.Min(r.RawLength, r.RawPayload.Length)).Replace("-", "")
                        : "";
                    sw.WriteLine(string.Join(";",
                        r.Sequence.ToString(CultureInfo.InvariantCulture),
                        ts,
                        r.DeviceName,
                        r.OperationName,
                        $"0x{r.Address:X4}",
                        $"0x{r.Value:X8}",
                        r.Status.ToString(),
                        r.ResponseDelayUs.ToString(CultureInfo.InvariantCulture),
                        r.InterFrameDelayUs.ToString(CultureInfo.InvariantCulture),
                        hex));
                }

                AppendDiagLog($"[trace] Saved {ordered.Count} records to {dlg.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed: {ex.Message}", "Save Trace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── CAN/UART Trace: Open (.rply) ───────────────────────────────────────

        private void BtnCanOpenTraceFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = GetCanUartTracesDirectory();

            var dlg = new OpenFileDialog
            {
                Title = "Open UART/CAN trace",
                Filter = "Replay trace (*.rply)|*.rply|All files (*.*)|*.*",
                InitialDirectory = dir,
            };

            if (dlg.ShowDialog(this) != true)
                return;

            try
            {
                var records = ParseRplyFile(dlg.FileName);
                if (records.Count == 0)
                {
                    MessageBox.Show("No valid records found in the file.", "Open Trace", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Clear existing data and load trace
                _canDiagStore.Clear();
                _rawCanLines.Clear();
                if (TblRawCan != null) TblRawCan.Text = string.Empty;
                _canDiagRecording = false;
                _canDiagTraceLoaded = true;
                UpdateCanDiagRecordingButtons();

                // Add records oldest-first (store prepends, so last added = newest on top)
                foreach (var r in records)
                    _canDiagStore.Append(r);

                _canDiagCurrentPage = 1;
                RefreshCanDiagView();

                AppendDiagLog($"[trace] Loaded {records.Count} records from {dlg.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Load failed: {ex.Message}", "Open Trace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static List<LsmCanDiagRecord> ParseRplyFile(string path)
        {
            var records = new List<LsmCanDiagRecord>();

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//"))
                    continue;

                var parts = line.Split(';');
                if (parts.Length < 9)
                    continue;

                try
                {
                    ushort seq = ushort.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                    DateTime ts = DateTime.ParseExact(parts[1].Trim(), "yyyy-MM-dd HH:mm:ss.fff",
                        CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

                    byte deviceId = parts[2].Trim() switch
                    {
                        "OSRAM" => 1,
                        "NICHIA" => 0,
                        _ => byte.TryParse(parts[2].Trim().Replace("DEV", ""), System.Globalization.NumberStyles.HexNumber, null, out byte d) ? d : (byte)255,
                    };

                    LsmCanDiagOperation op = parts[3].Trim() switch
                    {
                        "W" => LsmCanDiagOperation.Write,
                        "R" => LsmCanDiagOperation.Read,
                        "CAN" => LsmCanDiagOperation.CanRaw,
                        _ => LsmCanDiagOperation.Read,
                    };

                    ushort addr = Convert.ToUInt16(parts[4].Trim().Replace("0x", ""), 16);
                    uint value = Convert.ToUInt32(parts[5].Trim().Replace("0x", ""), 16);

                    LsmCanDiagStatus status = Enum.TryParse<LsmCanDiagStatus>(parts[6].Trim(), true, out var s)
                        ? s : LsmCanDiagStatus.Ok;

                    ushort rspDelay = ushort.Parse(parts[7].Trim(), CultureInfo.InvariantCulture);
                    ushort ifDelay = ushort.Parse(parts[8].Trim(), CultureInfo.InvariantCulture);

                    byte[] rawPayload = [];
                    byte rawLen = 0;
                    if (parts.Length > 9 && !string.IsNullOrWhiteSpace(parts[9]))
                    {
                        string hex = parts[9].Trim();
                        rawPayload = new byte[hex.Length / 2];
                        for (int i = 0; i < rawPayload.Length; i++)
                            rawPayload[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                        rawLen = (byte)Math.Min(rawPayload.Length, 255);
                    }

                    records.Add(new LsmCanDiagRecord
                    {
                        Sequence = seq,
                        RecordType = LsmCanDiagRecord.RegisterIoRecordType,
                        SourceTimestamp = 0,
                        Address = addr,
                        ResponseDelayUs = rspDelay,
                        InterFrameDelayUs = ifDelay,
                        Value = value,
                        Checksum = 0,
                        DeviceId = deviceId,
                        Operation = op,
                        Status = status,
                        RawLength = rawLen,
                        RawPayload = rawPayload,
                        ReceivedUtc = ts,
                    });
                }
                catch
                {
                    // Skip malformed lines
                    continue;
                }
            }

            return records;
        }

        // ── CAN Monitor: tab switching ──────────────────────────────────────────

        private enum CanTab { Monitor, RawCan, UartTransaction }
        private CanTab _activeCanTab = CanTab.Monitor;
        private readonly System.Collections.Generic.List<string> _rawCanLines = [];
        private const int RawCanMaxLines = 500;

        private void BtnCanTabMonitor_Click(object sender, RoutedEventArgs e)  => SetCanTab(CanTab.Monitor);
        private void BtnCanTabRawCan_Click(object sender, RoutedEventArgs e)   => SetCanTab(CanTab.RawCan);
        private void BtnCanTabUart_Click(object sender, RoutedEventArgs e)     => SetCanTab(CanTab.UartTransaction);

        private void SetCanTab(CanTab tab)
        {
            _activeCanTab = tab;

            if (LvCanDiag != null)   LvCanDiag.Visibility   = tab == CanTab.Monitor          ? Visibility.Visible : Visibility.Collapsed;
            if (ScvRawCan != null)   ScvRawCan.Visibility   = tab == CanTab.RawCan            ? Visibility.Visible : Visibility.Collapsed;
            if (GridUartTx != null)  GridUartTx.Visibility  = tab == CanTab.UartTransaction  ? Visibility.Visible : Visibility.Collapsed;

            // Update pill button styles
            ApplyCanTabStyle(BtnCanTabMonitor,  tab == CanTab.Monitor);
            ApplyCanTabStyle(BtnCanTabRawCan,   tab == CanTab.RawCan);
            ApplyCanTabStyle(BtnCanTabUart,     tab == CanTab.UartTransaction);

            // Update centered title
            if (LblCanTabTitle != null)
            {
                LblCanTabTitle.Text = tab switch
                {
                    CanTab.RawCan => "CAN-UART",
                    CanTab.UartTransaction => "UART Transaction",
                    _ => "UART Monitor",
                };
            }

            if (tab == CanTab.RawCan)
                FlushRawCanText();
        }

        private static void ApplyCanTabStyle(System.Windows.Controls.Button? btn, bool active)
        {
            if (btn == null) return;
            if (active)
            {
                btn.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1EA6E6"));
                btn.Foreground = System.Windows.Media.Brushes.White;
                btn.BorderBrush = btn.Background;
                btn.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                btn.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F6F6F6"));
                btn.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#444444"));
                btn.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D8D8D8"));
                btn.FontWeight = FontWeights.Normal;
            }
        }

        private void AppendRawCanLine(LsmCanDiagRecord record, bool flushUi = true)
        {
            string line;
            if (record.IsCanRawFrame)
            {
                // CAN frame format: > CAN[ ts ID=0x123 [DLC] AA BB CC DD ]
                string idStr = record.IsExtendedCanId ? $"0x{record.CanId:X8}" : $"0x{record.CanId:X3}";
                string dataHex = record.RawLength > 0
                    ? BitConverter.ToString(record.RawPayload, 0, Math.Min(record.RawLength, record.RawPayload.Length)).Replace("-", " ")
                    : "";
                line = $"> CAN[ {record.SourceTimestamp} ID={idStr} [{record.RawLength}] {dataHex} ]";
            }
            else
            {
                // UART diagnostic format: > cCAN[ UnixTs 0xHEX_FULL_PAYLOAD ]
                string hex = record.RawLength > 0
                    ? "0x" + BitConverter.ToString(record.RawPayload, 0, Math.Min(record.RawLength, record.RawPayload.Length)).Replace("-", "")
                    : $"0x{record.Value:X8}";
                line = $"> cCAN[ {record.SourceTimestamp} {hex} ]";
            }
            _rawCanLines.Add(line);
            if (_rawCanLines.Count > RawCanMaxLines)
                _rawCanLines.RemoveAt(0);

            if (flushUi && _activeCanTab == CanTab.RawCan)
                FlushRawCanText();
        }

        private void FlushRawCanText()
        {
            if (TblRawCan == null) return;
            TblRawCan.Text = string.Join("\n", _rawCanLines);
            // Scroll to end
            ScvRawCan?.ScrollToEnd();
        }

        private void FlushPendingRawCanLines()
        {
            while (_pendingRawCanRecords.TryDequeue(out var record))
                AppendRawCanLine(record, flushUi: false);

            if (_activeCanTab == CanTab.RawCan)
                FlushRawCanText();
        }

        // ── CAN Monitor: row double-click → detail popup ────────────────────────

        private void LvCanDiag_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (LvCanDiag?.SelectedItem is CanDiagRowView row && row.Record != null)
            {
                var win = new CanDetailWindow(row.Record) { Owner = this };
                win.Show();
            }
        }

        private sealed class CanDiagRowView
        {
            public string Timestamp { get; init; } = string.Empty;
            public string Number { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public string Address { get; init; } = string.Empty;
            public string MemoryType { get; init; } = string.Empty;
            public string Device { get; init; } = string.Empty;
            public string Operation { get; init; } = string.Empty;
            public string Value { get; init; } = string.Empty;
            public string Error { get; init; } = string.Empty;
            public int RawSequence { get; init; }
            public ushort RawAddress { get; init; }
            public uint RawSourceTimestamp { get; init; }
            public DateTime RawDisplayTimestampUtc { get; init; }
            public DateTime RawReceivedUtc { get; init; }
            public LsmCanDiagRecord? Record { get; init; }

            public static CanDiagRowView FromRecord(LsmCanDiagRecord record)
            {
                string regName, memType, addrStr, valueStr;

                if (record.IsCanRawFrame)
                {
                    // CAN raw frame: show CAN ID as address, data bytes as value
                    regName = "CAN";
                    memType = "BUS";
                    addrStr = record.IsExtendedCanId ? $"0x{record.CanId:X8}" : $"0x{record.CanId:X3}";
                    valueStr = record.RawLength > 0
                        ? "0x" + BitConverter.ToString(record.RawPayload, 0, Math.Min(record.RawLength, record.RawPayload.Length)).Replace("-", "")
                        : "/";
                }
                else
                {
                    var resolved = LsmRegisterMap.ResolveFromDeviceId(record.Address, record.DeviceId, record.IsNichiaEepromAccess);
                    regName = resolved.Name;
                    memType = resolved.MemType;
                    addrStr = $"0x{record.Address:X4}";
                    valueStr = FormatValueHex(record);
                }

                return new CanDiagRowView
                {
                    Timestamp = record.EffectiveDisplayTimestampUtc.ToLocalTime().ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
                    Number = record.EffectiveDisplaySequence.ToString(CultureInfo.InvariantCulture),
                    Name = regName,
                    Address = addrStr,
                    MemoryType = memType,
                    Device = $"0x{record.DeviceId:X2}",
                    Operation = record.OperationName,
                    Value = valueStr,
                    Error = record.Status == LsmCanDiagStatus.Ok ? "/" : record.Status switch
                    {
                        LsmCanDiagStatus.Timeout => "timeout",
                        LsmCanDiagStatus.CrcMismatch => "CRC",
                        _ => "Error",
                    },
                    RawSequence = record.EffectiveDisplaySequence,
                    RawAddress = record.Address,
                    RawSourceTimestamp = record.SourceTimestamp,
                    RawDisplayTimestampUtc = record.EffectiveDisplayTimestampUtc,
                    RawReceivedUtc = record.ReceivedUtc,
                    Record = record,
                };
            }
            /// <summary>
            /// Format Value hex from data register bytes (skip UART header: SYNC+Resp+DLC+Addr = 4 bytes).
            /// Classic VILS shows concatenated register values as "0xDATA..." with truncation.
            /// </summary>
            private static string FormatValueHex(LsmCanDiagRecord record)
            {
                if (record.RawLength >= 5 && record.RawPayload.Length >= 5)
                {
                    // Data starts at byte 4, skip last 2 CRC-16 bytes
                    int dataStart = 4;
                    int dataEnd = record.RawLength - 2; // exclude CRC-16
                    if (dataEnd > dataStart && dataEnd <= record.RawPayload.Length)
                    {
                        var sb = new System.Text.StringBuilder("0x");
                        for (int i = dataStart; i < dataEnd; i++)
                            sb.Append(record.RawPayload[i].ToString("X2"));
                        return sb.ToString();
                    }
                }
                // Fallback: simple value field
                return $"0x{record.Value:X8}";
            }
        }

        private void HandleLvdsFrameReady(byte[] frame, LvdsFrameMeta meta)
        {
            // Guard: reject stale callbacks that arrive via Dispatcher.BeginInvoke
            // after Stop has been pressed.
            bool ethActive = (_nichiaEthCapture != null && _nichiaEthCapture.IsCapturing)
                          || (_osramEthCapture != null && _osramEthCapture.IsCapturing);
            if (!ethActive)
                return;

            // Update LVDS stats labels
            LblLvdsFrameCount.Text = $"Frames: {meta.FrameId} ({meta.ValidLines}/{meta.LinesExpected} lines)";
            LblLvdsSyncLoss.Text = $"Sync_error: {meta.SyncLosses}  CRC_error: {meta.CrcErrors}  Parity_error: {meta.ParityErrors}";

            // FPS from Ethernet capture
            double fps = _nichiaEthCapture?.FpsEma ?? _osramEthCapture?.FpsEma ?? 0;
            LblLvdsFps.Text = $"FPS: {fps:F1}";
            UpdateMainEcuState(fps);
            _communicationFaultControlWindow?.UpdateEcuState(fps);

            // Status text is now shown only in Frame Statistics, no need to duplicate here

            // During pause: keep sync matching alive (ring buffer + _matchedAForDiff)
            // so B frames still find the correct A. But don't update _latestB or render.
            if (_playback.IsPaused)
            {
                var pauseMatch = FindBestMatchA(frame, _currentWidth * _currentHeight);
                lock (_frameLock) { _matchedAForDiff = pauseMatch; }
                return;
            }

            // Find best-matching A frame BEFORE storing B, then store both
            // atomically under _frameLock so GeneratorLoopAsync always reads
            // a consistent (B, matchedA) pair.
            var matched = FindBestMatchA(frame, _currentWidth * _currentHeight);

            lock (_frameLock)
            {
                _cameraDisplaySyncEnabled = true;
                _lvdsCameraDisplayCredits = Math.Min(_lvdsCameraDisplayCredits + 1, 2);
                _lvdsFramesSinceSyncReset++;
                _lvdsComparisonReady = _lvdsFramesSinceSyncReset >= LvdsComparisonWarmupFrames;
                _matchedAForDiff = matched;
                _latestB = new Frame(_currentWidth, _currentHeight, frame, DateTime.UtcNow);
                _lastLvdsFrameUtc = DateTime.UtcNow;
                _lvdsSignalLost = false;
            }

            // Pane C is updated immediately from the camera callback. Update the
            // raw LVDS bitmap here as well so pane B cannot lag behind the frame
            // already published for comparison while RenderAll is between ticks.
            if (!_playback.IsPaused && frame.Length == _currentWidth * _currentHeight)
                BitmapUtils.Blit(_wbB, frame, _currentWidth);

            ReleasePendingCameraFrameAfterLvds();
            _baslerCapture?.UseHardwareTrigger();

            // When the main playback loop is NOT running (user didn't press Start),
            // render LVDS frame directly on pane B (standalone LVDS capture mode).
            // In standalone mode the playback render loop is idle, so no contention.
            if (_playback.Cts == null)
            {
                RenderLvdsOnly(frame);
            }
        }

        private void UpdateMainEcuState(double lvdsFps)
        {
            if (MainEcuStateLed == null || MainEcuStateText == null)
                return;

            bool running = lvdsFps > 0.0;
            MainEcuStateLed.Fill = new SolidColorBrush(running
                ? StatusLedRunningGreen
                : Color.FromRgb(198, 40, 40));
            MainEcuStateLed.Stroke = new SolidColorBrush(running
                ? StatusLedRunningGreenStroke
                : Color.FromRgb(142, 0, 0));
            MainEcuStateText.Text = running ? "Running" : "Stop/Failsafe";
        }

        private void ResetLvdsStatusForNewSession()
        {
            if (LblLvdsFrameCount != null) LblLvdsFrameCount.Text = "Frames: 0";
            if (LblLvdsSyncLoss != null) LblLvdsSyncLoss.Text = "Sync_error: 0  CRC_error: 0  Parity_error: 0";
            if (LblLvdsFps != null) LblLvdsFps.Text = "FPS: 0.0";
            UpdateMainEcuState(0.0);
        }

        private void RenderLvdsOnly(byte[] frame)
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
            if (!_lvdsComparisonReady)
            {
                Array.Clear(_diffBgr);
                BitmapUtils.Blit(_wbD, _diffBgr, w * 3);
                if (LblDiffMode != null)
                    LblDiffMode.Text = "Comparison warming up...";
                ApplyNoSignalUiState(noSignal: false);
                return;
            }

            DiffRenderer.RenderCompareToBgr(_diffBgr, aData, frame.Length == w * h ? frame : _noSignalGrayFrame,
                w, h, _diffThreshold, _zeroZeroIsWhite,
                out var minDiff, out var maxDiff, out _,
                out _, out var meanAbsDiff, out var aboveDeadband,
                out var totalDarkPixels);
            BitmapUtils.Blit(_wbD, _diffBgr, w * 3);

            if (LblDiffMode != null)
                FormatComparisonStats(maxDiff, minDiff, meanAbsDiff, aboveDeadband, totalDarkPixels);

            ApplyNoSignalUiState(noSignal: false);
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
            RenderNoSignalFrames();
            ApplyNoSignalUiState(noSignal: true);
        }

        // ── End LVDS ────────────────────────────────────────────────────────

        private void CmbLiveNic_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_settingsManager.IsLoading) return;
            _avtpLiveDeviceHint = LiveNicSelector.GetSelectedDeviceName(CmbLiveNic);
            SaveUiSettings();
            UpdateCommunicationFaultAvailability();

            // Re-sync selected device type to SmartVisio Box when NIC selection changes.
            _ = TrySyncDeviceModeToAurixAsync("nic-change");
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

        private static readonly string[] ComparisonModeLabels = ["LVDS-AVTP", "LSM-LVDS", "LSM-AVTP"];

        private void CmbComparisonMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_settingsManager.IsLoading) return;
            _comparisonMode = CmbComparisonMode?.SelectedIndex ?? 0;
            UpdateComparisonModeLabel();
            SaveUiSettings();
            RenderAll();
        }

        private void UpdateComparisonModeLabel()
        {
            if (RunCompModeLabel != null)
                RunCompModeLabel.Text = ComparisonModeLabels[Math.Clamp(_comparisonMode, 0, ComparisonModeLabels.Length - 1)];
        }

        private void TxtMac_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_settingsManager.IsLoading) return;

            if (TxtSrcMac != null)
                _srcMac = TxtSrcMac.Text?.Trim() ?? "3C:CE:15:00:00:19";
            if (TxtDstMac != null)
                _dstMac = TxtDstMac.Text?.Trim() ?? "01:00:5E:16:00:12";

            SaveUiSettings();
            SendAvtpSourceFilterCommand();
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
            _flickerInjection.Dispose();
            _deviceModeSyncTimer.Stop();
            StopAutomationApi();
            SaveUiSettings();
            if (_recordingManager.IsRecording) StopRecording();
            StopAll();
            // Send STOP to SmartVisio Box so g_diagSniffEnabled is cleared for the next C# session
            try
            {
                string? txDev = GetTxPcapDeviceNameOrNull();
                if (!string.IsNullOrWhiteSpace(txDev))
                    DiagSniffCommand.Send(txDev, start: false, null);
            }
            catch { /* ignore */ }
            try { StopDiagRetryTimer(); } catch { /* ignore */ }
            try { _canDiagWatchdogTimer?.Stop(); } catch { /* ignore */ }
            try { _flickerControlWindow?.Close(); } catch { /* ignore */ }
            try { _lvdsSimulationControlWindow?.Close(); } catch { /* ignore */ }
            try { _ethConfigWindow?.Close(); } catch { /* ignore */ }
            try { _cameraConfigWindow?.Close(); } catch { /* ignore */ }
            try { _apiConfigWindow?.Close(); } catch { /* ignore */ }
            try { _osramDefectControlWindow?.Close(); } catch { /* ignore */ }
            try { _nichiaDefectControlWindow?.Close(); } catch { /* ignore */ }
            try { _communicationFaultControlWindow?.Close(); } catch { /* ignore */ }
            try { StopCanDiagCapture(); } catch { /* ignore */ }
            try { StopNichiaEthCapture(); } catch { /* ignore */ }
            try { StopOsramEthCapture(); } catch { /* ignore */ }
            try { StopBaslerCapture(); } catch { /* ignore */ }
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtFps.Text, out var fps) || fps <= 0) fps = 100;

            if (!_playback.IsRunning)
            {
                Start(fps);
                if (BtnStart != null) BtnStart.Content = "⏸ Pause";
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

        private void StartBlackKeepalive()
        {
            if (_modeOfOperation != ModeOfOperation.PlayerFromFiles
                || _communicationFaultState.AvtpFaultEnabled)
                return;

            int fps = int.TryParse(TxtFps?.Text, out int parsedFps) && parsedFps > 0
                ? parsedFps
                : 100;

            if (!_txManager.IsReady)
            {
                ushort ethType = ParseHexUshort(_avtpEtherType, 0x22F0);
                byte stIdByte = ParseHexByte(_streamIdLastByte, 0x50);
                string? deviceHint = LiveNicSelector.GetSelectedDeviceName(CmbLiveNic) ?? _avtpLiveDeviceHint;
                _txManager.Initialize(deviceHint, _srcMac, _dstMac,
                    _vlanId, _vlanPriority, ethType, stIdByte);
            }

            _txManager.StartBlackLoop(fps);
        }

        /// <summary>
        /// Helper: Stop playback and send BLACK AVTP frames to LSM.
        /// Used when: Stop button pressed, AVI playback ends, or PCAP playback completes without loop.
        /// Ensures LSM is cleared (off) rather than stuck on last frame.
        /// </summary>
        private void SendBlackAndStop(string reason = "")
        {
            if (_modeOfOperation != ModeOfOperation.PlayerFromFiles)
            {
                // AVTP Live: normal stop without black loop
                StopAll();
                return;
            }

            _ = int.TryParse(TxtFps.Text, out int fps);

            // Stop UI/loops
            StopAll();

            // StopAll() closes the shared pcap device handle (singleton) used
            // by captures AND the TX transmitter.  We MUST reinitialize the TX
            // to reopen a fresh handle before starting the black loop.
            {
                ushort ethType = ParseHexUshort(_avtpEtherType, 0x22F0);
                byte stIdByte = ParseHexByte(_streamIdLastByte, 0x50);
                string? deviceHint = LiveNicSelector.GetSelectedDeviceName(CmbLiveNic) ?? _avtpLiveDeviceHint;
                _txManager.Initialize(deviceHint, _srcMac, _dstMac,
                    _vlanId, _vlanPriority, ethType, stIdByte);
            }

            // Start BLACK TX loop — strict 0x00 for LSM keepalive.
            _txManager.StartBlackLoop(fps);

            string msg = string.IsNullOrWhiteSpace(reason)
                ? "Player STOP: sending BLACK AVTP (Signal not available)."
                : $"Player {reason}: sending BLACK AVTP (Signal not available).";
            LblStatus.Text = msg;
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            SendBlackAndStop("STOP");
            ResetLvdsStatusForNewSession();
        }

        private void ChkLoopPlaying_Changed(object sender, RoutedEventArgs e)
        {
            _loopPlayingEnabled = ChkLoopPlaying?.IsChecked == true;
            _aviPlayer.LoopEnabled = _loopPlayingEnabled;
            if (LoopPlaybackIcon != null)
                LoopPlaybackIcon.Foreground = _loopPlayingEnabled ? Brushes.ForestGreen : Brushes.Gray;
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
                LblStatus, ShowSaveFeedback, HideSaveFeedback);
        }

        private void BtnOpenSnapshots_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Open the parent outputs folder (contains frameSnapshots, videoRecords, etc.)
                string snapshotDir = RecordingManager.GetFrameSnapshotsOutputDirectory();
                string outputsDir = System.IO.Path.GetDirectoryName(snapshotDir) ?? snapshotDir;
                Directory.CreateDirectory(outputsDir);
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{outputsDir}\"",
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

            // AVI fps must match the actual recording rate. RenderAll is called by
            // UiRefreshLoop at ~60fps (16ms period), NOT at the AVTP input rate (~100fps).
            // Using _playback.TargetFps would create a mismatch causing VLC playback to be too fast.
            // The recorder's PatchAviFps will further refine the header to the exact measured rate.
            const int RecordFps = 50;
            int fps = RecordFps;

            // Pass pane C dimensions if Basler camera is active
            int cW = 0, cH = 0;
            var latC = _latestC;
            if (latC != null)
            {
                cW = latC.Width;
                cH = latC.Height;
            }
            var (success, error, statusMessage) = _recordingManager.StartRecording(fps, _diffThreshold, cW, cH);

            if (success)
            {
                if (BtnRecord != null)
                {
                    BtnRecord.Content = "⏹ Stop Rec";
                    BtnRecord.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0x33, 0x33));
                    BtnRecord.Foreground = System.Windows.Media.Brushes.White;
                }
                // Disable other buttons while recording
                if (BtnStart != null) BtnStart.IsEnabled = false;
                if (BtnPrev != null) BtnPrev.IsEnabled = false;
                if (BtnNext != null) BtnNext.IsEnabled = false;
                if (BtnStop != null) BtnStop.IsEnabled = false;
                if (BtnSave != null) BtnSave.IsEnabled = false;
                if (BtnLoadFiles != null) BtnLoadFiles.IsEnabled = false;
                LblStatus.Text = statusMessage ?? "Recording started.";
            }
            else
            {
                if (BtnRecord != null) BtnRecord.Content = "⏺ Record";
                MessageBox.Show($"Failed to start recording: {error}", "Record error");
            }
        }

        private void StopRecording()
        {
            try { _recordingFeedbackCts?.Cancel(); } catch { }
            try { _recordingFeedbackCts?.Dispose(); } catch { }
            _recordingFeedbackCts = new CancellationTokenSource();
            CancellationToken feedbackToken = _recordingFeedbackCts.Token;
            ShowSaveFeedback("Saving recorded video...", Brushes.DimGray);

            LblStatus.Text = _recordingManager.StopRecording();
            if (BtnRecord != null)
            {
                BtnRecord.Content = "⏺ Record";
                BtnRecord.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xDD, 0xDD));
                BtnRecord.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x00, 0x00));
            }
            // Re-enable Pause and Stop buttons after recording stops
            if (BtnStart != null) BtnStart.IsEnabled = true;
            if (BtnStop != null) BtnStop.IsEnabled = true;
            if (BtnSave != null) BtnSave.IsEnabled = true;

            _ = ShowRecordedVideoSavedFeedbackAsync(feedbackToken);
        }

        private async Task ShowRecordedVideoSavedFeedbackAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(1200, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    return;

                ShowSaveFeedback("Recorded video saved!", Brushes.ForestGreen);

                await Task.Delay(2500, cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                    HideSaveFeedback();
            }
            catch (OperationCanceledException)
            {
                // A newer recording or application shutdown replaced this feedback operation.
            }
        }

        private void Pause()
        {
            _playback.Pause();
            if (_modeOfOperation == ModeOfOperation.PlayerFromFiles)
            {
                // Freeze the UI loop, but keep the generator and AVTP TX alive.
                _playback.PauseGate.Set();
            }

            // Freeze the currently displayed frames so overlays match the frozen bitmap.
            lock (_frameLock)
            {
                _pausedA = _latestA ?? new Frame(_currentWidth, _currentHeight, GetASourceBytes(), DateTime.UtcNow);

                bool ethActive = HasRecentLvdsFrame();

                if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor && ethActive && _latestB != null)
                {
                    // Real ETH LVDS: freeze the actual B from Ethernet and use
                    // the sync-matched A for the diff so D reflects real ECU differences.
                    // Also display the matched A in pane A so all three panes are
                    // temporally consistent (A leads B by ECU round-trip latency;
                    // without this, the bar in A would be shifted right vs B).
                    _pausedB = _latestB;
                    _pausedMatchedA = _matchedAForDiff ?? _pausedA;
                    _pausedA = _pausedMatchedA;
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
                    // PlayerFromFiles: prefer sync-matched A when real ETH B is active
                    _pausedB = _latestB ?? _pausedA;
                    if (ethActive && _latestB != null && _matchedAForDiff != null)
                    {
                        _pausedMatchedA = _matchedAForDiff;
                        _pausedA = _pausedMatchedA;
                    }
                    else
                    {
                        _pausedMatchedA = _pausedA;
                    }
                    _pausedD = _latestD ?? AbsDiff(_pausedMatchedA, _pausedB);
                }
            }

            // Re-render once from the frozen snapshot to avoid any race with in-flight frame updates.
            RenderAll();

            if (BtnStart != null) BtnStart.Content = "▶ Start";
            if (LblRunInfoA != null) LblRunInfoA.Text = "Paused";
            if (LblRunInfoB != null) LblRunInfoB.Text = "Paused";
            if (LblRunInfoC != null) LblRunInfoC.Text = "Paused";
            LblStatus.Text = "Paused.";

            ApplyButtonStates(isRunning: true, isPaused: true);
            UpdateOverlaysAll();
        }

        private void Resume()
        {
            _playback.Resume();
            _playback.PauseGate.Set();

            // The ECU may have entered failsafe while Pause stopped the UI
            // watchdog. Do not reuse the pre-pause LVDS image after Resume;
            // require a fresh LVDS frame to prove that the ECU is transmitting.
            lock (_frameLock)
            {
                _latestB = null;
                _matchedAForDiff = null;
            }
            _lastLvdsFrameUtc = DateTime.UtcNow;
            _lvdsSignalLost = true;

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

            if (BtnStart != null) BtnStart.Content = "⏸ Pause";

            ApplyButtonStates(isRunning: true, isPaused: false);

            double shownFps = GetShownFps(avtpInFps: _playback.AvtpInFpsEma);
            bool isAviZero = _lastLoaded == LoadedSource.Avi && shownFps <= 0.0;
            if (LblRunInfoA != null)
                LblRunInfoA.Text = StatusFormatter.FormatRunInfoA(true, false, shownFps, isAviZero);
            if (LblRunInfoB != null)
                LblRunInfoB.Text = StatusFormatter.FormatRunInfoB(true, false, _lvdsSignalLost, _playback.BFpsEma);

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
            ApplyButtonStates(_playback.IsRunning, _playback.IsPaused);
            LblStatus.Text = SourceLoaderHelper.GetPcapStatusMessage();

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

            var result = _sourceLoader.LoadImage(path);
            _pgmFrame = result.Frame;
            _lvdsFrame84 = result.LvdsFrame;

            _lastLoaded = LoadedSource.Image;
            ApplyButtonStates(_playback.IsRunning, _playback.IsPaused);
            LblStatus.Text = result.StatusMessage;

            if (_playback.Cts == null || _playback.IsPaused) RenderOneFrameNow();
        }

        private void LoadAvi(string path)
        {
            PrepareForNewSource(clearAvtpFrame: true);
            _aviPlayer.LoopEnabled = _loopPlayingEnabled;
            _aviPlayer.Load(path);
            _lastLoaded = LoadedSource.Avi;
            ApplyButtonStates(_playback.IsRunning, _playback.IsPaused);
            LblStatus.Text = _aviPlayer.BuildStatusMessage();

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
            ResetCameraDisplaySync();

            // Force immediate first refresh for pane A/C run-info labels.
            _runInfoALastUpdateTicks = 0;
            _runInfoCLastUpdateTicks = 0;

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
            // Send device-mode command to ECU BEFORE any captures start.
            // DeviceModeCommand opens/closes the global pcap device handle,
            // so it must run while no captures are active.
            // -------------------------------------------------
            try
            {
                string? txDev = GetTxPcapDeviceNameOrNull();
                if (!string.IsNullOrWhiteSpace(txDev) &&
                    !_communicationFaultState.LvdsFaultEnabled &&
                    !_communicationFaultState.CanUartFaultEnabled)
                    DeviceModeCommand.SendDeviceMode(txDev, _currentDeviceType, AppendDiagLog);
            }
            catch (Exception ex) { AppendDiagLog($"[cmd] Start device-mode: {ex.Message}"); }

            // -------------------------------------------------
            // TX init (ONLY in Generator/Player mode)
            // -------------------------------------------------
            if (_modeOfOperation == ModeOfOperation.PlayerFromFiles
                && !_communicationFaultState.AvtpFaultEnabled)
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

                LblStatus.Text = _communicationFaultState.AvtpFaultEnabled
                    ? "AVTP Fault Injection: Signal not available."
                    : StatusFormatter.FormatPlayerRunning(fps, avtpEnabled: true);
                _playback.RunningStatusText = LblStatus.Text;
            }
            else
            {
                // AVTP Live Monitor:
                _ = Task.Run(() => UiRefreshLoop(ct));

                // Until the first frame arrives, show explicit waiting message.
                LblStatus.Text = StatusFormatter.FormatWaitingForSignal();

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
                string? deviceHint = LiveNicSelector.GetSelectedDeviceName(CmbLiveNic) ?? _avtpLiveDeviceHint;

                // Persist the final hint so next Start uses the same interface.
                _avtpLiveDeviceHint = deviceHint;

                _liveCapture.StartEthernetCapture(deviceHint);
            }

            // Pane B real LVDS over Ethernet (SmartVisio Box GETH), managed by Start/Stop.
            // Keep this receiver active during an AVTP fault so the UI can observe
            // the ECU's delayed LVDS shutdown naturally.
            if (_currentDeviceType == LsmDeviceType.Nichia)
            {
                StartNichiaEthCapture();
            }
            else
            {
                StartOsramEthCapture();
            }

            // Arm LVDS timeout for every session. Pane B must remain unavailable
            // until a real LVDS frame arrives; the generator must not fabricate
            // B from A while the ECU is already in failsafe before Start.
            _lastLvdsFrameUtc = DateTime.UtcNow;
            _lvdsSignalLost = true;
            ResetLvdsStatusForNewSession();

            // Pane C: Basler USB3 camera live capture. Opening the pylon device
            // takes seconds, so it runs off the dispatcher; blocking the UI thread
            // here also delays the pane B LVDS frames and the pane D comparison.
            // At session start there is no confirmed LVDS frame yet, so the camera
            // starts in free-run: a failsafe ECU would never deliver the hardware
            // trigger pane C waits for.
            StartBaslerCapture(useFreeRunFallback: true);
            _lastBaslerFrameUtc = DateTime.UtcNow;
            // Pane C must stay on "Signal not available" until this session's first
            // frame: opening the camera takes a moment and the previous session's
            // picture must never be mistaken for live content.
            _baslerSignalLost = true;
            ClearPaneCImage();

            if (_communicationFaultState.AvtpFaultEnabled)
            {
                lock (_frameLock)
                {
                    _latestC = null;
                }
                if (NoSignalC != null) NoSignalC.Visibility = Visibility.Visible;
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
                            // No loop: stop with BLACK
                            SendBlackAndStop("PCAP-END");
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
            _flickerInjection.Cancel();
            // Exit fullscreen if active
            ExitFullscreen();

            if (_recordingManager.IsRecording) StopRecording();
            _playback.Resume();
            _playback.Stop();
            if (BtnStart != null) BtnStart.Content = "▶ Start";

            lock (_frameLock)
            {
                _pausedA = null;
                _pausedB = null;
                _pausedD = null;
                _latestC = null;
            }
            _lastLvdsFrameUtc = DateTime.MinValue;
            _lvdsSignalLost = false;
            _lastBaslerFrameUtc = DateTime.MinValue;
            _baslerSignalLost = false;
            _baslerDispWindowFrames = 0;
            _baslerDispFps = 0;
            _baslerDispWindowStartTicks = _baslerDispFpsSw.ElapsedTicks;
            _runInfoALastUpdateTicks = 0;
            _runInfoCLastUpdateTicks = 0;
            _canDiagWatchdogRecovering = false;
            _canDiagSessionHadTraffic = false;
            _canDiagConsecutiveRestarts = 0;
            _canDiagLastRecordUtc = DateTime.MinValue;
            StopDiagRetryTimer();
            ResetSyncState();
            ResetCameraDisplaySync();

            StopRenderLoops();


            // Stop all live capture sources (CAN diag capture is independent)
            _liveCapture.StopAll();
            StopNichiaEthCapture();
            StopOsramEthCapture();
            // The camera stays open so the next Start renders pane C immediately;
            // HandleBaslerFrameReady drops frames while playback is stopped and
            // Window_Closing releases the device.

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
            ClearOverlay(Pane.C);
            ClearOverlay(Pane.D);

            // Pane C: show "Signal not available", clear label, reset zoom
            if (NoSignalC != null) NoSignalC.Visibility = Visibility.Visible;
            if (LblRunInfoC != null) LblRunInfoC.Text = "";
            ClearPaneCImage();
            _zoomPan.Reset((int)Pane.C);

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

            _scenePlayer.Load(scenePath);
            _lastLoaded = LoadedSource.Scene;
            ApplyButtonStates(_playback.IsRunning, _playback.IsPaused);

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
            int lastPcapGen = _liveCapture.FrameGeneration; // track PCAP frame changes

            // AVTP TX rate limiter: cap at 100fps (10ms) regardless of generator loop fps.
            // This matches Avtp_new.can's proven 10ms cadence that the ECU expects.
            const double AvtpTxPeriodMs = 10.0;
            var lastAvtpTxTime = sw.Elapsed - TimeSpan.FromMilliseconds(AvtpTxPeriodMs); // allow first send immediately
        
            while (!ct.IsCancellationRequested)
            {
                next += period;
        
                // A: either AVTP latest or PGM/AVI/Scene fallback (depending on what you loaded)
                var aBytes = GetASourceBytes();
                var a = new Frame(_currentWidth, _currentHeight, aBytes, DateTime.UtcNow);
                _playback.IncrementCountA();

                // For PCAP sources: only push to sync ring when the PCAP replay has
                // produced a genuinely NEW frame. Duplicate pushes (same frame data at
                // 100fps while PCAP produces ~63fps) pollute the ring and degrade the
                // NCC-based A↔B matching precision during animation.
                bool isPcap = (_lastLoaded == LoadedSource.Pcap);
                bool isNewPcapFrame;
                if (isPcap)
                {
                    int gen = _liveCapture.FrameGeneration;
                    isNewPcapFrame = (gen != lastPcapGen);
                    if (isNewPcapFrame) lastPcapGen = gen;
                }
                else
                {
                    isNewPcapFrame = true; // non-PCAP: always treat as new
                }

                // In AVTP live mode, HandleLiveFrameReady is the sole owner of
                // the sync ring. Pushing the generator's polling copy as well
                // creates duplicate/stale candidates while AVTP runs at 100 fps.
                if (isNewPcapFrame && _modeOfOperation != ModeOfOperation.AvtpLiveMonitor)
                    PushSyncFrame(a);
        
                // B: prefer real Ethernet LVDS when available. Do not fabricate
                // B from A while the LVDS signal is unavailable.
                Frame b;
                bool useRealEthB = false;
                Frame? genMatchedA = null;
                bool hasEthLvds = HasRecentLvdsFrame();
                if (hasEthLvds)
                {
                    lock (_frameLock)
                    {
                        if (_latestB != null
                            && _latestB.Width == _currentWidth
                            && _latestB.Height == _currentHeight)
                        {
                            b = _latestB;
                            genMatchedA = _matchedAForDiff;
                            useRealEthB = true;
                        }
                        else
                        {
                            b = _lvdsSignalLost || _communicationFaultState.AvtpFaultEnabled
                                ? new Frame(_currentWidth, _currentHeight, _noSignalGrayFrame, DateTime.UtcNow)
                                : new Frame(_currentWidth, _currentHeight,
                                    ApplyValueDelta(a.Data, _bValueDelta), DateTime.UtcNow);
                        }
                    }
                }
                else if (_lvdsSignalLost || _communicationFaultState.AvtpFaultEnabled)
                {
                    b = new Frame(_currentWidth, _currentHeight, _noSignalGrayFrame, DateTime.UtcNow);
                }
                else
                {
                    var bBytes = ApplyValueDelta(a.Data, _bValueDelta);
                    b = new Frame(_currentWidth, _currentHeight, bBytes, DateTime.UtcNow);
                }

                if (!useRealEthB)
                    _playback.IncrementCountB();
        
                // D: diff — use frame-matched A when real ETH B is active
                Frame diffA = (useRealEthB && genMatchedA != null) ? genMatchedA : a;
                var d = AbsDiff(diffA, b);
        
                // -----------------------------
                // AVTP Ethernet TX (ONLY PlayerFromFiles)
                // Rate-limited to 100fps (10ms) to match ECU expectations
                // (identical cadence to Avtp_new.can)
                // -----------------------------
                if (_modeOfOperation == ModeOfOperation.PlayerFromFiles)
                {
                    var elapsed = sw.Elapsed;
                    bool txAllowed = (elapsed - lastAvtpTxTime).TotalMilliseconds >= AvtpTxPeriodMs;
                    try
                    {
                        if (txAllowed)
                        {
                            if (isPcap)
                            {
                                if (isNewPcapFrame || _playback.IsPaused)
                                {
                                    var txFrame = _liveCapture.AvtpTxFrame;
                                    if (txFrame != null)
                                    {
                                        await _txManager.SendFrameAsync(txFrame, ct);
                                        lastAvtpTxTime = elapsed;
                                    }
                                }
                            }
                            else
                            {
                                await _txManager.SendFrameAsync(a.Data, ct);
                                lastAvtpTxTime = elapsed;
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                }

                // Detect AVI end-of-file when loop is disabled → auto-stop with BLACK
                if (_lastLoaded == LoadedSource.Avi && _aviPlayer.IsAtEnd)
                {
                    _ = Dispatcher.BeginInvoke(() => SendBlackAndStop("END-OF-FILE"));
                    break;
                }
        
                // If pause was activated exactly during the iteration, do not publish frame.
                // Keep the pacing delay below so AVTP TX remains rate-limited while paused.
                if (!_playback.IsPaused && _playback.PauseGate.IsSet)
                {
                    lock (_frameLock)
                    {
                        _latestA = a;
                        if (!useRealEthB)
                            _latestB = b;
                        _latestD = d;
                    }
                    _playback.IncrementCountD();
                }
        
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

            // Keep camera acquisition aligned with the current LVDS state even
            // when a mode transition skips the LVDS timeout edge.
            SynchronizeBaslerTriggerWithLvds();

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

            // LVDS Ethernet: if ECU powers off or enters failsafe, LVDS frames
            // stop arriving. Detect timeout in every running mode, including
            // AVTP Generator, and reset pane B + D to "Signal not available".
            if (_playback.IsRunning
                && !_playback.IsPaused
                && !_lvdsSignalLost
                && _lastLvdsFrameUtc != DateTime.MinValue
                && (DateTime.UtcNow - _lastLvdsFrameUtc) > TimeSpan.FromSeconds(LiveSignalLostTimeoutSec))
            {
                _lvdsSignalLost = true;
                _baslerCapture?.UseFreeRunFallback();
                lock (_frameLock)
                {
                    _latestB = null;
                    _matchedAForDiff = null;
                }
                ResetCameraDisplaySync();
                _cameraDisplaySyncEnabled = false;
                if (LblLvdsFps != null) LblLvdsFps.Text = "FPS: 0.0";
                UpdateMainEcuState(0.0);
                _communicationFaultControlWindow?.UpdateEcuState(0.0);
            }

            // Basler camera: if ECU powers off, no LVDS → no trigger → no camera frames.
            // Detect timeout and show "Signal not available" on pane C.
            if (_playback.IsRunning
                && !_playback.IsPaused
                && !_baslerSignalLost
                && _lastBaslerFrameUtc != DateTime.MinValue
                && _baslerCapture != null && _baslerCapture.IsCapturing
                && !_baslerCapture.IsFreeRunFallback
                && (DateTime.UtcNow - _lastBaslerFrameUtc) > TimeSpan.FromSeconds(LiveSignalLostTimeoutSec))
            {
                _baslerSignalLost = true;
                lock (_frameLock)
                {
                    _latestC = null;
                }
                _baslerDispWindowFrames = 0;
                _baslerDispFps = 0;
                _baslerDispWindowStartTicks = _baslerDispFpsSw.ElapsedTicks;
            }

            // In AVTP Live mode, while waiting for the first frame, keep showing "Signal not available".
            if (ShouldShowNoSignalWhileRunning())
            {
                RenderNoSignalFrames();
                ApplyNoSignalUiState(noSignal: true);

                // AVTP may still be waiting for its first gateway frame while
                // the camera is already producing valid black failsafe frames.
                // Keep pane C independent from the global waiting overlay.
                if (_baslerCapture?.IsFreeRunFallback == true && NoSignalC != null)
                    NoSignalC.Visibility = Visibility.Collapsed;

                UpdateBaslerRunInfoLabel();

                if (!_playback.WasWaitingForSignal && LblStatus != null)
                {
                    LblStatus.Text = StatusFormatter.FormatWaitingForSignal();
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
            // Snapshot B and its matched A atomically. HandleLvdsFrameReady updates
            // both values together; reading matched A after releasing this lock can
            // pair the previous B with the next LVDS match during animation.
            Frame? matchedA;
            if (_playback.IsPaused)
            {
                matchedA = _pausedMatchedA;
            }
            else
            {
                lock (_frameLock)
                {
                    matchedA = _matchedAForDiff;
                    b = _latestB ?? b;
                }
            }
            bool hasRealEthB = HasRecentLvdsFrame();
            bool liveLvdsNoSignal = _modeOfOperation == ModeOfOperation.AvtpLiveMonitor
                && !_playback.IsPaused
                && !hasRealEthB;

            if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor && !_playback.IsPaused
                && !_lvdsSignalLost && !liveLvdsNoSignal)
            {
                // B already comes from HandleLvdsFrameReady() via _latestB.
                // Keep current b from the real LVDS capture.
            }

            // B post-processing (forced dead pixel + optional compensation)
            // Use the sync-matched A when available so dark-pixel detection
            // aligns with the same A reference used for the diff comparison.
            Frame aForPostProcess = (hasRealEthB && matchedA != null) ? matchedA : a;
            b = ApplyBPostProcessing(aForPostProcess, b);

            // For diff rendering, use the matched A frame (sync-corrected) when ETH B is active,
            // so the comparison reflects actual ECU processing differences, not animation timing.
            byte[] diffARef = (hasRealEthB && matchedA != null) ? matchedA.Data : a.Data;

            if (!_communicationFaultState.AvtpFaultEnabled)
                BitmapUtils.Blit(_wbA, a.Data, a.Stride);
            BitmapUtils.Blit(_wbB, b.Data, b.Stride);

            // Select comparison operands based on _comparisonMode:
            // 0 = LVDS-AVTP (A ref vs B measured), 1 = LSM-LVDS (B ref vs C↓ measured), 2 = LSM-AVTP (A ref vs C↓ measured)
            byte[] diffLeft;
            byte[] diffRight;
            int cmpMode = _comparisonMode;
            bool comparisonReady = cmpMode != 0 || !hasRealEthB || _lvdsComparisonReady;

            if (cmpMode == 1 || cmpMode == 2)
            {
                // Modes involving camera: use pre-computed downscaled buffer from HandleBaslerFrameReady
                byte[] cameraData = (_downscaledCameraFrame != null
                    && _downscaledCameraFrame.Length == _currentWidth * _currentHeight
                    && !_baslerSignalLost)
                    ? _downscaledCameraFrame
                    : _noSignalGrayFrame;

                if (cmpMode == 1)
                {
                    // LSM-LVDS: LVDS is reference (A), downscaled camera is measured (B)
                    diffLeft = b.Data;
                    diffRight = cameraData;
                }
                else
                {
                    // AVTP-LSM: AVTP is reference (A), downscaled camera is measured (B)
                    // This mode is independent of LVDS timing. Do not use the
                    // LVDS sync-matched A, which can be stale after an LVDS fault.
                    diffLeft = a.Data;
                    diffRight = cameraData;
                }
            }
            else
            {
                // Default: LVDS-AVTP (A ref vs B measured)
                diffLeft = diffARef;
                diffRight = b.Data;
            }

            // For camera comparison modes, relax the zero-detection threshold (optical noise prevents exact 0)
            byte zeroThr = (byte)(cmpMode > 0 ? 5 : 0);

            int minDiff;
            int maxDiff;
            double meanAbsDiff;
            int aboveDeadband;
            int totalDarkPixels;
            if (comparisonReady)
            {
                DiffRenderer.RenderCompareToBgr(_diffBgr, diffLeft, diffRight, _currentWidth, _currentHeight, _diffThreshold,
                    _zeroZeroIsWhite,
                    out minDiff, out maxDiff, out _,
                    out _, out meanAbsDiff, out aboveDeadband,
                    out totalDarkPixels, zeroThr);
            }
            else
            {
                Array.Clear(_diffBgr);
                minDiff = 0;
                maxDiff = 0;
                meanAbsDiff = 0.0;
                aboveDeadband = 0;
                totalDarkPixels = 0;
            }
            BitmapUtils.Blit(_wbD, _diffBgr, _currentWidth * 3);

            // Record what we render (A/B in Gray8; D in Bgr24). Diff buffer is reused, so copy it.
            if (_recordingManager.IsRecording && !_playback.IsPaused && _playback.Cts != null)
            {
                var dCopy = new byte[_diffBgr.Length];
                Buffer.BlockCopy(_diffBgr, 0, dCopy, 0, dCopy.Length);
                _recordingManager.TryEnqueueFrame(a.Data, b.Data, dCopy);
            }

            if (LblDiffMode != null)
            {
                CaptureFlickerCandidateFrames(a, b);
                if (comparisonReady)
                    FormatComparisonStats(maxDiff, minDiff, meanAbsDiff, aboveDeadband, totalDarkPixels);
                else
                    LblDiffMode.Text = "Comparison warming up...";
            }

            // Per-pane no-signal visibility.
            // When AVTP is lost: show "Signal not available" on A, B, D (comparison meaningless without reference).
            // When LVDS is lost: show "Signal not available" on B and on D only
            // for comparison modes that use LVDS as an operand. LSM-AVTP compares
            // AVTP A directly against camera C, so D remains valid without B.
            if (_communicationFaultState.AvtpFaultEnabled)
            {
                if (NoSignalA != null) NoSignalA.Visibility = Visibility.Visible;
                if (NoSignalB != null)
                    NoSignalB.Visibility = (_lvdsSignalLost || liveLvdsNoSignal) ? Visibility.Visible : Visibility.Collapsed;
                if (NoSignalD != null)
                    NoSignalD.Visibility = _lvdsSignalLost || liveLvdsNoSignal || _comparisonMode != 1
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            else if (_modeOfOperation == ModeOfOperation.AvtpLiveMonitor && !_liveCapture.HasAvtpFrame)
            {
                if (NoSignalA != null) NoSignalA.Visibility = Visibility.Visible;
                if (NoSignalB != null) NoSignalB.Visibility = Visibility.Visible;
                if (NoSignalD != null) NoSignalD.Visibility = Visibility.Visible;
            }
            else if (_lvdsSignalLost || liveLvdsNoSignal)
            {
                if (NoSignalA != null) NoSignalA.Visibility = Visibility.Collapsed;
                if (NoSignalB != null) NoSignalB.Visibility = Visibility.Visible;
                if (NoSignalD != null) NoSignalD.Visibility = _comparisonMode == 2
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            else
            {
                ApplyNoSignalUiState(noSignal: false);
            }

            // Pane C (Basler camera): independent signal-lost overlay
            if (_baslerSignalLost)
            {
                if (NoSignalC != null) NoSignalC.Visibility = Visibility.Visible;
            }

            if (_comparisonMode > 0 && _baslerSignalLost && NoSignalD != null)
                NoSignalD.Visibility = Visibility.Visible;
            else if (!_baslerSignalLost && _baslerCapture != null && _baslerCapture.IsCapturing)
            {
                if (NoSignalC != null) NoSignalC.Visibility = Visibility.Collapsed;
            }

            UpdateFpsLabels();
        }

        private void SynchronizeBaslerTriggerWithLvds()
        {
            if (_baslerCapture == null || !_baslerCapture.IsCapturing)
                return;

            if (HasRecentLvdsFrame())
                _baslerCapture.UseHardwareTrigger();
            else
                _baslerCapture.UseFreeRunFallback();
        }

        private void UpdateFpsLabels()
        {
            _playback.TryUpdateFpsEstimates(out _, out _, out _);

            bool allowUiRefreshA = true;
            bool allowUiRefreshC = true;
            if (_playback.Cts != null && !_playback.IsPaused)
            {
                long nowTicks = _runInfoUiSw.ElapsedTicks;
                double dtA = (nowTicks - _runInfoALastUpdateTicks) / (double)Stopwatch.Frequency;
                double dtC = (nowTicks - _runInfoCLastUpdateTicks) / (double)Stopwatch.Frequency;
                allowUiRefreshA = dtA >= RunInfoUiUpdatePeriodSec;
                allowUiRefreshC = dtC >= RunInfoUiUpdatePeriodSec;
                if (allowUiRefreshA) _runInfoALastUpdateTicks = nowTicks;
                if (allowUiRefreshC) _runInfoCLastUpdateTicks = nowTicks;
            }

            bool noSignal = ShouldShowNoSignalWhileRunning();
            bool avtpNoSignal = noSignal || _communicationFaultState.AvtpFaultEnabled;
            // RenderAll shows A/B as unavailable when AVTP Live has no frame,
            // even when LVDS Ethernet already delivered frames. Keep the run
            // info labels driven by that same source-of-truth state.
            bool avtpLiveNoFrame = _modeOfOperation == ModeOfOperation.AvtpLiveMonitor
                && !_liveCapture.HasAvtpFrame;
            bool paneANoSignal = avtpNoSignal || avtpLiveNoFrame;
            // AVTP and LVDS are independent inputs. An AVTP fault must hide A,
            // but B can still be valid when the ECU continues transmitting real
            // LVDS frames (including valid black frames).
            bool paneBNoSignal = avtpLiveNoFrame || _lvdsSignalLost;
            bool isRunning = _playback.Cts != null;
            bool isPaused = _playback.IsPaused;


            if (LblRunInfoA != null)
            {
                if (allowUiRefreshA || paneANoSignal || !isRunning || isPaused)
                {
                    if (paneANoSignal)
                    {
                        LblRunInfoA.Text = "";
                    }
                    else
                    {
                        double shownFps = GetShownFps(_playback.AvtpInFpsEma);
                        bool isAviZero = _lastLoaded == LoadedSource.Avi && shownFps <= 0.0;
                        LblRunInfoA.Text = StatusFormatter.FormatRunInfoA(isRunning, isPaused, shownFps, isAviZero);
                    }
                }
            }

            if (LblRunInfoB != null)
            {
                double paneBFps = 0.0;
                if (!paneBNoSignal)
                {
                    if (_nichiaEthCapture != null && _nichiaEthCapture.IsCapturing && _nichiaEthCapture.FpsEma > 0.0)
                        paneBFps = _nichiaEthCapture.FpsEma;
                    else if (_osramEthCapture != null && _osramEthCapture.IsCapturing && _osramEthCapture.FpsEma > 0.0)
                        paneBFps = _osramEthCapture.FpsEma;
                    else
                        paneBFps = _playback.BFpsEma;
                }

                LblRunInfoB.Text = StatusFormatter.FormatRunInfoB(isRunning, isPaused, paneBNoSignal, paneBFps);
            }

            // Pane C: Basler camera FPS
            // Prefer hardware grab-thread FPS (_baslerCapture.FpsEma), which tracks
            // trigger cadence directly and is immune to UI dispatcher jitter.
            // Keep UI-window FPS as fallback when camera object is temporarily null.
            if (LblRunInfoC != null)
            {
                if (allowUiRefreshC || !isRunning || isPaused)
                {
                    double paneCFps = (_baslerCapture != null && _baslerCapture.IsCapturing && _baslerCapture.FpsEma > 0.0)
                        ? _baslerCapture.FpsEma
                        : _baslerDispFps;

                    if (_baslerCapture?.IsFreeRunFallback == true && paneCFps <= 0.0)
                        paneCFps = BaslerFallbackDisplayFps;

                    // Nichia path: apply a small display-only offset so LSM monitor aligns
                    // better with LVDS monitor reading (does not affect internal processing).
                    if (_currentDeviceType == LsmDeviceType.Nichia && paneCFps > 0.0)
                        paneCFps += NichiaLsmFpsDisplayOffset;

                    if (!isRunning)
                        LblRunInfoC.Text = "";
                    else if (isPaused)
                        LblRunInfoC.Text = "Paused";
                    else if (_baslerSignalLost && _baslerCapture?.IsFreeRunFallback != true)
                        LblRunInfoC.Text = "";
                    else if (paneCFps > 0.0)
                        LblRunInfoC.Text = $"Running @: {paneCFps:F1} fps";
                    else
                        LblRunInfoC.Text = "Running";
                }
            }
        }

        private void UpdateBaslerRunInfoLabel()
        {
            if (LblRunInfoC == null || _playback.Cts == null || _playback.IsPaused)
                return;

            double paneCFps = (_baslerCapture != null && _baslerCapture.IsCapturing && _baslerCapture.FpsEma > 0.0)
                ? _baslerCapture.FpsEma
                : _baslerDispFps;

            if (_baslerCapture?.IsFreeRunFallback == true && paneCFps <= 0.0)
                paneCFps = BaslerFallbackDisplayFps;

            if (_currentDeviceType == LsmDeviceType.Nichia && paneCFps > 0.0)
                paneCFps += NichiaLsmFpsDisplayOffset;

            LblRunInfoC.Text = paneCFps > 0.0
                ? $"Running @: {paneCFps:F1} fps"
                : "Running";
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

        private static bool IsAbruptFrameTransition(byte[] previous, byte[] current)
        {
            if (previous.Length != current.Length || previous.Length == 0)
                return false;

            const int SampleStep = 8;
            const int DifferenceThreshold = 24;
            int changedPixels = 0;
            long totalDifference = 0;
            int sampledPixels = 0;

            for (int i = 0; i < current.Length; i += SampleStep)
            {
                int difference = Math.Abs(current[i] - previous[i]);
                totalDifference += difference;
                if (difference >= DifferenceThreshold)
                    changedPixels++;
                sampledPixels++;
            }

            double meanDifference = (double)totalDifference / sampledPixels;
            double changedRatio = (double)changedPixels / sampledPixels;
            return meanDifference >= 18.0 && changedRatio >= 0.20;
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
            long bestSad = long.MaxValue;
            int bestAge = int.MaxValue;
            double bestCandidateNcc = double.MinValue;
            const double MinimumCandidateNcc = 0.5;

            // Search newest-first. If adjacent animation frames produce nearly
            // identical NCC values, the newest candidate is the safest choice.
            int head = _syncRingHead;
            for (int age = 0; age < SyncRingSize; age++)
            {
                int i = (head - age) & (SyncRingSize - 1);
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

                long sad = 0;
                for (int j = 0; j < expectedLen; j++)
                    sad += Math.Abs(aData[j] - bData[j]);

                // A nearly uniform A frame has no meaningful NCC, but it can
                // still be the exact temporal predecessor of a structured B
                // frame at an animation transition. Never discard it before
                // the position-sensitive SAD comparison.
                bool structurallyValid = ncc >= MinimumCandidateNcc;
                bool sadIsBetter = sad < bestSad;
                bool sameSadPrefersHigherNcc = sad == bestSad && ncc > bestCandidateNcc;
                bool sameMetricsPreferNewer = sad == bestSad
                    && Math.Abs(ncc - bestCandidateNcc) <= 0.002
                    && age < bestAge;
                if (sadIsBetter || sameSadPrefersHigherNcc || sameMetricsPreferNewer)
                {
                    // NCC validates global structure; SAD decides exact content
                    // alignment. This prevents a narrow moving bar from winning
                    // merely because its global NCC is marginally higher.
                    bestSad = sad;
                    bestCandidateNcc = ncc;
                    best = f;
                    bestAge = age;
                }

                if (structurallyValid && ncc > bestNcc)
                    bestNcc = ncc;
            }

            // ── Quality gate ─────────────────────────────────────────────────
            // If the best NCC is still poor (< 0.5), the match is unreliable.
            // Fall back to the most recent A as a safe default.
            if (best == null || bestNcc < 0.5)
            {
                // A valid low-SAD match is preferable to the most recent A
                // when B is at a transition and NCC is undefined for the
                // matching flat predecessor.
                if (best != null)
                {
                    _lastMatchNcc = bestNcc; // report actual NCC for diagnostics
                    return best;
                }

                var recent = GetMostRecentSyncFrame(expectedLen);
                if (recent != null)
                {
                    _lastMatchNcc = bestNcc;
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
            _lvdsFramesSinceSyncReset = 0;
            _lvdsComparisonReady = false;
        }

        private void ImgA_MouseMove(object sender, MouseEventArgs e) => ShowPixelInfo(e, GetDisplayedFrameForPane(Pane.A), LblA);
        private void ImgB_MouseMove(object sender, MouseEventArgs e) => ShowPixelInfo(e, GetDisplayedFrameForPane(Pane.B), LblB);
        private void ImgC_MouseMove(object sender, MouseEventArgs e) => ShowPixelInfo(e, _latestC, LblC);
        private void ImgD_MouseMove(object sender, MouseEventArgs e) => ShowPixelInfoDiff(e, LblD);

        private void ImgA_MouseLeave(object sender, MouseEventArgs e) => LblA.Text = "";
        private void ImgB_MouseLeave(object sender, MouseEventArgs e) => LblB.Text = "";
        private void ImgC_MouseLeave(object sender, MouseEventArgs e) => LblC.Text = "";
        private void ImgD_MouseLeave(object sender, MouseEventArgs e) => LblD.Text = "";

        private static Pane PaneFromSender(object sender)
        {
            if (sender is System.Windows.Controls.Image img)
            {
                return img.Name switch
                {
                    "ImgA" => Pane.A,
                    "ImgB" => Pane.B,
                    "ImgC" => Pane.C,
                    _ => Pane.D,
                };
            }
            return Pane.A;
        }

        // ─── Fullscreen pane toggle ────────────────────────────────────────

        private System.Windows.Controls.Border GetPaneBorder(Pane pane) => pane switch
        {
            Pane.A => PaneA,
            Pane.B => PaneB,
            Pane.C => PaneC,
            _ => PaneD,
        };

        private void ToggleFullscreen(Pane pane)
        {
            if (_fullscreenPane != null)
            {
                // Exit fullscreen
                ExitFullscreen();
            }
            else
            {
                // Enter fullscreen for the selected pane
                EnterFullscreen(pane);
            }

            // Refresh overlays if paused
            if (_playback.IsPaused)
                UpdateOverlaysAll();
        }

        private void EnterFullscreen(Pane pane)
        {
            var border = GetPaneBorder(pane);

            // Save original grid position
            _fsOrigRow = Grid.GetRow(border);
            _fsOrigCol = Grid.GetColumn(border);
            _fsOrigRowSpan = Grid.GetRowSpan(border);
            _fsOrigColSpan = Grid.GetColumnSpan(border);

            // Hide all other panes
            System.Windows.Controls.Border[] allPanes = [PaneA, PaneB, PaneC, PaneD];
            foreach (var p in allPanes)
            {
                if (p != border) p.Visibility = Visibility.Collapsed;
            }

            // Hide sidebars and comparison bar
            SidebarCan.Visibility = Visibility.Collapsed;
            SidebarInfo.Visibility = Visibility.Collapsed;
            ComparisonBar.Visibility = Visibility.Collapsed;

            // ButtonBar stays visible below the pane (in its natural row 3, spanning all columns)
            Grid.SetColumnSpan(ButtonBar, 3);

            // Make the selected pane span rows 0-2 (leave row 3 for ButtonBar, hide row 4)
            Grid.SetRow(border, 0);
            Grid.SetColumn(border, 0);
            Grid.SetRowSpan(border, 3);
            Grid.SetColumnSpan(border, 3);
            border.Margin = new Thickness(4);

            _fullscreenPane = pane;
        }

        private void ExitFullscreen()
        {
            if (_fullscreenPane == null) return;

            var pane = _fullscreenPane.Value;
            var border = GetPaneBorder(pane);

            // Reset zoom/pan for this pane (coordinates are invalid after container resize)
            _zoomPan.Reset((int)pane);
            ClearOverlay(pane);

            // Restore original grid position
            Grid.SetRow(border, _fsOrigRow);
            Grid.SetColumn(border, _fsOrigCol);
            Grid.SetRowSpan(border, _fsOrigRowSpan);
            Grid.SetColumnSpan(border, _fsOrigColSpan);

            // Restore original margin
            if (pane == Pane.B)
                border.Margin = new Thickness(4, 4, 6, 4);
            else if (pane == Pane.C)
                border.Margin = new Thickness(6, 4, 4, 4);
            else
                border.Margin = new Thickness(4);

            // Show all panes again
            PaneA.Visibility = Visibility.Visible;
            PaneB.Visibility = Visibility.Visible;
            PaneC.Visibility = Visibility.Visible;
            PaneD.Visibility = Visibility.Visible;

            // Show sidebars, button bar, comparison bar
            SidebarCan.Visibility = Visibility.Visible;
            SidebarInfo.Visibility = Visibility.Visible;
            ButtonBar.Visibility = Visibility.Visible;
            ComparisonBar.Visibility = Visibility.Visible;

            // Restore ButtonBar to its normal grid span
            Grid.SetColumnSpan(ButtonBar, 2);

            _fullscreenPane = null;
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

            // Plain double-click (no Ctrl): toggle fullscreen for this pane
            if (e.ClickCount >= 2 && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                ToggleFullscreen(pane);
                e.Handled = true;
                return;
            }

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

            if (e.Source is not System.Windows.Controls.Image img) { lbl.Text = ""; return; }
            var pane = PaneFromSender(img);
            var (_, ovr, _) = GetPaneVisuals(pane);

            if (!PixelInspector.TryGetPixelXY(e, f, img, ovr, out int x, out int y)) { lbl.Text = ""; return; }

            byte v = f.Data[y * f.Stride + x];
            lbl.Text = PixelInspector.FormatGrayscaleInfo(x, y, v, f.Width);
        }

        private void ShowPixelInfoDiff(MouseEventArgs e, System.Windows.Controls.TextBlock lbl)
        {
            if (_playback.Cts == null || ShouldShowNoSignalWhileRunning()) { lbl.Text = ""; return; }

            var a = GetDisplayedFrameForPane(Pane.A);
            var b = GetDisplayedFrameForPane(Pane.B);

            // Select correct reference/measured pair based on comparison mode
            int cmpMode = _comparisonMode;
            if (cmpMode == 1 || cmpMode == 2)
            {
                Frame? cameraFrame = null;
                var dsBuf = _downscaledCameraFrame;
                if (dsBuf != null && dsBuf.Length == _currentWidth * _currentHeight)
                    cameraFrame = new Frame(_currentWidth, _currentHeight, dsBuf, DateTime.UtcNow);

                if (cmpMode == 1)
                {
                    // LSM-LVDS: reference = LVDS (B), measured = camera
                    a = b;
                    b = cameraFrame;
                }
                else
                {
                    // LSM-AVTP: reference = AVTP (A), measured = camera
                    b = cameraFrame;
                }
            }

            var refFrame = a ?? b;
            if (refFrame == null) { lbl.Text = ""; return; }

            if (e.Source is not System.Windows.Controls.Image img) { lbl.Text = ""; return; }
            var pane = PaneFromSender(img);
            var (_, ovr, _) = GetPaneVisuals(pane);

            if (!PixelInspector.TryGetPixelXY(e, refFrame, img, ovr, out int x, out int y)) { lbl.Text = ""; return; }

            int idx = (y * refFrame.Stride) + x;
            byte av = (a != null && idx < a.Data.Length) ? a.Data[idx] : (byte)0;
            byte bv = (b != null && idx < b.Data.Length) ? b.Data[idx] : (byte)0;

            // Select labels matching the active comparison mode
            string labelA, labelB;
            if (cmpMode == 1) { labelA = "LVDS"; labelB = "LSM"; }
            else if (cmpMode == 2) { labelA = "AVTP"; labelB = "LSM"; }
            else { labelA = "AVTP"; labelB = "LVDS"; }
            lbl.Text = PixelInspector.FormatDiffInfo(x, y, av, bv, refFrame.Width, labelA, labelB);
        }

        /// <summary>
        /// Parses a hex string like "0x22F0" or "22F0" to ushort. Returns fallback on failure.
        /// </summary>
        private static ushort ParseHexUshort(string? text, ushort fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text[2..];
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
                text = text[2..];
            return byte.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : fallback;
        }

        // ─── Menu handlers ─────────────────────────────────────────────────────────

        private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

        private FlickerControlWindow? _flickerControlWindow;
        private AvtpLvdsSimulationControlWindow? _lvdsSimulationControlWindow;
        private Window? _ethConfigWindow;
        private CameraConfigWindow? _cameraConfigWindow;
        private ApiConfigurationWindow? _apiConfigWindow;
        private FlickerDetectionConfiguration _flickerConfiguration = new();
        private readonly FlickerInjectionController _flickerInjection = new();
        private readonly FlickerDetector _flickerDetector = new();
        private bool _flickerDetectionEnabled;
        private Frame? _flickerCandidateA;
        private Frame? _flickerCandidateB;
        private Frame? _flickerCandidateC;
        private byte[]? _flickerCandidateD;
        private int _flickerCandidatePeakPixels;
        private byte[]? _flickerCandidateReportReference;
        private byte[]? _flickerCandidateReportMeasured;
        private TextBox? _flickerFrameCountTextBox;
        private TextBox? _flickerThresholdTextBox;
        private Border? _flickerFrameCountBorder;
        private Border? _flickerThresholdBorder;
        private Border? _flickerStatusBadge;
        private TextBlock? _flickerStatusHeaderText;
        private TextBlock? _flickerDetectedCountText;
        private Ellipse? _flickerDetectedIndicator;
        private Button? _flickerInjectButton;
        private ToggleButton? _flickerPolarityToggle;
        private TextBlock? _flickerInfoText;
        private DispatcherTimer? _flickerInfoClearTimer;
        private ListView? _flickerEventLog;
        private readonly ObservableCollection<FlickerLogEntry> _flickerLogEvents = [];
        private string _flickerInfoMessage = string.Empty;
        private Brush _flickerInfoForeground = Brushes.Black;
        private string _flickerSortColumn = nameof(FlickerLogEntry.Time);
        private bool _flickerSortAscending = true;
        private GridViewColumnHeader? _flickerLastSortHeader;

        private sealed record FlickerLogEntry(string Time, string Status, string Metric, string Frames);

        /// <summary>
        /// Wraps a config GroupBox in a DockPanel with an OK button at the bottom.
        /// Returns a tuple of (wrapper, okButton) so the caller can wire up close logic.
        /// </summary>
        private static (DockPanel wrapper, Button okBtn) WrapWithOkButton(UIElement content)
        {
            var okBtn = new Button { Content = "OK", Padding = new Thickness(24, 4, 24, 4), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 4) };
            DockPanel.SetDock(okBtn, Dock.Bottom);
            var panel = new DockPanel { LastChildFill = true };
            panel.Children.Add(okBtn);
            panel.Children.Add(content);
            return (panel, okBtn);
        }

        private static ToggleButton CreateFlickerPolaritySwitch()
        {
            var toggle = new ToggleButton
            {
                Width = 64,
                Height = 25,
                Margin = new Thickness(-25, 0, 0, 0),
                ToolTip = "Switch between bright and dark flicker"
            };

            var track = new FrameworkElementFactory(typeof(Border)) { Name = "Track" };
            track.SetValue(Border.BackgroundProperty, Brushes.Black);
            track.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xB8, 0xBD, 0xC2)));
            track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            track.SetValue(Border.PaddingProperty, new Thickness(3));

            var thumb = new FrameworkElementFactory(typeof(Ellipse)) { Name = "Thumb" };
            thumb.SetValue(FrameworkElement.WidthProperty, 17.0);
            thumb.SetValue(FrameworkElement.HeightProperty, 17.0);
            thumb.SetValue(Shape.FillProperty, new SolidColorBrush(Color.FromRgb(0xB8, 0xBD, 0xC2)));
            thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);

            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.AppendChild(thumb);
            track.AppendChild(grid);

            var template = new ControlTemplate(typeof(ToggleButton))
            {
                VisualTree = track
            };

            var checkedTrigger = new Trigger
            {
                Property = ToggleButton.IsCheckedProperty,
                Value = true
            };
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                Brushes.White, "Track"));
            checkedTrigger.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Right, "Thumb"));
            template.Triggers.Add(checkedTrigger);

            var disabledTrigger = new Trigger
            {
                Property = ToggleButton.IsEnabledProperty,
                Value = false
            };
            disabledTrigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE0)),
                "Track"));
            template.Triggers.Add(disabledTrigger);

            toggle.IsChecked = true;
            toggle.Template = template;
            return toggle;
        }

        private static ToggleButton CreateFlickerEnableSwitch()
        {
            var toggle = new ToggleButton
            {
                Width = 46,
                Height = 24,
                ToolTip = "Enable or disable flicker detection and monitoring"
            };

            var track = new FrameworkElementFactory(typeof(Border)) { Name = "Track" };
            track.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xD0, 0xD4, 0xD8)));
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            track.SetValue(Border.PaddingProperty, new Thickness(3));

            var thumb = new FrameworkElementFactory(typeof(Ellipse)) { Name = "Thumb" };
            thumb.SetValue(FrameworkElement.WidthProperty, 18.0);
            thumb.SetValue(FrameworkElement.HeightProperty, 18.0);
            thumb.SetValue(Shape.FillProperty, Brushes.White);
            thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);

            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.AppendChild(thumb);
            track.AppendChild(grid);

            var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = track };
            var checkedTrigger = new Trigger
            {
                Property = ToggleButton.IsCheckedProperty,
                Value = true
            };
            checkedTrigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0x1F, 0x5A, 0x91)),
                "Track"));
            checkedTrigger.Setters.Add(new Setter(
                FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Right,
                "Thumb"));
            template.Triggers.Add(checkedTrigger);
            toggle.Template = template;
            return toggle;
        }

        private void MenuFlickerControl_Click(object sender, RoutedEventArgs e)
        {
            if (_flickerControlWindow != null && _flickerControlWindow.IsVisible) { _flickerControlWindow.Activate(); return; }
            var flickerWindow = new FlickerControlWindow();

            var flickerControls = new Grid { Margin = new Thickness(0) };
            for (int row = 0; row < 5; row++)
                flickerControls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            flickerControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            flickerControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            flickerControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            flickerControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            flickerControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            flickerControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var flickerDescription = new TextBlock
            {
                Text = "Configure flicker detection and optionally inject test frames on LSM Monitor",
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Gray
            };
            Grid.SetRow(flickerDescription, 0);
            Grid.SetColumnSpan(flickerDescription, 7);
            flickerControls.Children.Add(flickerDescription);

            var flickerSeparator = new Rectangle
            {
                Height = 1,
                Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                Margin = new Thickness(0, 8, 0, 2)
            };
            Grid.SetRow(flickerSeparator, 1);
            Grid.SetColumnSpan(flickerSeparator, 7);
            flickerControls.Children.Add(flickerSeparator);
            flickerWindow.FlickerInjectionControlsHost.Children.Add(flickerControls);

            var enableToggle = CreateFlickerEnableSwitch();
            enableToggle.IsChecked = _flickerDetectionEnabled;
            enableToggle.Checked += FlickerEnableToggle_Changed;
            enableToggle.Unchecked += FlickerEnableToggle_Changed;
            flickerWindow.FlickerEnableControlHost.Children.Add(enableToggle);
            flickerWindow.FlickerEnableToggle = enableToggle;

            _flickerInjectButton = new Button
            {
                Content = "Inject Fault",
                Width = 96, Height = 28,
                Background = Brushes.DodgerBlue,
                Foreground = Brushes.White
            };
            _flickerInjectButton.Click += FlickerInjectButton_Click;
            _flickerInjectButton.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(_flickerInjectButton, 4);
            Grid.SetColumn(_flickerInjectButton, 5);
            flickerControls.Children.Add(_flickerInjectButton);

            _flickerFrameCountBorder = CreateFaultStyleSpinner(
                _flickerConfiguration.FlickeringFramesThreshold, 1,
                FlickerDetectionConfiguration.MinFrameCount, FlickerDetectionConfiguration.MaxFrameCount,
                out _flickerFrameCountTextBox);
            _flickerFrameCountTextBox.TextChanged += FlickerConfigurationTextChanged;
            AddFlickerLabel(flickerControls, "Detection Threshold:", 2, 0);
            AddFlickerControl(flickerControls, _flickerFrameCountBorder, 2, 1);
            AddFlickerUnit(flickerControls, "frames", 2, 2);

            _flickerThresholdBorder = CreateFaultStyleSpinner(
                _flickerConfiguration.DeviationTrigger, 1,
                FlickerDetectionConfiguration.MinTriggerThreshold, FlickerDetectionConfiguration.MaxTriggerThreshold,
                out _flickerThresholdTextBox);
            _flickerThresholdTextBox.TextChanged += FlickerConfigurationTextChanged;
            AddFlickerLabel(flickerControls, "Deviation Trigger:", 3, 0);
            AddFlickerControl(flickerControls, _flickerThresholdBorder, 3, 1);
            AddFlickerUnit(flickerControls, "abs. dev", 3, 2);

            AddFlickerLabel(flickerControls, "Injection Polarity:", 4, 0);
            _flickerPolarityToggle = CreateFlickerPolaritySwitch();
            _flickerPolarityToggle.IsChecked = _flickerConfiguration.InjectionPolarity == FlickerInjectionPolarity.White;
            _flickerPolarityToggle.Checked += FlickerPolarityToggle_Changed;
            _flickerPolarityToggle.Unchecked += FlickerPolarityToggle_Changed;
            FlickerPolarityToggle_Changed(_flickerPolarityToggle, new RoutedEventArgs());
            AddFlickerControl(flickerControls, _flickerPolarityToggle, 4, 1);
            AddFlickerUnit(flickerControls, "Dark / Bright", 4, 2);

            _flickerInfoText = new TextBlock
            {
                MinHeight = 18,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Visible
            };

            _flickerInfoClearTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _flickerInfoClearTimer.Tick += (_, _) =>
            {
                _flickerInfoClearTimer.Stop();
                _flickerInfoMessage = string.Empty;
                if (_flickerInfoText != null)
                {
                    _flickerInfoText.Text = string.Empty;
                }
            };

            _flickerEventLog = flickerWindow.FlickerEventLogView;
            var flickerLogView = new ListCollectionView((IList)_flickerLogEvents);
            flickerLogView.SortDescriptions.Add(new SortDescription(
                nameof(FlickerLogEntry.Time), ListSortDirection.Ascending));
            _flickerEventLog.ItemsSource = flickerLogView;
            _flickerInfoText = flickerWindow.FlickerInfoText;
            _flickerInfoText.Text = _flickerInfoMessage;
            _flickerInfoText.Foreground = _flickerInfoForeground;
            _flickerStatusBadge = flickerWindow.FlickerStatusBadge;
            _flickerStatusHeaderText = flickerWindow.FlickerStatusHeaderText;
            _flickerDetectedCountText = flickerWindow.FlickerDetectedCountTextBlock;
            _flickerDetectedIndicator = flickerWindow.FlickerDetectedIndicatorEllipse;
            UpdateFlickerDetectedCount();
            flickerWindow.FlickerLogColumnHeaderClicked += FlickerEventLog_ColumnHeaderClick;
            flickerWindow.FlickerLogCopyRequested += CopySelectedFlickerLogEntry;
            flickerWindow.FlickerLogClearRequested += ClearFlickerLog;
            flickerWindow.FlickerLogSaveRequested += SaveFlickerLog;
            flickerWindow.FlickerFolderOpenRequested += OpenFlickerFolder;
            UpdateFlickerInjectionButton();
            UpdateFlickerFeatureEnabledState(flickerWindow);

            flickerWindow.Closed += (_, _) =>
            {
                if (_flickerPolarityToggle != null)
                {
                    _flickerConfiguration = _flickerConfiguration with
                    {
                        InjectionPolarity = _flickerPolarityToggle.IsChecked == true
                            ? FlickerInjectionPolarity.White
                            : FlickerInjectionPolarity.Black
                    };
                }
                _flickerInfoClearTimer?.Stop();
                _flickerControlWindow = null;
            };
            _flickerControlWindow = flickerWindow;
            flickerWindow.Show();
        }

        private void MenuLvdsSimulationControl_Click(object sender, RoutedEventArgs e)
        {
            if (_lvdsSimulationControlWindow != null && _lvdsSimulationControlWindow.IsVisible)
            {
                _lvdsSimulationControlWindow.Activate();
                return;
            }

            HiddenConfigPanel.Children.Remove(GrpAppSettings);
            GrpAppSettings.Margin = new Thickness(0);
            var lvdsWindow = new AvtpLvdsSimulationControlWindow();
            lvdsWindow.LvdsContentHost.Content = GrpAppSettings;
            lvdsWindow.Closed += (_, _) =>
            {
                lvdsWindow.LvdsContentHost.Content = null;
                GrpAppSettings.Margin = new Thickness(8);
                HiddenConfigPanel.Children.Add(GrpAppSettings);
                _lvdsSimulationControlWindow = null;
            };
            _lvdsSimulationControlWindow = lvdsWindow;
            lvdsWindow.Show();
        }

        /// <summary>
        /// Builds a GroupBox header matching the Communication Fault Injection windows:
        /// bold icon + bold title on the left, a rounded status badge on the right.
        /// </summary>
        private static Grid CreateFaultStyleHeader(
            string icon, string title, string badgeText, string badgeBackground, string badgeForeground,
            out Border statusBadge, out TextBlock statusText)
        {
            var header = new Grid();
            var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
            titlePanel.Children.Add(new TextBlock { Text = icon, FontWeight = FontWeights.Bold, Foreground = Brushes.DarkBlue, Margin = new Thickness(0, 0, 5, 0) });
            titlePanel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, Foreground = Brushes.Black });
            header.Children.Add(titlePanel);

            statusText = new TextBlock
            {
                Text = badgeText,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)new BrushConverter().ConvertFromString(badgeForeground)!
            };
            statusBadge = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = (Brush)new BrushConverter().ConvertFromString(badgeBackground)!,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3, 8, 3),
                Child = statusText
            };
            header.Children.Add(statusBadge);
            return header;
        }

        /// <summary>
        /// Builds a bordered numeric spinner box matching the Duration control used by
        /// the Communication Fault Injection windows (64px text box + up/down RepeatButtons).
        /// </summary>
        private static Border CreateFaultStyleSpinner(
            int initialValue, int step, int min, int max, out TextBox textBox)
        {
            textBox = new TextBox
            {
                Width = 62, Height = 18, MinWidth = 62, MaxWidth = 62, MinHeight = 18, MaxHeight = 18,
                Text = initialValue.ToString(CultureInfo.InvariantCulture),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0, 16, 0)
            };
            var capturedTextBox = textBox;
            capturedTextBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;

                ClampFlickerSpinnerValue(capturedTextBox, max);
                e.Handled = true;
            };
            capturedTextBox.LostFocus += (_, _) => ClampFlickerSpinnerValue(capturedTextBox, max);

            var upButton = new RepeatButton { Width = 16, Height = 9, MinWidth = 16, MaxWidth = 16, MinHeight = 9, MaxHeight = 9, Content = "▲", FontSize = 6, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
            var downButton = new RepeatButton { Width = 16, Height = 9, MinWidth = 16, MaxWidth = 16, MinHeight = 9, MaxHeight = 9, Content = "▼", FontSize = 6, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
            textBox.Foreground = Brushes.Black;
            upButton.Foreground = Brushes.Black;
            downButton.Foreground = Brushes.Black;
            upButton.Click += (_, _) => ChangeFlickerSpinnerValue(capturedTextBox, step, min, max);
            downButton.Click += (_, _) => ChangeFlickerSpinnerValue(capturedTextBox, -step, min, max);

            var arrowStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Width = 16, Height = 18, MinWidth = 16, MaxWidth = 16, MinHeight = 18, MaxHeight = 18
            };
            Panel.SetZIndex(arrowStack, 1);
            arrowStack.Children.Add(upButton);
            arrowStack.Children.Add(downButton);

            var innerGrid = new Grid { Width = 62, Height = 18, MinWidth = 62, MaxWidth = 62, MinHeight = 18, MaxHeight = 18, ClipToBounds = true };
            innerGrid.Children.Add(textBox);
            innerGrid.Children.Add(arrowStack);

            return new Border
            {
                Width = 64, Height = 20,
                HorizontalAlignment = HorizontalAlignment.Left,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#ABADB3")!,
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                CornerRadius = new CornerRadius(2),
                Child = innerGrid
            };
        }

        private static void ChangeFlickerSpinnerValue(TextBox textBox, int delta, int min, int max)
        {
            int current = int.TryParse(textBox.Text, out int parsed) ? parsed : min;
            textBox.Text = Math.Clamp(current + delta, min, max).ToString(CultureInfo.InvariantCulture);
        }

        private static void ClampFlickerSpinnerValue(TextBox textBox, int max)
        {
            if (!int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value <= max)
                return;

            textBox.Text = max.ToString(CultureInfo.InvariantCulture);
        }

        private static void AddFlickerLabel(Grid grid, string text, int row, int column)
        {
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, column);
            grid.Children.Add(label);
        }

        private static void AddFlickerControl(Grid grid, UIElement control, int row, int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            grid.Children.Add(control);
        }

        private static void AddFlickerUnit(Grid grid, string text, int row, int column)
        {
            var unit = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 10, 4) };
            Grid.SetRow(unit, row);
            Grid.SetColumn(unit, column);
            grid.Children.Add(unit);
        }

        private void FlickerInjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_flickerDetectionEnabled)
                return;

            if (_flickerInjection.IsActive)
            {
                _flickerInjection.Cancel();
                SetFlickerInfo("Flicker injection stopped.", Brushes.DarkRed);
                return;
            }

            if (!TryReadFlickerConfiguration(out var configuration, out string errorMessage))
            {
                SetFlickerInfo(errorMessage, Brushes.DarkRed);
                return;
            }

            _flickerConfiguration = configuration;
            var latestCameraFrame = _latestC;
            if (latestCameraFrame == null)
            {
                SetFlickerInfo("No LSM camera frame is available yet.", Brushes.DarkRed);
                return;
            }

            _flickerConfiguration = configuration with
            {
                InjectionPolarity = _flickerPolarityToggle?.IsChecked == true
                    ? FlickerInjectionPolarity.White
                    : FlickerInjectionPolarity.Black
            };
            _flickerDetector.UpdateConfiguration(_flickerConfiguration);
            _flickerDetector.Reset();
            byte[] detectionBaseline = FrameDownscaler.DownscaleBlockAverage(
                latestCameraFrame.Data, latestCameraFrame.Width, latestCameraFrame.Height,
                _currentWidth, _currentHeight);
            _flickerDetector.ProcessFrame(
                detectionBaseline,
                _currentWidth,
                _currentHeight);
            ClearFlickerCandidateFrames();

            if (!_flickerInjection.TryStart(latestCameraFrame.Data, latestCameraFrame.Width, latestCameraFrame.Height, _flickerConfiguration))
            {
                SetFlickerInfo("Flicker injection is already active or the frame is invalid.", Brushes.DarkRed);
                return;
            }

            UpdateFlickerInjectionButton();
            SetFlickerInfo(
                $"Injecting flicker for {_flickerConfiguration.FlickeringFramesThreshold} frame(s).",
                Brushes.DarkRed);
        }

        private void FlickerInjectionCompleted()
        {
            UpdateFlickerInjectionButton();
            SetFlickerInfo("Flicker injection completed.", Brushes.ForestGreen, clearAfterSeconds: 10);
        }

        private void FlickerDetectorStatusChanged(FlickerDetectionStatusSnapshot snapshot)
        {
            UpdateFlickerStatusBadge(snapshot.Status);
            var entry = new FlickerLogEntry(
                DateTime.Now.ToString("HH:mm:ss.fff"),
                snapshot.Status.ToString(),
                snapshot.LastMeasuredMetric.ToString("F0"),
                snapshot.CandidateFrameCount.ToString());
            if (_flickerLogEvents != null)
            {
                _flickerLogEvents.Add(entry);
                while (_flickerLogEvents.Count > 100)
                    _flickerLogEvents.RemoveAt(0);
                UpdateFlickerDetectedCount();
                _flickerEventLog?.ScrollIntoView(entry);
            }

            if (snapshot.Status == FlickerDetectionStatus.Detected)
                _ = ExportFlickerEvidenceAsync(snapshot);

        }

        private void FlickerEventLog_ColumnHeaderClick(object? sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader header || header.Column == null)
                return;

            string? column = header.Column.Header?.ToString()?.TrimEnd(' ', '▲', '▼');
            string property = column switch
            {
                "Time" => nameof(FlickerLogEntry.Time),
                "Status" => nameof(FlickerLogEntry.Status),
                "Metric" => nameof(FlickerLogEntry.Metric),
                "Frames" => nameof(FlickerLogEntry.Frames),
                _ => string.Empty,
            };
            if (string.IsNullOrEmpty(property) || _flickerEventLog?.ItemsSource is not ListCollectionView view)
                return;

            if (property == _flickerSortColumn)
                _flickerSortAscending = !_flickerSortAscending;
            else
            {
                _flickerSortColumn = property;
                _flickerSortAscending = true;
            }

            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(
                    _flickerSortColumn,
                    _flickerSortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending));
            }

            if (_flickerLastSortHeader != null)
                _flickerLastSortHeader.Column.Header = _flickerLastSortHeader.Column.Header?.ToString()?.TrimEnd(' ', '▲', '▼');
            header.Column.Header = $"{column} {(_flickerSortAscending ? '▲' : '▼')}";
            _flickerLastSortHeader = header;
        }

        private void CopySelectedFlickerLogEntry(object? sender, RoutedEventArgs e)
        {
            if (_flickerEventLog?.SelectedItem is not FlickerLogEntry entry)
                return;

            // Keep the pasted row visually unchanged while preventing Windows Suggested Actions
            // from interpreting the leading timestamp as a calendar time.
            string clipboardTime = entry.Time.Replace(":", ":\u200B", StringComparison.Ordinal);
            Clipboard.SetText($"{clipboardTime}\t{entry.Status}\t{entry.Metric}\t{entry.Frames}");
        }

        private void ClearFlickerLog(object? sender, RoutedEventArgs e)
        {
            _flickerLogEvents?.Clear();
            UpdateFlickerDetectedCount();
        }

        private void UpdateFlickerDetectedCount()
        {
            if (_flickerDetectedCountText == null || _flickerDetectedIndicator == null)
                return;

            int detectedCount = GetDetectedFlickerCount();
            _flickerDetectedCountText.Text = detectedCount.ToString(CultureInfo.InvariantCulture);
            _flickerDetectedIndicator.Fill = detectedCount > 0 ? Brushes.Red : new SolidColorBrush(Color.FromRgb(0xB8, 0xBD, 0xC2));
        }

        private int GetDetectedFlickerCount()
        {
            return _flickerLogEvents.Count(entry =>
                string.Equals(entry.Status, FlickerDetectionStatus.Detected.ToString(), StringComparison.Ordinal));
        }

        private static string GetFlickerLogsDirectory()
        {
            string root = RecordingManager.FindRepoRootWithDocs(AppContext.BaseDirectory)
                ?? Directory.GetCurrentDirectory();
            string directory = System.IO.Path.Combine(root, "docs", "outputs", "flickerDetections", "Logs");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string GetFlickerDetectionsDirectory()
        {
            string root = RecordingManager.FindRepoRootWithDocs(AppContext.BaseDirectory)
                ?? Directory.GetCurrentDirectory();
            string directory = System.IO.Path.Combine(root, "docs", "outputs", "flickerDetections");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private void OpenFlickerFolder(object? sender, RoutedEventArgs e)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{GetFlickerDetectionsDirectory()}\"",
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open folder: {ex.Message}", "Open Flicker Folder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveFlickerLog(object? sender, RoutedEventArgs e)
        {
            if (_flickerLogEvents == null || _flickerLogEvents.Count == 0)
            {
                MessageBox.Show("No Flicker log entries to save.", "Save Flicker Log", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string path = System.IO.Path.Combine(GetFlickerLogsDirectory(), $"FLK_Log_{DateTime.Now:yyyy_MM_dd_HHmmss}.log");
            try
            {
                using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
                writer.WriteLine("Time\tStatus\tMetric\tFrames");
                foreach (FlickerLogEntry entry in _flickerLogEvents)
                    writer.WriteLine($"{entry.Time}\t{entry.Status}\t{entry.Metric}\t{entry.Frames}");

                SetFlickerInfo($"Flicker log saved: ~\\outputs\\flickerDetections\\Logs\\{System.IO.Path.GetFileName(path)}", Brushes.ForestGreen);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log($"Failed to save Flicker log '{path}': {ex}");
                MessageBox.Show($"Could not save the Flicker log.\n{ex.Message}", "Save Flicker Log", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateFlickerStatusBadge(FlickerDetectionStatus status)
        {
            if (_flickerStatusBadge == null || _flickerStatusHeaderText == null)
                return;

            (string text, Brush background, Brush foreground) = status switch
            {
                FlickerDetectionStatus.Candidate => ("Candidate", Brushes.LemonChiffon, Brushes.DarkGoldenrod),
                FlickerDetectionStatus.Detected => ("Detected", Brushes.MistyRose, Brushes.DarkRed),
                FlickerDetectionStatus.Cooldown => ("Cooldown", Brushes.LightCyan, Brushes.DarkCyan),
                _ => ("Idle", new SolidColorBrush(Color.FromRgb(0xD9, 0xEA, 0xD3)), Brushes.ForestGreen),
            };
            _flickerStatusHeaderText.Text = text;
            _flickerStatusBadge.Background = background;
            _flickerStatusHeaderText.Foreground = foreground;
        }

        private async Task ExportFlickerEvidenceAsync(FlickerDetectionStatusSnapshot snapshot)
        {
            Frame? a = _flickerCandidateA;
            Frame? b = _flickerCandidateB;
            Frame? c = _flickerCandidateC ?? _latestC;

            if (c == null)
            {
                SetFlickerInfo("Flicker detected, but candidate snapshot is unavailable.", Brushes.DarkRed);
                return;
            }

            try
            {
                lock (_frameLock)
                {
                    a ??= _latestA;
                    b ??= _latestB;
                }

                a ??= new Frame(_currentWidth, _currentHeight, GetASourceBytes(), c.TimestampUtc);
                b ??= new Frame(_currentWidth, _currentHeight, _noSignalGrayFrame, c.TimestampUtc);
                var bPost = ApplyBPostProcessing(a, b);
                byte[] cameraData = FrameDownscaler.DownscaleBlockAverage(
                    c.Data, c.Width, c.Height, _currentWidth, _currentHeight);
                var dCopy = _flickerCandidateD ?? CreateFlickerEvidenceDiff(bPost.Data, cameraData);
                byte[] reportReference = _flickerCandidateReportReference
                    ?? bPost.Data;
                byte[] reportMeasured = _flickerCandidateReportMeasured
                    ?? cameraData;
                string outputDirectory = await FlickerEvidenceExporter.ExportAsync(
                    snapshot.EventId ?? $"FLK-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}",
                    a, bPost, c, dCopy, reportReference, reportMeasured,
                    _currentWidth, _currentHeight,
                    snapshot);
                SetFlickerInfo($"Flicker detected! Evidence saved: {outputDirectory}", Brushes.DarkBlue);
            }
            catch (Exception ex)
            {
                AppendDiagLog($"[flicker] Evidence export failed: {ex.Message}");
                SetFlickerInfo("Flicker evidence export failed.", Brushes.DarkRed);
            }
        }

        private void CaptureFlickerCandidateFrames(
            Frame a, Frame b)
        {
            if (_flickerDetector.Snapshot.Status != FlickerDetectionStatus.Candidate
                || _flickerCandidateC == null)
                return;

            byte[] cameraData = FrameDownscaler.DownscaleBlockAverage(
                _flickerCandidateC.Data,
                _flickerCandidateC.Width,
                _flickerCandidateC.Height,
                _currentWidth,
                _currentHeight);
            _flickerCandidateA = new Frame(a.Width, a.Height, (byte[])a.Data.Clone(), a.TimestampUtc);
            _flickerCandidateB = new Frame(b.Width, b.Height, (byte[])b.Data.Clone(), b.TimestampUtc);
            _flickerCandidateD = CreateFlickerEvidenceDiff(b.Data, cameraData);
            _flickerCandidateReportReference = (byte[])b.Data.Clone();
            _flickerCandidateReportMeasured = cameraData;
        }

        private byte[] CreateFlickerEvidenceDiff(byte[] reference, byte[] measured)
        {
            var diff = new byte[_currentWidth * _currentHeight * 3];
            DiffRenderer.RenderCompareToBgr(
                diff, reference, measured, _currentWidth, _currentHeight, _diffThreshold,
                _zeroZeroIsWhite,
                out _, out _, out _, out _, out _, out _, out _, (byte)5);
            return diff;
        }

        private void ClearFlickerCandidateFrames()
        {
            _flickerCandidateA = null;
            _flickerCandidateB = null;
            _flickerCandidateC = null;
            _flickerCandidateD = null;
            _flickerCandidateReportReference = null;
            _flickerCandidateReportMeasured = null;
            _flickerCandidatePeakPixels = 0;
        }

        private void UpdateFlickerInjectionButton()
        {
            if (_flickerInjectButton == null)
                return;

            bool active = _flickerInjection.IsActive;
            _flickerInjectButton.Content = active ? "⏹ Stop" : "Inject Fault";
            _flickerInjectButton.Background = active
                ? new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33))
                : Brushes.DodgerBlue;
            _flickerInjectButton.Foreground = Brushes.White;

            if (_flickerPolarityToggle != null)
                _flickerPolarityToggle.IsEnabled = !active;
        }

        private void FlickerPolarityToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_flickerPolarityToggle == null)
                return;

            bool isBlack = _flickerPolarityToggle.IsChecked == true;
            _flickerPolarityToggle.Content = isBlack ? "Dark" : "Bright";
            _flickerPolarityToggle.Background = isBlack
                ? Brushes.Black
                : Brushes.White;
            _flickerPolarityToggle.Foreground = isBlack ? Brushes.White : Brushes.Black;
            _flickerConfiguration = _flickerConfiguration with
            {
                InjectionPolarity = isBlack
                    ? FlickerInjectionPolarity.Black
                    : FlickerInjectionPolarity.White
            };
            _flickerDetector.UpdateConfiguration(_flickerConfiguration);
        }

        private void FlickerEnableToggle_Changed(object sender, RoutedEventArgs e)
        {
            _flickerDetectionEnabled = sender is ToggleButton toggle && toggle.IsChecked == true;
            if (!_flickerDetectionEnabled)
            {
                _flickerInjection.Cancel();
                _flickerDetector.Reset(notifyStatus: false);
                ClearFlickerCandidateFrames();
            }

            UpdateFlickerInjectionButton();
            if (_flickerControlWindow != null)
                UpdateFlickerFeatureEnabledState(_flickerControlWindow);
        }

        private void UpdateFlickerFeatureEnabledState(FlickerControlWindow window)
        {
            bool active = _flickerDetectionEnabled;
            window.FlickerEnableStateText.Text = active ? "Enabled" : "Disabled";
            window.FlickerEnableStateText.Foreground = active ? Brushes.DarkBlue : Brushes.Gray;
            if (_flickerInjectButton != null)
                _flickerInjectButton.IsEnabled = active;
            if (_flickerFrameCountTextBox != null)
                _flickerFrameCountTextBox.IsEnabled = active && !_flickerInjection.IsActive;
            if (_flickerThresholdTextBox != null)
                _flickerThresholdTextBox.IsEnabled = active && !_flickerInjection.IsActive;
            if (_flickerPolarityToggle != null)
                _flickerPolarityToggle.IsEnabled = active && !_flickerInjection.IsActive;
            Brush controlBackground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xE8));
            Brush controlBorder = active ? new SolidColorBrush(Color.FromRgb(0xAB, 0xAD, 0xB3)) : new SolidColorBrush(Color.FromRgb(0xB8, 0xBD, 0xC2));
            Brush controlForeground = active ? Brushes.Black : new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x96));
            if (_flickerFrameCountBorder != null)
            {
                _flickerFrameCountBorder.IsEnabled = active && !_flickerInjection.IsActive;
                _flickerFrameCountBorder.Background = controlBackground;
                _flickerFrameCountBorder.BorderBrush = controlBorder;
                _flickerFrameCountTextBox!.Foreground = controlForeground;
            }
            if (_flickerThresholdBorder != null)
            {
                _flickerThresholdBorder.IsEnabled = active && !_flickerInjection.IsActive;
                _flickerThresholdBorder.Background = controlBackground;
                _flickerThresholdBorder.BorderBrush = controlBorder;
                _flickerThresholdTextBox!.Foreground = controlForeground;
            }
        }

        private void FlickerConfigurationTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!TryReadFlickerConfiguration(out var configuration, out _))
                return;

            _flickerConfiguration = configuration with
            {
                InjectionPolarity = _flickerPolarityToggle?.IsChecked == true
                    ? FlickerInjectionPolarity.White
                    : FlickerInjectionPolarity.Black
            };
            _flickerDetector.UpdateConfiguration(_flickerConfiguration);
        }

        private bool TryReadFlickerConfiguration(out FlickerDetectionConfiguration configuration, out string errorMessage)
        {
            if (_flickerFrameCountTextBox != null)
                ClampFlickerSpinnerValue(
                    _flickerFrameCountTextBox,
                    FlickerDetectionConfiguration.MaxFrameCount);
            if (_flickerThresholdTextBox != null)
                ClampFlickerSpinnerValue(
                    _flickerThresholdTextBox,
                    FlickerDetectionConfiguration.MaxTriggerThreshold);

            configuration = new FlickerDetectionConfiguration
            {
                FlickeringFramesThreshold = ParseFlickerInt(_flickerFrameCountTextBox?.Text),
                DeviationTrigger = ParseFlickerInt(_flickerThresholdTextBox?.Text),
            };

            var errors = configuration.Validate();
            errorMessage = errors.Count == 0 ? string.Empty : string.Join(" ", errors);
            return errors.Count == 0;
        }

        private static int ParseFlickerInt(string? text) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : -1;

        private void SetFlickerInfo(string message, Brush foreground, int? clearAfterSeconds = null)
        {
            _flickerInfoMessage = message;
            _flickerInfoForeground = foreground;
            if (_flickerInfoText == null || _flickerInfoClearTimer == null)
                return;

            _flickerInfoClearTimer.Stop();
            _flickerInfoText.Text = message;
            _flickerInfoText.Foreground = foreground;
            if (clearAfterSeconds.HasValue)
            {
                _flickerInfoClearTimer.Interval = TimeSpan.FromSeconds(clearAfterSeconds.Value);
                _flickerInfoClearTimer.Start();
            }
        }

        private void MenuEthernetConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_ethConfigWindow != null && _ethConfigWindow.IsVisible) { _ethConfigWindow.Activate(); return; }
            HiddenConfigPanel.Children.Remove(GrpEthernetConfig);
            var (wrapper, okBtn) = WrapWithOkButton(GrpEthernetConfig);
            _ethConfigWindow = new Window
            {
                Title = "Ethernet Configuration",
                Width = 500, Height = 410,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = wrapper,
                ResizeMode = ResizeMode.NoResize,
            };
            okBtn.Click += (_, _) => _ethConfigWindow.Close();
            _ethConfigWindow.Closed += (s, a) => { ((DockPanel)_ethConfigWindow.Content).Children.Clear(); HiddenConfigPanel.Children.Add(GrpEthernetConfig); _ethConfigWindow = null; };
            _ethConfigWindow.Show();
        }

        private void MenuCameraConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_cameraConfigWindow is { IsVisible: true }) { _cameraConfigWindow.Activate(); return; }
            _cameraConfigWindow = new CameraConfigWindow(AppendDiagLog, _baslerCapture);
            _cameraConfigWindow.Closed += (_, _) => _cameraConfigWindow = null;
            _cameraConfigWindow.Show();
        }

        private void MenuApiConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_apiConfigWindow is { IsVisible: true }) { _apiConfigWindow.Activate(); return; }
            _apiConfigWindow = new ApiConfigurationWindow()
            {
                OnSettingsSaved = (allowRemote, enableHttps, bindAddress, port, apiKey, cidrs) =>
                {
                    _apiAllowRemote = allowRemote;
                    _apiEnableHttps = enableHttps;
                    _apiBindAddress = bindAddress;
                    _apiPort = port;
                    _apiKey = apiKey;
                    _apiAllowedCidrs = cidrs;
                }
            };
            _apiConfigWindow.Closed += (_, _) => _apiConfigWindow = null;
            _apiConfigWindow.Show();
        }

        private void MenuOsramDefectControl_Click(object sender, RoutedEventArgs e)
        {
            InitializeOsramDefectInjection();
            EnsureOsramDefectControlWindow();

            if (_osramDefectControlWindow == null)
                return;

            if (_osramDefectControlWindow.IsVisible)
            {
                _osramDefectControlWindow.Activate();
                return;
            }

            _osramDefectControlWindow.Show();
            _osramDefectControlWindow.Activate();
        }

        private void MenuNichiaDefectControl_Click(object sender, RoutedEventArgs e)
        {
            InitializeOsramDefectInjection();
            EnsureNichiaDefectControlWindow();

            if (_nichiaDefectControlWindow == null)
                return;

            if (_nichiaDefectControlWindow.IsVisible)
            {
                _nichiaDefectControlWindow.Activate();
                return;
            }

            _nichiaDefectControlWindow.Show();
            _nichiaDefectControlWindow.Activate();
        }

        private void MenuCommunicationFaultControl_Click(object sender, RoutedEventArgs e)
        {
            if (_communicationFaultControlWindow is { IsVisible: true })
            {
                _communicationFaultControlWindow.Activate();
                return;
            }

            _communicationFaultControlWindow ??= new CommunicationFaultControlWindow(_communicationFaultState);
            _communicationFaultControlWindow.FaultStateChanged -= ApplyCommunicationFaultState;
            _communicationFaultControlWindow.FaultStateChanged += ApplyCommunicationFaultState;
            _communicationFaultControlWindow.LvdsFaultStateChanged -= ApplyLvdsFaultState;
            _communicationFaultControlWindow.LvdsFaultStateChanged += ApplyLvdsFaultState;
            _communicationFaultControlWindow.CanUartFaultStateChanged -= ApplyCanUartFaultState;
            _communicationFaultControlWindow.CanUartFaultStateChanged += ApplyCanUartFaultState;
            UpdateCommunicationFaultAvailability();
            double currentLvdsFps = _currentDeviceType == LsmDeviceType.Nichia
                ? _nichiaEthCapture?.FpsEma ?? 0.0
                : _osramEthCapture?.FpsEma ?? 0.0;
            _communicationFaultControlWindow.UpdateEcuState(currentLvdsFps);
            _communicationFaultControlWindow.Closed += (_, _) => _communicationFaultControlWindow = null;
            _communicationFaultControlWindow.Show();
            _communicationFaultControlWindow.Activate();
        }

        private void ApplyCommunicationFaultState()
        {
            var txManager = _txManager;
            if (txManager != null)
                txManager.AvtpFaultEnabled = _communicationFaultState.AvtpFaultEnabled;

                if (_communicationFaultState.AvtpFaultEnabled)
            {
                RenderAll();
                if (_playback.Cts != null)
                {
                    LblStatus.Text = "AVTP Fault Injection: Signal not available.";
                    _playback.RunningStatusText = LblStatus.Text;
                }
            }
            else if (_playback.Cts != null)
            {
                if (_modeOfOperation == ModeOfOperation.PlayerFromFiles
                    && txManager != null && !txManager.IsReady)
                {
                    ushort ethType = ParseHexUshort(_avtpEtherType, 0x22F0);
                    byte stIdByte = ParseHexByte(_streamIdLastByte, 0x50);
                    string? deviceHint = LiveNicSelector.GetSelectedDeviceName(CmbLiveNic) ?? _avtpLiveDeviceHint;
                    txManager.Initialize(deviceHint, _srcMac, _dstMac,
                        _vlanId, _vlanPriority, ethType, stIdByte);
                }

                if (_modeOfOperation == ModeOfOperation.PlayerFromFiles
                    && _playback.Cts == null)
                {
                    StartBlackKeepalive();
                }

                // Fault recovery must clear the same signal-loss latches that
                // Stop -> Start clears, without stopping the playback loop.
                lock (_frameLock)
                {
                    _latestB = null;
                    _matchedAForDiff = null;
                    _latestC = null;
                }
                // Keep LVDS latched as unavailable until the ECU sends a new
                // frame after POR or sleep-wakeup. The next LVDS callback clears
                // this latch when real traffic resumes.
                _lvdsSignalLost = true;
                _baslerSignalLost = false;
                _lastLvdsFrameUtc = DateTime.MinValue;
                _lastBaslerFrameUtc = DateTime.UtcNow;
                _baslerDispWindowFrames = 0;
                _baslerDispFps = 0;
                _baslerDispWindowStartTicks = _baslerDispFpsSw.ElapsedTicks;

                // Keep an already active LVDS receiver alive. Recreating it here
                // resets its reassembly state and FPS EMA, which causes a visible
                // 0 -> nominal-FPS restart and can disrupt the next fault cycle.
                bool lvdsCaptureActive = _currentDeviceType == LsmDeviceType.Nichia
                    ? _nichiaEthCapture?.IsCapturing == true
                    : _osramEthCapture?.IsCapturing == true;
                if (!lvdsCaptureActive)
                {
                    if (_currentDeviceType == LsmDeviceType.Nichia)
                        StartNichiaEthCapture();
                    else
                        StartOsramEthCapture();
                }
                if (_baslerCapture == null || !_baslerCapture.IsCapturing)
                    StartBaslerCapture();

                RenderAll();
                RestoreAvtpInfoAfterFault();
            }
        }

        private void ApplyCanUartFaultState()
        {
            string? txDev = GetTxPcapDeviceNameOrNull();
            if (string.IsNullOrWhiteSpace(txDev))
            {
                AppendDiagLog("[cmd] No NIC selected - CAN-UART fault command not sent");
                if (_communicationFaultState.CanUartFaultEnabled)
                {
                    _communicationFaultState.CanUartFaultEnabled = false;
                    _communicationFaultControlWindow?.ClearCanUartFaultForExternalChange(notify: false);
                }
                return;
            }

            try
            {
                bool sent = CanUartFaultCommand.Send(
                    txDev,
                    _communicationFaultState.CanUartFaultMode,
                    _communicationFaultState.CanUartFaultDirection,
                    _communicationFaultState.CanUartFaultDurationMilliseconds,
                    _communicationFaultState.CanUartMode,
                    _communicationFaultState.CanUartFaultEnabled,
                    AppendDiagLog);
                if (!sent && _communicationFaultState.CanUartFaultEnabled)
                {
                    _communicationFaultState.CanUartFaultEnabled = false;
                    _communicationFaultControlWindow?.ClearCanUartFaultForExternalChange(notify: false);
                }
            }
            catch (Exception ex)
            {
                AppendDiagLog($"[cmd] CAN-UART fault command error: {ex.Message}");
                if (_communicationFaultState.CanUartFaultEnabled)
                {
                    _communicationFaultState.CanUartFaultEnabled = false;
                    _communicationFaultControlWindow?.ClearCanUartFaultForExternalChange(notify: false);
                }
            }
        }

        private void ApplyLvdsFaultState()
        {
            string? txDev = GetTxPcapDeviceNameOrNull();
            if (string.IsNullOrWhiteSpace(txDev))
            {
                AppendDiagLog("[cmd] No NIC selected - LVDS fault command not sent");
                return;
            }

            try
            {
                bool sent = LvdsFaultCommand.Send(
                    txDev,
                    _currentDeviceType,
                    _communicationFaultState.LvdsFaultDurationMilliseconds,
                    _communicationFaultState.LvdsFaultEnabled,
                    AppendDiagLog);
                if (!sent && _communicationFaultState.LvdsFaultEnabled)
                {
                    _communicationFaultState.LvdsFaultEnabled = false;
                    _communicationFaultControlWindow?.ClearLvdsFaultForExternalChange(notify: false);
                }
            }
            catch (Exception ex)
            {
                AppendDiagLog($"[cmd] LVDS fault command error: {ex.Message}");
                if (_communicationFaultState.LvdsFaultEnabled)
                {
                    _communicationFaultState.LvdsFaultEnabled = false;
                    _communicationFaultControlWindow?.ClearLvdsFaultForExternalChange(notify: false);
                }
            }
        }

        private void UpdateCommunicationFaultAvailability()
        {
            if (_communicationFaultControlWindow == null)
                return;

            _communicationFaultControlWindow.IsAvtpFaultAvailable =
                _modeOfOperation == ModeOfOperation.PlayerFromFiles;
            _communicationFaultControlWindow.IsLvdsFaultAvailable =
                _controlMode == 0 &&
                !string.IsNullOrWhiteSpace(GetTxPcapDeviceNameOrNull());
            _communicationFaultControlWindow.IsCanUartFaultAvailable =
                !string.IsNullOrWhiteSpace(GetTxPcapDeviceNameOrNull());
            _communicationFaultControlWindow.CanUartMode = _canUartMode;
        }

        private void RestoreAvtpInfoAfterFault()
        {
            if (_communicationFaultState.AvtpFaultEnabled)
                return;

            if (_playback.Cts == null)
            {
                LblStatus.Text = _modeOfOperation == ModeOfOperation.AvtpLiveMonitor
                    ? "Mode: AVTP Monitoring. Press Start to listen/capture live stream."
                    : "Mode: AVTP Generator. Load a file and press Start.";
                return;
            }

            if (_modeOfOperation == ModeOfOperation.PlayerFromFiles)
            {
                int fps = int.TryParse(TxtFps?.Text, out int parsedFps) && parsedFps > 0 ? parsedFps : 100;
                LblStatus.Text = StatusFormatter.FormatPlayerRunning(fps, avtpEnabled: true);
                _playback.RunningStatusText = LblStatus.Text;
            }
            else if (_liveCapture.HasAvtpFrame)
            {
                LblStatus.Text = "Running AVTP Monitoring.";
                _playback.RunningStatusText = LblStatus.Text;
            }
            else
            {
                LblStatus.Text = StatusFormatter.FormatWaitingForSignal();
                _playback.RunningStatusText = LblStatus.Text;
            }
        }
    }

}
