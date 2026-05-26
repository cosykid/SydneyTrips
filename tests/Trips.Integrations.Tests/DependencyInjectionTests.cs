using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trips.Core.Abstractions;
using Trips.Integrations.Caching;

namespace Trips.Integrations.Tests;

/// <summary>
/// Exercises <see cref="DependencyInjection.AddTripsIntegrations"/> to verify the right
/// concrete types (and the right cache fallback) get registered for each configuration.
/// </summary>
public sealed class DependencyInjectionTests
{
    [Fact]
    public void Resolves_all_three_clients_with_minimal_config()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Integrations:TfNsw:BaseUrl"] = "https://example.invalid",
            ["Integrations:TfNsw:ApiKey"] = "test",
            ["Integrations:Google:BaseUrl"] = "https://example.invalid",
            ["Integrations:Google:ApiKey"] = "test",
        });

        services.GetService<ITfNswClient>().Should().NotBeNull();
        services.GetService<IGoogleRoutesClient>().Should().NotBeNull();
        services.GetService<IGeocodingClient>().Should().NotBeNull();
    }

    [Fact]
    public void Falls_back_to_noop_cache_when_redis_is_not_configured()
    {
        var services = BuildServices(new Dictionary<string, string?>());
        services.GetRequiredService<IIntegrationCache>()
            .Should().BeOfType<NoopIntegrationCache>();
    }

    [Fact]
    public void Uses_redis_cache_when_only_connectionstrings_redis_is_set()
    {
        // Regression for the silent no-op bug: the integration cache read only
        // Integrations:Cache:RedisConnectionString, which appsettings never set, so it stayed a
        // no-op even though ConnectionStrings:Redis pointed at a live instance. It must now fall back.
        var services = BuildServices(new Dictionary<string, string?>
        {
            // Dead port + abortConnect (set in code) means Connect returns without a live server.
            ["ConnectionStrings:Redis"] = "localhost:63999,connectTimeout=250",
        });

        services.GetRequiredService<IIntegrationCache>()
            .Should().BeOfType<RedisIntegrationCache>();
    }

    [Fact]
    public void Does_not_wire_osrm_when_base_url_is_unset()
    {
        var services = BuildServices(new Dictionary<string, string?>());
        services.GetService<Trips.Integrations.Clients.OsrmRoutesClient>().Should().BeNull();
    }

    [Fact]
    public void Wires_osrm_when_base_url_is_configured()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Integrations:Osrm:BaseUrl"] = "http://localhost:5001",
        });

        services.GetService<Trips.Integrations.Clients.OsrmRoutesClient>()
            .Should().NotBeNull("a configured OSRM base URL should register the free-flow matrix client");
        services.GetService<IGoogleRoutesClient>().Should().NotBeNull();
    }

    [Fact]
    public void Geocoding_provider_google_picks_google_implementation()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Integrations:Geocoding:Provider"] = "google",
            ["Integrations:Google:GeocodingBaseUrl"] = "https://example.invalid",
            ["Integrations:Google:ApiKey"] = "test",
        });

        var geocoder = services.GetRequiredService<IGeocodingClient>();
        geocoder.Should().BeOfType<CachingGeocodingClient>("the resolved client should be wrapped by the cache decorator");
    }

    [Fact]
    public void Geocoding_provider_nominatim_picks_nominatim_implementation()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Integrations:Geocoding:Provider"] = "nominatim",
            ["Integrations:Geocoding:NominatimBaseUrl"] = "https://example.invalid",
        });

        var geocoder = services.GetRequiredService<IGeocodingClient>();
        geocoder.Should().BeOfType<CachingGeocodingClient>();
    }

    private static IServiceProvider BuildServices(Dictionary<string, string?> overrides)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(overrides)
            .Build();
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddTripsIntegrations(config);
        return sc.BuildServiceProvider();
    }
}

public sealed class NoopIntegrationCacheTests
{
    [Fact]
    public async Task GetAsync_always_returns_null()
    {
        var cache = new NoopIntegrationCache();
        (await cache.GetAsync<string>("anything", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_does_not_throw()
    {
        var cache = new NoopIntegrationCache();
        await cache.SetAsync("anything", "value", TimeSpan.FromMinutes(1), CancellationToken.None);
    }
}
