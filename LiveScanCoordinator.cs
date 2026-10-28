using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Comskip;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Detection;

/// <summary>
/// Tracks the live, still-running Comskip processes started by
/// <see cref="LiveScanStarter"/> against recordings that are still being written, keyed
/// by recording path, and owns what each of them has found so far
/// (<see cref="GetLiveBreaks"/>): the one answer the segment provider, the guide badges
/// and <c>/ComskipSegments/Live</c> all give. <see cref="TryHarvest"/> is how
/// <see cref="DetectionManager"/> collects the result once a recording settles, instead
/// of running a fresh full-file scan.
///
/// A live job is launched exactly like <see cref="Comskip.ComskipRunner"/> launches a
/// normal one (same args, same work dir, same output-file naming), except it is never
/// awaited to completion — the intent is that Comskip's own <c>live_tv=1</c> mode tails a
/// growing file and only exits when the process is killed, flushing a valid,
/// continuously-updated partial .edl to disk the whole time (confirmed in testing:
/// readable even after a hard kill). Comskip rewrites that file from scratch
/// (<c>"wb"</c>) every so many seconds of media, so a read can catch it empty or cut
/// short; a read counts only when the file was unchanged across it, and an empty one
/// never replaces a list that had breaks.
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
/// discarded, falling back to a full-file scan instead. While the recording is still in
/// progress a self-exited job is dropped and the starter brings it back on its next
/// pass, up to <see cref="MaxRestarts"/> times per recording; the restarted process
/// re-reads the file from the start, so until it has caught up its list is a prefix,
/// and breaks already known beyond that prefix are kept rather than withdrawn.
/// </summary>
public sealed class LiveScanCoordinator : IDisposable
{
    /// <summary>
    /// Safety net for a job nothing ever harvests (feature toggled off mid-recording,
    /// the recording watcher's settle check never fires, a plugin bug). Without this, an
    /// abandoned live Comskip process would run forever.
    /// </summary>
    private static readonly TimeSpan MaxJobAge = TimeSpan.FromHours(12);

    /// <summary>
    /// How many times a live job that exited on its own is started again for the same
    /// recording. Counted on the path, not the job: <see cref="LiveScanStarter"/> starts
    /// a job for any in-progress recording that is not tracked, which is how a
    /// self-exited one comes back, so a count on the job would reset with it.
    /// </summary>
    private const int MaxRestarts = 2;

    // A live scan must have read at least this much of the recording for its list to be
    // the whole picture: at harvest to be trusted, and while recording to be allowed to
    // withdraw breaks already known (a restarted process starts from the beginning).
    private const double MinHarvestProgress = 0.97;

    private readonly ILogger<LiveScanCoordinator> _logger;
    private readonly ConcurrentDictionary<string, LiveJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _restarts = new(StringComparer.OrdinalIgnoreCase);

    // Orders the live publishes and the harvest: a publish in flight finishes before
    // the harvest drops the job, and none starts after (the publisher re-checks
    // IsTracking under it), so a live list can never land on top of the final result.
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private bool _disposed;

    public LiveScanCoordinator(ILogger<LiveScanCoordinator> logger) => _logger = logger;

    /// <summary>True if a live job is already tracked for this path.</summary>
    public bool IsTracking(string path) => _jobs.ContainsKey(Normalize(path));

    /// <summary>The recording paths with a live job tracked right now.</summary>
    public IReadOnlyList<string> TrackedPaths() => _jobs.Keys.ToList();

    /// <summary>
    /// Launch a live-tuned Comskip process against <paramref name="mediaPath"/> and
    /// start tracking it. No-op if one is already tracked for this path, or if the
    /// recording has used up its restarts (see class remarks).
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

        var restarts = _restarts.GetValueOrDefault(key);
        if (restarts > MaxRestarts)
        {
            return; // gave up on this recording; said so when the count ran out
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
            var log = new OutputTail();

            // Must drain these even though we normally don't care about the content — an
            // unread, full redirected-output pipe would eventually block Comskip's own
            // writes for a job that can run for hours. Kept (bounded) so a self-exit can
            // be diagnosed instead of just logged as an unexplained surprise.
            process.OutputDataReceived += (_, e) => log.Add(e.Data);
            process.ErrorDataReceived += (_, e) => log.Add(e.Data);

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

            if (restarts == 0)
            {
                _logger.LogInformation("Started live Comskip scan for in-progress recording {Path}", mediaPath);
            }
            else
            {
                _logger.LogInformation("Restarted live Comskip scan for in-progress recording {Path} ({Restart} of {Max})", mediaPath, restarts, MaxRestarts);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start live Comskip scan for {Path}", mediaPath);
        }
    }

    /// <summary>
    /// The commercial breaks the live job for <paramref name="mediaPath"/> has found so
    /// far, in order, with the configured padding applied. Null when no live job tracks
    /// this path, its process already gave up (see class remarks), or it has nothing to
    /// say yet. A read that caught Comskip mid-rewrite, or an empty one after a list that
    /// had breaks, returns the previous answer; while a restarted process is still
    /// catching up, breaks it has not reached yet are kept from the previous answer.
    /// </summary>
    public IReadOnlyList<CommercialBreak>? GetLiveBreaks(string mediaPath)
    {
        if (!_jobs.TryGetValue(Normalize(mediaPath), out var job))
        {
            return null;
        }

        try
        {
            if (job.Process.HasExited)
            {
                return null;
            }
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (!TryReadStable(job.EdlPath, out var text))
        {
            return job.LastBreaks;
        }

        var padding = Plugin.Instance?.Configuration.PaddingSeconds ?? 0;
        var pass = EdlParser.ParseLines(text.Split('\n'), padding);
        if (pass.Count == 0 && job.LastBreaks is { Count: > 0 })
        {
            return job.LastBreaks; // Comskip's live list only ever grows: an empty read is a truncated one
        }

        IReadOnlyList<CommercialBreak> result = pass;
        if (job.LastBreaks is { Count: > 0 } last && ReadProgress(job.Process.Id, mediaPath) is < MinHarvestProgress)
        {
            // Catching up after a restart: this list is a prefix of what the previous
            // process had seen. Keep the breaks beyond where it has reached.
            var reach = pass.Count > 0 ? pass[^1].StartTicks : -1;
            result = pass.Concat(last.Where(b => b.StartTicks > reach)).OrderBy(b => b.StartTicks).ToList();
        }

        job.LastBreaks = result;
        return result;
    }

    /// <summary>
    /// Hold this around a live publish, and around <see cref="TryHarvest"/>, so neither
    /// overlaps the other; re-check <see cref="IsTracking"/> inside it before publishing.
    /// </summary>
    public async Task<IDisposable> LockPublishAsync()
    {
        await _publishGate.WaitAsync().ConfigureAwait(false);
        return new Releaser(_publishGate);
    }

    /// <summary>
    /// How far (0-1) the live Comskip process has read into the recording, from the kernel's
    /// file offset for its open handle (/proc/pid/fdinfo); null when it can't be told
    /// (not Linux, file not open, no access), in which case the result is used as before.
    /// </summary>
    private double? ReadProgress(int pid, string mediaPath)
    {
        try
        {
            var size = new FileInfo(mediaPath).Length;
            if (size <= 0 || !Directory.Exists($"/proc/{pid}/fd"))
            {
                return null;
            }

            long? position = null;
            foreach (var fd in Directory.GetFiles($"/proc/{pid}/fd"))
            {
                if (!string.Equals(new FileInfo(fd).LinkTarget, mediaPath, StringComparison.Ordinal))
                {
                    continue;
                }

                var posLine = File.ReadLines($"/proc/{pid}/fdinfo/{Path.GetFileName(fd)}")
                    .FirstOrDefault(l => l.StartsWith("pos:", StringComparison.Ordinal));
                if (posLine is not null && long.TryParse(posLine.AsSpan(4).Trim(), out var pos))
                {
                    position = Math.Max(position ?? 0, pos);
                }
            }

            return position is null ? null : Math.Min(1.0, (double)position.Value / size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read live Comskip progress for {Path}", mediaPath);
            return null;
        }
    }

    /// <summary>
    /// Reads a file Comskip may be rewriting at this instant: the read counts only when
    /// the file's size and write time were the same before and after it.
    /// </summary>
    private static bool TryReadStable(string path, out string text)
    {
        text = string.Empty;
        try
        {
            var before = new FileInfo(path);
            if (!before.Exists)
            {
                return false;
            }

            var (length, written) = (before.Length, before.LastWriteTimeUtc);
            text = File.ReadAllText(path);
            var after = new FileInfo(path);
            return after.Exists && after.Length == length && after.LastWriteTimeUtc == written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasAnyBreak(string edlPath)
    {
        try
        {
            return File.ReadLines(edlPath).Any(l => !string.IsNullOrWhiteSpace(l));
        }
        catch (IOException)
        {
            return true; // unreadable right now: don't second-guess it here
        }
    }

    /// <summary>
    /// Stop tracking the live job for <paramref name="mediaPath"/> (if any) and return the
    /// .edl it already produced. Returns null — caller falls back to a normal full-file
    /// Comskip run — if no live job was tracked for this path, it never produced an .edl,
    /// or (see class remarks) it had already exited on its own, which makes its partial
    /// .edl unreliable rather than a trustworthy stand-in for the whole recording. Hold
    /// <see cref="LockPublishAsync"/> around the call.
    /// </summary>
    public string? TryHarvest(string mediaPath)
    {
        var key = Normalize(mediaPath);
        _restarts.TryRemove(key, out _);
        if (!_jobs.TryRemove(key, out var job))
        {
            return null;
        }

        // Still running means we're the ones ending it, because the recording actually
        // settled — the intended, trustworthy path. Already exited means it gave up on
        // its own at some earlier, unknown point; do not use its .edl.
        var selfExited = job.Process.HasExited;

        // Alive isn't enough: a live scan can fall behind the recording (seen live: a busy
        // server, two back-to-back recordings plus Intro Skipper, left one still reading the
        // early part of the file when the recording ended; its result said "no commercials",
        // a full scan found 3). Read how far into the file it got before stopping it.
        var progress = selfExited ? null : ReadProgress(job.Process.Id, mediaPath);

        TryKill(job.Process);
        job.Process.Dispose();

        if (selfExited)
        {
            LogSelfExit(mediaPath, job, restarting: false);
            return null;
        }

        if (progress is < MinHarvestProgress)
        {
            _logger.LogInformation(
                "Live Comskip scan for {Path} had only read {Percent:P0} of the recording; discarding it for a full scan",
                mediaPath, progress);
            return null;
        }

        if (File.Exists(job.EdlPath) && !HasAnyBreak(job.EdlPath))
        {
            // Ad-free channels are normally skipped by the channel lists before a live scan
            // starts, so "none at all" is more likely a scan that missed them: confirm it.
            _logger.LogInformation("Live Comskip scan for {Path} found no breaks; confirming with a full scan", mediaPath);
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
    /// Drop any tracked job whose process has already exited on its own (see class
    /// remarks; the starter brings it back while the recording is in progress, up to
    /// <see cref="MaxRestarts"/> times), and kill and drop one whose source file is
    /// gone or that's run past a sanity ceiling.
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
                    var restarts = _restarts.AddOrUpdate(path, 1, (_, count) => count + 1);
                    LogSelfExit(path, job, restarting: restarts <= MaxRestarts);
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
                _restarts.TryRemove(path, out _);
                _logger.LogWarning("Abandoning stale live Comskip scan for {Path}", path);
                TryKill(job.Process);
                job.Process.Dispose();
            }
        }
    }

    private void LogSelfExit(string mediaPath, LiveJob job, bool restarting)
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
            + "pause in the recording writer. Its partial result is discarded; {Next} The full scan once "
            + "the recording settles is the final word either way; consider raising live_tv_retries if this recurs.\n"
            + "--- Comskip output (tail) ---\n{Output}",
            mediaPath,
            exitCode,
            restarting
                ? "it is started again while the recording is still in progress."
                : $"it has been restarted {MaxRestarts} times already, so live detection stops for this recording.",
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
        _restarts.Clear();
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _gate;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => _gate.Release();
    }

    private sealed record LiveJob(Process Process, string EdlPath, DateTime StartedUtc, OutputTail Log)
    {
        /// <summary>The last answer given for this job, so a bad read or a catch-up prefix never shrinks it.</summary>
        public IReadOnlyList<CommercialBreak>? LastBreaks { get; set; }
    }
}
