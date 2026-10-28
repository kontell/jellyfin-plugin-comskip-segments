using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ComskipSegments.Comskip;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

/// <summary>
/// Decides which breaks from a recording in progress are worth storing when each
/// reading is a full analysis over the frames decoded so far (the periodic analysis a
/// Comskip built with <c>live_master_interval</c> writes to <c>.master.edl</c>). Every
/// such pass reports every break it currently believes in; a break is stored once it
/// has been reported in enough consecutive passes (a start that holds still). Its end
/// follows the latest pass: while the break is still on air the end grows with each
/// pass, and the stored row is updated. Withdrawal is symmetric: a stored break stays
/// until the same number of consecutive passes have not reported it, so one pass
/// changing its mind mid-break does not delete and re-create the row. Measured on a
/// full NFL broadcast with 60-second passes: every one-pass false break vanished on the
/// next pass, and real starts held to within 0.1 s from first sight.
/// </summary>
public sealed class LiveBreakTracker
{
    /// <summary>Two reports whose starts are this close are the same break.</summary>
    private const long StartToleranceTicks = (long)(1.5 * TimeSpan.TicksPerSecond);

    private readonly int _confirmPasses;
    private List<Candidate> _candidates = new();

    /// <param name="confirmPasses">Consecutive passes a break must appear in before it is stored.</param>
    public LiveBreakTracker(int confirmPasses)
    {
        _confirmPasses = Math.Max(1, confirmPasses);
    }

    /// <summary>The breaks currently worth storing, sorted by start.</summary>
    public IReadOnlyList<CommercialBreak> Confirmed { get; private set; } = Array.Empty<CommercialBreak>();

    /// <summary>How many passes have been observed.</summary>
    public int Passes { get; private set; }

    /// <summary>Feeds one pass. Returns true when <see cref="Confirmed"/> changed.</summary>
    public bool Observe(IReadOnlyList<CommercialBreak> pass)
    {
        Passes++;
        var next = new List<Candidate>(pass.Count);
        var matched = new HashSet<Candidate>();
        foreach (var b in pass.OrderBy(b => b.StartTicks))
        {
            var previous = _candidates.FirstOrDefault(c => Math.Abs(c.StartTicks - b.StartTicks) <= StartToleranceTicks);
            if (previous is not null)
            {
                matched.Add(previous);
            }

            next.Add(previous is null
                ? new Candidate(b.StartTicks, b.EndTicks, 1, 0)
                : new Candidate(b.StartTicks, b.EndTicks, previous.Seen + 1, 0));
        }

        // A confirmed break the pass stopped reporting lingers, unchanged, until it has
        // been absent for as many passes as it took to confirm.
        foreach (var c in _candidates)
        {
            if (!matched.Contains(c) && c.Seen >= _confirmPasses && c.Missing + 1 < _confirmPasses)
            {
                next.Add(c with { Missing = c.Missing + 1 });
            }
        }

        next.Sort((a, b) => a.StartTicks.CompareTo(b.StartTicks));
        _candidates = next;
        var confirmed = next
            .Where(c => c.Seen >= _confirmPasses)
            .Select(c => new CommercialBreak(c.StartTicks, c.EndTicks))
            .ToList();
        var changed = !confirmed.SequenceEqual(Confirmed);
        Confirmed = confirmed;
        return changed;
    }

    private sealed record Candidate(long StartTicks, long EndTicks, int Seen, int Missing);
}
