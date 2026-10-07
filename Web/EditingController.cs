using Jellyfin.Plugin.ComskipSegments.Configuration;
using Jellyfin.Plugin.ComskipSegments.Editing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>
/// What the signed-in user may do to recordings, for the apps: show the edit buttons or not,
/// and say what a press will do (a marker or a trim, how long a trim can be undone).
/// </summary>
[ApiController]
[Route("ComskipSegments/Editing")]
[Authorize]
public sealed class EditingController : ControllerBase
{
    /// <param name="CanEdit">May edit recordings (administrator, or picked on the config page).</param>
    /// <param name="ProgramStartTrims">"Starts here" and found cards trim the file (otherwise they mark it).</param>
    /// <param name="KeepOriginal">Trims keep the original for good.</param>
    /// <param name="UndoDays">Otherwise, days a trim can be undone (0: it can't).</param>
    public sealed record EditingResponse(bool CanEdit, bool ProgramStartTrims, bool KeepOriginal, int UndoDays);

    [HttpGet]
    public ActionResult<EditingResponse> Get()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return Ok(new EditingResponse(
            RecordingEditors.CanEdit(User),
            cfg.ProgramStartAction == PluginConfiguration.ProgramStartTrim,
            cfg.AudioTrimKeepOriginal,
            cfg.AudioTrimKeepOriginal ? 0 : System.Math.Max(0, cfg.TrimUndoDays)));
    }
}
