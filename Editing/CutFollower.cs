using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Detection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Editing;

/// <summary>
/// Keeps a cut or trimmed recording's Undo when JellyDVR Space moves it, hidden original and all,
/// to another folder. Jellyfin sees a new item there, so the original's undo time and the cut
/// record move over to it, matched by file name with the old file gone.
/// </summary>
public sealed class CutFollower : IHostedService
{
    private readonly ILogger<CutFollower> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly CommercialCutStore _store;
    private readonly TrimBackups _backups;
    private readonly DetectionStore _detections;

    public CutFollower(ILogger<CutFollower> logger, ILibraryManager libraryManager, CommercialCutStore store, TrimBackups backups, DetectionStore detections)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _store = store;
        _backups = backups;
        _detections = detections;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _ = Task.Run(RememberPaths, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        return Task.CompletedTask;
    }

    /// <summary>Cuts from before records kept the path get it now, while their item is still where it was.</summary>
    private void RememberPaths()
    {
        foreach (var (id, record) in _store.All())
        {
            if (record.Path is null && _libraryManager.GetItemById(id) is BaseItem { Path: { } path })
            {
                _store.Save(id, record with { Path = path });
            }
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Video { Path: { } path } item || TrimBackups.Find(path) is not { } backup)
        {
            return;
        }

        try
        {
            _backups.Follow(backup);
            if (_store.TryGet(item.Id, out _))
            {
                return;
            }

            var name = Path.GetFileName(path);
            var (oldId, record) = _store.All().FirstOrDefault(r => r.Value.Path is { } p && p != path
                && string.Equals(Path.GetFileName(p), name, StringComparison.Ordinal) && !File.Exists(p));
            if (record is null)
            {
                return;
            }

            var aside = record.ForeignEdlAside is not null && File.Exists(TrimBackups.EdlFor(backup)) ? TrimBackups.EdlFor(backup) : record.ForeignEdlAside;
            _store.Move(oldId, item.Id, record with { Path = path, ForeignEdlAside = aside });
            _detections.Save(item.Id, new DetectionRecord(DetectionStatus.NoCommercials, Array.Empty<CommercialBreak>(), DateTime.UtcNow, DetectionRecord.SourceCut));
            _logger.LogInformation("The cut recording {Old} moved to {New}; it can still be undone there", record.Path, path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not follow the move of {Path}", path);
        }
    }
}
