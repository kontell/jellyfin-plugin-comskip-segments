using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ComskipSegments.AudioTrim;

/// <summary>
/// One recording being cut (or checked before a cut): a program-start trim, an audio-format
/// trim, or setting a program start. Shown by the clients as a "Cutting" animation with its
/// progress, and a Stop while that's still safe.
/// </summary>
public sealed class CutJob
{
    public const string PhaseChecking = "checking";
    public const string PhaseCutting = "cutting";
    public const string PhaseFinishing = "finishing";

    public Guid ItemId { get; init; }

    /// <summary>The show, for an episode: so a show's card can say one of its recordings is being cut.</summary>
    public Guid? SeriesId { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>"program-start" or "audio-format".</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>checking (scanning or looking for a card), cutting (ffmpeg writing the trimmed copy), finishing (putting it in place).</summary>
    public string Phase { get; set; } = PhaseChecking;

    /// <summary>Stop is offered while nothing has been touched yet: the original is only replaced while finishing.</summary>
    public bool Stoppable => Phase != PhaseFinishing;

    public long Bytes { get; set; }

    public long DoneBytes { get; set; }

    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;

    [JsonIgnore]
    internal string? TempPath { get; set; }

    [JsonIgnore]
    internal string? FinalPath { get; set; }

    [JsonIgnore]
    internal CancellationTokenSource Cancel { get; } = new();
}

/// <summary>
/// Recordings being cut right now. Also the lock: one action that may rewrite a recording at a
/// time (seen live: four clicks started four trims of the same file, each about to swap its
/// copy in over a discarded original).
/// </summary>
public sealed class CutTracker
{
    private readonly ConcurrentDictionary<Guid, CutJob> _jobs = new();

    /// <summary>Claims the recording; false (and no job) while another action has it.</summary>
    public bool TryStart(BaseItem item, string kind, out CutJob job)
    {
        job = new CutJob
        {
            ItemId = item.Id,
            SeriesId = (item as Episode)?.SeriesId,
            Name = item is Episode e && !string.IsNullOrEmpty(e.SeriesName) ? $"{e.SeriesName}: {item.Name}" : item.Name,
            Kind = kind
        };
        return _jobs.TryAdd(item.Id, job);
    }

    public bool IsBusy(Guid itemId) => _jobs.ContainsKey(itemId);

    public void Finish(CutJob job)
    {
        _jobs.TryRemove(new KeyValuePair<Guid, CutJob>(job.ItemId, job));
        job.Cancel.Dispose();
    }

    /// <summary>Asks the job to stop; false when there's none or it's past the point of stopping.</summary>
    public bool Stop(Guid itemId)
    {
        if (!_jobs.TryGetValue(itemId, out var job) || !job.Stoppable)
        {
            return false;
        }

        try
        {
            job.Cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    /// <summary>The jobs, with progress measured from the files being written.</summary>
    public IReadOnlyList<CutJob> List()
    {
        foreach (var job in _jobs.Values)
        {
            job.DoneBytes = job.Phase switch
            {
                CutJob.PhaseCutting when job.TempPath is { } t => Size(t),
                CutJob.PhaseFinishing when job.FinalPath is { } f => Size(f),
                _ => 0
            };
            if (job.Bytes > 0)
            {
                // The trimmed copy comes out somewhat smaller than estimated (ffmpeg drops a
                // capture's filler packets), so never show it as done before it is.
                job.DoneBytes = Math.Min(job.DoneBytes, (long)(job.Bytes * 0.99));
            }
        }

        return _jobs.Values.OrderBy(j => j.StartedUtc).ToList();
    }

    private static long Size(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
