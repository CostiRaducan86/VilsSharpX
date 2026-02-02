using System;
using System.Collections.Generic;
using System.IO;

namespace VideoStreamPlayer;

/// <summary>
/// Loads PGM (Portable GrayMap) images in P2 (ASCII) and P5 (binary) formats.
/// </summary>
public static class PgmLoader
{
    /// <summary>
    /// Loads a PGM file and returns the image data.
    /// </summary>
    /// <param name="path">Path to the PGM file.</param>
    /// <returns>A <see cref="PgmImage"/> containing width, height, and pixel data.</returns>
    /// <exception cref="InvalidDataException">If the file format is unsupported.</exception>
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
            // ASCII format
            for (int i = 0; i < data.Length; i++)
                data[i] = byte.Parse(tokens[idx++]);
        }
        else if (magic == "P5")
        {
            // Binary format (simple scanner: find start of pixel bytes by counting newlines)
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
