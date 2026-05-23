using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using Trips.Integrations.Caching;
using Trips.Integrations.Configuration;

namespace Trips.Integrations.Tests;

/// <summary>
/// Exercises <see cref="RedisIntegrationCache"/> against an <see cref="IConnectionMultiplexer"/>
/// substitute so the test runs without a live Redis server. The cache only touches
/// <c>StringGetAsync</c> and <c>StringSetAsync</c>; we mock those two and verify the
/// prefix-handling + error-swallowing contract.
/// </summary>
public sealed class RedisIntegrationCacheTests
{
    [Fact]
    public async Task GetAsync_returns_deserialised_value_when_redis_returns_payload()
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(new RedisValue("{\"name\":\"hello\",\"value\":42}"));

        var cache = BuildCache(db);
        var result = await cache.GetAsync<Payload>("k", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Name.Should().Be("hello");
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task GetAsync_returns_null_when_key_missing()
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);

        var cache = BuildCache(db);
        (await cache.GetAsync<Payload>("missing", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_swallows_exceptions_and_returns_null()
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "boom"));

        var cache = BuildCache(db);
        (await cache.GetAsync<Payload>("k", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_writes_with_ttl()
    {
        var db = Substitute.For<IDatabase>();

        var cache = BuildCache(db);
        await cache.SetAsync("k", new Payload("v", 1), TimeSpan.FromMinutes(7), CancellationToken.None);

        // RedisIntegrationCache calls the 5-arg overload via StackExchange.Redis defaults.
        var calls = db.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "StringSetAsync").ToList();
        calls.Should().HaveCount(1);
        var args = calls[0].GetArguments();
        ((string?)(RedisKey)args[0]!).Should().EndWith(":k");
        ((TimeSpan?)args[2]).Should().Be(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task SetAsync_swallows_exceptions()
    {
        var db = Substitute.For<IDatabase>();
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "boom"));

        var cache = BuildCache(db);
        // Should not throw.
        await cache.SetAsync("k", new Payload("v", 1), TimeSpan.FromMinutes(1), CancellationToken.None);
    }

    [Fact]
    public async Task Key_prefix_is_applied()
    {
        var db = Substitute.For<IDatabase>();

        var cache = BuildCache(db, new IntegrationCacheOptions { KeyPrefix = "myenv:" });
        await cache.SetAsync("foo", new Payload("v", 1), TimeSpan.FromMinutes(1), CancellationToken.None);

        var setCall = db.ReceivedCalls().Single(c => c.GetMethodInfo().Name == "StringSetAsync");
        ((string?)(RedisKey)setCall.GetArguments()[0]!).Should().Be("myenv:foo");
    }

    private static RedisIntegrationCache BuildCache(IDatabase db, IntegrationCacheOptions? options = null)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return new RedisIntegrationCache(
            mux,
            Options.Create(options ?? new IntegrationCacheOptions()),
            NullLogger<RedisIntegrationCache>.Instance);
    }

    private sealed record Payload(string Name, int Value);
}
