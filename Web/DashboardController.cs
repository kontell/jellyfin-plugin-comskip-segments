using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ComskipSegments.AudioTrim;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using Jellyfin.Plugin.ComskipSegments.Detection;
using Jellyfin.Plugin.ComskipSegments.Editing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// The segments dashboard: every recording this plugin knows, its commercial breaks to check and
/// edit, and "Cut commercials out". For administrators and the users allowed to edit recordings.
/// Pictures and keyframes come from the program-start endpoints; Re-run Comskip is Detect with force.
/// </summary>
[ApiController]
[Route("ComskipSegments/Dashboard")]
[Authorize]
[RecordingEditor]
public sealed class DashboardController : ControllerBase
{
    private readonly ILogger<DashboardController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly DetectionStore _detections;
    private readonly DetectionManager _manager;
    private readonly CommercialCutStore _cutStore;
    private readonly CommercialCutService _cutter;
    private readonly CutTracker _cuts;
    private readonly TrimBackups _backups;
    private readonly IServiceProvider _serviceProvider;

    public DashboardController(
        ILogger<DashboardController> logger,
        ILibraryManager libraryManager,
        DetectionStore detections,
        DetectionManager manager,
        CommercialCutStore cutStore,
        CommercialCutService cutter,
        CutTracker cuts,
        TrimBackups backups,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _detections = detections;
        _manager = manager;
        _cutStore = cutStore;
        _cutter = cutter;
        _cuts = cuts;
        _backups = backups;
        _serviceProvider = serviceProvider;
    }

    /// <summary>A break, in seconds from the start of the file.</summary>
    public sealed record Span(double Start, double End);

    /// <param name="Status">detected, none, failed, cut.</param>
    /// <param name="Source">comskip, edl, edited, cut, cleared.</param>
    public sealed record Row(
        Guid Id,
        string Name,
        string? Series,
        string? Episode,
        string Kind,
        DateTime? Recorded,
        double Seconds,
        IReadOnlyList<Span> Breaks,
        string Status,
        string Source,
        bool Busy);

    public sealed record ListResponse(IReadOnlyList<Row> Items);

    /// <param name="Edl">ours (this plugin's), other (another program's, never overwritten), none.</param>
    /// <param name="Others">Segments other than commercials (another plugin's intro, a program-start preview): shown, not edited here.</param>
    public sealed record Detail(
        Row Row,
        string? Path,
        string Edl,
        DateTime? Scanned,
        IReadOnlyList<OtherSegment> Others,
        bool Scanning,
        Cut? Cut,
        CutResult? LastCut,
        string? CutRefusal,
        CutSettings Settings);

    public sealed record OtherSegment(string Type, double Start, double End);

    /// <param name="UndoUntil">When the kept original goes (null: kept for good, or none kept).</param>
    public sealed record Cut(DateTime CutUtc, double SecondsBefore, int Breaks, bool CanUndo, DateTime? UndoUntil);

    /// <param name="Rounding">keep-ad or keep-show (see <see cref="PluginConfiguration.CutRounding"/>).</param>
    /// <param name="UndoDays">Days a cut can be undone when originals aren't kept for good (0: it can't).</param>
    public sealed record CutSettings(string Rounding, bool KeepOriginal, int UndoDays, bool WriteEdl);

    public sealed record SaveRequest(IReadOnlyList<Span>? Breaks, bool WriteEdl);

    [HttpGet]
    public ActionResult<ListResponse> List()
    {
        var cuts = _cutStore.All();
        var ids = _detections.All().Select(r => r.Key).Concat(cuts.Keys).Distinct();
        var rows = new List<Row>();
        foreach (var id in ids)
        {
            if (_libraryManager.GetItemById(id) is BaseItem item && item is Video)
            {
                rows.Add(RowOf(item, cuts.ContainsKey(id)));
            }
        }

        return Ok(new ListResponse(rows.OrderByDescending(r => r.Recorded).ToList()));
    }

    [HttpGet("{itemId}")]
    public async Task<ActionResult<Detail>> Get([FromRoute] Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item)
        {
            return NotFound();
        }

        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var hasCut = _cutStore.TryGet(itemId, out var cutRecord);

        // Undone some other way (the item menu's Undo trim puts the original back too): not cut any more.
        if (hasCut && TrimBackups.Find(item.Path) is null && (!_detections.TryGet(itemId, out var now) || now.Source != DetectionRecord.SourceCut))
        {
            _cutStore.Remove(itemId);
            hasCut = false;
        }
        var edl = "none";
        if (item.Path is not null && DetectionManager.SiblingEdlPath(item.Path) is { } edlPath && System.IO.File.Exists(edlPath))
        {
            edl = _detections.IsOwnedEdl(edlPath) ? "ours" : "other";
        }

        Cut? cut = null;
        if (hasCut)
        {
            var backup = TrimBackups.Find(item.Path);
            cut = new Cut(cutRecord.CutUtc, cutRecord.SecondsBefore, cutRecord.BreaksBefore.Count, backup is not null, backup is null ? null : _backups.ExpiresUtc(backup));
        }

        return Ok(new Detail(
            RowOf(item, hasCut),
            item.Path,
            edl,
            _detections.TryGet(itemId, out var record) ? record.UpdatedUtc : null,
            await OthersAsync(item).ConfigureAwait(false),
            _manager.IsInFlight(itemId),
            cut,
            _cutter.LastResult(itemId),
            _cutter.Refusal(item),
            new CutSettings(cfg.CutRounding, cfg.AudioTrimKeepOriginal, cfg.AudioTrimKeepOriginal ? 0 : Math.Max(0, cfg.TrimUndoDays), cfg.PlaceEdlBesideRecording)));
    }

    /// <summary>Saves the breaks as edited (empty: no commercials).</summary>
    [HttpPut("{itemId}/Breaks")]
    public async Task<ActionResult<EditResult>> Save([FromRoute] Guid itemId, [FromBody] SaveRequest request, CancellationToken ct)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item)
        {
            return NotFound();
        }

        var breaks = (request.Breaks ?? Array.Empty<Span>())
            .Select(b => new CommercialBreak((long)(b.Start * 1e7), (long)(b.End * 1e7)))
            .ToList();
        return Ok(await _cutter.SaveBreaksAsync(item, breaks, request.WriteEdl, ct).ConfigureAwait(false));
    }

    /// <summary>Starts cutting the commercials out (follow it in ComskipSegments/Cuts).</summary>
    [HttpPost("{itemId}/Cut")]
    public ActionResult<EditResult> CutOut([FromRoute] Guid itemId) =>
        _libraryManager.GetItemById(itemId) is BaseItem item ? Ok(_cutter.Cut(item)) : NotFound();

    [HttpPost("{itemId}/UndoCut")]
    public async Task<ActionResult<EditResult>> UndoCut([FromRoute] Guid itemId) =>
        _libraryManager.GetItemById(itemId) is BaseItem item ? Ok(await _cutter.UndoAsync(item).ConfigureAwait(false)) : NotFound();

    private Row RowOf(BaseItem item, bool cut)
    {
        var hasRecord = _detections.TryGet(item.Id, out var record);
        var status = cut && record?.Source == DetectionRecord.SourceCut ? "cut"
            : !hasRecord ? "none"
            : record!.Status switch
            {
                DetectionStatus.Detected => "detected",
                DetectionStatus.Failed => "failed",
                _ => "none"
            };
        var episode = item is Episode e && e.ParentIndexNumber is { } s && e.IndexNumber is { } n ? $"S{s:00}E{n:00}" : null;
        var breaks = hasRecord && record!.Status == DetectionStatus.Detected
            ? record.Breaks.Select(b => new Span(b.StartTicks / 1e7, b.EndTicks / 1e7)).ToList()
            : new List<Span>();
        return new Row(
            item.Id,
            item.Name,
            (item as Episode)?.SeriesName,
            episode,
            item is Episode ? "episode" : item is MediaBrowser.Controller.Entities.Movies.Movie ? "movie" : "video",
            item.DateCreated == default ? null : item.DateCreated,
            (item.RunTimeTicks ?? 0) / 1e7,
            breaks,
            status,
            record?.Source ?? (hasRecord ? DetectionRecord.SourceComskip : "none"),
            _cuts.IsBusy(item.Id) || _manager.IsInFlight(item.Id));
    }

    private async Task<IReadOnlyList<OtherSegment>> OthersAsync(BaseItem item)
    {
        try
        {
            var segments = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
            var types = Enum.GetValues<MediaSegmentType>().Where(t => t != MediaSegmentType.Commercial).ToArray();
            var found = await segments.GetSegmentsAsync(item, types, _libraryManager.GetLibraryOptions(item), filterByProvider: false).ConfigureAwait(false);
            return found
                .OrderBy(s => s.StartTicks)
                .Select(s => new OtherSegment(s.Type.ToString(), s.StartTicks / 1e7, s.EndTicks / 1e7))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the other segments of {Path}", item.Path);
            return Array.Empty<OtherSegment>();
        }
    }
}
