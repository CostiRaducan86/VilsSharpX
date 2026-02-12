using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace VilsSharpX;

/// <summary>
/// Represents a single item in a scene (image with delay).
/// </summary>
public sealed class SceneItem
{
    public required string Path { get; init; }
    public required byte[] Data { get; init; }
    public required int DelayMs { get; init; }
}

/// <summary>
/// Loads and parses scene files (.scene) that define sequences of images with timing.
/// </summary>
public static class SceneLoader
{
    /// <summary>
    /// Loads a scene file and returns the list of scene items.
    /// </summary>
    /// <param name="scenePath">Path to the .scene file.</param>
    /// <param name="cropWidth">Target crop width (e.g., 320).</param>
    /// <param name="cropHeight">Target crop height (e.g., 80).</param>
    /// <returns>Tuple of scene items list and loop flag.</returns>
    /// <exception cref="InvalidOperationException">If no valid scene items are found.</exception>
    public static (List<SceneItem> items, bool loop) Load(string scenePath, int cropWidth, int cropHeight)
    {
        string text = File.ReadAllText(scenePath);

        // 1) Prefer minimal scene format (paths + delayMs + loop)
        var (steps, simpleLoop) = ParseSimpleScene(text);
        var items = new List<SceneItem>();

        if (steps.Count > 0)
        {
            foreach (var (p, stepDelayMs) in steps)
            {
                string resolved = p;
                if (!Path.IsPathRooted(resolved))
                    resolved = Path.Combine(Path.GetDirectoryName(scenePath) ?? string.Empty, resolved);

                var img = ImageUtils.LoadImageAsGray8(resolved);
                if (img.width < cropWidth || img.height < cropHeight)
                    throw new InvalidOperationException($"Scene item '{resolved}' expected at least {cropWidth}x{cropHeight}, got {img.width}x{img.height}.");

                var cropped = ImageUtils.CropTopLeftGray8(img.data, img.width, img.height, cropWidth, cropHeight);
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
                if (!Path.IsPathRooted(resolved))
                    resolved = Path.Combine(Path.GetDirectoryName(scenePath) ?? "", resolved);

                int delayMs = defaultDelayMs;
                var mItemDelay = Regex.Match(block, @"\bdelayMs\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                if (mItemDelay.Success && int.TryParse(mItemDelay.Groups[1].Value, out var itemDelay) && itemDelay > 0)
                    delayMs = itemDelay;

                var img = ImageUtils.LoadImageAsGray8(resolved);
                if (img.width < cropWidth || img.height < cropHeight)
                    throw new InvalidOperationException($"Scene item '{resolved}' expected at least {cropWidth}x{cropHeight}, got {img.width}x{img.height}.");

                var cropped = ImageUtils.CropTopLeftGray8(img.data, img.width, img.height, cropWidth, cropHeight);
                items.Add(new SceneItem { Path = resolved, Data = cropped, DelayMs = delayMs });
            }

            if (items.Count == 0)
                throw new InvalidOperationException("No valid scene items found. Expected either: (a) one image path per line, or (b) legacy blocks containing filename=\"...\".");

            simpleLoop = loop;
        }

        return (items, simpleLoop);
    }

    /// <summary>
    /// Parses the simple scene format (one image per line with optional key=value settings).
    /// </summary>
    private static (List<(string path, int delayMs)> steps, bool loop) ParseSimpleScene(string text)
    {
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
                if (key.Equals("img", StringComparison.OrdinalIgnoreCase) || 
                    key.Equals("image", StringComparison.OrdinalIgnoreCase) || 
                    key.Equals("step", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Extracts top-level '{ ... }' blocks from legacy scene format.
    /// </summary>
    private static List<string> SplitTopLevelObjects(string text)
    {
        var blocks = new List<string>();
        int depth = 0;
        int start = -1;
        bool inString = false;
        
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
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
}
