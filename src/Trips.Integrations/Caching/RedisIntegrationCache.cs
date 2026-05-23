using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Trips.Integrations.Configuration;

namespace Trips.Integrations.Caching;

/// <summary>
/// Redis-backed cache. Serialises values as JSON; surfaces miss/error as <c>null</c>
/// so a cache outage never breaks the live call.
/// </summary>
internal sealed class RedisIntegrationCache : IIntegrationCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly IConnectionMultiplexer _redis;
    private readonly IntegrationCacheOptions _options;
    private readonly ILogger<RedisIntegrationCache> _logger;

    public RedisIntegrationCache(
        IConnectionMultiplexer redis,
        IOptions<IntegrationCacheOptions> options,
        ILogger<RedisIntegrationCache> logger)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var db = _redis.GetDatabase();
            var prefixedKey = Prefix(key);
            var value = await db.StringGetAsync(prefixedKey).ConfigureAwait(false);
            if (value.IsNullOrEmpty)
            {
                return null;
            }

            var json = (string?)value;
            return json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Redis cache read failed for key {Key}; falling back to upstream", key);
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var db = _redis.GetDatabase();
            var prefixedKey = Prefix(key);
            var json = JsonSerializer.Serialize(value, JsonOptions);
            await db.StringSetAsync(prefixedKey, json, ttl).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Redis cache write failed for key {Key}; ignoring", key);
        }
    }

    private string Prefix(string key) => _options.KeyPrefix + key;
}

/// <summary>
/// Drop-in cache used when Redis is not configured. Always misses, never stores —
/// callers behave exactly as if the decorator was not in the pipeline.
/// </summary>
internal sealed class NoopIntegrationCache : IIntegrationCache
{
    public Task<T?> GetAsync<T>(string key, CancellationToken ct) where T : class
        => Task.FromResult<T?>(null);

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct) where T : class
        => Task.CompletedTask;
}
