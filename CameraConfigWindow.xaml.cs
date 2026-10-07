using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Basler.Pylon;
using Microsoft.Win32;

#pragma warning disable CS8602 // _camera is guaranteed non-null after Connect

namespace VilsSharpX;

public partial class CameraConfigWindow : Window
{
    private Camera? _camera;
    private bool _isGrabbing;
    private WriteableBitmap? _previewBitmap;
    private readonly Action<string> _log;
    private string? _loadedPfsPath;
    private PfsConfigParser? _loadedPfs;
    private BaslerCameraCapture? _sharedCapture; // non-null = camera already open from MainWindow
    private readonly Func<Task>? _releaseSharedCapture; // asks MainWindow to close its camera
    private readonly Func<Task<BaslerCameraCapture?>>? _reopenSharedCapture; // asks MainWindow to reopen it

    // Zoom state
    private bool _isFitMode = true;
    private const double ZoomMin = 0.1;
    private const double ZoomMax = 10.0;
    private const double ZoomStep = 1.15;

    // Drag-pan state
    private bool _isDragging;
    private Point _dragStart;
    private double _dragStartOffsetH;
    private double _dragStartOffsetV;

    // FPS tracking
    private readonly Stopwatch _fpsSw = Stopwatch.StartNew();
    private long _lastFrameTicks;
    private double _fpsEma;
    private long _frameCount;

    public CameraConfigWindow(Action<string> log, BaslerCameraCapture? sharedCapture = null,
        Func<Task>? releaseSharedCapture = null,
        Func<Task<BaslerCameraCapture?>>? reopenSharedCapture = null)
    {
        InitializeComponent();
        _log = log;
        _releaseSharedCapture = releaseSharedCapture;
        _reopenSharedCapture = reopenSharedCapture;
        PopulateComboDefaults();
        PreviewKeyDown += Window_PreviewKeyDown;

        // If the camera is already open from MainWindow, enter shared mode
        // (even when its grab is stopped: the device is exclusive-access).
        if (sharedCapture?.InternalCamera != null)
        {
            _sharedCapture = sharedCapture;
            Loaded += (_, _) => EnterSharedMode();
        }
    }

    /// <summary>
    /// Shared mode: camera is already open via BaslerCameraCapture (MainWindow).
    /// We borrow its Camera object for parameter reading — no new Open() needed.
    /// Connect/Play/Stop are disabled; user controls capture from MainWindow.
    /// Preview is fed by subscribing to the shared capture's OnFrameReady.
    /// </summary>
    private void EnterSharedMode()
    {
        _camera = _sharedCapture!.InternalCamera;
        if (_camera == null)
        {
            _log("[camcfg] Shared camera not available");
            return;
        }

        BtnConnectToggle.IsChecked = true;
        BtnConnectToggle.IsEnabled = _releaseSharedCapture != null;

        string model = _camera.CameraInfo[CameraInfoKey.ModelName] ?? "?";
        string serial = _camera.CameraInfo[CameraInfoKey.SerialNumber] ?? "?";
        TxtStatus.Text = $"Connected (external): {model} ({serial})";
        _log($"[camcfg] Shared mode: {model} (S/N {serial})");

        _isGrabbing = _sharedCapture.IsCapturing;

        // Read params in background to avoid blocking the UI
        Task.Run(() =>
        {
            try
            {
                // Read while camera is grabbing — some params may be unavailable
                Dispatcher.BeginInvoke(ReadParametersFromCamera);
            }
            catch { /* ignore */ }
        });

        // Subscribe to shared capture for live preview
        _sharedCapture.OnFrameReady += OnSharedFrameReady;
        TxtNoPreview.Visibility = Visibility.Collapsed;

        UpdateButtonStates();
        SetGrabbingParamsEnabled(!_isGrabbing); // can't change AOI while grabbing
    }

    /// <summary>
    /// Hands the shared camera back from MainWindow so this window can own it
    /// (Disconnect, then Connect opens it locally with full parameter control).
    /// </summary>
    private async Task ReleaseSharedModeAsync()
    {
        if (_sharedCapture == null || _releaseSharedCapture == null) return;

        _sharedCapture.OnFrameReady -= OnSharedFrameReady;
        _sharedCapture = null;
        _camera = null;
        _isGrabbing = false;
        BtnConnectToggle.IsEnabled = false;
        TxtStatus.Text = "Disconnecting...";
        UpdateButtonStates();

        try
        {
            await _releaseSharedCapture();
        }
        catch (Exception ex)
        {
            _log($"[camcfg] Release shared camera error: {ex.Message}");
        }

        TxtStatus.Text = "Disconnected";
        TxtFps.Text = "--";
        BtnConnectToggle.IsEnabled = true;
        SetGrabbingParamsEnabled(true);
        UpdateButtonStates();
        _log("[camcfg] Shared camera released by MainWindow");
    }

    /// <summary>Enter in the parameter panel writes the edited values to the camera.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _camera == null) return;
        if (e.OriginalSource is not (TextBox or ComboBox or ComboBoxItem or CheckBox)) return;

        if (_isGrabbing)
            ApplyLiveParameters();
        else
            ApplyParametersToCamera();
        ReadParametersFromCamera();
        _log($"[camcfg] Parameters applied (grabbing={_isGrabbing}), ExposureTimeRaw={TbExposureTime.Text}");
        e.Handled = true;
    }

    private void OnSharedFrameReady(byte[] frame, int w, int h)
    {
        // This fires on Pylon's grab thread — marshal to UI
        Dispatcher.BeginInvoke(() => RenderPreview(frame, w, h));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Toolbar handlers
    // ═══════════════════════════════════════════════════════════════

    private async void BtnConnectToggle_Checked(object sender, RoutedEventArgs e)
    {
        // In shared mode, connection is already established via EnterSharedMode()
        if (_sharedCapture != null) return;
        if (_reopenSharedCapture != null)
        {
            // Pane C and this window must use the same pylon device (exclusive access).
            await ReconnectSharedModeAsync();
            return;
        }
        ConnectCamera();
        if (_camera == null)
            BtnConnectToggle.IsChecked = false; // revert if failed
    }

    private async Task ReconnectSharedModeAsync()
    {
        BtnConnectToggle.IsEnabled = false;
        TxtStatus.Text = "Connecting...";

        BaslerCameraCapture? capture = null;
        try
        {
            capture = await _reopenSharedCapture!();
        }
        catch (Exception ex)
        {
            _log($"[camcfg] Reopen shared camera error: {ex.Message}");
        }

        BtnConnectToggle.IsEnabled = true;
        if (capture?.InternalCamera == null)
        {
            TxtStatus.Text = "Connect failed (see diagnostic.log)";
            BtnConnectToggle.IsChecked = false;
            return;
        }

        _sharedCapture = capture;
        EnterSharedMode();
    }

    private async void BtnConnectToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_sharedCapture != null)
        {
            await ReleaseSharedModeAsync();
            return;
        }
        DisconnectCamera();
    }
    private void BtnContinuousShot_Click(object sender, RoutedEventArgs e)
    {
        if (_sharedCapture != null)
        {
            ApplyParametersToCamera();
            _sharedCapture.StartGrab();
            _isGrabbing = true;
            SetGrabbingParamsEnabled(false);
            UpdateButtonStates();
            return;
        }
        StartGrabbing();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (_sharedCapture != null)
        {
            _sharedCapture.StopGrab();
            _isGrabbing = false;
            SetGrabbingParamsEnabled(true);
            UpdateButtonStates();
            return;
        }
        StopGrabbing();
    }
    private void BtnZoomFit_Click(object sender, RoutedEventArgs e) => ResetZoomToFit();
    private void BtnLoadPfs_Click(object sender, RoutedEventArgs e) => LoadPfsFile();
    private void BtnSavePfs_Click(object sender, RoutedEventArgs e) => SavePfsFile();
    private void BtnUserSetLoad_Click(object sender, RoutedEventArgs e) => UserSetLoad();
    private void BtnUserSetSave_Click(object sender, RoutedEventArgs e) => UserSetSave();
    private void BtnOk_Click(object sender, RoutedEventArgs e) => Close();

    // ═══════════════════════════════════════════════════════════════
    //  Zoom: Ctrl+Scroll = zoom, Ctrl+DoubleClick = fit, Ctrl+Drag = pan
    // ═══════════════════════════════════════════════════════════════

    private void PreviewHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;

        double factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
        double newScale = PreviewScale.ScaleX * factor;
        newScale = Math.Clamp(newScale, ZoomMin, ZoomMax);

        PreviewScale.ScaleX = newScale;
        PreviewScale.ScaleY = newScale;
        SwitchToZoomMode();
    }

    private void PreviewHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Ctrl+DoubleClick → reset to fit
        if (e.ClickCount == 2 && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            ResetZoomToFit();
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _isFitMode) return;
        _isDragging = true;
        _dragStart = e.GetPosition(PreviewScroller);
        _dragStartOffsetH = PreviewScroller.HorizontalOffset;
        _dragStartOffsetV = PreviewScroller.VerticalOffset;
        PreviewHost.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewHost_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        PreviewHost.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void PreviewHost_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(PreviewScroller);
        double dx = _dragStart.X - pos.X;
        double dy = _dragStart.Y - pos.Y;
        PreviewScroller.ScrollToHorizontalOffset(_dragStartOffsetH + dx);
        PreviewScroller.ScrollToVerticalOffset(_dragStartOffsetV + dy);
        e.Handled = true;
    }

    private void SwitchToZoomMode()
    {
        if (!_isFitMode) return;
        _isFitMode = false;
        // Copy bitmap to zoom image, show scroller
        ImgPreviewZoom.Source = _previewBitmap;
        ImgPreviewFit.Visibility = Visibility.Collapsed;
        PreviewScroller.Visibility = Visibility.Visible;
    }

    private void ResetZoomToFit()
    {
        PreviewScale.ScaleX = 1;
        PreviewScale.ScaleY = 1;
        _isFitMode = true;
        _isDragging = false;
        // Show fit image, hide scroller
        ImgPreviewFit.Source = _previewBitmap;
        ImgPreviewFit.Visibility = Visibility.Visible;
        PreviewScroller.Visibility = Visibility.Collapsed;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Camera connection
    // ═══════════════════════════════════════════════════════════════

    private void ConnectCamera()
    {
        try
        {
            if (_camera != null) DisconnectCamera();

            _camera = new Camera();
            _camera.Open();

            string model = _camera.CameraInfo[CameraInfoKey.ModelName] ?? "?";
            string serial = _camera.CameraInfo[CameraInfoKey.SerialNumber] ?? "?";
            _log($"[camcfg] Connected: {model} (S/N {serial})");
            TxtStatus.Text = $"Connected: {model} ({serial})";

            ReadParametersFromCamera();
            UpdateButtonStates();
        }
        catch (Exception ex)
        {
            _log($"[camcfg] Connect failed: {ex.Message}");
            MessageBox.Show($"Failed to connect camera:\n{ex.Message}", "Camera Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _camera?.Dispose();
            _camera = null;
        }
    }

    private void DisconnectCamera()
    {
        if (_camera == null) return;
        try
        {
            StopGrabbing();
            _camera.Close();
            _camera.Dispose();
        }
        catch (Exception ex)
        {
            _log($"[camcfg] Disconnect error: {ex.Message}");
        }
        _camera = null;
        TxtStatus.Text = "Disconnected";
        _log("[camcfg] Camera disconnected");
        UpdateButtonStates();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Grabbing (preview)
    // ═══════════════════════════════════════════════════════════════

    private void StartGrabbing()
    {
        if (_camera == null || _isGrabbing) return;
        try
        {
            ApplyParametersToCamera();
            _camera.StreamGrabber.ImageGrabbed += OnImageGrabbed;
            _camera.StreamGrabber.Start(GrabStrategy.LatestImages, GrabLoop.ProvidedByStreamGrabber);
            _isGrabbing = true;
            _frameCount = 0;
            _lastFrameTicks = _fpsSw.ElapsedTicks;
            TxtNoPreview.Visibility = Visibility.Collapsed;
            _log("[camcfg] Continuous grab started");
            UpdateButtonStates();
            SetGrabbingParamsEnabled(false);
        }
        catch (Exception ex)
        {
            _log($"[camcfg] Start grab error: {ex.Message}");
            MessageBox.Show($"Failed to start grabbing:\n{ex.Message}", "Grab Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopGrabbing()
    {
        if (_camera == null || !_isGrabbing) return;
        try
        {
            _camera.StreamGrabber.Stop();
            _camera.StreamGrabber.ImageGrabbed -= OnImageGrabbed;
        }
        catch { /* ignore */ }
        _isGrabbing = false;
        _log("[camcfg] Grab stopped");
        UpdateButtonStates();
        SetGrabbingParamsEnabled(true);
    }

    private void OnImageGrabbed(object? sender, ImageGrabbedEventArgs e)
    {
        try
        {
            IGrabResult gr = e.GrabResult;
            if (!gr.GrabSucceeded) return;

            int w = gr.Width, h = gr.Height;
            if (gr.PixelData is not byte[] src || src.Length < w * h) return;

            var frame = new byte[w * h];
            Buffer.BlockCopy(src, 0, frame, 0, frame.Length);

            // FPS
            long now = _fpsSw.ElapsedTicks;
            double dt = (now - _lastFrameTicks) / (double)Stopwatch.Frequency;
            _lastFrameTicks = now;
            if (dt > 0.0001)
            {
                double instant = 1.0 / dt;
                _fpsEma = _frameCount == 0 ? instant : _fpsEma * 0.95 + instant * 0.05;
            }
            _frameCount++;

            Dispatcher.BeginInvoke(() => RenderPreview(frame, w, h));
        }
        catch { /* ignore */ }
    }

    private void RenderPreview(byte[] frame, int w, int h)
    {
        if (_previewBitmap == null || _previewBitmap.PixelWidth != w || _previewBitmap.PixelHeight != h)
        {
            _previewBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Gray8, null);
            // Assign to whichever mode is active
            if (_isFitMode)
                ImgPreviewFit.Source = _previewBitmap;
            else
                ImgPreviewZoom.Source = _previewBitmap;
        }
        _previewBitmap.WritePixels(new Int32Rect(0, 0, w, h), frame, w, 0);
        TxtResolution.Text = $"{w} × {h}";
        double fps = _sharedCapture != null ? _sharedCapture.FpsEma : _fpsEma;
        TxtFps.Text = _isGrabbing ? $"{fps:F1} fps" : "--";
    }

    // ═══════════════════════════════════════════════════════════════
    //  .pfs file handling
    // ═══════════════════════════════════════════════════════════════

    private void LoadPfsFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Load Pylon Feature Stream",
            Filter = "Pylon Feature Stream (*.pfs)|*.pfs|All Files (*.*)|*.*",
            InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs", "inputs"),
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            _loadedPfs = PfsConfigParser.Load(dlg.FileName);
            _loadedPfsPath = dlg.FileName;
            TxtPfsFile.Text = Path.GetFileName(dlg.FileName);
            PopulateUiFromPfs(_loadedPfs);
            _log($"[camcfg] Loaded .pfs: {dlg.FileName}");

            if (_camera != null && !_isGrabbing)
                ApplyParametersToCamera();
        }
        catch (Exception ex)
        {
            _log($"[camcfg] Load .pfs error: {ex.Message}");
            MessageBox.Show($"Failed to load .pfs file:\n{ex.Message}", "Load Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SavePfsFile()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Pylon Feature Stream",
            Filter = "Pylon Feature Stream (*.pfs)|*.pfs",
            FileName = _loadedPfsPath != null ? Path.GetFileName(_loadedPfsPath) : "CameraConfig.pfs",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var pfs = _loadedPfs ?? new PfsConfigParser();
            MergeUiIntoPfs(pfs);
            pfs.Save(dlg.FileName);
            _loadedPfsPath = dlg.FileName;
            TxtPfsFile.Text = Path.GetFileName(dlg.FileName);
            _log($"[camcfg] Saved .pfs: {dlg.FileName}");
        }
        catch (Exception ex)
        {
            _log($"[camcfg] Save .pfs error: {ex.Message}");
            MessageBox.Show($"Failed to save .pfs file:\n{ex.Message}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  User Set commands
    // ═══════════════════════════════════════════════════════════════

    private void UserSetLoad()
    {
        if (_camera == null || _isGrabbing) return;
        try
        {
            var sel = CbUserSetSelector.SelectedItem?.ToString();
            if (sel != null)
                _camera.Parameters[PLCamera.UserSetSelector].TrySetValue(sel);
            _camera.Parameters[PLCamera.UserSetLoad].Execute();
            ReadParametersFromCamera();
            _log($"[camcfg] UserSetLoad executed ({sel})");
        }
        catch (Exception ex)
        {
            _log($"[camcfg] UserSetLoad error: {ex.Message}");
        }
    }

    private void UserSetSave()
    {
        if (_camera == null || _isGrabbing) return;
        try
        {
            var sel = CbUserSetSelector.SelectedItem?.ToString();
            if (sel != null)
                _camera.Parameters[PLCamera.UserSetSelector].TrySetValue(sel);
            ApplyParametersToCamera();
            _camera.Parameters[PLCamera.UserSetSave].Execute();
            _log($"[camcfg] UserSetSave executed ({sel})");
        }
        catch (Exception ex)
        {
            _log($"[camcfg] UserSetSave error: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Read parameters FROM camera → UI
    // ═══════════════════════════════════════════════════════════════

    private void ReadParametersFromCamera()
    {
        if (_camera == null) return;
        var p = _camera.Parameters;

        // Analog
        TrySetCombo(CbGainAuto, p[PLCamera.GainAuto]);
        TrySetText(TbGainRaw, p[PLCamera.GainRaw]);
        TrySetCheck(ChkGammaEnable, p[PLCamera.GammaEnable]);
        TrySetText(TbGamma, p[PLCamera.Gamma]);
        TrySetText(TbDigitalShift, p[PLCamera.DigitalShift]);

        // Image format
        TrySetCombo(CbPixelFormat, p[PLCamera.PixelFormat]);
        TrySetCheck(ChkReverseX, p[PLCamera.ReverseX]);
        TrySetCheck(ChkReverseY, p[PLCamera.ReverseY]);

        // AOI
        TrySetText(TbWidth, p[PLCamera.Width]);
        TrySetText(TbHeight, p[PLCamera.Height]);
        TrySetText(TbOffsetX, p[PLCamera.OffsetX]);
        TrySetText(TbOffsetY, p[PLCamera.OffsetY]);

        // Acquisition
        TrySetCombo(CbTriggerSelector, p[PLCamera.TriggerSelector]);
        TrySetCombo(CbTriggerMode, p[PLCamera.TriggerMode]);
        TrySetCombo(CbTriggerSource, p[PLCamera.TriggerSource]);
        TrySetCombo(CbTriggerActivation, p[PLCamera.TriggerActivation]);
        TrySetText(TbTriggerDelay, p[PLCamera.TriggerDelayAbs]);
        TrySetCombo(CbExposureMode, p[PLCamera.ExposureMode]);
        TrySetCombo(CbExposureAuto, p[PLCamera.ExposureAuto]);
        TrySetText(TbExposureTime, p[PLCamera.ExposureTimeRaw]);
        TrySetCombo(CbShutterMode, p[PLCamera.ShutterMode]);

        // Digital I/O
        TrySetCombo(CbLineSelector, p[PLCamera.LineSelector]);
        TrySetCombo(CbLineMode, p[PLCamera.LineMode]);
        TrySetCombo(CbLineFormat, p[PLCamera.LineFormat]);
        TrySetCheck(ChkLineInverter, p[PLCamera.LineInverter]);
        TrySetTextByName(TbLineDebouncerTime, "LineDebouncerTimeRaw");

        // Config sets
        TrySetCombo(CbUserSetSelector, p[PLCamera.UserSetSelector]);
        TrySetCombo(CbDefaultStartupSet, p[PLCamera.UserSetDefaultSelector]);

        TxtResolution.Text = $"{TbWidth.Text} × {TbHeight.Text}";
    }

    // ═══════════════════════════════════════════════════════════════
    //  Apply parameters FROM UI → camera
    // ═══════════════════════════════════════════════════════════════

    private void ApplyParametersToCamera()
    {
        if (_camera == null) return;
        var p = _camera.Parameters;

        // Image format first
        TryApplyCombo(p[PLCamera.PixelFormat], CbPixelFormat);
        TryApplyCheck(p[PLCamera.ReverseX], ChkReverseX);
        TryApplyCheck(p[PLCamera.ReverseY], ChkReverseY);

        // AOI — reset offsets first to avoid range clamp
        p[PLCamera.OffsetX].TrySetValue(0);
        p[PLCamera.OffsetY].TrySetValue(0);
        TryApplyLong(p[PLCamera.Width], TbWidth);
        TryApplyLong(p[PLCamera.Height], TbHeight);
        TryApplyLong(p[PLCamera.OffsetX], TbOffsetX);
        TryApplyLong(p[PLCamera.OffsetY], TbOffsetY);

        // Analog
        TryApplyCombo(p[PLCamera.GainAuto], CbGainAuto);
        TryApplyLong(p[PLCamera.GainRaw], TbGainRaw);
        TryApplyCheck(p[PLCamera.GammaEnable], ChkGammaEnable);
        TryApplyDouble(p[PLCamera.Gamma], TbGamma);
        TryApplyLong(p[PLCamera.DigitalShift], TbDigitalShift);

        // Acquisition
        TryApplyCombo(p[PLCamera.TriggerSelector], CbTriggerSelector);
        TryApplyCombo(p[PLCamera.TriggerMode], CbTriggerMode);
        TryApplyCombo(p[PLCamera.TriggerSource], CbTriggerSource);
        TryApplyCombo(p[PLCamera.TriggerActivation], CbTriggerActivation);
        TryApplyDouble(p[PLCamera.TriggerDelayAbs], TbTriggerDelay);
        TryApplyCombo(p[PLCamera.ExposureMode], CbExposureMode);
        TryApplyCombo(p[PLCamera.ExposureAuto], CbExposureAuto);
        TryApplyLong(p[PLCamera.ExposureTimeRaw], TbExposureTime);
        TryApplyCombo(p[PLCamera.ShutterMode], CbShutterMode);

        // Digital I/O
        TryApplyCombo(p[PLCamera.LineSelector], CbLineSelector);
        TryApplyCombo(p[PLCamera.LineMode], CbLineMode);
        TryApplyCombo(p[PLCamera.LineFormat], CbLineFormat);
        TryApplyCheck(p[PLCamera.LineInverter], ChkLineInverter);
        TryApplyLongByName("LineDebouncerTimeRaw", TbLineDebouncerTime);

        // Config sets
        TryApplyCombo(p[PLCamera.UserSetSelector], CbUserSetSelector);
        TryApplyCombo(p[PLCamera.UserSetDefaultSelector], CbDefaultStartupSet);
    }

    /// <summary>Applies only the features the camera accepts while grabbing.</summary>
    private void ApplyLiveParameters()
    {
        if (_camera == null) return;
        var p = _camera.Parameters;

        TryApplyLong(p[PLCamera.OffsetX], TbOffsetX);
        TryApplyLong(p[PLCamera.OffsetY], TbOffsetY);
        TryApplyCombo(p[PLCamera.GainAuto], CbGainAuto);
        TryApplyLong(p[PLCamera.GainRaw], TbGainRaw);
        TryApplyCheck(p[PLCamera.GammaEnable], ChkGammaEnable);
        TryApplyDouble(p[PLCamera.Gamma], TbGamma);
        TryApplyLong(p[PLCamera.DigitalShift], TbDigitalShift);
        TryApplyDouble(p[PLCamera.TriggerDelayAbs], TbTriggerDelay);
        TryApplyCombo(p[PLCamera.ExposureAuto], CbExposureAuto);
        TryApplyLong(p[PLCamera.ExposureTimeRaw], TbExposureTime);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Populate UI from .pfs (no camera needed)
    // ═══════════════════════════════════════════════════════════════

    private void PopulateUiFromPfs(PfsConfigParser pfs)
    {
        // Analog
        SetComboFromPfs(CbGainAuto, pfs.Get("GainAuto"));
        SetTextFromPfs(TbGainRaw, pfs.Get("GainRaw"));
        SetCheckFromPfs(ChkGammaEnable, pfs.Get("GammaEnable"));
        SetTextFromPfs(TbGamma, pfs.Get("Gamma"));
        SetTextFromPfs(TbDigitalShift, pfs.Get("DigitalShift"));

        // Image format
        SetComboFromPfs(CbPixelFormat, pfs.Get("PixelFormat"));
        SetCheckFromPfs(ChkReverseX, pfs.Get("ReverseX"));
        SetCheckFromPfs(ChkReverseY, pfs.Get("ReverseY"));

        // AOI
        SetTextFromPfs(TbWidth, pfs.Get("Width"));
        SetTextFromPfs(TbHeight, pfs.Get("Height"));
        SetTextFromPfs(TbOffsetX, pfs.Get("OffsetX"));
        SetTextFromPfs(TbOffsetY, pfs.Get("OffsetY"));

        // Acquisition
        SetComboFromPfs(CbTriggerSelector, pfs.Get("TriggerSelector"));
        SetComboFromPfs(CbTriggerMode, pfs.Get("TriggerMode", "{TriggerSelector=FrameStart}") ?? pfs.Get("TriggerMode"));
        SetComboFromPfs(CbTriggerSource, pfs.Get("TriggerSource", "{TriggerSelector=FrameStart}") ?? pfs.Get("TriggerSource"));
        SetComboFromPfs(CbTriggerActivation, pfs.Get("TriggerActivation", "{TriggerSelector=FrameStart}") ?? pfs.Get("TriggerActivation"));
        SetTextFromPfs(TbTriggerDelay, pfs.Get("TriggerDelayAbs", "{TriggerSelector=FrameStart}") ?? pfs.Get("TriggerDelayAbs"));
        SetComboFromPfs(CbExposureMode, pfs.Get("ExposureMode"));
        SetComboFromPfs(CbExposureAuto, pfs.Get("ExposureAuto"));
        SetTextFromPfs(TbExposureTime, pfs.Get("ExposureTimeRaw"));
        SetComboFromPfs(CbShutterMode, pfs.Get("ShutterMode"));

        // Digital I/O
        SetComboFromPfs(CbLineSelector, pfs.Get("LineSelector"));
        SetComboFromPfs(CbLineMode, pfs.Get("LineMode", "{LineSelector=Line1}") ?? pfs.Get("LineMode"));
        SetComboFromPfs(CbLineFormat, pfs.Get("LineFormat", "{LineSelector=Line1}") ?? pfs.Get("LineFormat"));
        SetCheckFromPfs(ChkLineInverter, pfs.Get("LineInverter", "{LineSelector=Line1}") ?? pfs.Get("LineInverter"));
        SetTextFromPfs(TbLineDebouncerTime, pfs.Get("LineDebouncerTimeRaw", "{LineSelector=Line1}") ?? pfs.Get("LineDebouncerTimeRaw"));

        TxtResolution.Text = $"{TbWidth.Text} × {TbHeight.Text}";
    }

    private void MergeUiIntoPfs(PfsConfigParser pfs)
    {
        pfs.Set("GainAuto", ComboText(CbGainAuto));
        pfs.Set("GainRaw", TbGainRaw.Text);
        pfs.Set("GammaEnable", ChkGammaEnable.IsChecked == true ? "1" : "0");
        pfs.Set("Gamma", TbGamma.Text);
        pfs.Set("DigitalShift", TbDigitalShift.Text);

        pfs.Set("PixelFormat", ComboText(CbPixelFormat));
        pfs.Set("ReverseX", ChkReverseX.IsChecked == true ? "1" : "0");
        pfs.Set("ReverseY", ChkReverseY.IsChecked == true ? "1" : "0");

        pfs.Set("Width", TbWidth.Text);
        pfs.Set("Height", TbHeight.Text);
        pfs.Set("OffsetX", TbOffsetX.Text);
        pfs.Set("OffsetY", TbOffsetY.Text);

        pfs.Set("TriggerSelector", ComboText(CbTriggerSelector));
        pfs.Set("TriggerMode", "{TriggerSelector=FrameStart}", ComboText(CbTriggerMode));
        pfs.Set("TriggerSource", "{TriggerSelector=FrameStart}", ComboText(CbTriggerSource));
        pfs.Set("TriggerActivation", "{TriggerSelector=FrameStart}", ComboText(CbTriggerActivation));
        pfs.Set("TriggerDelayAbs", "{TriggerSelector=FrameStart}", TbTriggerDelay.Text);
        pfs.Set("ExposureMode", ComboText(CbExposureMode));
        pfs.Set("ExposureAuto", ComboText(CbExposureAuto));
        pfs.Set("ExposureTimeRaw", TbExposureTime.Text);
        pfs.Set("ShutterMode", ComboText(CbShutterMode));

        pfs.Set("LineSelector", ComboText(CbLineSelector));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Combo box defaults
    // ═══════════════════════════════════════════════════════════════

    private void PopulateComboDefaults()
    {
        CbGainAuto.ItemsSource = new[] { "Off", "Once", "Continuous" };
        CbGainAuto.SelectedIndex = 0;

        CbPixelFormat.ItemsSource = new[] { "Mono8", "Mono12", "Mono12Packed" };
        CbPixelFormat.SelectedIndex = 0;

        CbTriggerSelector.ItemsSource = new[] { "FrameStart", "AcquisitionStart" };
        CbTriggerSelector.SelectedIndex = 0;

        CbTriggerMode.ItemsSource = new[] { "On", "Off" };
        CbTriggerMode.SelectedIndex = 0;

        CbTriggerSource.ItemsSource = new[] { "Line1", "Line2", "Line3", "Line4", "Software" };
        CbTriggerSource.SelectedIndex = 2;

        CbTriggerActivation.ItemsSource = new[] { "RisingEdge", "FallingEdge", "AnyEdge", "LevelHigh", "LevelLow" };
        CbTriggerActivation.SelectedIndex = 0;

        CbExposureMode.ItemsSource = new[] { "Timed", "TriggerWidth" };
        CbExposureMode.SelectedIndex = 0;

        CbExposureAuto.ItemsSource = new[] { "Off", "Once", "Continuous" };
        CbExposureAuto.SelectedIndex = 0;

        CbShutterMode.ItemsSource = new[] { "Global", "Rolling", "GlobalResetRelease" };
        CbShutterMode.SelectedIndex = 0;

        CbLineSelector.ItemsSource = new[] { "Line1", "Line2", "Line3", "Line4" };
        CbLineSelector.SelectedIndex = 0;

        CbLineMode.ItemsSource = new[] { "Input", "Output" };
        CbLineMode.SelectedIndex = 0;

        CbLineFormat.ItemsSource = new[] { "OptoCoupled", "TTL", "LVDS", "RS422" };
        CbLineFormat.SelectedIndex = 0;

        CbUserSetSelector.ItemsSource = new[] { "Default", "UserSet1", "UserSet2", "UserSet3" };
        CbUserSetSelector.SelectedIndex = 2;

        CbDefaultStartupSet.ItemsSource = new[] { "Default", "UserSet1", "UserSet2", "UserSet3" };
        CbDefaultStartupSet.SelectedIndex = 2;

        // Default text values
        TbGainRaw.Text = "0";
        TbGamma.Text = "1.0";
        TbDigitalShift.Text = "0";
        TbWidth.Text = "1720";
        TbHeight.Text = "440";
        TbOffsetX.Text = "214";
        TbOffsetY.Text = "480";
        TbTriggerDelay.Text = "0.0";
        TbExposureTime.Text = "19000";
        TbLineDebouncerTime.Text = "0";
    }

    // ═══════════════════════════════════════════════════════════════
    //  UI state management
    // ═══════════════════════════════════════════════════════════════

    private void UpdateButtonStates()
    {
        bool connected = _camera != null;
        bool grabbing = _isGrabbing;
        bool isSharedMode = _sharedCapture != null;
        bool canCalibrate = connected || (isSharedMode && _sharedCapture!.IsCapturing);

        // Play/Stop: enabled in both local and shared mode
        BtnContinuousShot.IsEnabled = connected && !grabbing;
        BtnStop.IsEnabled = connected && grabbing;
        BtnUserSetLoad.IsEnabled = connected && !grabbing;
        BtnUserSetSave.IsEnabled = connected && !grabbing;
        BtnAutoCalibAoi.IsEnabled = canCalibrate;
        BtnResetAoi.IsEnabled = canCalibrate;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Auto-Calibration AOI
    // ═══════════════════════════════════════════════════════════════

    private async void BtnAutoCalibAoi_Click(object sender, RoutedEventArgs e)
    {
        // Prefer shared capture (MainWindow's live camera) for calibration
        if (_sharedCapture != null && _sharedCapture.IsCapturing)
        {
            BtnAutoCalibAoi.IsEnabled = false;
            BtnResetAoi.IsEnabled = false;
            TxtStatus.Text = "Auto-calibrating...";

            var result = await Task.Run(() => _sharedCapture.RunAutoCalibration());

            ShowCalibrationResult(result);
            // Refresh displayed AOI parameters
            if (_camera != null) ReadParametersFromCamera();
            TxtStatus.Text = _camera != null
                ? $"Connected (external): {_camera.CameraInfo[CameraInfoKey.ModelName]} ({_camera.CameraInfo[CameraInfoKey.SerialNumber]})"
                : "Disconnected";
            UpdateButtonStates();
            return;
        }

        // Fallback: use local camera connection
        if (_camera == null)
        {
            MessageBox.Show("Camera is not connected. Connect the camera or start capture from the main window first.",
                "Auto-Calibrate", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Must stop grabbing to change AOI
        bool wasGrabbing = _isGrabbing;
        if (wasGrabbing) StopGrabbing();

        // Reset AOI to full sensor, grab one frame, detect, apply
        BaslerAutoCalibration.ResetAoi(_camera, _log);

        try
        {
            _camera.StreamGrabber.Start(1, GrabStrategy.OneByOne, GrabLoop.ProvidedByUser);
            var grabResult = _camera.StreamGrabber.RetrieveResult(5000, TimeoutHandling.Return);
            if (grabResult == null || !grabResult.GrabSucceeded)
            {
                _log("[camcfg-cal] Failed to grab calibration frame");
                MessageBox.Show("Failed to grab a frame for calibration.", "Auto-Calibrate",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                if (wasGrabbing) StartGrabbing();
                return;
            }

            int w = grabResult.Width, h = grabResult.Height;
            if (grabResult.PixelData is not byte[] pixels || pixels.Length < w * h)
            {
                grabResult.Dispose();
                if (wasGrabbing) StartGrabbing();
                return;
            }
            var frame = new byte[w * h];
            Buffer.BlockCopy(pixels, 0, frame, 0, frame.Length);
            grabResult.Dispose();

            var cal = new BaslerAutoCalibration();
            var result = cal.DetectMatrixRegion(frame, w, h);
            if (result != null)
                BaslerAutoCalibration.ApplyToCamera(_camera, result, _log);

            ShowCalibrationResult(result);
            ReadParametersFromCamera();
        }
        catch (Exception ex)
        {
            _log($"[camcfg-cal] Error: {ex.Message}");
            MessageBox.Show($"Calibration error: {ex.Message}", "Auto-Calibrate",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (wasGrabbing) StartGrabbing();
    }

    private async void BtnResetAoi_Click(object sender, RoutedEventArgs e)
    {
        // Prefer shared capture
        if (_sharedCapture != null && _sharedCapture.IsCapturing)
        {
            BtnAutoCalibAoi.IsEnabled = false;
            BtnResetAoi.IsEnabled = false;
            TxtStatus.Text = "Resetting AOI...";

            await Task.Run(() => _sharedCapture.ResetCalibration());

            _log("[camcfg-cal] AOI reset via shared capture");
            if (_camera != null) ReadParametersFromCamera();
            TxtStatus.Text = _camera != null
                ? $"Connected (external): {_camera.CameraInfo[CameraInfoKey.ModelName]} ({_camera.CameraInfo[CameraInfoKey.SerialNumber]})"
                : "Disconnected";
            UpdateButtonStates();
            return;
        }

        if (_camera == null) return;

        bool wasGrabbing = _isGrabbing;
        if (wasGrabbing) StopGrabbing();

        BaslerAutoCalibration.ResetAoi(_camera, _log);
        ReadParametersFromCamera();

        if (wasGrabbing) StartGrabbing();
    }

    private static void ShowCalibrationResult(BaslerAutoCalibration.CalibrationResult? result)
    {
        if (result != null)
        {
            MessageBox.Show($"Auto-calibration succeeded.\n\n" +
                $"Detected LED matrix:\n" +
                $"  Offset: ({result.OffsetX}, {result.OffsetY})\n" +
                $"  Size: {result.Width} × {result.Height}\n" +
                $"  Threshold: {result.ThresholdUsed}\n" +
                $"  Bright pixels: {result.BrightPixelCount}",
                "Auto-Calibrate", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("No LED matrix detected.\n\nMake sure a bright pattern (e.g. all-white) " +
                "is displayed on the LSM and the camera can see it clearly.",
                "Auto-Calibrate", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Disables parameters that cannot be changed while grabbing is active.
    /// AOI (Width, Height, OffsetX, OffsetY), PixelFormat, TriggerMode/Source/Activation,
    /// and ReverseX/Y require the camera to be stopped.
    /// </summary>
    private void SetGrabbingParamsEnabled(bool enabled)
    {
        // AOI size requires stop; OffsetX/Y can be changed live
        TbWidth.IsEnabled = enabled;
        TbHeight.IsEnabled = enabled;

        // Image format controls that require stop
        CbPixelFormat.IsEnabled = enabled;
        ChkReverseX.IsEnabled = enabled;
        ChkReverseY.IsEnabled = enabled;

        // Trigger configuration requires stop
        CbTriggerSelector.IsEnabled = enabled;
        CbTriggerMode.IsEnabled = enabled;
        CbTriggerSource.IsEnabled = enabled;
        CbTriggerActivation.IsEnabled = enabled;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Unsubscribe from shared capture frames
        if (_sharedCapture != null)
            _sharedCapture.OnFrameReady -= OnSharedFrameReady;

        // Only disconnect if we own the camera (not shared mode)
        if (_sharedCapture == null)
            DisconnectCamera();
        else
            _camera = null; // release reference without closing

        base.OnClosed(e);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers — read from camera parameter → UI control
    // ═══════════════════════════════════════════════════════════════

    private static void TrySetCombo(ComboBox cb, IParameter param)
    {
        try { var v = param.ToString(); if (v != null) SetComboFromPfs(cb, v); }
        catch { /* parameter not available */ }
    }

    private static void TrySetText(TextBox tb, IParameter param)
    {
        try { tb.Text = param.ToString() ?? ""; }
        catch { /* parameter not available */ }
    }

    private static void TrySetCheck(CheckBox chk, IParameter param)
    {
        try
        {
            var v = param.ToString();
            chk.IsChecked = v == "1" || v?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch { /* parameter not available */ }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers — UI control → camera parameter
    // ═══════════════════════════════════════════════════════════════

    private void TrySetTextByName(TextBox tb, string paramName)
    {
        try { tb.Text = _camera!.Parameters[paramName].ToString() ?? ""; }
        catch { /* parameter not available */ }
    }

    private void TryApplyLongByName(string paramName, TextBox tb)
    {
        try
        {
            if (long.TryParse(tb.Text.Trim(), out long v))
                _camera!.Parameters[paramName].ParseAndSetValue(v.ToString());
        }
        catch { /* ignore */ }
    }

    private static void TryApplyCombo(IParameter param, ComboBox cb)
    {
        try
        {
            var val = cb.SelectedItem?.ToString();
            if (val != null) param.ParseAndSetValue(val);
        }
        catch { /* ignore if not writable/available */ }
    }

    private static void TryApplyLong(IParameter param, TextBox tb)
    {
        try
        {
            if (long.TryParse(tb.Text.Trim(), out long v))
                param.ParseAndSetValue(v.ToString());
        }
        catch { /* ignore */ }
    }

    private static void TryApplyDouble(IParameter param, TextBox tb)
    {
        try
        {
            if (double.TryParse(tb.Text.Trim(), CultureInfo.InvariantCulture, out double v))
                param.ParseAndSetValue(v.ToString(CultureInfo.InvariantCulture));
        }
        catch { /* ignore */ }
    }

    private static void TryApplyCheck(IParameter param, CheckBox chk)
    {
        try { param.ParseAndSetValue(chk.IsChecked == true ? "true" : "false"); }
        catch { /* ignore */ }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers — .pfs value → UI control
    // ═══════════════════════════════════════════════════════════════

    private static void SetComboFromPfs(ComboBox cb, string? value)
    {
        if (value == null) return;
        for (int i = 0; i < cb.Items.Count; i++)
        {
            if (string.Equals(cb.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                cb.SelectedIndex = i;
                return;
            }
        }
        cb.Items.Add(value);
        cb.SelectedIndex = cb.Items.Count - 1;
    }

    private static void SetTextFromPfs(TextBox tb, string? value)
    {
        if (value != null) tb.Text = value;
    }

    private static void SetCheckFromPfs(CheckBox chk, string? value)
    {
        if (value != null)
            chk.IsChecked = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string ComboText(ComboBox cb) => cb.SelectedItem?.ToString() ?? "";
}
