using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.AudioTrim;

public sealed record AudioTrimApplyResult(
    bool Attempted, bool Trimmed, double LeadTrimSeconds, double TrailTrimSeconds, string Message)
{
    public static readonly AudioTrimApplyResult Skipped = new(false, false, 0, 0, "already processed");
}

/// <summary>
/// Orchestrates one item's audio-format trim end to end: scan, decide, cut, swap, and
/// (best-effort) tell Jellyfin to re-probe the file. Deliberately has NO dependency on
/// <see cref="Detection.DetectionManager"/> or <see cref="Detection.DetectionStore"/> —
/// callers that need to invalidate/re-run Comskip after a trim shifts an
/// already-published timeline (the manual per-item path; the automatic path never has
/// this problem because the trim runs before Comskip ever sees the item) do that
/// themselves, which keeps this service usable from either without a constructor cycle.
/// </summary>
public sealed class AudioTrimService
{
    private readonly ILogger<AudioTrimService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly AudioChannelScanner _scanner;
    private readonly RecordingTrimmer _trimmer;
    private readonly AudioTrimStore _store;

    public AudioTrimService(
        ILogger<AudioTrimService> logger,
        IServiceProvider serviceProvider,
        AudioChannelScanner scanner,
        RecordingTrimmer trimmer,
        AudioTrimStore store)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _scanner = scanner;
        _trimmer = trimmer;
        _store = store;
    }

    /// <summary>
    /// Scans (and, if warranted, trims) <paramref name="item"/>'s recording. No-ops if
    /// this item already has a completed (Applied or NotNeeded) record, unless
    /// <paramref name="force"/> is set — the same "manual bypasses the cache" convention
    /// as <see cref="Detection.DetectionManager.Enqueue"/>.
    /// </summary>
    public async Task<AudioTrimApplyResult> ApplyIfNeededAsync(
        BaseItem item, PluginConfiguration cfg, bool force, CancellationToken cancellationToken, CutJob? job = null)
    {
        if (!force && _store.IsCompleted(item.Id))
        {
            return AudioTrimApplyResult.Skipped;
        }

        if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            return new AudioTrimApplyResult(true, false, 0, 0, "item has no resolvable file on disk");
        }

        var mediaEncoder = _serviceProvider.GetService<IMediaEncoder>();
        if (mediaEncoder is null || string.IsNullOrWhiteSpace(mediaEncoder.ProbePath) || string.IsNullOrWhiteSpace(mediaEncoder.EncoderPath))
        {
            const string msg = "could not resolve Jellyfin's ffmpeg/ffprobe paths (IMediaEncoder)";
            _logger.LogWarning("Audio-trim scan skipped for {Path}: {Message}", item.Path, msg);
            _store.Save(item.Id, new AudioTrimRecord(AudioTrimStatus.Failed, 0, 0, null, msg, DateTime.UtcNow));
            return new AudioTrimApplyResult(true, false, 0, 0, msg);
        }

        TrimPlan plan;
        try
        {
            plan = await _scanner.ScanAsync(
                mediaEncoder.ProbePath,
                item.Path,
                cfg.AudioTrimMinCoreFraction,
                cfg.AudioTrimMinJunkSeconds,
                cfg.AudioTrimPaddingSeconds,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Audio-trim channel scan failed for {Path}", item.Path);
            _store.Save(item.Id, new AudioTrimRecord(AudioTrimStatus.Failed, 0, 0, null, $"scan failed: {ex.Message}", DateTime.UtcNow));
            return new AudioTrimApplyResult(true, false, 0, 0, $"scan failed: {ex.Message}");
        }

        if (!plan.NeedsTrim)
        {
            _logger.LogInformation("Audio-trim: nothing to do for {Path} ({Reason})", item.Path, plan.Reason);
            _store.Save(item.Id, new AudioTrimRecord(AudioTrimStatus.NotNeeded, 0, 0, null, plan.Reason, DateTime.UtcNow));
            return new AudioTrimApplyResult(true, false, 0, 0, plan.Reason);
        }

        _logger.LogInformation("Audio-trim: applying to {Path} ({Reason})", item.Path, plan.Reason);
        var outcome = await _trimmer.ApplyAsync(
            mediaEncoder.EncoderPath,
            cfg.WorkDirectory,
            item.Path,
            plan,
            cfg.AudioTrimKeepOriginal,
            cancellationToken,
            job).ConfigureAwait(false);

        if (!outcome.Success)
        {
            _logger.LogWarning("Audio-trim: ffmpeg cut/swap failed for {Path}: {Message}", item.Path, outcome.Message);
            _store.Save(item.Id, new AudioTrimRecord(AudioTrimStatus.Failed, 0, 0, null, outcome.Message, DateTime.UtcNow));
            return new AudioTrimApplyResult(true, false, 0, 0, outcome.Message);
        }

        _store.Save(
            item.Id,
            new AudioTrimRecord(AudioTrimStatus.Applied, plan.LeadTrimSeconds, plan.TrailTrimSeconds, outcome.BackupPath, outcome.Message, DateTime.UtcNow));

        await RefreshMetadataBestEffort(item, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Audio-trim: applied to {Path} (-{Lead:F1}s lead, -{Trail:F1}s trail)",
            item.Path, plan.LeadTrimSeconds, plan.TrailTrimSeconds);

        return new AudioTrimApplyResult(true, true, plan.LeadTrimSeconds, plan.TrailTrimSeconds, outcome.Message);
    }

    /// <summary>
    /// Forces Jellyfin to re-probe the file (new duration, new audio channel count) now
    /// that it's been rewritten out from under the library item. Resolved lazily via
    /// <see cref="IServiceProvider"/>, same rationale as everywhere else in this plugin
    /// that touches Jellyfin's provider graph — see the note on
    /// <see cref="Detection.DetectionManager"/>'s constructor.
    /// </summary>
    private async Task RefreshMetadataBestEffort(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            var fileSystem = _serviceProvider.GetService<IFileSystem>();
            if (fileSystem is null)
            {
                _logger.LogWarning("Could not resolve IFileSystem; skipping metadata refresh for {Path}", item.Path);
                return;
            }

            var directoryService = new DirectoryService(fileSystem);
            var refreshOptions = new MetadataRefreshOptions(directoryService)
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllMetadata = false,
                ForceSave = true,
                EnableRemoteContentProbe = true
            };

            await item.RefreshMetadata(refreshOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trim applied but metadata refresh failed for {Path}; audio channel count may show stale until the next library scan", item.Path);
        }
    }
}
