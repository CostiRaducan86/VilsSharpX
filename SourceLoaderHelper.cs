using System;

namespace VilsSharpX;

/// <summary>
/// Helper for loading various source files (Image, PCAP, AVI, Scene).
/// Reduces MainWindow complexity by centralizing source loading logic.
/// </summary>
public sealed class SourceLoaderHelper
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _lvdsHeight;

    public SourceLoaderHelper(int width, int height, int lvdsHeight)
    {
        _width = width;
        _height = height;
        _lvdsHeight = lvdsHeight;
    }

    /// <summary>
    /// Result of loading an image file.
    /// </summary>
    public class ImageLoadResult
    {
        public required byte[] Frame { get; init; }
        public byte[]? LvdsFrame { get; init; }
        public string StatusMessage { get; init; } = string.Empty;
    }

    /// <summary>
    /// Loads a single image file (PGM, BMP, PNG) and crops to expected dimensions.
    /// </summary>
    public ImageLoadResult LoadImage(string path)
    {
        var (width, height, data) = ImageUtils.LoadImageAsGray8(path);

        if (width < _width || height < _height)
            throw new InvalidOperationException($"Expected at least {_width}x{_height}, got {width}x{height}.");

        // Always crop A to WxH from top-left
        var frame = ImageUtils.CropTopLeftGray8(data, width, height, _width, _height);

        // Keep optional LVDS buffer (also top-left) for future usage
        byte[]? lvdsFrame = height >= _lvdsHeight 
            ? ImageUtils.CropTopLeftGray8(data, width, height, _width, _lvdsHeight) 
            : null;

        return new ImageLoadResult
        {
            Frame = frame,
            LvdsFrame = lvdsFrame,
            StatusMessage = "Image loaded (PGM Gray8 or BMP/PNG→Gray8 u8). Press Start to begin rendering."
        };
    }

    /// <summary>
    /// Loads a sequence image for A or B channel.
    /// </summary>
    public byte[] LoadSequenceImage(string path)
    {
        var (width, height, data) = ImageUtils.LoadImageAsGray8(path);

        if (width < _width || height < _height)
            throw new InvalidOperationException($"Expected at least {_width}x{_height}, got {width}x{height}.");

        return ImageUtils.CropTopLeftGray8(data, width, height, _width, _height);
    }

    /// <summary>
    /// Validates that an image meets minimum dimension requirements.
    /// </summary>
    public bool ValidateImageDimensions(int width, int height, out string error)
    {
        if (width < _width || height < _height)
        {
            error = $"Expected at least {_width}x{_height}, got {width}x{height}.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Gets PCAP loaded status message.
    /// </summary>
    public static string GetPcapStatusMessage() => "PCAP loaded. Press Start to begin replay.";
}
