namespace Trips.Mocks;

/// <summary>
/// Hands out test-owned mock servers. Tests resolve this through their fixture, call
/// <see cref="Create"/> to get a fresh trio bound to ephemeral ports, then dispose via
/// <see cref="IMockServerSet.Dispose"/> at teardown.
/// </summary>
public interface IFixtureServerFactory
{
    IMockServerSet Create();
}

/// <summary>
/// The three mock servers exposed together, with their base URLs ready to plug into
/// <c>HttpClient.BaseAddress</c> on the integration clients.
/// </summary>
public interface IMockServerSet : IDisposable
{
    string TfNswBaseUrl { get; }
    string GoogleBaseUrl { get; }
    string GoogleGeocodingBaseUrl { get; }
    string NominatimBaseUrl { get; }
}

/// <summary>
/// Concrete factory used by tests. Each call returns a fresh instance — WireMock instances are
/// cheap to start (a few ms) and dynamic ports keep parallel test runs isolated.
/// </summary>
public sealed class FixtureServerFactory : IFixtureServerFactory
{
    private readonly string _fixturesRoot;

    public FixtureServerFactory(string? fixturesRoot = null)
    {
        _fixturesRoot = fixturesRoot ?? FixturePaths.FindFixturesRoot();
    }

    public IMockServerSet Create()
    {
        var tfnsw = TfNswMockServer.Start(_fixturesRoot);
        var google = GoogleRoutesMockServer.Start(_fixturesRoot);
        var nominatim = NominatimMockServer.Start(_fixturesRoot);
        return new MockServerSet(tfnsw, google, nominatim);
    }

    private sealed class MockServerSet : IMockServerSet
    {
        private readonly TfNswMockServer _tfnsw;
        private readonly GoogleRoutesMockServer _google;
        private readonly NominatimMockServer _nominatim;

        public MockServerSet(TfNswMockServer tfnsw, GoogleRoutesMockServer google, NominatimMockServer nominatim)
        {
            _tfnsw = tfnsw;
            _google = google;
            _nominatim = nominatim;
        }

        public string TfNswBaseUrl => _tfnsw.BaseUrl;
        public string GoogleBaseUrl => _google.BaseUrl;
        public string GoogleGeocodingBaseUrl => _google.GeocodingBaseUrl;
        public string NominatimBaseUrl => _nominatim.BaseUrl;

        public void Dispose()
        {
            _tfnsw.Dispose();
            _google.Dispose();
            _nominatim.Dispose();
        }
    }
}
