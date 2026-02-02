using System;

namespace VideoStreamPlayer;

/// <summary>
/// Provides dark pixel compensation (Cassandra-style) for ECU output simulation.
/// When a pixel in the input (A) has signal but the ECU output (B) is forced to 0 (dark pixel),
/// this compensator boosts neighboring pixels to simulate the expected behavior.
/// </summary>
public static class DarkPixelCompensation
{
    /// <summary>
    /// Neighborhood offsets and boost percentages for dark pixel compensation.
    /// Less aggressive compensation using a small cross and diagonal pattern.
    /// </summary>
    public static readonly (int dx, int dy, int pct)[] CompensationOffsets =
    [
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

    /// <summary>
    /// Applies dark pixel compensation and optional forced dead pixel to frame B.
    /// </summary>
    /// <param name="a">Input frame A.</param>
    /// <param name="b">ECU output frame B.</param>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="compensationEnabled">Whether to apply neighborhood compensation.</param>
    /// <param name="forcedDeadPixelId">1-based pixel ID to force to 0 (0 = disabled).</param>
    /// <returns>New frame with compensation applied, or original B if no changes needed.</returns>
    public static Frame ApplyBPostProcessing(Frame a, Frame b, int width, int height, bool compensationEnabled, int forcedDeadPixelId)
    {
        if (!compensationEnabled && forcedDeadPixelId <= 0) 
            return b;

        byte[] baseB;
        if (forcedDeadPixelId > 0)
        {
            baseB = new byte[b.Data.Length];
            Buffer.BlockCopy(b.Data, 0, baseB, 0, baseB.Length);

            int idx = forcedDeadPixelId - 1;
            if (idx >= 0 && idx < baseB.Length) 
                baseB[idx] = 0;
        }
        else
        {
            // No forced pixel; use the current B buffer as base
            baseB = b.Data;
        }

        if (!compensationEnabled)
        {
            // Only the forced pixel feature is active.
            return forcedDeadPixelId > 0 ? new Frame(b.Width, b.Height, baseB, b.TimestampUtc) : b;
        }

        // Apply dark pixel compensation (Cassandra-style small neighborhood)
        byte[]? outData = null;
        int n = width * height;
        
        for (int i = 0; i < n && i < a.Data.Length && i < baseB.Length; i++)
        {
            // Dark pixel: A has signal but ECU output is forced to 0
            if (a.Data[i] == 0 || baseB[i] != 0) 
                continue;

            outData ??= ImageUtils.Copy(baseB);

            int y = i / width;
            int x = i - (y * width);
            
            foreach (var (dx, dy, pct) in CompensationOffsets)
            {
                int nx = x + dx;
                int ny = y + dy;
                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) 
                    continue;

                int ni = (ny * width) + nx;
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
            return forcedDeadPixelId > 0 ? new Frame(b.Width, b.Height, baseB, b.TimestampUtc) : b;
        }

        return new Frame(b.Width, b.Height, outData, b.TimestampUtc);
    }
}
