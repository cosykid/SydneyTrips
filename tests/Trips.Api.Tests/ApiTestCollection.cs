namespace Trips.Api.Tests;

/// <summary>
/// Single xUnit collection bringing every API test class under one shared <see cref="TripsApiFactory"/>.
/// xUnit serialises tests within a collection by default, which matches what we want — every test
/// truncates state, so concurrent tests would collide.
/// </summary>
[CollectionDefinition("ApiTests")]
public sealed class ApiTestCollection : ICollectionFixture<TripsApiFactory>
{
}
