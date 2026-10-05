using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Detection;
using Jellyfin.Plugin.ComskipSegments.ProgramStart;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// Program start, for the players' "Program starts here" / "Remember this card" buttons and
/// the item menus' "Find program start". Admin only: it can trim recordings. Synchronous like
/// the audio-trim endpoint — the work is a few seconds of decoding, and the point of a click
/// is to see what it did.
/// </summary>
[ApiController]
[Route("ComskipSegments/ProgramStart")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ProgramStartController : ControllerBase
{
    private readonly ILogger<ProgramStartController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ProgramStartService _service;
    private readonly ProgramStartStore _store;
    private readonly DetectionManager _detectionManager;
    private readonly DetectionStore _detectionStore;
    private readonly IServiceProvider _serviceProvider;

    public ProgramStartController(
        ILogger<ProgramStartController> logger,
        ILibraryManager libraryManager,
        ProgramStartService service,
        ProgramStartStore store,
        DetectionManager detectionManager,
        DetectionStore detectionStore,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _service = service;
        _store = store;
        _detectionManager = detectionManager;
        _detectionStore = detectionStore;
        _serviceProvider = serviceProvider;
    }

    public sealed record StatusResponse(double? StartSeconds, string? Source, string? Action, string? Message, DateTime? UpdatedUtc, int Cards, bool CanUndo = false);

    public sealed record CardResponse(Guid Id, string Label, string? Channel, string? SourcePath, double SourceSeconds, DateTime CreatedUtc);

    /// <summary>The item's program start, if any, and how many cards are remembered.</summary>
    [HttpGet("{itemId}")]
    public ActionResult<StatusResponse> Status([FromRoute] Guid itemId)
    {
        var canUndo = _libraryManager.GetItemById(itemId) is BaseItem item && ProgramStartService.BackupOf(item) is not null;
        return _store.TryGet(itemId, out var r) && r.Source is not (ProgramStartRecord.SourceNone or ProgramStartRecord.SourceUndone)
            ? Ok(new StatusResponse(r.StartSeconds, r.Source, r.Action, r.Message, r.UpdatedUtc, _store.Cards.Count, canUndo))
            : Ok(new StatusResponse(null, null, null, null, null, _store.Cards.Count, canUndo));
    }

    /// <summary>"Undo trim": the kept original goes back in place of the trimmed recording.</summary>
    [HttpPost("{itemId}/Undo")]
    public Task<ActionResult<ProgramStartResult>> Undo([FromRoute] Guid itemId) =>
        Run(itemId, item => _service.UndoTrimAsync(item));

    /// <summary>"Program starts here": the program begins at <paramref name="seconds"/>.</summary>
    [HttpPost("{itemId}")]
    public Task<ActionResult<ProgramStartResult>> Set([FromRoute] Guid itemId, [FromQuery] double seconds, CancellationToken ct) =>
        Run(itemId, item => _service.SetAsync(item, seconds));

    /// <summary>"Remember this card": the frame at <paramref name="seconds"/> is the channel's card before programs.</summary>
    [HttpPost("{itemId}/Card")]
    public Task<ActionResult<ProgramStartResult>> RememberCard(
        [FromRoute] Guid itemId, [FromQuery] double seconds, [FromQuery] string? label, [FromQuery] string? channel, CancellationToken ct) =>
        Run(itemId, item => _service.RememberCardAsync(item, seconds, label, channel));

    /// <summary>"Find program start": look for a remembered card in the opening minutes.</summary>
    [HttpPost("{itemId}/Find")]
    public Task<ActionResult<ProgramStartResult>> Find([FromRoute] Guid itemId, CancellationToken ct) =>
        Run(itemId, item => _service.FindAsync(item));

    /// <summary>Removes the item's program start (the marker; a trim stays).</summary>
    [HttpDelete("{itemId}")]
    public async Task<ActionResult<ProgramStartResult>> Clear([FromRoute] Guid itemId, CancellationToken ct)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item)
        {
            return NotFound(new ProgramStartResult(false, false, null, "No such item."));
        }

        return Ok(await _service.ClearAsync(item, ct).ConfigureAwait(false)
            ? new ProgramStartResult(true, false, null, "Program start removed.")
            : new ProgramStartResult(false, false, null, "It had no program start."));
    }

    [HttpGet("Cards")]
    public ActionResult Cards() =>
        Ok(_store.Cards.Select(c => new CardResponse(c.Id, c.Label, c.Channel, c.SourcePath, c.SourceSeconds, c.CreatedUtc)));

    [HttpDelete("Cards/{cardId}")]
    public ActionResult DeleteCard([FromRoute] Guid cardId) =>
        _service.RemoveCard(cardId) ? Ok(new { Message = "Card forgotten." }) : NotFound(new { Message = "No such card." });

    private async Task<ActionResult<ProgramStartResult>> Run(Guid itemId, Func<BaseItem, Task<ProgramStartResult>> work)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item)
        {
            return NotFound(new ProgramStartResult(false, false, null, "No such item."));
        }

        if (item.Path is null || !System.IO.File.Exists(item.Path))
        {
            return BadRequest(new ProgramStartResult(false, false, null, "It has no file on disk."));
        }

        try
        {
            // The service claims the recording: a second action on it is refused while one
            // runs (seen live: four clicks started four trims of one file).
            var task = work(item);
            // Searching the opening minutes takes ~25 s, and JDVR's client gives up on a
            // silent request after ~10 s: answer within 8 s, and let a longer search carry on
            // (the work uses no request services; the apps show it as "Checking").
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false) != task)
            {
                return Ok(new ProgramStartResult(true, false, null, "Still looking; the recording shows the progress, and the result applies by itself.") { Background = true });
            }

            var result = await task.ConfigureAwait(false);
            return result.Message.StartsWith("Already working", StringComparison.Ordinal) ? Conflict(result) : Ok(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Program start failed for {Path}", item.Path);
            return StatusCode(500, new ProgramStartResult(false, false, null, $"Unexpected error: {ex.Message}"));
        }
    }
}
