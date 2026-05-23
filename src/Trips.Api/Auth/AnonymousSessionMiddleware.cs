using System.Globalization;

namespace Trips.Api.Auth;

/// <summary>
/// Stamps a long-lived <c>trips_session</c> cookie carrying a random GUID on every browser that
/// reaches the API. The GUID acts as the anonymous owner of trips — no login, no password, just
/// "this browser made this trip". Cookies are httpOnly + SameSite=Lax + Secure-in-prod so they
/// flow with same-site form/XHR requests but aren't readable by client JS.
///
/// The cookie is read first; if missing or malformed a fresh GUID is minted and Set-Cookie is
/// appended to the response. The middleware also exposes the resolved GUID via
/// <c>HttpContext.Items["trips.session-id"]</c> so <see cref="CurrentSession"/> can pick it up.
/// </summary>
public sealed class AnonymousSessionMiddleware
{
    /// <summary>The cookie name. Kept stable across deploys — changing it logs everyone out.</summary>
    public const string CookieName = "trips_session";

    /// <summary>Two years. Long enough that the cookie effectively persists; short enough that
    /// stale browsers eventually get a fresh ID without a forced rotation.</summary>
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(730);

    private readonly RequestDelegate _next;

    public AnonymousSessionMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (sessionId, wasMinted) = ResolveOrMint(context);
        context.Items[CurrentSession.SessionItemKey] = sessionId;

        if (wasMinted)
        {
            // Append before the response starts; if downstream middleware writes the body before
            // returning, the Set-Cookie still rides on the same response because Append goes
            // through HttpResponse.Headers which is set before the body flush.
            context.Response.Cookies.Append(CookieName, sessionId.ToString("N", CultureInfo.InvariantCulture), BuildCookieOptions(context));
        }

        return _next(context);
    }

    private static (Guid SessionId, bool Minted) ResolveOrMint(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var raw)
            && Guid.TryParseExact(raw, "N", out var existing)
            && existing != Guid.Empty)
        {
            return (existing, false);
        }

        return (Guid.NewGuid(), true);
    }

    private static CookieOptions BuildCookieOptions(HttpContext context) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        // In dev the FE runs over http://localhost:3000 → http://localhost:5000 — Secure would
        // strip the cookie. In prod the API is always behind TLS.
        Secure = context.Request.IsHttps,
        MaxAge = CookieLifetime,
        Path = "/",
        IsEssential = true,
    };
}
