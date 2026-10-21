using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Editing;

/// <summary>A kept original found in the library folders.</summary>
/// <param name="Path">The hidden backup.</param>
/// <param name="RecordingPath">The recording it's the original of.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Orphan">Its recording is gone (deleted, or moved without it): nothing can undo to it.</param>
public sealed record KeptOriginal(string Path, string RecordingPath, long Bytes, bool Orphan);

/// <summary>
/// Every kept original (see <see cref="TrimBackups"/>) in the library and recording folders,
/// tracked or not: for the dashboard's total and Delete all, and to delete the ones left behind
/// when their recording was deleted or moved without them (Jellyfin deletes a recording's
/// files by name, and the hidden original's name starts with a dot, so it stays).
/// </summary>
public sealed class KeptOriginals
{
    /// <summary>How long an original may be without its recording before it's deleted (a move may be under way).</summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromDays(1);

    private readonly ILogger<KeptOriginals> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly TrimBackups _backups;
    private readonly ConcurrentDictionary<string, DateTime> _orphanSince = new(StringComparer.Ordinal);
    private readonly object _scanLock = new();
    private (DateTime At, List<KeptOriginal> List)? _last;

    public KeptOriginals(ILogger<KeptOriginals> logger, ILibraryManager libraryManager, IServiceProvider serviceProvider, TrimBackups backups)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _serviceProvider = serviceProvider;
        _backups = backups;
    }

    /// <summary>The recording a hidden original belongs to, or null when the name isn't one.</summary>
    public static string? RecordingOf(string backupPath)
    {
        var name = System.IO.Path.GetFileName(backupPath);
        var ext = System.IO.Path.GetExtension(name);
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        if (!name.StartsWith('.') || name.StartsWith("._", StringComparison.Ordinal) || ext.Length == 0 || ext.Equals(".edl", StringComparison.OrdinalIgnoreCase)
            || !stem.EndsWith(".original", StringComparison.Ordinal) || stem.Length <= ".original".Length + 1)
        {
            return null;
        }

        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(backupPath)!, stem[1..^".original".Length] + ext);
    }

    /// <summary>All of them; a scan less than <paramref name="maxAge"/> old is reused.</summary>
    public List<KeptOriginal> List(TimeSpan maxAge)
    {
        lock (_scanLock)
        {
            if (_last is { } last && DateTime.UtcNow - last.At < maxAge)
            {
                return last.List;
            }

            var list = Scan();
            _last = (DateTime.UtcNow, list);
            return list;
        }
    }

    /// <summary>Deletes one kept original and the .edl kept with it; false (logged) when it couldn't.</summary>
    public bool Delete(string backupPath)
    {
        try
        {
            File.Delete(backupPath);
            var edl = TrimBackups.EdlFor(backupPath);
            if (File.Exists(edl))
            {
                File.Delete(edl);
            }

            _backups.Forget(backupPath);
            _orphanSince.TryRemove(backupPath, out _);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the kept original {Path}", backupPath);
            return false;
        }
    }

    /// <summary>The next scan starts afresh (something was deleted or cut).</summary>
    public void Changed()
    {
        lock (_scanLock)
        {
            _last = null;
        }
    }

    /// <summary>Deletes the originals that have been without their recording for <see cref="OrphanGrace"/>.</summary>
    public void DeleteOrphans()
    {
        var list = List(TimeSpan.Zero);
        var now = DateTime.UtcNow;
        foreach (var o in list)
        {
            if (!o.Orphan)
            {
                _orphanSince.TryRemove(o.Path, out _);
                continue;
            }

            var since = _orphanSince.GetOrAdd(o.Path, now);
            if (now - since >= OrphanGrace && Delete(o.Path))
            {
                _logger.LogInformation("Deleted the kept original {Path} ({Bytes} bytes): its recording has been gone since {Since}", o.Path, o.Bytes, since);
            }
        }

        Changed();
    }

    private List<KeptOriginal> Scan()
    {
        var found = new List<KeptOriginal>();
        foreach (var root in Roots())
        {
            try
            {
                foreach (var f in FilesUnder(root))
                {
                    if (RecordingOf(f) is not { } recording)
                    {
                        continue;
                    }

                    long bytes = 0;
                    try
                    {
                        bytes = new FileInfo(f).Length;
                    }
                    catch (IOException)
                    {
                    }

                    found.Add(new KeptOriginal(f, recording, bytes, !File.Exists(recording)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A share that's away: its originals are left alone until it's back.
                _logger.LogDebug(ex, "Could not look for kept originals under {Root}", root);
            }
        }

        return found;
    }

    /// <summary>
    /// The hidden originals under <paramref name="root"/>, leaving out hidden and NAS folders:
    /// Episode History's trash (".jdvr-trash", where a trashed recording keeps its original),
    /// Synology's "#recycle" and "@eaDir".
    /// </summary>
    private static IEnumerable<string> FilesUnder(string root)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var dirs = new Stack<string>();
        dirs.Push(root);
        while (dirs.Count > 0)
        {
            var dir = dirs.Pop();
            foreach (var f in Directory.EnumerateFiles(dir, ".*.original.*", options))
            {
                yield return f;
            }

            foreach (var d in Directory.EnumerateDirectories(dir, "*", options))
            {
                var n = System.IO.Path.GetFileName(d);
                if (n.Length > 0 && n[0] is not ('.' or '#' or '@'))
                {
                    dirs.Push(d);
                }
            }
        }
    }

    /// <summary>The library and recording folders, without ones inside another.</summary>
    private List<string> Roots()
    {
        var roots = _libraryManager.GetVirtualFolders().SelectMany(f => f.Locations).ToList();
        try
        {
            if (_serviceProvider.GetService<IRecordingsManager>() is { } recordings)
            {
                roots.AddRange(recordings.GetRecordingFolders().SelectMany(f => f.Locations));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not list the recording folders");
        }

        var dirs = roots.Where(Directory.Exists).Select(r => System.IO.Path.TrimEndingDirectorySeparator(r) + System.IO.Path.DirectorySeparatorChar).Distinct(StringComparer.Ordinal).ToList();
        return dirs.Where(d => !dirs.Any(o => o != d && d.StartsWith(o, StringComparison.Ordinal))).ToList();
    }
}
