using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Configuration;
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
/// Gets the breaks a live Comskip scan has found so far into Jellyfin's media segments
/// while the recording is still being written, so a client that reads Jellyfin's own
/// segments (Kodi through pvr.kofin, Jellyfin's skip prompt) can skip them while catching
/// up on a programme still being recorded, not only the web and TV forks that ask
/// <c>/ComskipSegments/Live</c>.
///
/// It writes nothing itself. <see cref="Providers.ComskipSegmentProvider"/> serves the
/// live breaks for a recording in progress, and this asks Jellyfin to re-run the segment
/// providers for the item (<see cref="IMediaSegmentManager.RunSegmentPluginProviders"/>,
/// without overwrite) whenever the live breaks changed or the rows are missing. That
/// replaces this provider's rows alone, leaves other providers' and hand-edited rows
/// alone, and is exactly what a Media Segment Scan does, so a scan during the recording
/// stores the same breaks instead of wiping them. Rows go missing because Jellyfin
/// deletes an item's segments whenever a refresh notices the file changed ("File changed,
/// pruning extracted data"), which a growing recording is by definition; every poll
/// checks they are still there.
///
/// A recording has no library item until something asks the library to look at its
/// path (Jellyfin 12 makes one at the start of the recording; older servers do not), so
/// once there is a break to store the nearest folder the library already knows is asked
/// to refresh it, at most once a minute until the item exists. The harvest and a live
/// publish never overlap (<see cref="LiveScanCoordinator.LockPublishAsync"/>), so the
/// final result is never written over by a live list.
/// </summary>
public sealed class LiveSegmentPublisher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RefreshRequestInterval = TimeSpan.FromSeconds(60);

    /// <summary>A stored row this close to a break, at both ends, is that break's row.</summary>
    private const long RowToleranceTicks = (long)(0.5 * TimeSpan.TicksPerSecond);

    private readonly ILogger<LiveSegmentPublisher> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly LiveScanCoordinator _coordinator;

    // Per recording path: the breaks last handed to Jellyfin, and when a library
    // refresh was last asked for, so the request is not repeated every poll.
    private readonly ConcurrentDictionary<string, IReadOnlyList<CommercialBreak>> _published = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _refreshRequested = new(StringComparer.Ordinal);

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
                    await PollOnceAsync(stoppingToken).ConfigureAwait(false);
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

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var paths = _coordinator.TrackedPaths();
        foreach (var gone in _published.Keys.Concat(_refreshRequested.Keys).Distinct().Where(p => !paths.Contains(p, StringComparer.Ordinal)).ToList())
        {
            _published.TryRemove(gone, out _);
            _refreshRequested.TryRemove(gone, out _);
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

            var breaks = _coordinator.GetLiveBreaks(path);
            if (breaks is null)
            {
                continue; // nothing yet, or the process gave up
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
                await StoreAsync(item, path, breaks, library, segments, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not store the live breaks for {Path}", path);
            }
        }
    }

    private async Task StoreAsync(
        Video item,
        string path,
        IReadOnlyList<CommercialBreak> breaks,
        ILibraryManager library,
        IMediaSegmentManager segments,
        CancellationToken cancellationToken)
    {
        var options = library.GetLibraryOptions(item);
        var unchanged = _published.TryGetValue(path, out var last) && last.SequenceEqual(breaks);
        if (unchanged && breaks.Count == 0)
        {
            return;
        }

        if (unchanged)
        {
            var rows = (await segments
                .GetSegmentsAsync(item, new[] { MediaSegmentType.Commercial }, options, filterByProvider: true)
                .ConfigureAwait(false)).ToList();
            if (breaks.All(b => rows.Any(r => Math.Abs(r.StartTicks - b.StartTicks) <= RowToleranceTicks && Math.Abs(r.EndTicks - b.EndTicks) <= RowToleranceTicks)))
            {
                return; // still there
            }
        }

        using (await _coordinator.LockPublishAsync().ConfigureAwait(false))
        {
            if (!_coordinator.IsTracking(path))
            {
                return; // harvested meanwhile: the final result is on its way
            }

            await segments.RunSegmentPluginProviders(item, options, forceOverwrite: false, cancellationToken).ConfigureAwait(false);
        }

        _published[path] = breaks;
        if (unchanged)
        {
            _logger.LogInformation("Put back {Count} commercial break(s) found so far in {Path} after Jellyfin cleared them", breaks.Count, item.Path);
        }
        else
        {
            _logger.LogInformation("Stored {Count} commercial break(s) found so far in {Path}", breaks.Count, item.Path);
        }
    }

    /// <summary>
    /// Ask the library to look at <paramref name="path"/>, so the recording gets an item
    /// to hang segments on before it finishes: the nearest folder the library already has
    /// an item for is queued with the new path and its folders in RefreshPaths, the same
    /// refresh Jellyfin's own recordings manager triggers, and only the library root when
    /// no such folder exists.
    /// </summary>
    private void RequestLibraryRefresh(string path, ILibraryManager library)
    {
        var now = DateTime.UtcNow;
        if (_refreshRequested.TryGetValue(path, out var last) && now - last < RefreshRequestInterval)
        {
            return;
        }

        _refreshRequested[path] = now;

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var parent = Path.GetDirectoryName(dir) ?? string.Empty;
        Guid? target = null;
        string? targetName = null;
        for (var folder = dir; !string.IsNullOrEmpty(folder) && target is null; folder = Path.GetDirectoryName(folder))
        {
            if (library.FindByPath(folder, true) is { } ancestor)
            {
                target = ancestor.Id;
                targetName = ancestor.Path;
            }
        }

        if (target is null)
        {
            var root = library.GetVirtualFolders()
                .FirstOrDefault(v => v.Locations is not null && v.Locations.Any(l => IsUnder(path, l)));
            if (root is null || !Guid.TryParse(root.ItemId, out var rootId))
            {
                _logger.LogWarning("{Path} is in no library; its live breaks cannot be stored until it is", path);
                return;
            }

            target = rootId;
            targetName = root.Name;
        }

        if (_serviceProvider.GetService<IProviderManager>() is not { } providers
            || _serviceProvider.GetService<IFileSystem>() is not { } fileSystem)
        {
            return;
        }

        _logger.LogInformation("Asking the library ({Folder}) to pick up the in-progress recording {Path}", targetName, path);
        providers.QueueRefresh(
            target.Value,
            new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                RefreshPaths = new[] { path, dir, parent }
            },
            RefreshPriority.High);
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.Ordinal);
    }
}
