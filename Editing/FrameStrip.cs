using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.AudioTrim;

namespace Jellyfin.Plugin.ComskipSegments.Editing;

/// <summary>One decoded frame: its time (seconds from the start of the file), whether it's a keyframe, the picture.</summary>
public sealed record StripFrame(double T, bool Key, byte[] Jpeg);

/// <summary>
/// A run of consecutive frames from one moment on, decoded in one go, for frame-by-frame
/// stepping in the dashboard's editor: one request per few seconds instead of one per frame.
/// Starts the decode a little before the keyframe ahead of the moment (so every frame decodes
/// whole), keeps the file's own timestamps, and lets ffmpeg drop the frames before the moment;
/// each picture's time comes from ffmpeg's showinfo, so it's exactly that frame's.
/// </summary>
public static partial class FrameStrip
{
    /// <remarks>
    /// <paramref name="every"/>: only every so many frames (for playing faster than normal: fewer
    /// pictures, the same stretch of time). <paramref name="vaapiDevice"/>: decode on that GPU,
    /// several times faster than the CPU; null, or when it fails, the CPU decodes.
    /// </remarks>
    public static async Task<IReadOnlyList<StripFrame>> DecodeAsync(
        string ffmpegPath, string ffprobePath, string path, double from, int count, int width, CancellationToken ct, int every = 1, string? vaapiDevice = null)
    {
        var (start, _) = await AudioChannelScanner.ProbeFormatTimes(ffprobePath, path, ct).ConfigureAwait(false);
        var keys = await Keyframes.AroundAsync(ffprobePath, path, from + 3, 12, ct).ConfigureAwait(false);
        var keyBefore = keys.Where(k => k <= from + 0.002).Cast<double?>().LastOrDefault();
        var seek = Math.Max(0, (keyBefore ?? from - 10) - 0.5);

        if (vaapiDevice is not null)
        {
            var onGpu = await RunDecodeAsync(ffmpegPath, path, start, seek, from, keys, count, width, every, vaapiDevice, ct).ConfigureAwait(false);
            if (onGpu.Count > 0)
            {
                return onGpu;
            }
        }

        return await RunDecodeAsync(ffmpegPath, path, start, seek, from, keys, count, width, every, null, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<StripFrame>> RunDecodeAsync(
        string ffmpegPath, string path, double start, double seek, double from, IReadOnlyList<double> keys, int count, int width, int every, string? vaapiDevice, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var fromAbs = (start + from - 0.0005).ToString("F4", CultureInfo.InvariantCulture);
        width = Math.Clamp(width, 120, 1280);
        var pick = $"select='gte(t\\,{fromAbs})'" + (every > 1 ? $",select='not(mod(n\\,{every}))'" : string.Empty);
        var args = new List<string> { "-hide_banner", "-nostdin" };
        if (vaapiDevice is not null)
        {
            args.AddRange(new[] { "-hwaccel", "vaapi", "-hwaccel_device", vaapiDevice, "-hwaccel_output_format", "vaapi" });
        }

        args.AddRange(new[]
        {
            "-copyts",
            "-ss", seek.ToString("F3", CultureInfo.InvariantCulture), "-i", path,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", vaapiDevice is not null
                ? $"{pick},scale_vaapi=w={width}:h=-2:format=nv12,hwdownload,format=nv12,showinfo"
                : $"{pick},showinfo,scale={width}:-2",
            "-fps_mode", "passthrough", "-frames:v", Math.Clamp(count, 1, 300).ToString(CultureInfo.InvariantCulture),
            "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "5", "pipe:1"
        });
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start ffmpeg");
        var errTask = process.StandardError.ReadToEndAsync(ct);
        using var jpegs = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(jpegs, ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var err = await errTask.ConfigureAwait(false);

        var times = PtsTime().Matches(err)
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - start)
            .ToList();
        var pictures = Split(jpegs.ToArray());
        var frames = new List<StripFrame>(Math.Min(times.Count, pictures.Count));
        for (var i = 0; i < Math.Min(times.Count, pictures.Count); i++)
        {
            var t = Math.Round(times[i], 4);
            frames.Add(new StripFrame(t, keys.Any(k => Math.Abs(k - t) < 0.002), pictures[i]));
        }

        return frames;
    }

    /// <summary>A run of black frames: from its first black frame to the first frame after it.</summary>
    public sealed record BlackRun(double Start, double End);

    /// <summary>
    /// The nearest run of black frames after (or before) <paramref name="from"/>, searched a minute
    /// at a time up to <paramref name="maxSeconds"/> away; null when there's none. Breaks usually
    /// start and end on one, which is how Comskip finds them.
    /// </summary>
    /// <remarks>
    /// <paramref name="vaapiDevice"/>: decode on that GPU (the one Jellyfin transcodes with), about
    /// twice as fast as the CPU with almost no CPU at all; null, or when it fails, the CPU decodes
    /// with a few threads so the server stays responsive. The two can differ by a frame at the edge
    /// of a very dark stretch.
    /// </remarks>
    public static async Task<BlackRun?> FindBlackAsync(
        string ffmpegPath, string ffprobePath, string path, double from, bool forward, double maxSeconds, string? vaapiDevice, CancellationToken ct)
    {
        const double Chunk = 60;
        var (start, duration) = await AudioChannelScanner.ProbeFormatTimes(ffprobePath, path, ct).ConfigureAwait(false);
        for (var done = 0.0; done < maxSeconds; done += Chunk)
        {
            var a = forward ? from + done : from - done - Chunk;
            var b = a + Chunk + (forward ? 2 : 0);
            a = Math.Max(0, a);
            b = Math.Min(duration > 0 ? duration : b, b);
            if (b <= a)
            {
                return null;
            }

            var runs = vaapiDevice is null ? null : await BlackRunsAsync(ffmpegPath, path, start, a, b - a, vaapiDevice, ct).ConfigureAwait(false);
            if (runs is null)
            {
                vaapiDevice = null;
                runs = await BlackRunsAsync(ffmpegPath, path, start, a, b - a, null, ct).ConfigureAwait(false) ?? new List<BlackRun>();
            }

            var hit = forward
                ? runs.FirstOrDefault(r => r.Start > from + 0.02)
                : runs.LastOrDefault(r => r.Start < from - 0.02);
            if (hit is not null)
            {
                return hit;
            }

            if ((forward && b >= duration && duration > 0) || (!forward && a <= 0))
            {
                return null;
            }
        }

        return null;
    }

    // The black runs in a stretch; null when ffmpeg failed (a GPU that can't decode this file).
    private static async Task<List<BlackRun>?> BlackRunsAsync(string ffmpegPath, string path, double start, double from, double length, string? vaapiDevice, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var args = new List<string> { "-hide_banner", "-nostdin" };
        if (vaapiDevice is not null)
        {
            args.AddRange(new[] { "-hwaccel", "vaapi", "-hwaccel_device", vaapiDevice, "-hwaccel_output_format", "vaapi" });
        }
        else
        {
            args.AddRange(new[] { "-threads", "4" });
        }

        args.AddRange(new[]
        {
            "-copyts", "-ss", from.ToString("F3", CultureInfo.InvariantCulture), "-t", length.ToString("F3", CultureInfo.InvariantCulture), "-i", path,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            // Small is plenty to tell black.
            "-vf", (vaapiDevice is not null ? "scale_vaapi=w=192:h=108:format=nv12,hwdownload,format=nv12," : "scale=192:-2,")
                + "blackdetect=d=0.01:pix_th=0.10:pic_th=0.98",
            "-f", "null", "-"
        });
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start ffmpeg");
        _ = process.StandardOutput.ReadToEndAsync(ct);
        var err = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return null;
        }

        return BlackLine().Matches(err)
            .Select(m => new BlackRun(
                Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - start, 4),
                Math.Round(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - start, 4)))
            .ToList();
    }

    [GeneratedRegex(@"black_start:(-?[0-9.]+)\s+black_end:(-?[0-9.]+)")]
    private static partial Regex BlackLine();

    // showinfo's line per frame: "... n:   3 pts: 1234 pts_time:119.951767 ...".
    [GeneratedRegex(@"\bn:\s*\d+\s+pts:\s*-?\d+\s+pts_time:(-?[0-9.]+)")]
    private static partial Regex PtsTime();

    // The MJPEG pipe is JPEGs back to back: each starts with FF D8 FF.
    private static List<byte[]> Split(byte[] data)
    {
        var starts = new List<int>();
        for (var i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD8 && data[i + 2] == 0xFF)
            {
                starts.Add(i);
            }
        }

        var result = new List<byte[]>(starts.Count);
        for (var s = 0; s < starts.Count; s++)
        {
            var end = s + 1 < starts.Count ? starts[s + 1] : data.Length;
            result.Add(data[starts[s]..end]);
        }

        return result;
    }
}
