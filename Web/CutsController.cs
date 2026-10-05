using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ComskipSegments.AudioTrim;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// Recordings being cut right now (program-start and audio-format trims, manual or after a
/// recording), with progress, for the apps' "Cutting" animation; and Stop while it's safe.
/// </summary>
[ApiController]
[Route("ComskipSegments/Cuts")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class CutsController : ControllerBase
{
    private readonly CutTracker _cuts;

    public CutsController(CutTracker cuts) => _cuts = cuts;

    [HttpGet]
    public ActionResult<IReadOnlyList<CutJob>> List() => Ok(_cuts.List());

    /// <summary>Stops the cut (while checking or cutting: the original is untouched until the cut copy goes in).</summary>
    [HttpPost("{itemId}/Stop")]
    public ActionResult Stop([FromRoute] Guid itemId) =>
        _cuts.Stop(itemId)
            ? Ok(new { Message = "Stopping; the recording stays as it was." })
            : Conflict(new { Message = "Nothing to stop: it isn't being cut, or the cut copy is already going in." });
}
