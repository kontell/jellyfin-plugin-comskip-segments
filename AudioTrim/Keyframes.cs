using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ComskipSegments.AudioTrim;

/// <summary>
/// Where a recording's keyframes are. A cut copies the video as it is, so it can only start on
/// one: started anywhere else, the frames up to the next keyframe can't be decoded and the
/// picture only appears there, seconds after the sound (broadcast recordings here have one every
/// 4.27 s). Cuts snap to a keyframe, and the editors offer the ones either side of a chosen
/// moment to pick from.
/// </summary>
public static class Keyframes
{
    /// <summary>A cut is started this much before its keyframe, so rounding can't miss it.</summary>
    public const double Margin = 0.01;

    /// <summary>
    /// The video keyframes within <paramref name="window"/> seconds of <paramref name="seconds"/>
    /// (from the start of the file), in order; empty when they can't be read.
    /// </summary>
    public static async Task<IReadOnlyList<double>> AroundAsync(
        string ffprobePath, string path, double seconds, double window, CancellationToken ct)
    {
        var (start, _) = await AudioChannelScanner.ProbeFormatTimes(ffprobePath, path, ct).ConfigureAwait(false);
        var from = start + Math.Max(0, seconds - window);
        var to = start + seconds + window;
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in new[]
        {
            "-v", "error", "-select_streams", "v:0",
            // Absolute stream times: -read_intervals doesn't take off the file's start time.
            "-read_intervals", string.Create(CultureInfo.InvariantCulture, $"{from:F3}%{to:F3}"),
            "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return Array.Empty<double>();
        }

        _ = process.StandardError.ReadToEndAsync(ct);
        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var found = new SortedSet<double>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length >= 2 && parts[1].StartsWith('K')
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
                && t >= from && t <= to)
            {
                found.Add(Math.Round(t - start, 3));
            }
        }

        return new List<double>(found);
    }

    /// <summary>
    /// The keyframe a cut asked to start at <paramref name="seconds"/> really starts on: the last
    /// one at or before it (a little more is kept, never less), or null when none is near.
    /// </summary>
    public static async Task<double?> AtOrBeforeAsync(string ffprobePath, string path, double seconds, CancellationToken ct)
    {
        foreach (var window in new[] { 20.0, 120.0 })
        {
            var keys = await AroundAsync(ffprobePath, path, seconds, window, ct).ConfigureAwait(false);
            for (var i = keys.Count - 1; i >= 0; i--)
            {
                // A keyframe's own time, as the editors send it back, counts as "at".
                if (keys[i] <= seconds + 0.02)
                {
                    return keys[i];
                }
            }
        }

        return null;
    }
}
