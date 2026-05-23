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
        services.AddOptions<GeocodingOptions>()
            .Bind(configuration.GetSection(GeocodingOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<IntegrationCacheOptions>()
            .Bind(configuration.GetSection(IntegrationCacheOptions.SectionName))
            .ValidateOnStart();

        AddRedisCache(services, configuration);
        AddTfNsw(services);
        AddGoogleRoutes(services);
        AddGeocoding(services, configuration);

        return services;
    }

    private static void AddRedisCache(IServiceCollection services, IConfiguration configuration)
    {
        var cacheSection = configuration.GetSection(IntegrationCacheOptions.SectionName);
        var connString = cacheSection["RedisConnectionString"];
        if (string.IsNullOrWhiteSpace(connString))
        {
            // No Redis configured — decorators become a pass-through.
            services.TryAddSingleton<IIntegrationCache, NoopIntegrationCache>();
            return;
        }

        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(connString));
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

    private static void AddGoogleRoutes(IServiceCollection services)
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

        services.AddSingleton<IGoogleRoutesClient>(sp =>
        {
            var live = sp.GetRequiredService<GoogleRoutesClient>();
            var cache = sp.GetRequiredService<IIntegrationCache>();
            var cacheOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IntegrationCacheOptions>>();
            return new CachingGoogleRoutesClient(live, cache, cacheOptions);
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
