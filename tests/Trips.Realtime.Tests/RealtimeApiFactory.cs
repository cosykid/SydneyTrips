using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Trips.Api.Stubs;
using Trips.Core.Abstractions;
using Trips.Core.Contracts;
using Trips.Data;

namespace Trips.Realtime.Tests;

/// <summary>
/// Hosts the full Trips.Api WebApplicationFactory against a Postgres+PostGIS Testcontainer, then
/// exposes utilities for opening authenticated SignalR connections at <c>/hubs/trip</c>. The hub
/// tests need the real authentication + DI pipeline because the hub depends on
/// <c>TripAuthorizationService</c> + identity claims — there's no cheap stub for that.
/// </summary>
public sealed class RealtimeApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    static RealtimeApiFactory()
    {
        // AddTripsRealtime calls AddStackExchangeRedis(connectionString) at Program.cs registration
        // time, before the factory's ConfigureAppConfiguration override can clear it. Env vars beat
        // appsettings.json in the default chain, so this forces SignalR onto its in-memory backplane.
        Environment.SetEnvironmentVariable("ConnectionStrings__Redis", string.Empty);
    }

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgis/postgis:16-3.4")
        .WithDatabase("trips")
        .WithUsername("trips")
        .WithPassword("trips")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync().ConfigureAwait(false);
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TripsDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        await _postgres.DisposeAsync().ConfigureAwait(false);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureAppConfiguration((ctx, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Trips"] = ConnectionString,
                // Redis not configured — SignalR falls back to in-memory backplane, which is fine for tests.
                ["ConnectionStrings:Redis"] = string.Empty,
                ["Auth:JwtKey"] = "this-is-a-very-long-test-key-for-jwt-signing-purposes-32-bytes!!",
                ["Auth:Issuer"] = "SydneyTrips.Tests",
                ["Auth:Audience"] = "SydneyTrips.Tests.Client",
                ["Optimisation:MaxConcurrent"] = "2",
                // Worker disabled in hub tests to keep poll loops from interfering. GtfsWorker tests
                // construct the worker manually.
                ["Realtime:Gtfs:Enabled"] = "false",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISolver>();
            services.RemoveAll<ITfNswClient>();
            services.RemoveAll<IGoogleRoutesClient>();
            services.RemoveAll<IGeocodingClient>();
            services.AddSingleton<ISolver, StubSolver>();
            services.AddSingleton<ITfNswClient, StubTfNswClient>();
            services.AddSingleton<IGoogleRoutesClient, StubGoogleRoutesClient>();
            services.AddSingleton<IGeocodingClient, StubGeocodingClient>();

            // Re-register the DbContext to disable Npgsql command-execution-strategy retries and
            // enable detailed error reporting. The default registration set up by AddTripsData
            // was producing transient FK violations against the Identity tables under parallel
            // cross-assembly test runs — the symptom looked like a phantom AspNetUserTokens
            // insert before the AspNetUsers row was committed. Pinning a fresh DbContext options
            // here side-steps the issue and gives us readable diagnostics if anything regresses.
            services.RemoveAll<DbContextOptions<TripsDbContext>>();
            services.AddDbContext<TripsDbContext>(opts =>
            {
                opts.UseNpgsql(ConnectionString, npg => npg.UseNetTopologySuite());
                opts.EnableDetailedErrors();
                opts.EnableSensitiveDataLogging();
            });
        });
    }

    public async Task ResetAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripsDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE "stops", "driver_routes", "solutions", "optimisation_runs",
                "candidate_nodes", "participants", "trip_events", "trips",
                "AspNetUserTokens", "AspNetUserRoles", "AspNetUserLogins", "AspNetUserClaims",
                "AspNetUsers", "AspNetRoleClaims", "AspNetRoles"
            RESTART IDENTITY CASCADE;
            """).ConfigureAwait(false);
    }

    /// <summary>Register a user, return an authenticated client + access token + user id.</summary>
    public async Task<AuthenticatedUser> CreateAuthenticatedUserAsync(
        string email,
        string password = "password123",
        string displayName = "Tester")
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password, displayName))
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokenResponse>().ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(tokens);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return new AuthenticatedUser(client, tokens.AccessToken, Guid.Parse(tokens.UserId), email, displayName);
    }
}

public sealed record AuthenticatedUser(HttpClient HttpClient, string AccessToken, Guid UserId, string Email, string DisplayName);
