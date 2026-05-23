namespace Trips.Realtime.Tests;

/// <summary>
/// xUnit collection so every hub test class shares the same Postgres+SignalR host. Tests inside the
/// collection serialise on xUnit's default scheduler, which matches what we want — each test calls
/// <see cref="RealtimeApiFactory.ResetAsync"/> in <see cref="IAsyncLifetime.InitializeAsync"/>.
/// </summary>
[CollectionDefinition("RealtimeTests")]
public sealed class RealtimeTestCollection : ICollectionFixture<RealtimeApiFactory>
{
}
