namespace Trips.Api.Auth;

/// <summary>
/// Per-request handle on the anonymous session GUID assigned by
/// <see cref="AnonymousSessionMiddleware"/>. The middleware writes the value into
/// <c>HttpContext.Items</c> under <see cref="SessionItemKey"/>; this scoped service exposes it to
/// endpoints without re-parsing the cookie.
/// </summary>
public sealed class CurrentSession
{
    /// <summary>HttpContext.Items key that <see cref="AnonymousSessionMiddleware"/> uses.</summary>
    public const string SessionItemKey = "trips.session-id";

    private readonly IHttpContextAccessor _accessor;

    public CurrentSession(IHttpContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        _accessor = accessor;
    }

    /// <summary>The anonymous-session GUID identifying this browser. Never <see cref="Guid.Empty"/>
    /// when the middleware ran — but endpoints should still guard defensively for tests / direct
    /// invocations that bypass the pipeline.</summary>
    public Guid SessionId
    {
        get
        {
            var ctx = _accessor.HttpContext;
            if (ctx is null)
            {
                return Guid.Empty;
            }
            return ctx.Items.TryGetValue(SessionItemKey, out var value) && value is Guid g ? g : Guid.Empty;
        }
    }
}
