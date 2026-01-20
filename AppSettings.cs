using System;
using System.IO;
using System.Text.Json;

namespace VideoStreamPlayer;

public sealed class AppSettings
{
    public const int CurrentSettingsVersion = 4;

    public int SettingsVersion { get; set; } = CurrentSettingsVersion;

    public int Fps { get; set; } = 100;
    public int BDelta { get; set; } = 0;
    public int Deadband { get; set; } = 0;
    public bool ZeroZeroIsWhite { get; set; } = false;

    // Dark pixel tools
    public int ForcedDeadPixelId { get; set; } = 0; // 0 disables
    public bool DarkPixelCompensationEnabled { get; set; } = false;

    // Live AVTP capture (Ethernet via Npcap/SharpPcap). When enabled, the app will attempt
    // to sniff ethertype 0x22F0 and reassemble RVF frames.
    public bool AvtpLiveEnabled { get; set; } = true;
    public string? AvtpLiveDeviceHint { get; set; } = null;

    // Optional: also listen for RVFU over UDP in AVTP Live mode.
    // UI toggle was removed; keep it configurable via settings.json.
    public bool AvtpLiveUdpEnabled { get; set; } = true;

    // 0 = player (files/pcap/avi/scene), 1 = live AVTP monitor
    public int ModeOfOperation { get; set; } = 1;
}

public static class AppSettingsStore
{
    private const string FileName = "settings.json";

    public static string GetSettingsPath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoStreamPlayer");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, FileName);
    }

    public static AppSettings LoadOrDefault(string? path = null)
    {
        path ??= GetSettingsPath();
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

            bool migrated = false;

            if (settings.SettingsVersion < 2)
            {
                // Migration: enforce current defaults.
                settings.ZeroZeroIsWhite = false;
                settings.DarkPixelCompensationEnabled = false;
                settings.SettingsVersion = 2;
                migrated = true;
            }

            if (settings.SettingsVersion < 3)
            {
                settings.SettingsVersion = 3;
                migrated = true;
            }

            if (settings.SettingsVersion < 4)
            {
                // Migration: CANoe gateway settings were removed.
                settings.SettingsVersion = 4;
                migrated = true;
            }

            if (settings.SettingsVersion < AppSettings.CurrentSettingsVersion)
            {
                settings.SettingsVersion = AppSettings.CurrentSettingsVersion;
                migrated = true;
            }

            if (migrated)
            {
                try { Save(settings, path); } catch { /* ignore */ }
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= GetSettingsPath();
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
