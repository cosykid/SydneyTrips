using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using StackExchange.Redis;
using Trips.Core.Abstractions;
using Trips.Integrations.Caching;
using Trips.Integrations.Clients;
using Trips.Integrations.Configuration;

namespace Trips.Integrations;

/// <summary>
/// DI registration for the external-integration layer. Wires HttpClient instances with
/// resilience pipelines, registers caching decorators, and reads all configuration from
/// the supplied <see cref="IConfiguration"/>. Does <em>not</em> register a hosted service —
/// the API project owns its own Program.cs and decides when to call this.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Wire the integration clients (TfNSW, Google Routes, geocoding) plus their Redis cache
    /// decorators and resilience pipelines. Returns the same <paramref name="services"/> for
    /// fluent chaining.
    /// </summary>
    /// <remarks>
    /// Required configuration keys:
    /// <list type="bullet">
    /// <item><c>Integrations:TfNsw:BaseUrl</c>, <c>ApiKey</c></item>
    /// <item><c>Integrations:Google:BaseUrl</c>, <c>ApiKey</c></item>
    /// <item><c>Integrations:Geocoding:Provider</c> = <c>google|nominatim</c></item>
    /// <item><c>Integrations:Cache:RedisConnectionString</c> (optional; absent ⇒ no-op cache)</item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddTripsIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<TfNswOptions>()
            .Bind(configuration.GetSection(TfNswOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<GoogleRoutesOptions>()
            .Bind(configuration.GetSection(GoogleRoutesOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<OsrmOptions>()
            .Bind(configuration.GetSection(OsrmOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<GeocodingOptions>()
            .Bind(configuration.GetSection(GeocodingOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<IntegrationCacheOptions>()
            .Bind(configuration.GetSection(IntegrationCacheOptions.SectionName))
            .ValidateOnStart();

        AddRedisCache(services, configuration);
        // Only wire the live TfNSW client when there's an API key to call it with. Otherwise
        // every TripPlan/Coord request would 401, the catch in ParticipantCandidateNodeService
        // would swallow it, and we'd quietly degrade to Home-only candidates — which is exactly
        // what produces "no PT, everyone picked up at their doorstep" in local dev. Leaving
        // ITfNswClient unregistered here lets Program.cs's TryAdd fall through to the stub,
        // which generates plausible PT-bearing candidates for development.
        var tfnswKey = configuration.GetSection(TfNswOptions.SectionName)["ApiKey"];
        if (!string.IsNullOrWhiteSpace(tfnswKey))
        {
            AddTfNsw(services);
        }
        AddGoogleRoutes(services, configuration);
        AddGeocoding(services, configuration);

        return services;
    }

    private static void AddRedisCache(IServiceCollection services, IConfiguration configuration)
    {
        var cacheSection = configuration.GetSection(IntegrationCacheOptions.SectionName);
        var connString = cacheSection["RedisConnectionString"];
        if (string.IsNullOrWhiteSpace(connString))
        {
            // Fall back to the shared ConnectionStrings:Redis — the same instance the SignalR
            // backplane already uses. Without this fallback the integration cache silently became a
            // no-op whenever only ConnectionStrings:Redis was set (the common case: that's all
            // appsettings.json defines), so every Google Routes matrix call paid full freight,
            // uncached, every run. The dedicated Integrations:Cache:RedisConnectionString still wins
            // when set, for environments that want the cache on a different instance.
            connString = configuration.GetConnectionString("Redis");
        }
        if (string.IsNullOrWhiteSpace(connString))
        {
            // No Redis configured anywhere — decorators become a pass-through.
            services.TryAddSingleton<IIntegrationCache, NoopIntegrationCache>();
            return;
        }

        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            // AbortOnConnectFail=false: the cache is an optimisation, not a hard dependency, so a
            // missing/unreachable Redis must not crash API startup. RedisIntegrationCache already
            // swallows per-call failures and falls back to upstream, so a degraded Redis degrades to
            // "no cache" rather than an outage.
            var redisOptions = ConfigurationOptions.Parse(connString!);
            redisOptions.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(redisOptions);
        });
        services.TryAddSingleton<IIntegrationCache, RedisIntegrationCache>();
    }

    private static void AddTfNsw(IServiceCollection services)
    {
        services.AddHttpClient<TfNswClient>(TfNswClient.HttpClientName, (sp, http) =>
            {
                var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TfNswOptions>>().Value;
                http.BaseAddress = new Uri(opts.BaseUrl, UriKind.Absolute);
                http.Timeout = TimeSpan.FromSeconds(opts.RequestTimeoutSeconds);
                if (!string.IsNullOrWhiteSpace(opts.ApiKey))
                {
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("apikey", opts.ApiKey);
                }
                http.DefaultRequestHeaders.Accept.Clear();
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddStandardResilienceHandler(ConfigureResilience);

        services.AddSingleton<ITfNswClient>(sp =>
        {
            var live = sp.GetRequiredService<TfNswClient>();
            var cache = sp.GetRequiredService<IIntegrationCache>();
            var cacheOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IntegrationCacheOptions>>();
            return new CachingTfNswClient(live, cache, cacheOptions);
        });
    }

    private static void AddGoogleRoutes(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient<GoogleRoutesClient>(GoogleRoutesClient.HttpClientName, (sp, http) =>
            {
                var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GoogleRoutesOptions>>().Value;
                http.BaseAddress = new Uri(opts.BaseUrl, UriKind.Absolute);
                http.Timeout = TimeSpan.FromSeconds(opts.RequestTimeoutSeconds);
                if (!string.IsNullOrWhiteSpace(opts.ApiKey))
                {
                    http.DefaultRequestHeaders.Add("X-Goog-Api-Key", opts.ApiKey);
                }
                http.DefaultRequestHeaders.Accept.Clear();
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddStandardResilienceHandler(ConfigureResilience);

        // When a self-hosted OSRM is configured, wire its HttpClient so the planner's free-flow
        // matrix is served locally at zero marginal cost instead of Google's per-element Route Matrix.
        var osrm = new OsrmOptions();
        configuration.GetSection(OsrmOptions.SectionName).Bind(osrm);
        if (osrm.Enabled)
        {
            services.AddHttpClient<OsrmRoutesClient>(OsrmRoutesClient.HttpClientName, (sp, http) =>
                {
                    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OsrmOptions>>().Value;
                    http.BaseAddress = new Uri(opts.BaseUrl, UriKind.Absolute);
                    http.Timeout = TimeSpan.FromSeconds(opts.RequestTimeoutSeconds);
                    http.DefaultRequestHeaders.Accept.Clear();
                    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                })
                .AddStandardResilienceHandler(ConfigureResilience);
        }

        services.AddSingleton<IGoogleRoutesClient>(sp =>
        {
            var google = sp.GetRequiredService<GoogleRoutesClient>();
            // HybridRoutesClient sends free-flow (planning) matrices to OSRM and keeps traffic-aware
            // ETAs + polylines on Google. With no OSRM configured the free-flow source is null and the
            // hybrid forwards everything to Google — identical to the pre-OSRM behaviour.
            IFreeFlowMatrixClient? freeFlow = osrm.Enabled ? sp.GetRequiredService<OsrmRoutesClient>() : null;
            var hybrid = new HybridRoutesClient(google, freeFlow);
            var cache = sp.GetRequiredService<IIntegrationCache>();
            var cacheOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IntegrationCacheOptions>>();
            return new CachingGoogleRoutesClient(hybrid, cache, cacheOptions);
        });
    }

    private static void AddGeocoding(IServiceCollection services, IConfiguration configuration)
    {
        var provider = ReadProvider(configuration);

        if (provider == GeocodingProvider.Google)
        {
            services.AddHttpClient<GoogleGeocodingClient>(GoogleGeocodingClient.HttpClientName, (sp, http) =>
                {
                    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GoogleRoutesOptions>>().Value;
                    var geo = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GeocodingOptions>>().Value;
                    http.BaseAddress = new Uri(opts.GeocodingBaseUrl, UriKind.Absolute);
                    http.Timeout = TimeSpan.FromSeconds(geo.RequestTimeoutSeconds);
                    if (!string.IsNullOrWhiteSpace(opts.ApiKey))
                    {
                        http.DefaultRequestHeaders.Add("key", opts.ApiKey);
                    }
                    http.DefaultRequestHeaders.Accept.Clear();
                    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                })
                .AddStandardResilienceHandler(ConfigureResilience);

            services.AddSingleton<IGeocodingClient>(sp =>
            {
                var live = sp.GetRequiredService<GoogleGeocodingClient>();
                var cache = sp.GetRequiredService<IIntegrationCache>();
                var cacheOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IntegrationCacheOptions>>();
                return new CachingGeocodingClient(live, cache, cacheOptions);
            });
        }
        else
        {
            services.AddHttpClient<NominatimGeocodingClient>(NominatimGeocodingClient.HttpClientName, (sp, http) =>
                {
                    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GeocodingOptions>>().Value;
                    http.BaseAddress = new Uri(opts.NominatimBaseUrl, UriKind.Absolute);
                    http.Timeout = TimeSpan.FromSeconds(opts.RequestTimeoutSeconds);
                    http.DefaultRequestHeaders.UserAgent.Clear();
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(opts.NominatimUserAgent);
                    http.DefaultRequestHeaders.Accept.Clear();
                    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                })
                .AddStandardResilienceHandler(ConfigureResilience);

            services.AddSingleton<IGeocodingClient>(sp =>
            {
                var live = sp.GetRequiredService<NominatimGeocodingClient>();
                var cache = sp.GetRequiredService<IIntegrationCache>();
                var cacheOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IntegrationCacheOptions>>();
                return new CachingGeocodingClient(live, cache, cacheOptions);
            });
        }
    }

    /// <summary>
    /// Standard resilience pipeline — retry on transient + 5xx, circuit-break on sustained failures,
    /// total timeout covering the whole pipeline. Values stay close to the package defaults.
    /// </summary>
    private static void ConfigureResilience(HttpStandardResilienceOptions options)
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.UseJitter = true;
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.MinimumThroughput = 10;
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(10);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
    }

    private static GeocodingProvider ReadProvider(IConfiguration configuration)
    {
        var raw = configuration[GeocodingOptions.SectionName + ":Provider"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return GeocodingProvider.Nominatim;
        }
        return Enum.TryParse<GeocodingProvider>(raw, ignoreCase: true, out var p) ? p : GeocodingProvider.Nominatim;
    }
}
