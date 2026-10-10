using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComskipSegments.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Comskip;

/// <summary>
/// "Install Comskip" on the settings page: downloads a self-contained Comskip built from the
/// upstream GPL-2.0 source (this repository's "Build Comskip" release, see comskip/README.md)
/// for this server's processor, checks it against the checksum pinned below, puts it with a
/// default comskip.ini and a work folder in Jellyfin's data folder, and fills in the three paths.
/// An existing comskip.ini there is kept (it may have been tuned). Linux x86-64 and arm64 only;
/// elsewhere, Comskip is installed by hand and its path set as before.
/// </summary>
public sealed class ComskipInstaller
{
    // The release the button installs. A new Comskip build means new values here (the
    // checksums are the .sha256 files published with it) and a plugin release.
    public const string Version = "0.83.001";
    private const string ReleaseTag = "comskip-0.83.001-a140b6a";
    private const string ReleaseUrl = "https://github.com/mackandall/jellyfin-plugin-comskip-segments/releases/download/" + ReleaseTag + "/";
    private const string Sha256X64 = "96c923bfe03c6d45aafab2eb3123beecc883abc384e1a1baa434a087b6048e14";
    private const string Sha256Arm64 = "ff3d2be10e87b5944d3a8fc098ea3386a14da4022df9feb1631249d977437c2a";

    private static readonly SemaphoreSlim Busy = new(1, 1);

    private readonly ILogger<ComskipInstaller> _logger;
    private readonly IHttpClientFactory _http;
    private readonly IApplicationPaths _paths;

    public ComskipInstaller(ILogger<ComskipInstaller> logger, IHttpClientFactory http, IApplicationPaths paths)
    {
        _logger = logger;
        _http = http;
        _paths = paths;
    }

    public sealed record InstallResult(bool Ok, string Message, string? Version, string? ComskipPath);

    public sealed record ComskipInfo(bool Installed, string? Version, string? ComskipPath, bool CanInstall, string InstallVersion, string? CannotInstallReason);

    /// <summary>Whether a Comskip is set up to run (its path set and the file there).</summary>
    public static bool IsInstalled(PluginConfiguration cfg) =>
        !string.IsNullOrWhiteSpace(cfg.ComskipPath) && File.Exists(cfg.ComskipPath);

    /// <summary>What a scan says when there's no Comskip to run.</summary>
    public const string NotInstalledMessage =
        "Comskip isn't installed on the server yet: open the Comskip Commercial Segments plugin's settings and press Install Comskip (or set the path to your own Comskip).";

    /// <summary>The asset for this server, or why there isn't one.</summary>
    private static (string? Asset, string? Sha256, string? Reason) AssetForThisServer()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return (null, null, "Install Comskip is for Linux servers. On this server, install Comskip yourself and set its path below.");
        }

        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => ("comskip-linux-x64", Sha256X64, null),
            Architecture.Arm64 => ("comskip-linux-arm64", Sha256Arm64, null),
            var a => (null, null, $"There's no Comskip build for this processor ({a}) yet. Install Comskip yourself and set its path below."),
        };
    }

    public ComskipInfo Info(PluginConfiguration cfg)
    {
        var (asset, _, reason) = AssetForThisServer();
        var installed = IsInstalled(cfg);
        return new ComskipInfo(installed, installed ? ReadVersion(cfg.ComskipPath) : null, installed ? cfg.ComskipPath : null, asset is not null, Version, reason);
    }

    public async Task<InstallResult> InstallAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var (asset, sha256, reason) = AssetForThisServer();
        if (asset is null || sha256 is null)
        {
            return new InstallResult(false, reason!, null, null);
        }

        if (!await Busy.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new InstallResult(false, "Already installing Comskip.", null, null);
        }

        try
        {
            var folder = Path.Combine(_paths.DataPath, "comskip");
            var work = Path.Combine(folder, "work");
            Directory.CreateDirectory(work);
            var binary = Path.Combine(folder, "comskip");
            var ini = Path.Combine(folder, "comskip.ini");

            _logger.LogInformation("Installing Comskip {Version} ({Asset}) into {Folder}", Version, asset, folder);
            var download = binary + ".download";
            using (var client = _http.CreateClient(NamedClient.Default))
            {
                client.Timeout = TimeSpan.FromMinutes(5);
                using var response = await client.GetAsync(ReleaseUrl + asset, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return new InstallResult(false, $"Couldn't download Comskip ({(int)response.StatusCode} from GitHub). Check the server can reach github.com, or install Comskip yourself.", null, null);
                }

                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = File.Create(download);
                await body.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            string actual;
            await using (var file = File.OpenRead(download))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            }

            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(download);
                _logger.LogWarning("Comskip download checksum mismatch: {Actual} (expected {Expected})", actual, sha256);
                return new InstallResult(false, "The downloaded Comskip didn't match its checksum, so it wasn't installed. Try again later.", null, null);
            }

            File.Move(download, binary, overwrite: true);
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            if (!File.Exists(ini))
            {
                await using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.ComskipSegments.Comskip.comskip.ini")
                    ?? throw new InvalidOperationException("comskip.ini resource missing");
                await using var file = File.Create(ini);
                await res.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            var version = ReadVersion(binary);
            if (version is null)
            {
                return new InstallResult(false, "Comskip was downloaded but doesn't start on this server, so the settings weren't changed.", null, null);
            }

            cfg.ComskipPath = binary;
            cfg.ComskipIniPath = ini;
            cfg.WorkDirectory = work;
            Plugin.Instance?.SaveConfiguration();
            _logger.LogInformation("Installed {Version} at {Path}", version, binary);
            return new InstallResult(true, $"Installed {version}. Paths are filled in and saved.", version, binary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installing Comskip failed");
            return new InstallResult(false, $"Installing Comskip failed: {ex.Message}", null, null);
        }
        finally
        {
            Busy.Release();
        }
    }

    /// <summary>"Comskip 0.83.001" from the program's first line, or null when it doesn't start.</summary>
    private string? ReadVersion(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null)
            {
                return null;
            }

            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            foreach (var line in output.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("Comskip ", StringComparison.Ordinal))
                {
                    var comma = l.IndexOf(',', StringComparison.Ordinal);
                    return comma > 0 ? l[..comma] : l;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't run {Path} for its version", path);
        }

        return null;
    }
}
