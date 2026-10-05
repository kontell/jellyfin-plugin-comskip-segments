using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ComskipSegments.ProgramStart;

/// <summary>
/// Frames for program-start cards: tiny greyscale pictures from ffmpeg, compared by
/// normalized correlation. Measured on Starz recordings (rating cards of four films on four
/// Starz channels, TV-MA / R / TV-PG): a card scored 0.83–0.97 against another film's card,
/// while the closest non-card frame (promos, "NEXT" bumpers, a "STARTS IN" countdown on the
/// same teal background) scored at most 0.53.
/// </summary>
public static class CardFrames
{
    public const int Width = 96;
    public const int Height = 54;
    public const int FramesPerSecond = 2;
    private const int Size = Width * Height;

    /// <summary>One frame at <paramref name="seconds"/> into the recording, or null.</summary>
    public static async Task<byte[]?> GrabAsync(string ffmpeg, string path, double seconds, CancellationToken ct)
    {
        var frames = await DecodeAsync(ffmpeg, path, Math.Max(0, seconds), 0, ct).ConfigureAwait(false);
        return frames.Count > 0 ? frames[0] : null;
    }

    /// <summary>
    /// <see cref="FramesPerSecond"/> frames a second from <paramref name="from"/> for
    /// <paramref name="duration"/> seconds; frame i is at from + i / FramesPerSecond.
    /// </summary>
    public static Task<List<byte[]>> SampleAsync(string ffmpeg, string path, double from, double duration, CancellationToken ct) =>
        DecodeAsync(ffmpeg, path, Math.Max(0, from), duration, ct);

    private static async Task<List<byte[]>> DecodeAsync(string ffmpeg, string path, double from, double duration, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in new[] { "-v", "quiet", "-nostdin", "-ss", F(from) })
        {
            psi.ArgumentList.Add(a);
        }

        if (duration > 0)
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(F(duration));
        }

        foreach (var a in new[] { "-i", path, "-an", "-sn", "-vf", $"fps={FramesPerSecond},scale={Width}:{Height},format=gray" })
        {
            psi.ArgumentList.Add(a);
        }

        if (duration <= 0)
        {
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
        }

        foreach (var a in new[] { "-f", "rawvideo", "-" })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start ffmpeg");
        _ = process.StandardError.ReadToEndAsync(ct);
        var frames = new List<byte[]>();
        var stream = process.StandardOutput.BaseStream;
        try
        {
            while (true)
            {
                var frame = new byte[Size];
                var read = 0;
                while (read < Size)
                {
                    var n = await stream.ReadAsync(frame.AsMemory(read, Size - read), ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }

                if (read < Size)
                {
                    break;
                }

                frames.Add(frame);
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        return frames;
    }

    /// <summary>Normalized correlation of two frames: 1 = the same picture, 0 = unrelated.</summary>
    public static double Similarity(byte[] a, byte[] b)
    {
        double ma = 0, mb = 0;
        for (var i = 0; i < Size; i++)
        {
            ma += a[i];
            mb += b[i];
        }

        ma /= Size;
        mb /= Size;
        double num = 0, da = 0, db = 0;
        for (var i = 0; i < Size; i++)
        {
            var x = a[i] - ma;
            var y = b[i] - mb;
            num += x * y;
            da += x * x;
            db += y * y;
        }

        return da > 0 && db > 0 ? num / Math.Sqrt(da * db) : 0;
    }

    /// <summary>
    /// Black: the gap between a card and the program. Measured: those frames are flat (no
    /// pixel above 5 of 255), while a film's logo fading in already has a few faint ones, so
    /// "almost no pixel above 12" keeps the fade-in with the program.
    /// </summary>
    public static bool IsBlack(byte[] f)
    {
        var lit = 0;
        foreach (var v in f)
        {
            if (v > 12)
            {
                lit++;
            }
        }

        return lit < Size / 1000;
    }

    /// <summary>
    /// Where the program starts after a card that ends at frame <paramref name="cardEnd"/>
    /// (the last matching frame): past the card's fade, through the black gap, at the first
    /// picture. A card with no black after it within a few seconds ends where it ends.
    /// </summary>
    public static int StartAfterCard(IReadOnlyList<byte[]> frames, int cardEnd)
    {
        var limit = Math.Min(frames.Count, cardEnd + 1 + (8 * FramesPerSecond));
        var black = -1;
        for (var i = cardEnd + 1; i < limit; i++)
        {
            if (IsBlack(frames[i]))
            {
                black = i;
                break;
            }
        }

        if (black < 0)
        {
            return Math.Min(cardEnd + 1, frames.Count - 1);
        }

        var end = Math.Min(frames.Count, black + (10 * FramesPerSecond));
        for (var i = black; i < end; i++)
        {
            if (!IsBlack(frames[i]))
            {
                return i;
            }
        }

        return Math.Min(end, frames.Count - 1);
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
