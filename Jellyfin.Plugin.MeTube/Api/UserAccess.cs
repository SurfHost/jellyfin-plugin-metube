using System;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MeTube.Api;

/// <summary>Who may use the plugin, and whether they can see the library it fills.</summary>
public sealed class UserAccess
{
    private readonly IUserManager _userManager;

    /// <summary>Initializes a new instance of the <see cref="UserAccess"/> class.</summary>
    public UserAccess(IUserManager userManager)
    {
        _userManager = userManager;
    }

    /// <summary>
    /// Reads the calling user's id. Jellyfin.Api, which defines the claim name, is not
    /// on NuGet, so the string is spelled out here as GuestPass does. API keys
    /// authenticate as administrators without a user, which yields Guid.Empty.
    /// </summary>
    public static Guid GetUserId(ClaimsPrincipal principal)
    {
        var claim = principal.FindFirst("Jellyfin-UserId")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
    }

    /// <summary>Returns true for administrators.</summary>
    public static bool IsAdmin(ClaimsPrincipal principal) => principal.IsInRole("Administrator");

    /// <summary>Returns true when the user may search and add videos.</summary>
    public static bool IsAllowed(Guid userId, bool isAdmin, Guid[]? allowedUserIds) =>
        userId != Guid.Empty && (isAdmin || (allowedUserIds ?? Array.Empty<Guid>()).Contains(userId));

    /// <summary>Returns the user's name, or null when the user does not exist.</summary>
    public string? GetUserName(Guid userId) => _userManager.GetUserById(userId)?.Username;

    /// <summary>Returns true when the user can see the given library.</summary>
    public bool CanSeeLibrary(Guid userId, Guid libraryId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return false;
        }

        return user.HasPermission(PermissionKind.EnableAllFolders)
            || user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Contains(libraryId);
    }

    /// <summary>Returns true when the user may connect from outside the LAN.</summary>
    public bool HasRemoteAccess(Guid userId) =>
        _userManager.GetUserById(userId)?.HasPermission(PermissionKind.EnableRemoteAccess) ?? false;
}
