using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using Jellyfin.Plugin.ComskipSegments.Providers;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

/// <summary>
/// Stores the breaks a live Comskip scan has found so far as Jellyfin Commercial
/// segments while the recording is still being written, so a client that reads
/// Jellyfin's own segments (Kodi through pvr.kofin, for one) can skip them while
/// catching up on a programme still being recorded, not only the web and TV forks
/// that ask <c>/ComskipSegments/Live</c>.
///
/// Two things Jellyfin does get in the way, and both are handled here rather than
/// assumed away:
///
/// 1. A DVR recording has no library item until something asks the library to look
///    at its path (the recordings folder has no realtime monitor, and Jellyfin's own
///    refresh at completion is normally the first it gets). A targeted
///    <see cref="IProviderManager.QueueRefresh"/> on the owning library, with the
///    recording's path in RefreshPaths, makes the item appear within a scan or two;
///    it is asked for at most once a minute, and only once there is a break to store.
/// 2. Jellyfin deletes an item's segments whenever a refresh notices the file changed
///    ("File changed, pruning extracted data"), which a growing recording is by
///    definition. So every poll reconciles the rows against the current breaks instead
///    of remembering what it wrote: rows that vanished come back, rows whose end moved
///    are replaced (the store has create and delete only), rows no break accounts for
///    go.
///
/// When the Comskip in use writes the periodic full analysis (see
/// <see cref="LiveScanCoordinator"/>), each new pass goes through a
/// <see cref="LiveBreakTracker"/> first: a break is stored once two consecutive passes
/// report it, its end follows the latest pass, and it is withdrawn only after two passes
/// without it, which drops the one-pass false breaks a full analysis of a growing file
/// produces; breaks under 20 s are left to the final pass, which applies the ini's own
/// minimum. The live list, which only ever adds, is stored as it is.
///
/// The rows carry this plugin's provider id, so the publish that follows the harvest
/// (<see cref="DetectionManager"/> re-running the providers with overwrite) replaces
/// them in one go rather than leaving duplicates behind. Players must re-fetch an
/// item's segments rather than cache them by segment id for the same reason.
/// </summary>
public sealed class LiveSegmentPublisher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RefreshRequestInterval = TimeSpan.FromSeconds(60);

    /// <summary>Consecutive periodic passes a break must appear in before it is stored.</summary>
    private const int ConfirmPasses = 2;

    /// <summary>Shortest break the periodic analysis gets stored for; see <see cref="LiveBreakTracker"/>.</summary>
    private const double MinPeriodicBreakSeconds = 20;

    /// <summary>A row whose start is this close to a break's is that break's row.</summary>
    private const long StartToleranceTicks = (long)(1.5 * TimeSpan.TicksPerSecond);

    /// <summary>An end (or start) that moved by more than this gets the row replaced.</summary>
    private const long EndToleranceTicks = (long)(0.5 * TimeSpan.TicksPerSecond);

    private readonly ILogger<LiveSegmentPublisher> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly LiveScanCoordinator _coordinator;

    // Per recording path: when a library refresh was last asked for, so the request is
    // not repeated every poll while the item takes its time to appear.
    private readonly ConcurrentDictionary<string, DateTime> _refreshRequested = new(StringComparer.Ordinal);

    // Per recording path, while its live job writes the periodic analysis: the tracker
    // the passes feed, and when the pass it last saw was written.
    private readonly ConcurrentDictionary<string, Analysis> _analyses = new(StringComparer.Ordinal);

    public LiveSegmentPublisher(
        ILogger<LiveSegmentPublisher> logger,
        IServiceProvider serviceProvider,
        LiveScanCoordinator coordinator)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _coordinator = coordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                if (cfg is not null && cfg.PublishLiveSegments)
                {
                    await PollOnceAsync(cfg, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live segment publish failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(PluginConfiguration cfg, CancellationToken cancellationToken)
    {
        var paths = _coordinator.TrackedPaths();
        foreach (var gone in _refreshRequested.Keys.Concat(_analyses.Keys).Distinct().Where(p => !paths.Contains(p, StringComparer.Ordinal)).ToList())
        {
            _refreshRequested.TryRemove(gone, out _);
            _analyses.TryRemove(gone, out _);
        }

        if (paths.Count == 0)
        {
            return;
        }

        // Resolved here, not in the constructor: IMediaSegmentManager is built from the
        // segment providers, one of which this plugin registers, so taking it at
        // construction time is the circular dependency DetectionManager's remarks describe.
        if (_serviceProvider.GetService<ILibraryManager>() is not { } library
            || _serviceProvider.GetService<IMediaSegmentManager>() is not { } segments)
        {
            return;
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var edl = _coordinator.GetLiveEdl(path);
            if (edl is null)
            {
                continue; // nothing written yet, or the process gave up
            }

            IReadOnlyList<CommercialBreak> breaks;
            try
            {
                breaks = LiveScanCoordinator.IsPeriodicAnalysis(edl)
                    ? ConfirmedBreaks(path, edl, cfg)
                    : EdlParser.Parse(edl, cfg.PaddingSeconds);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // being rewritten by Comskip at this instant; the next poll gets it
            }

            if (library.FindByPath(path, false) is not Video item)
            {
                if (breaks.Count > 0)
                {
                    RequestLibraryRefresh(path, library);
                }

                continue;
            }

            try
            {
                await ReconcileAsync(item, breaks, library, segments).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not store the live breaks for {Path}", path);
            }
        }
    }

    /// <summary>
    /// The breaks worth storing from the periodic analysis: a new pass (the file was
    /// written since the last one seen) is fed to the path's tracker; otherwise the
    /// tracker's current answer stands, so a poll between passes still reconciles rows
    /// Jellyfin may have pruned.
    /// </summary>
    private IReadOnlyList<CommercialBreak> ConfirmedBreaks(string path, string edl, PluginConfiguration cfg)
    {
        var analysis = _analyses.GetOrAdd(path, _ => new Analysis(new LiveBreakTracker(ConfirmPasses, MinPeriodicBreakSeconds)));
        var written = File.GetLastWriteTimeUtc(edl);
        if (written != analysis.LastPassUtc)
        {
            var pass = EdlParser.Parse(edl, cfg.PaddingSeconds);
            analysis.LastPassUtc = written;
            if (analysis.Tracker.Observe(pass))
            {
                _logger.LogDebug(
                    "Periodic analysis {Pass} of {Path}: {Reported} break(s) reported, {Confirmed} confirmed",
                    analysis.Tracker.Passes, path, pass.Count, analysis.Tracker.Confirmed.Count);
            }
        }

        return analysis.Tracker.Confirmed;
    }

    /// <summary>
    /// Makes the item's Commercial rows match <paramref name="breaks"/>, touching only
    /// what changed. Every row the item has is considered, whoever wrote it: on a
    /// recording still being written they are all this scan's, and rows Jellyfin
    /// pruned simply come back as missing.
    /// </summary>
    private async Task ReconcileAsync(
        Video item,
        IReadOnlyList<CommercialBreak> breaks,
        ILibraryManager library,
        IMediaSegmentManager segments)
    {
        var options = library.GetLibraryOptions(item);
        var rows = (await segments
            .GetSegmentsAsync(item, new[] { MediaSegmentType.Commercial }, options, filterByProvider: false)
            .ConfigureAwait(false)).ToList();

        var create = new List<CommercialBreak>();
        foreach (var b in breaks)
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r.StartTicks - b.StartTicks) <= StartToleranceTicks);
            if (row is null)
            {
                create.Add(b);
                continue;
            }

            rows.Remove(row);
            if (Math.Abs(row.EndTicks - b.EndTicks) > EndToleranceTicks
                || Math.Abs(row.StartTicks - b.StartTicks) > EndToleranceTicks)
            {
                await segments.DeleteSegmentAsync(row.Id).ConfigureAwait(false);
                create.Add(b);
            }
        }

        // Rows no current break accounts for: the scan changed its mind about them.
        foreach (var orphan in rows)
        {
            await segments.DeleteSegmentAsync(orphan.Id).ConfigureAwait(false);
        }

        foreach (var b in create)
        {
            await segments.CreateSegmentAsync(
                new MediaSegmentDto
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    Type = MediaSegmentType.Commercial,
                    StartTicks = b.StartTicks,
                    EndTicks = b.EndTicks
                },
                ComskipSegmentProvider.ProviderId).ConfigureAwait(false);
        }

        var changes = rows.Count + create.Count;
        if (changes > 0)
        {
            _logger.LogInformation(
                "Stored {Count} commercial break(s) found so far in {Path} ({Changes} segment(s) changed)",
                breaks.Count, item.Path, changes);
        }
    }

    /// <summary>
    /// Ask the library that holds <paramref name="path"/> to look at it, so the recording
    /// gets an item to hang segments on before it finishes. Jellyfin's TriggerRefresh only
    /// works on an item that already exists; a queued refresh of the library root with the
    /// new path (and its folders) in RefreshPaths is what creates one.
    /// </summary>
    private void RequestLibraryRefresh(string path, ILibraryManager library)
    {
        var now = DateTime.UtcNow;
        if (_refreshRequested.TryGetValue(path, out var last) && now - last < RefreshRequestInterval)
        {
            return;
        }

        _refreshRequested[path] = now;

        var folder = library.GetVirtualFolders()
            .FirstOrDefault(v => v.Locations is not null && v.Locations.Any(l => IsUnder(path, l)));
        if (folder is null || !Guid.TryParse(folder.ItemId, out var rootId))
        {
            _logger.LogWarning("{Path} is in no library; its live breaks cannot be stored until it is", path);
            return;
        }

        if (_serviceProvider.GetService<IProviderManager>() is not { } providers
            || _serviceProvider.GetService<IFileSystem>() is not { } fileSystem)
        {
            return;
        }

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var parent = Path.GetDirectoryName(dir) ?? string.Empty;
        _logger.LogInformation("Asking library {Library} to pick up the in-progress recording {Path}", folder.Name, path);
        providers.QueueRefresh(
            rootId,
            new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                RefreshPaths = new[] { path, dir, parent }
            },
            RefreshPriority.High);
    }

    private sealed class Analysis
    {
        public Analysis(LiveBreakTracker tracker) => Tracker = tracker;

        public LiveBreakTracker Tracker { get; }

        public DateTime LastPassUtc { get; set; }
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.Ordinal);
    }
}
