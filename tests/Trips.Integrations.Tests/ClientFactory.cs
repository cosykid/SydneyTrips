using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trips.Core.Abstractions;
using Trips.Mocks;

namespace Trips.Integrations.Tests;

/// <summary>
/// Spins up a DI container pointing the integration clients at the running mock servers.
/// Returns a fresh provider per call so each test gets isolated HTTP plumbing.
/// </summary>
internal static class ClientFactory
{
    public static ServiceProvider BuildProvider(IMockServerSet servers, string geocodingProvider = "nominatim")
    {
        var dict = new Dictionary<string, string?>
        {
            ["Integrations:TfNsw:BaseUrl"] = servers.TfNswBaseUrl,
            ["Integrations:TfNsw:ApiKey"] = "test-key",
            ["Integrations:Google:BaseUrl"] = servers.GoogleBaseUrl,
            ["Integrations:Google:GeocodingBaseUrl"] = servers.GoogleGeocodingBaseUrl,
            ["Integrations:Google:ApiKey"] = "test-key",
            ["Integrations:Geocoding:Provider"] = geocodingProvider,
            ["Integrations:Geocoding:NominatimBaseUrl"] = servers.NominatimBaseUrl,
            ["Integrations:Geocoding:NominatimUserAgent"] = "SydneyTrips-Tests/1.0",

            // No Redis configured — decorators fall through to the live (mocked) calls.
            ["Integrations:Cache:RedisConnectionString"] = "",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTripsIntegrations(config);
        return services.BuildServiceProvider();
    }

    public static ITfNswClient TfNsw(IMockServerSet servers) =>
        BuildProvider(servers).GetRequiredService<ITfNswClient>();

    public static IGoogleRoutesClient GoogleRoutes(IMockServerSet servers) =>
        BuildProvider(servers).GetRequiredService<IGoogleRoutesClient>();

    public static IGeocodingClient Geocoding(IMockServerSet servers, string provider = "nominatim") =>
        BuildProvider(servers, provider).GetRequiredService<IGeocodingClient>();
}
