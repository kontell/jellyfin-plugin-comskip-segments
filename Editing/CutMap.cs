using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ComskipSegments.Editing;

/// <summary>One stretch of the recording a commercial cut keeps, in seconds of the uncut file.</summary>
public readonly record struct KeptPart(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>
/// What a commercial cut keeps of a recording, and where a moment of the uncut file ends up in
/// the cut one (and back, for Undo): resume positions and program-start markers move with it.
/// </summary>
public sealed class CutMap
{
    // A sliver shorter than this between two breaks, or at either end, isn't worth a part.
    private const double MinPart = 1.0;

    public CutMap(IReadOnlyList<KeptPart> parts) => Parts = parts;

    public IReadOnlyList<KeptPart> Parts { get; }

    public double KeptSeconds => Parts.Sum(p => p.Length);

    /// <summary>
    /// The parts between <paramref name="breaks"/> (start, end in seconds, any order, may overlap)
    /// of a recording <paramref name="duration"/> seconds long.
    /// </summary>
    public static CutMap Between(IEnumerable<(double Start, double End)> breaks, double duration)
    {
        var parts = new List<KeptPart>();
        var at = 0.0;
        foreach (var (start, end) in breaks.Where(b => b.End > b.Start).OrderBy(b => b.Start))
        {
            var s = Math.Clamp(start, 0, duration);
            if (s - at >= MinPart)
            {
                parts.Add(new KeptPart(at, s));
            }

            at = Math.Max(at, Math.Clamp(end, 0, duration));
        }

        if (duration - at >= MinPart)
        {
            parts.Add(new KeptPart(at, duration));
        }

        return new CutMap(parts);
    }

    /// <summary>
    /// Where <paramref name="seconds"/> of the uncut file plays in the cut one. A moment inside a
    /// break goes to where the show carries on after it; past the end, to the end.
    /// </summary>
    public double Forward(double seconds)
    {
        var before = 0.0;
        foreach (var p in Parts)
        {
            if (seconds < p.Start)
            {
                return before;
            }

            if (seconds <= p.End)
            {
                return before + (seconds - p.Start);
            }

            before += p.Length;
        }

        return before;
    }

    /// <summary>Where <paramref name="seconds"/> of the cut file was in the uncut one.</summary>
    public double Back(double seconds)
    {
        var before = 0.0;
        foreach (var p in Parts)
        {
            if (seconds <= before + p.Length)
            {
                return p.Start + Math.Max(0, seconds - before);
            }

            before += p.Length;
        }

        return Parts.Count > 0 ? Parts[^1].End : seconds;
    }
}
