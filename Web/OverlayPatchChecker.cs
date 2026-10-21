using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// On startup, checks that jellyfin-web's index.html still loads this plugin's browser scripts
/// (a jellyfin-web upgrade replaces the file and drops them). Where Jellyfin may write the file
/// and <see cref="Jellyfin.Plugin.ComskipSegments.Configuration.PluginConfiguration.AutoInstallWebOverlay"/> is on, it puts them
/// back itself; otherwise it logs what to run.
/// </summary>
public sealed class OverlayPatchChecker : IHostedService
{
    private readonly ILogger<OverlayPatchChecker> _logger;
    private readonly WebOverlayPatcher _patcher;

    public OverlayPatchChecker(ILogger<OverlayPatchChecker> logger, WebOverlayPatcher patcher)
    {
        _logger = logger;
        _patcher = patcher;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = _patcher.Status();
            switch (status.State)
            {
                case "nofile":
                    _logger.LogDebug("Comskip web patch check: {Path} not found, skipping", status.IndexPath);
                    return Task.CompletedTask;
                case "installed":
                    _logger.LogInformation("Comskip web patches present in {Path}", status.IndexPath);
                    return Task.CompletedTask;
            }

            if (status.CanWrite && Plugin.Instance?.Configuration.AutoInstallWebOverlay != false)
            {
                var result = _patcher.Install();
                _logger.LogInformation("Comskip web overlay was {State} in {Path}: {Message}", status.State, status.IndexPath, result.Message);
                return Task.CompletedTask;
            }

            _logger.LogWarning(
                "Comskip web patch is {State} at {Path}, so the web client shows no commercial marks on the seek bar. " +
                "This is normal after a jellyfin-web upgrade, which replaces index.html. Use Install web overlay on the " +
                "plugin's settings page{Fix}, or run Web/install-overlay.sh with sudo, then hard-refresh the page.",
                status.State,
                status.IndexPath,
                status.FixCommand is null ? string.Empty : " (Jellyfin can't write the file; first run: " + status.FixCommand + ")");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Comskip web patch check failed (non-fatal)");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
