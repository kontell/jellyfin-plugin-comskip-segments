using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Detection;
using Jellyfin.Plugin.ComskipSegments.ProgramStart;
using Jellyfin.Plugin.ComskipSegments.Editing;
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
/// the item menus' "Find program start". Administrators and the users allowed to edit recordings
/// (see <see cref="RecordingEditors"/>): it can trim recordings. Synchronous like
/// the audio-trim endpoint — the work is a few seconds of decoding, and the point of a click
/// is to see what it did.
/// </summary>
[ApiController]
[Route("ComskipSegments/ProgramStart")]
[Authorize]
[RecordingEditor]
public sealed class ProgramStartController : ControllerBase
{
    private readonly ILogger<ProgramStartController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ProgramStartService _service;
    private readonly ProgramStartStore _store;
    private readonly DetectionManager _detectionManager;
    private readonly DetectionStore _detectionStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly TrimBackups _backups;

    public ProgramStartController(
        ILogger<ProgramStartController> logger,
        ILibraryManager libraryManager,
        ProgramStartService service,
        ProgramStartStore store,
        DetectionManager detectionManager,
        DetectionStore detectionStore,
        IServiceProvider serviceProvider,
        TrimBackups backups)
    {
        _backups = backups;
        _logger = logger;
        _libraryManager = libraryManager;
        _service = service;
        _store = store;
        _detectionManager = detectionManager;
        _detectionStore = detectionStore;
        _serviceProvider = serviceProvider;
    }

    // UndoUntil: when a kept original's undo time is up (null: kept for good, or nothing to undo).
    public sealed record StatusResponse(double? StartSeconds, string? Source, string? Action, string? Message, DateTime? UpdatedUtc, int Cards, bool CanUndo = false, DateTime? UndoUntil = null);

    public sealed record CardResponse(Guid Id, string Label, string? Channel, string? SourcePath, double SourceSeconds, DateTime CreatedUtc);

    /// <summary>The item's program start, if any, and how many cards are remembered.</summary>
    [HttpGet("{itemId}")]
    public ActionResult<StatusResponse> Status([FromRoute] Guid itemId)
    {
        var backup = _libraryManager.GetItemById(itemId) is BaseItem item ? ProgramStartService.BackupOf(item) : null;
        var canUndo = backup is not null;
        var undoUntil = backup is null ? null : _backups.ExpiresUtc(backup);
        return _store.TryGet(itemId, out var r) && r.Source is not (ProgramStartRecord.SourceNone or ProgramStartRecord.SourceUndone)
            ? Ok(new StatusResponse(r.StartSeconds, r.Source, r.Action, r.Message, r.UpdatedUtc, _store.Cards.Count, canUndo, undoUntil))
            : Ok(new StatusResponse(null, null, null, null, null, _store.Cards.Count, canUndo, undoUntil));
    }

    /// <summary>"Undo trim": the kept original goes back in place of the trimmed recording.</summary>
    [HttpPost("{itemId}/Undo")]
    public Task<ActionResult<ProgramStartResult>> Undo([FromRoute] Guid itemId) =>
        Run(itemId, item => _service.UndoTrimAsync(item));

    /// <summary>"Program starts here": the program begins at <paramref name="seconds"/>.</summary>
    [HttpPost("{itemId}")]
    public Task<ActionResult<ProgramStartResult>> Set([FromRoute] Guid itemId, [FromQuery] double seconds, [FromQuery] bool keyframe, CancellationToken ct) =>
        Run(itemId, item => _service.SetAsync(item, seconds, keyframe));

    public sealed record KeyframesResponse(double Seconds, double? Before, double? After);

    /// <summary>
    /// The keyframes either side of <paramref name="seconds"/>: a cut can only start on one, so the
    /// editors offer these two (with <see cref="Frame"/> pictures) instead of the moment itself.
    /// Before is at or before the moment, After the next one.
    /// </summary>
    [HttpGet("{itemId}/Keyframes")]
    public async Task<ActionResult<KeyframesResponse>> KeyframesAround([FromRoute] Guid itemId, [FromQuery] double seconds, CancellationToken ct)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || item.Path is null || !System.IO.File.Exists(item.Path))
        {
            return NotFound();
        }

        var probe = _serviceProvider.GetService<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>()?.ProbePath;
        if (string.IsNullOrEmpty(probe))
        {
            return StatusCode(500);
        }

        var keys = await AudioTrim.Keyframes.AroundAsync(probe, item.Path, seconds, 30, ct).ConfigureAwait(false);
        var before = keys.Where(k => k <= seconds + 0.02).Cast<double?>().LastOrDefault();
        var after = keys.Where(k => k > seconds + 0.02).Cast<double?>().FirstOrDefault();
        return Ok(new KeyframesResponse(seconds, before, after));
    }

    /// <summary>The picture at <paramref name="seconds"/> (a JPEG, <paramref name="width"/> wide).</summary>
    [HttpGet("{itemId}/Frame")]
    public async Task<ActionResult> Frame([FromRoute] Guid itemId, [FromQuery] double seconds, [FromQuery] int width = 480, CancellationToken ct = default)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || item.Path is null || !System.IO.File.Exists(item.Path))
        {
            return NotFound();
        }

        var ffmpeg = _serviceProvider.GetService<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>()?.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            return StatusCode(500);
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in new[]
        {
            "-v", "error", "-nostdin", "-ss", seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", item.Path,
            "-frames:v", "1", "-vf", $"scale={Math.Clamp(width, 120, 1280)}:-2", "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "4", "pipe:1"
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
        {
            return StatusCode(500);
        }

        _ = process.StandardError.ReadToEndAsync(ct);
        using var jpeg = new System.IO.MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(jpeg, ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return jpeg.Length == 0 ? NotFound() : File(jpeg.ToArray(), "image/jpeg");
    }

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
