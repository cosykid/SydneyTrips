using Trips.Mocks;

namespace Trips.Integrations.Tests;

/// <summary>
/// xUnit collection-scoped fixture that boots the three WireMock mocks once per test class.
/// Tests opt in by adding <c>[Collection(MockServerCollection.Name)]</c> or inheriting from
/// <see cref="MockServerTestBase"/>.
/// </summary>
public sealed class MockServerFixture : IDisposable
{
    public IMockServerSet Servers { get; }

    public MockServerFixture()
    {
        var factory = new FixtureServerFactory();
        Servers = factory.Create();
    }

    public void Dispose() => Servers.Dispose();
}

[CollectionDefinition(Name)]
public sealed class MockServerCollection : ICollectionFixture<MockServerFixture>
{
    public const string Name = "MockServers";
}

[Collection(MockServerCollection.Name)]
public abstract class MockServerTestBase
{
    protected MockServerFixture Fixture { get; }

    protected MockServerTestBase(MockServerFixture fixture)
    {
        Fixture = fixture;
    }
}
