using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Editing;

/// <summary>
/// The folders a deleted kept original leaves behind. A recording moved or deleted without its
/// hidden original keeps its folder (the original counts as a video there); once the original
/// goes too, the folder holds only artwork and .nfo files, and the library shows an empty movie,
/// season or show.
/// </summary>
public static class EmptiedFolders
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".mkv", ".mp4", ".m4v", ".m2ts", ".mts", ".mpg", ".mpeg", ".avi", ".wtv", ".mov", ".webm", ".vob", ".iso", ".wmv", ".strm"
    };

    /// <summary>
    /// Removes <paramref name="dir"/> when no video is left anywhere in it, then its parent
    /// (a show above a season) the same way, up to but never one of <paramref name="roots"/>
    /// (each with a trailing separator) nor a folder outside them. The folders removed.
    /// </summary>
    public static List<string> Remove(string dir, IReadOnlyList<string> roots, ILogger logger)
    {
        var removed = new List<string>();
        while (Inside(dir, roots) && Directory.Exists(dir))
        {
            try
            {
                if (Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(f => VideoExtensions.Contains(Path.GetExtension(f))))
                {
                    break;
                }

                Directory.Delete(dir, recursive: true);
                logger.LogInformation("Removed the emptied folder {Folder}", dir);
                removed.Add(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove the emptied folder {Folder}", dir);
                break;
            }

            dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir))!;
        }

        return removed;
    }

    private static bool Inside(string dir, IReadOnlyList<string> roots)
    {
        var d = Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar;
        return roots.Any(r => d.Length > r.Length && d.StartsWith(r, StringComparison.Ordinal));
    }
}
