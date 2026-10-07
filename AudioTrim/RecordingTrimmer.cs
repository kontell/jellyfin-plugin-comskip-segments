using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Editing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.AudioTrim;

public sealed record TrimOutcome(bool Success, string? BackupPath, string Message);

/// <summary>
/// Runs the actual ffmpeg cut for a <see cref="TrimPlan"/> and swaps the result into
/// place at the recording's original path, so the library item's path never changes.
///
/// Always writes ffmpeg's output to <paramref name="workDir"/> first (a location this
/// plugin already treats as guaranteed-writable — see
/// <see cref="Configuration.PluginConfiguration.WorkDirectory"/>) and only touches the
/// recordings folder itself once that output is verified to exist and be a sane size,
/// so a crash or a full disk mid-run can never leave the original recording missing.
/// </summary>
public sealed class RecordingTrimmer
{
    private readonly ILogger<RecordingTrimmer> _logger;
    private readonly TrimBackups _backups;

    public RecordingTrimmer(ILogger<RecordingTrimmer> logger, TrimBackups backups)
    {
        _logger = logger;
        _backups = backups;
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

        Directory.CreateDirectory(workDir);

        var ext = Path.GetExtension(recordingPath);
        var tempOutput = Path.Combine(workDir, $"audiotrim-{Guid.NewGuid():N}{ext}");

        try
        {
            if (job is not null)
            {
                job.TempPath = tempOutput;
                job.Bytes = (long)(SafeLength(recordingPath) * (plan.TotalDurationSeconds - plan.LeadTrimSeconds - plan.TrailTrimSeconds) / plan.TotalDurationSeconds);
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
    /// recordings folder (the work directory doesn't have to be the same volume), so falls back
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
