using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

/// <summary>
/// Tracks the live, still-running Comskip processes started by
/// <see cref="LiveScanStarter"/> against recordings that are still being written, keyed
/// by recording path. <see cref="TryHarvest"/> is how <see cref="DetectionManager"/>
/// collects the result once a recording settles, instead of running a fresh full-file
/// scan.
///
/// A live job is launched exactly like <see cref="Comskip.ComskipRunner"/> launches a
/// normal one (same args, same work dir, same output-file naming), except it is never
/// awaited to completion — the intent is that Comskip's own <c>live_tv=1</c> mode tails a
/// growing file and only exits when the process is killed, flushing a valid,
/// continuously-updated partial .edl to disk the whole time (confirmed in testing:
/// readable even after a hard kill).
///
/// In practice, confirmed live against a real recording, Comskip can also exit ON ITS OWN
/// well before the recording finishes — observed exiting after ~2 minutes of a 21-minute
/// recording, apparently hitting comskip.ini's <c>live_tv_retries</c> limit on a pause in
/// the writer's pacing (the ini's own comment suggests raising it, e.g. "Change to 16 when
/// using live_tv in BTV"). A self-exited process's .edl only covers whatever it saw before
/// it gave up — silently trusting it as "the" result for the whole recording would be
/// **worse** than not having this feature at all. So every path that would hand a job's
/// .edl to a caller first checks whether the process is still alive: still running means
/// we killed it ourselves because the recording actually settled (the intended path,
/// trustworthy); already exited on its own means the partial is unreliable and is
/// discarded, falling back to a full-file scan instead.
/// </summary>
public sealed class LiveScanCoordinator : IDisposable
{
    /// <summary>
    /// Safety net for a job nothing ever harvests (feature toggled off mid-recording,
    /// the recording watcher's settle check never fires, a plugin bug). Without this, an
    /// abandoned live Comskip process would run forever.
    /// </summary>
    private static readonly TimeSpan MaxJobAge = TimeSpan.FromHours(12);

    private readonly ILogger<LiveScanCoordinator> _logger;
    private readonly ConcurrentDictionary<string, LiveJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public LiveScanCoordinator(ILogger<LiveScanCoordinator> logger) => _logger = logger;

    /// <summary>True if a live job is already tracked for this path.</summary>
    public bool IsTracking(string path) => _jobs.ContainsKey(Normalize(path));

    /// <summary>
    /// Launch a live-tuned Comskip process against <paramref name="mediaPath"/> and
    /// start tracking it. No-op if one is already tracked for this path.
    /// </summary>
    public void Start(string comskipPath, string iniPath, string workDir, string mediaPath)
    {
        if (_disposed)
        {
            return;
        }

        var key = Normalize(mediaPath);
        if (_jobs.ContainsKey(key))
        {
            return;
        }

        comskipPath = comskipPath.Trim();
        iniPath = iniPath.Trim();
        workDir = workDir.Trim();

        try
        {
            Directory.CreateDirectory(workDir);

            var psi = new ProcessStartInfo
            {
                FileName = comskipPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            };
            psi.ArgumentList.Add($"--ini={iniPath}");
            psi.ArgumentList.Add($"--output={workDir}");
            psi.ArgumentList.Add(mediaPath);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = false };
            var log = new StringBuilder();

            // Must drain these even though we normally don't care about the content — an
            // unread, full redirected-output pipe would eventually block Comskip's own
            // writes for a job that can run for hours. Kept (bounded) so a self-exit can
            // be diagnosed instead of just logged as an unexplained surprise.
            process.OutputDataReceived += (_, e) => AppendBounded(log, e.Data);
            process.ErrorDataReceived += (_, e) => AppendBounded(log, e.Data);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var baseName = Path.GetFileNameWithoutExtension(mediaPath);
            var edlPath = Path.Combine(workDir, baseName + ".edl");
            var job = new LiveJob(process, edlPath, DateTime.UtcNow, log);

            if (!_jobs.TryAdd(key, job))
            {
                // Lost a race with another Start() call for the same path; keep the
                // other one, discard ours.
                TryKill(process);
                return;
            }

            _logger.LogInformation("Started live Comskip scan for in-progress recording {Path}", mediaPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start live Comskip scan for {Path}", mediaPath);
        }
    }

    /// <summary>
    /// Stop tracking the live job for <paramref name="mediaPath"/> (if any) and return the
    /// .edl it already produced. Returns null — caller falls back to a normal full-file
    /// Comskip run — if no live job was tracked for this path, it never produced an .edl,
    /// or (see class remarks) it had already exited on its own, which makes its partial
    /// .edl unreliable rather than a trustworthy stand-in for the whole recording.
    /// </summary>
    /// <summary>
    /// The partial .edl a live job is writing for a recording still in progress, for
    /// showing "commercials found so far". Null when no live job tracks this path, its
    /// process already gave up (see class remarks), or it hasn't written anything yet.
    /// </summary>
    public string? GetLiveEdl(string mediaPath)
    {
        if (!_jobs.TryGetValue(Normalize(mediaPath), out var job))
        {
            return null;
        }

        try
        {
            return !job.Process.HasExited && File.Exists(job.EdlPath) ? job.EdlPath : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public string? TryHarvest(string mediaPath)
    {
        if (!_jobs.TryRemove(Normalize(mediaPath), out var job))
        {
            return null;
        }

        // Still running means we're the ones ending it, because the recording actually
        // settled — the intended, trustworthy path. Already exited means it gave up on
        // its own at some earlier, unknown point; do not use its .edl.
        var selfExited = job.Process.HasExited;

        TryKill(job.Process);
        job.Process.Dispose();

        if (selfExited)
        {
            LogSelfExit(mediaPath, job);
            return null;
        }

        if (File.Exists(job.EdlPath))
        {
            _logger.LogInformation("Harvested live Comskip result for {Path}", mediaPath);
            return job.EdlPath;
        }

        _logger.LogInformation(
            "Live Comskip scan for {Path} produced no .edl (recording may have been too short); falling back to a full scan",
            mediaPath);
        return null;
    }

    /// <summary>
    /// Kill and drop any tracked job whose process has already exited on its own (see
    /// class remarks), whose source file is gone, or that's run past a sanity ceiling.
    /// </summary>
    public void PruneStale()
    {
        var cutoff = DateTime.UtcNow - MaxJobAge;
        foreach (var kv in _jobs)
        {
            var path = kv.Key;
            var job = kv.Value;

            bool hasExited;
            try
            {
                hasExited = job.Process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // Lost a race with a concurrent TryHarvest()/Dispose() for this same job
                // (it already removed and disposed the Process); nothing left to prune.
                continue;
            }

            if (hasExited)
            {
                if (_jobs.TryRemove(path, out _))
                {
                    LogSelfExit(path, job);
                    job.Process.Dispose();
                }

                continue;
            }

            var stale = !File.Exists(path) || job.StartedUtc < cutoff;
            if (!stale)
            {
                continue;
            }

            if (_jobs.TryRemove(path, out _))
            {
                _logger.LogWarning("Abandoning stale live Comskip scan for {Path}", path);
                TryKill(job.Process);
                job.Process.Dispose();
            }
        }
    }

    private void LogSelfExit(string mediaPath, LiveJob job)
    {
        int? exitCode = null;
        try
        {
            exitCode = job.Process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        _logger.LogWarning(
            "Live Comskip scan for {Path} exited on its own (exit code {ExitCode}) before the recording "
            + "finished, well short of a full pass — likely comskip.ini's live_tv_retries giving up on a "
            + "pause in the recording writer. Discarding its partial result and falling back to a full scan "
            + "once the recording settles; consider raising live_tv_retries if this recurs.\n"
            + "--- Comskip output (tail) ---\n{Output}",
            mediaPath,
            exitCode,
            Tail(job.Log.ToString(), 20));
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

    private static void AppendBounded(StringBuilder log, string? line)
    {
        if (line is null)
        {
            return;
        }

        // Bounded so an hours-long job's captured output can't grow unreasonably; only
        // the tail is ever surfaced (in LogSelfExit), so trimming the front is fine.
        const int MaxLength = 8192;
        if (log.Length > MaxLength)
        {
            log.Remove(0, log.Length - MaxLength);
        }

        log.AppendLine(line);
    }

    private static string Normalize(string path) => path.Trim();

    private void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not kill live Comskip process");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var job in _jobs.Values.ToList())
        {
            TryKill(job.Process);
            job.Process.Dispose();
        }

        _jobs.Clear();
    }

    private sealed record LiveJob(Process Process, string EdlPath, DateTime StartedUtc, StringBuilder Log);
}
