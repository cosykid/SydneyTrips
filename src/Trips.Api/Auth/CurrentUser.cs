using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Trips.Api.Auth;

/// <summary>
/// Adapter that pulls the authenticated user's identity claims out of <see cref="HttpContext.User"/>.
/// Used by endpoints that need the caller's user id without binding to ASP.NET Identity's
/// <c>UserManager</c> on the request path.
/// </summary>
public sealed class CurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUser(IHttpContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        _accessor = accessor;
    }

    public bool IsAuthenticated => _accessor.HttpContext?.User?.Identity?.IsAuthenticated == true;

    /// <summary>The Identity user id (string-form Guid) from the JWT <c>sub</c> claim.</summary>
    public string? UserId => _accessor.HttpContext?.User?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                              ?? _accessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>The user id parsed as a <see cref="Guid"/>. Identity stores ids as strings, but our
    /// domain expects Guids — we parse defensively and return <see cref="Guid.Empty"/> when absent.</summary>
    public Guid UserIdGuid
    {
        get
        {
            var id = UserId;
            return id is not null && Guid.TryParse(id, out var g) ? g : Guid.Empty;
        }
    }

    public string? Email => _accessor.HttpContext?.User?.FindFirstValue(JwtRegisteredClaimNames.Email)
                            ?? _accessor.HttpContext?.User?.FindFirstValue(ClaimTypes.Email);

    public string? DisplayName => _accessor.HttpContext?.User?.FindFirstValue("name");
}
