using System;

namespace VideoStreamPlayer;

/// <summary>
/// Manages UI settings load/save operations.
/// </summary>
public sealed class UiSettingsManager
{
    private readonly string _settingsPath;
    private readonly int _maxPixelCount;
    private bool _isLoading;

    public UiSettingsManager(int width, int height)
    {
        _settingsPath = AppSettingsStore.GetSettingsPath();
        _maxPixelCount = width * height;
    }

    /// <summary>
    /// Whether settings are currently being loaded (to prevent save during load).
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => _isLoading = value;
    }

    /// <summary>
    /// Settings file path.
    /// </summary>
    public string SettingsPath => _settingsPath;

    /// <summary>
    /// Loads settings and returns the validated AppSettings.
    /// Sets IsLoading to true during load.
    /// </summary>
    public AppSettings Load()
    {
        _isLoading = true;
        try
        {
            var s = AppSettingsStore.LoadOrDefault(_settingsPath);

            // Validate and clamp values
            s.Fps = Math.Clamp(s.Fps, 1, 1000);
            s.BDelta = Math.Clamp(s.BDelta, -255, 255);
            s.Deadband = Math.Clamp(s.Deadband, 0, 255);
            s.ForcedDeadPixelId = Math.Clamp(s.ForcedDeadPixelId, 0, _maxPixelCount);

            return s;
        }
        finally
        {
            // Note: caller must set IsLoading = false after applying settings to UI
        }
    }

    /// <summary>
    /// Marks loading as complete.
    /// </summary>
    public void FinishLoading()
    {
        _isLoading = false;
    }

    /// <summary>
    /// Saves settings if not currently loading.
    /// </summary>
    public bool TrySave(AppSettings settings)
    {
        if (_isLoading) return false;

        try
        {
            AppSettingsStore.Save(settings, _settingsPath);
            return true;
        }
        catch
        {
            // Ignore I/O errors
            return false;
        }
    }

    /// <summary>
    /// Creates settings from current UI state values.
    /// </summary>
    public static AppSettings CreateFromState(
        int fps,
        int bDelta,
        byte diffThreshold,
        bool zeroZeroIsWhite,
        int forcedDeadPixelId,
        bool darkPixelCompensationEnabled,
        bool avtpLiveEnabled,
        string? avtpLiveDeviceHint,
        bool avtpLiveUdpEnabled,
        int modeOfOperation,
        string srcMac,
        string dstMac)
    {
        return new AppSettings
        {
            Fps = fps,
            BDelta = bDelta,
            Deadband = diffThreshold,
            ZeroZeroIsWhite = zeroZeroIsWhite,
            ForcedDeadPixelId = forcedDeadPixelId,
            DarkPixelCompensationEnabled = darkPixelCompensationEnabled,
            AvtpLiveEnabled = avtpLiveEnabled,
            AvtpLiveDeviceHint = avtpLiveDeviceHint,
            AvtpLiveUdpEnabled = avtpLiveUdpEnabled,
            ModeOfOperation = modeOfOperation,
            SrcMac = srcMac,
            DstMac = dstMac
        };
    }
}
