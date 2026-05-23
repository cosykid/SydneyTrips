namespace Trips.Integrations.Caching;

/// <summary>
/// Thin cache abstraction used by the integration decorators. The Redis implementation
/// stores JSON; the no-op implementation makes the decorators a pass-through when
/// Redis is not configured (useful in pure unit tests).
/// </summary>
public interface IIntegrationCache
{
    /// <summary>Returns the cached value at <paramref name="key"/>, or <c>null</c> on miss.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken ct) where T : class;

    /// <summary>Stores <paramref name="value"/> at <paramref name="key"/> with the given TTL.</summary>
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct) where T : class;
}
