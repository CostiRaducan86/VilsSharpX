namespace VilsSharpX;

/// <summary>
/// Represents supported LSM (Lighting System Module) device types with their respective resolutions.
/// </summary>
public enum LsmDeviceType
{
    /// <summary>
    /// Osram 2.0 - 320x80 resolution
    /// </summary>
    Osram20,

    /// <summary>
    /// Osram 2.05 - 320x80 resolution
    /// </summary>
    Osram205,

    /// <summary>
    /// Nichia - 256x64 resolution
    /// </summary>
    Nichia
}

/// <summary>
/// Helper class to manage LSM device type configurations.
/// </summary>
public static class LsmDeviceTypeExtensions
{
    /// <summary>
    /// Gets the display name for the device type.
    /// </summary>
    public static string GetDisplayName(this LsmDeviceType deviceType)
    {
        return deviceType switch
        {
            LsmDeviceType.Osram20 => "Osram 2.0",
            LsmDeviceType.Osram205 => "Osram 2.05",
            LsmDeviceType.Nichia => "Nichia",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Gets the active frame width for the device type.
    /// </summary>
    public static int GetActiveWidth(this LsmDeviceType deviceType)
    {
        return deviceType switch
        {
            LsmDeviceType.Osram20 => 320,
            LsmDeviceType.Osram205 => 320,
            LsmDeviceType.Nichia => 256,
            _ => 320
        };
    }

    /// <summary>
    /// Gets the active frame height for the device type.
    /// </summary>
    public static int GetActiveHeight(this LsmDeviceType deviceType)
    {
        return deviceType switch
        {
            LsmDeviceType.Osram20 => 80,
            LsmDeviceType.Osram205 => 80,
            LsmDeviceType.Nichia => 64,
            _ => 80
        };
    }
}
