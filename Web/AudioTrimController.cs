using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.AudioTrim;
using Jellyfin.Plugin.ComskipSegments.Detection;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// Admin-only control surface for the audio-format trim feature: the config page's
/// manual-trim button and the item-details "Fix opening/closing audio format" menu
/// entry both land here.
///
/// Unlike <see cref="ComskipSegmentsController"/>'s Detect endpoint, this always runs
/// synchronously and returns the outcome directly — the scan itself is cheap (a few
/// seconds of audio-only decode even for a multi-hour recording) and the point of a
/// manual click is to see the result of THIS click, not to poll a queue.
/// </summary>
[ApiController]
[Route("ComskipSegments/AudioTrim")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class AudioTrimController : ControllerBase
{
    private readonly ILogger<AudioTrimController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly AudioTrimService _audioTrim;
    private readonly AudioTrimStore _audioTrimStore;
    private readonly DetectionManager _detectionManager;
    private readonly DetectionStore _detectionStore;
    private readonly IServiceProvider _serviceProvider;

    public AudioTrimController(
        ILogger<AudioTrimController> logger,
        ILibraryManager libraryManager,
        AudioTrimService audioTrim,
        AudioTrimStore audioTrimStore,
        DetectionManager detectionManager,
        DetectionStore detectionStore,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _audioTrim = audioTrim;
        _audioTrimStore = audioTrimStore;
        _detectionManager = detectionManager;
        _detectionStore = detectionStore;
        _serviceProvider = serviceProvider;
    }

    public sealed record ApplyResponse(bool Attempted, bool Trimmed, double LeadTrimSeconds, double TrailTrimSeconds, string Message);

    public sealed record StatusResponse(string? Status, double? LeadTrimSeconds, double? TrailTrimSeconds, string? BackupPath, string? Message, DateTime? UpdatedUtc);

    /// <summary>
    /// Scans (and, if warranted, trims) one item right now. Always forced — same
    /// one-click-does-something expectation as the item menu's "Scan for commercials"
    /// entry — so a repeat click after a NotNeeded/Applied result re-scans rather than
    /// silently no-op'ing.
    ///
    /// If a trim actually happens and Comskip has already published commercial-skip
    /// segments for this item, those timestamps are now wrong (the file's timeline
    /// shifted by the lead trim), so this clears them and force-requeues Comskip —
    /// the automatic pipeline never hits this because it always trims BEFORE Comskip
    /// runs for the first time.
    /// </summary>
    [HttpPost("Apply/{itemId}")]
    public async Task<ActionResult<ApplyResponse>> Apply([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnableAudioFormatTrim)
        {
            _logger.LogWarning("Audio-trim Apply rejected for {ItemId}: feature disabled in plugin settings", itemId);
            return BadRequest(new ApplyResponse(false, false, 0, 0, "Audio-format trim is disabled in plugin settings."));
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogWarning("Audio-trim Apply rejected: no library item with ID {ItemId}", itemId);
            return NotFound(new ApplyResponse(false, false, 0, 0, $"No library item with ID {itemId}"));
        }

        if (item.Path is null || !System.IO.File.Exists(item.Path))
        {
            _logger.LogWarning("Audio-trim Apply rejected for {ItemId}: no resolvable file on disk (Path={Path})", itemId, item.Path);
            return BadRequest(new ApplyResponse(false, false, 0, 0, "Item has no resolvable file on disk"));
        }

        try
        {
            var result = await _audioTrim.ApplyIfNeededAsync(item, cfg, force: true, cancellationToken).ConfigureAwait(false);

            if (result.Trimmed && result.LeadTrimSeconds > 0)
            {
                await InvalidateStaleCommercialBreaks(itemId, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Manual audio-trim for {Path}: {Message}", item.Path, result.Message);
            return Ok(new ApplyResponse(result.Attempted, result.Trimmed, result.LeadTrimSeconds, result.TrailTrimSeconds, result.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error during manual audio-trim for {Path}", item.Path);
            return StatusCode(500, new ApplyResponse(false, false, 0, 0, $"Unexpected error: {ex.Message}"));
        }
    }

    [HttpGet("Status/{itemId}")]
    public ActionResult<StatusResponse> Status([FromRoute] Guid itemId)
    {
        if (_audioTrimStore.TryGet(itemId, out var record))
        {
            return Ok(new StatusResponse(
                record.Status.ToString(), record.LeadTrimSeconds, record.TrailTrimSeconds, record.BackupPath, record.Message, record.UpdatedUtc));
        }

        return Ok(new StatusResponse(null, null, null, null, null, null));
    }

    /// <summary>
    /// A lead trim shifts every timestamp in the file by however many seconds were cut
    /// from the front, so any commercial-skip segments Comskip already published against
    /// the pre-trim file are now pointing at the wrong moments. Clear them and force a
    /// fresh Comskip pass against the corrected file.
    /// </summary>
    private async Task InvalidateStaleCommercialBreaks(Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            var segmentManager = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
            await segmentManager.DeleteSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear stale commercial-skip segments after audio-trim for {ItemId}", itemId);
        }

        _detectionStore.Remove(itemId);

        // NOT force: true here. Clearing the stale record above is unconditional (a
        // wrong segment is worse than no segment, regardless of channel), but RE-RUNNING
        // Comskip specifically is exactly what the show/channel block-lists exist to
        // prevent (e.g. a commercial-free premium movie channel) — a forced re-enqueue
        // would silently override that filter as a side effect of an unrelated feature.
        // A non-forced enqueue re-applies the same gate the item passed (or didn't)
        // originally.
        _detectionManager.Enqueue(itemId, force: false);
    }
}
