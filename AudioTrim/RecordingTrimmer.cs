using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Editing;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.AudioTrim;

public sealed record TrimOutcome(bool Success, string? BackupPath, string Message);

/// <summary>
/// Runs the actual ffmpeg cut for a <see cref="TrimPlan"/> and swaps the result into
/// place at the recording's original path, so the library item's path never changes.
///
/// Writes ffmpeg's output to a hidden file beside the recording first (so a cut needs room on
/// the recording's own drive, never on the server's system drive, and ends with a rename), and
/// only replaces the recording once that output is verified to exist and be a sane size, so a
/// crash or a full disk mid-run can never leave the original recording missing. Where the
/// recording's folder can't be written, falls back to <paramref name="workDir"/>.
/// </summary>
public sealed class RecordingTrimmer
{
    private readonly ILogger<RecordingTrimmer> _logger;
    private readonly TrimBackups _backups;
    private readonly IMediaEncoder _encoder;

    public RecordingTrimmer(ILogger<RecordingTrimmer> logger, TrimBackups backups, IMediaEncoder encoder)
    {
        _logger = logger;
        _backups = backups;
        _encoder = encoder;
    }

    public async Task<TrimOutcome> ApplyAsync(
        string ffmpegPath,
        string workDir,
        string recordingPath,
        TrimPlan plan,
        bool keepOriginal,
        CancellationToken cancellationToken,
        CutJob? job = null)
    {
        if (!plan.NeedsTrim)
        {
            return new TrimOutcome(false, null, "no trim needed");
        }

        plan = await OnKeyframeAsync(recordingPath, plan, cancellationToken).ConfigureAwait(false);
        var ext = Path.GetExtension(recordingPath);
        var keptBytes = (long)(SafeLength(recordingPath) * (plan.TotalDurationSeconds - plan.LeadTrimSeconds - plan.TrailTrimSeconds) / plan.TotalDurationSeconds);
        var tempOutput = TempBeside(recordingPath, ext) ?? Path.Combine(workDir, $"audiotrim-{Guid.NewGuid():N}{ext}");
        Directory.CreateDirectory(Path.GetDirectoryName(tempOutput)!);

        // Refuse up front rather than fill the drive: a full system drive breaks Jellyfin itself.
        var free = FreeBytes(tempOutput);
        if (free >= 0 && free < keptBytes + MinFreeAfterCut)
        {
            _logger.LogWarning(
                "Not trimming {Path}: the cut needs about {Need:N0} MB and {Folder} has {Free:N0} MB free",
                recordingPath, (keptBytes + MinFreeAfterCut) / 1048576, Path.GetDirectoryName(tempOutput), free / 1048576);
            return new TrimOutcome(false, null, $"not enough free space for the cut ({free / 1073741824.0:F1} GB free, needs {(keptBytes + MinFreeAfterCut) / 1073741824.0:F1} GB)");
        }

        try
        {
            if (job is not null)
            {
                job.TempPath = tempOutput;
                job.Bytes = keptBytes;
                job.Phase = CutJob.PhaseCutting;
            }

            var runResult = await RunFfmpeg(ffmpegPath, recordingPath, tempOutput, plan, cancellationToken).ConfigureAwait(false);
            if (!runResult.Success)
            {
                return new TrimOutcome(false, null, runResult.Message);
            }

            var originalLength = SafeLength(recordingPath);
            var trimmedLength = SafeLength(tempOutput);
            var expectedFraction = (plan.TotalDurationSeconds - plan.LeadTrimSeconds - plan.TrailTrimSeconds) / plan.TotalDurationSeconds;

            // Stream copy, so the trimmed file's size should track the fraction of the
            // recording that was kept, within a generous margin (variable bitrate,
            // container overhead). Well outside that margin means something went wrong
            // (e.g. ffmpeg silently produced a near-empty file) — refuse to swap it in.
            if (trimmedLength <= 0 || trimmedLength < originalLength * expectedFraction * 0.5)
            {
                TryDelete(tempOutput);
                return new TrimOutcome(
                    false, null,
                    $"trimmed output failed a sanity check (expected ~{expectedFraction:P0} of {originalLength} bytes, got {trimmedLength}); left the original untouched");
            }

            // From here the original is replaced: not stoppable any more (a half-done swap
            // could leave no playable file).
            if (job is not null)
            {
                job.Bytes = trimmedLength;
                job.FinalPath = recordingPath;
                job.Phase = CutJob.PhaseFinishing;
            }

            return SwapIntoPlace(recordingPath, tempOutput, keepOriginal);
        }
        finally
        {
            TryDelete(tempOutput);
        }
    }

    private async Task<(bool Success, string Message)> RunFfmpeg(
        string ffmpegPath, string inputPath, string outputPath, TrimPlan plan, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-nostdin");

        if (plan.LeadTrimSeconds > 0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(plan.LeadTrimSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);

        var keepDuration = plan.TotalDurationSeconds - plan.LeadTrimSeconds - plan.TrailTrimSeconds;
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(keepDuration.ToString("F3", CultureInfo.InvariantCulture));

        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-avoid_negative_ts");
        psi.ArgumentList.Add("make_zero");

        // Recordings keep a .mpg extension from Channels DVR even though the actual
        // container is MPEG-TS. Without an explicit -f, ffmpeg picks the muxer from
        // that extension and silently downgrades the output to MPEG-PS, which packs
        // AC3 into "private_stream_1" PES packets badly enough that players like VLC
        // misread the single 5.1 track as dozens of phantom audio tracks. Force TS so
        // the container always matches the source regardless of the file's extension.
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("mpegts");
        psi.ArgumentList.Add(outputPath);

        var log = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped: ffmpeg would otherwise keep writing a copy nobody will use.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            _logger.LogInformation("Trim of {Path} stopped", inputPath);
            return (false, "stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch ffmpeg for audio-trim of {Path}", inputPath);
            return (false, $"could not launch ffmpeg: {ex.Message}");
        }

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            _logger.LogWarning(
                "ffmpeg audio-trim failed for {Path} (exit code {ExitCode}).\n--- ffmpeg output (tail) ---\n{Output}",
                inputPath, process.ExitCode, Tail(log.ToString(), 25));
            return (false, $"ffmpeg exited {process.ExitCode}");
        }

        return (true, "ok");
    }

    /// <summary>
    /// Renames the original aside as the hidden backup (see <see cref="TrimBackups"/>) and moves
    /// the trimmed file into its exact place. The backup is kept for good with
    /// <paramref name="keepOriginal"/>, otherwise for the configured undo days (or deleted
    /// right away when that's 0). The trimmed file may be on a different filesystem than the
    /// recordings folder (when it was cut in the work directory), so falls back
    /// to copy+delete when a plain rename can't cross volumes.
    /// </summary>
    private TrimOutcome SwapIntoPlace(string recordingPath, string trimmedPath, bool keepOriginal)
    {
        // A second cut of the same recording (an audio fix, then a program start): the backup
        // already holds the recording as it was recorded, which is what Undo should bring back,
        // so the once-cut file is dropped instead of becoming the backup.
        var earlier = TrimBackups.Find(recordingPath);
        var backupPath = earlier ?? TrimBackups.PathFor(recordingPath);

        try
        {
            if (earlier is not null)
            {
                File.Delete(recordingPath);
            }
            else
            {
                File.Move(recordingPath, backupPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not move original recording aside before swapping in trimmed audio: {Path}", recordingPath);
            return new TrimOutcome(false, null, $"could not move original aside: {ex.Message}");
        }

        try
        {
            MoveAcrossVolumes(trimmedPath, recordingPath);
        }
        catch (Exception ex)
        {
            // Put the original back so the item is never left without a playable file.
            _logger.LogError(ex, "Could not move trimmed file into place for {Path}; restoring original", recordingPath);
            try
            {
                File.Move(backupPath, recordingPath, overwrite: true);
            }
            catch (Exception restoreEx)
            {
                _logger.LogCritical(restoreEx, "Failed to restore original recording after a failed swap: {Path} (backup at {Backup})", recordingPath, backupPath);
            }

            return new TrimOutcome(false, null, $"could not move trimmed file into place: {ex.Message}");
        }

        if (keepOriginal)
        {
            _backups.Forget(backupPath);
            return new TrimOutcome(true, backupPath, $"trim applied; original kept at {backupPath}");
        }

        var days = Plugin.Instance?.Configuration.TrimUndoDays ?? 0;
        if (days <= 0)
        {
            TryDelete(backupPath);
            _backups.Forget(backupPath);
            return new TrimOutcome(true, null, "trim applied; original discarded");
        }

        // A second cut keeps the first one's deadline: the original has been waiting since then.
        if (_backups.ExpiresUtc(backupPath) is null)
        {
            _backups.Expire(backupPath, DateTime.UtcNow.AddDays(days));
        }

        return new TrimOutcome(true, backupPath, $"trim applied; original kept {days} days for Undo at {backupPath}");
    }

    /// <summary>
    /// Moves the start of the cut to the keyframe at or before it (see <see cref="Keyframes"/>):
    /// started between keyframes, the picture would only appear at the next one, seconds into
    /// the sound.
    /// </summary>
    private async Task<TrimPlan> OnKeyframeAsync(string recordingPath, TrimPlan plan, CancellationToken ct)
    {
        if (plan.LeadTrimSeconds <= 0)
        {
            return plan;
        }

        double? key;
        try
        {
            key = await Keyframes.AtOrBeforeAsync(_encoder.ProbePath, recordingPath, plan.LeadTrimSeconds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't read the keyframes of {Path}; cutting at {Lead:F2}s as asked", recordingPath, plan.LeadTrimSeconds);
            return plan;
        }

        if (key is null)
        {
            return plan;
        }

        var lead = Math.Max(0, key.Value - Keyframes.Margin);
        _logger.LogInformation("Cut of {Path} starts on the keyframe at {Key:F2}s (asked {Lead:F2}s)", recordingPath, key.Value, plan.LeadTrimSeconds);
        return plan with { LeadTrimSeconds = lead, CoreDurationSeconds = plan.TotalDurationSeconds - lead - plan.TrailTrimSeconds };
    }

    // Room left on the drive once a cut is written, so the cut itself never fills it.
    private const long MinFreeAfterCut = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// The hidden file a cut of <paramref name="recordingPath"/> is written to, in the recording's
    /// own folder (Jellyfin's library scan skips dot files); null when that folder can't be
    /// written. A cut left behind by a crash or a restart is removed first.
    /// </summary>
    private string? TempBeside(string recordingPath, string ext)
    {
        var folder = Path.GetDirectoryName(recordingPath);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        var prefix = "." + Path.GetFileNameWithoutExtension(recordingPath) + ".cutting-";
        try
        {
            foreach (var stale in Directory.EnumerateFiles(folder, prefix + "*"))
            {
                _logger.LogInformation("Removing an unfinished cut left behind: {Path}", stale);
                TryDelete(stale);
            }

            var path = Path.Combine(folder, $"{prefix}{Guid.NewGuid():N}{ext}");
            using (File.Create(path))
            {
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation("Can't write beside {Path} ({Message}); cutting in the work directory", recordingPath, ex.Message);
            return null;
        }
    }

    /// <summary>Free bytes on the drive holding <paramref name="path"/>, or -1 when unknown.</summary>
    private static long FreeBytes(string path)
    {
        try
        {
            return new DriveInfo(Path.GetDirectoryName(path)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }

    private static void MoveAcrossVolumes(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite: true);
            File.Delete(source);
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Tail(string text, int lines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(no output captured)";
        }

        var all = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        var start = Math.Max(0, all.Length - lines);
        return string.Join('\n', all[start..]);
    }
}
