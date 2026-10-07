using System;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.ComskipSegments.Editing;

/// <summary>
/// Who may edit recordings (program start, cards, trims and their undo, the audio fix,
/// scanning for or removing commercials): administrators, plus the users picked on the config
/// page. Everyone else is refused here, and the apps don't show them the buttons.
/// </summary>
public static class RecordingEditors
{
    // Jellyfin's own claim names (Jellyfin.Api's ClaimsPrincipal helpers live outside the
    // NuGet packages; Episode History reads the user id the same way).
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string AdministratorRole = "Administrator";

    public static bool CanEdit(ClaimsPrincipal user)
    {
        if (user.IsInRole(AdministratorRole))
        {
            return true;
        }

        if (!Guid.TryParse(user.FindFirst(UserIdClaim)?.Value, out var id))
        {
            return false;
        }

        var allowed = Plugin.Instance?.Configuration.RecordingEditorUserIds ?? Array.Empty<string>();
        return allowed.Any(a => Guid.TryParse(a, out var g) && g == id);
    }
}

/// <summary>
/// Refuses a request (403, with a message the apps show) from anyone who may not edit
/// recordings. Goes with <c>[Authorize]</c>, which makes sure there is a signed-in user.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RecordingEditorAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!RecordingEditors.CanEdit(context.HttpContext.User))
        {
            context.Result = new ObjectResult(new { Message = "Only administrators and users allowed to edit recordings can do that." })
            {
                StatusCode = 403
            };
        }
    }
}
