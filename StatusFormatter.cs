using System;
using System.Globalization;

namespace VilsSharpX;

/// <summary>
/// Helper for formatting status labels and FPS info.
/// </summary>
public static class StatusFormatter
{
    /// <summary>
    /// Formats the running info label for pane A.
    /// </summary>
    public static string FormatRunInfoA(bool isRunning, bool isPaused, double shownFps, bool isAviWithZeroFps)
    {
        if (!isRunning) return "";
        if (isPaused) return "Paused";

        if (isAviWithZeroFps)
            return "Running";

        return $"Running @: {shownFps:F1} fps";
    }

    /// <summary>
    /// Formats the running info label for pane B.
    /// </summary>
    public static string FormatRunInfoB(bool isRunning, bool isPaused, bool noSignal, double bFpsEma)
    {
        if (!isRunning) return "";
        if (isPaused) return "Paused";

        if (noSignal)
            return $"Running @: {0.0:F1} fps";

        if (bFpsEma <= 0.0)
            return "Running";

        return $"Running @: {bFpsEma:F1} fps";
    }

    /// <summary>
    /// Formats the AVTP dropped stats string.
    /// </summary>
    public static string FormatAvtpDropped(int dropped, int gapFrames, int incomplete, int sumGaps)
    {
        return $"{dropped} (gapFrames={gapFrames}, incomplete={incomplete}, gaps={sumGaps})";
    }

    /// <summary>
    /// Formats the diff stats label.
    /// </summary>
    public static string FormatDiffStats(int maxDiff, int minDiff, double meanAbsDiff, int aboveDeadband, int totalDarkPixels)
    {
        return $"COMPARE (LVDS\u2212AVTP): max_positive_dev={Math.Max(0, maxDiff)}  max_negative_dev={Math.Min(0, minDiff)}  average_pixels_dev={meanAbsDiff:F0}  total_pixels_dev={aboveDeadband}  total_dark_pixels={totalDarkPixels}";
    }

    /// <summary>
    /// Updates the status text with lateSkip counter.
    /// </summary>
    public static string UpdateLateSkipInStatus(string currentStatus, int lateSkip)
    {
        if (lateSkip <= 0)
            return currentStatus;

        const string tag = "lateSkip=";
        string s = currentStatus ?? string.Empty;
        int idx = s.IndexOf(tag, StringComparison.Ordinal);

        if (idx >= 0)
        {
            int start = idx + tag.Length;
            int end = start;
            while (end < s.Length && char.IsDigit(s[end])) end++;
            return s.Substring(0, start)
                   + lateSkip.ToString(CultureInfo.InvariantCulture)
                   + s.Substring(end);
        }

        return string.IsNullOrWhiteSpace(s)
            ? $"lateSkip={lateSkip}"
            : $"{s} | lateSkip={lateSkip}";
    }

    /// <summary>
    /// Formats waiting for signal status message.
    /// </summary>
    public static string FormatWaitingForSignal(string logPath)
    {
        return $"Waiting for signal... (0.0 fps) (Mode=AVTP Live). Ethernet/AVTP capture best-effort. (log: {logPath})";
    }

    /// <summary>
    /// Formats the AVTP RVF status line (live capture stats).
    /// </summary>
    public static string FormatAvtpRvfStatus(
        string src, uint frameId, uint seq, int linesWritten, int expectedHeight, int seqGaps,
        int dropped, int gapFrames, int incomplete, int lateSkip)
    {
        string late = lateSkip > 0 ? $" | lateSkip={lateSkip}" : "";
        return $"AVTP RVF ({src}): frameId={frameId} seq={seq} lines={linesWritten}/{expectedHeight} gaps={seqGaps} | dropped={dropped} (gapFrames={gapFrames}, incomplete={incomplete}){late}";
    }

    /// <summary>
    /// Formats the stopped status message.
    /// </summary>
    public static string FormatStoppedStatus(string lastSrcLabel)
    {
        return $"AVTP RVF ({lastSrcLabel}): Stopped.";
    }

    /// <summary>
    /// Formats the player running status.
    /// </summary>
    public static string FormatPlayerRunning(int fps, bool avtpEnabled)
    {
        return avtpEnabled
            ? $"Running Player @ {fps} fps (AVTP TX enabled)"
            : $"Running Player @ {fps} fps";
    }
}
