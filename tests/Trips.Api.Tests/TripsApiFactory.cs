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
using Trips.Optimisation.OrTools;

namespace Trips.Api.Tests;

/// <summary>
/// Spins up a Postgres+PostGIS container and points <see cref="WebApplicationFactory{TEntryPoint}"/>
/// at it. Migrations are applied on first construction; tests share the container per fixture instance
/// for speed but reset state between cases via <see cref="ResetAsync"/>.
/// </summary>
public sealed class TripsApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
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
                ["Auth:JwtKey"] = "this-is-a-very-long-test-key-for-jwt-signing-purposes-32-bytes!!",
                ["Auth:Issuer"] = "SydneyTrips.Tests",
                ["Auth:Audience"] = "SydneyTrips.Tests.Client",
                ["Optimisation:MaxConcurrent"] = "2",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            // Replace external integrations + solver with stubs: API tests cover the endpoint surface
            // and runner glue, not the optimisation core or live TfNSW/Google calls. The real services
            // would otherwise block on retries against unreachable APIs (no API keys in CI).
            services.RemoveAll<ISolver>();
            services.RemoveAll<ITfNswClient>();
            services.RemoveAll<IGoogleRoutesClient>();
            services.RemoveAll<IGeocodingClient>();
            services.AddSingleton<ISolver, StubSolver>();
            services.AddSingleton<ITfNswClient, StubTfNswClient>();
            services.AddSingleton<IGoogleRoutesClient, StubGoogleRoutesClient>();
            services.AddSingleton<IGeocodingClient, StubGeocodingClient>();
        });
    }

    /// <summary>
    /// Removes every row from the domain tables. Identity tables are left intact so multiple
    /// tests can share registered users where convenient.
    /// </summary>
    public async Task ResetAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TripsDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE "stops", "driver_routes", "solutions", "optimisation_runs",
                "candidate_nodes", "participants", "trip_events", "trips",
                "AspNetUserTokens", "AspNetUserRoles", "AspNetUserLogins", "AspNetUserClaims",
                "AspNetUsers", "AspNetRoleClaims", "AspNetRoles"
            RESTART IDENTITY CASCADE;
            """).ConfigureAwait(false);
    }

    /// <summary>Register a user and return an authenticated HTTP client.</summary>
    public Task<(HttpClient Client, AuthTokenResponse Tokens)> CreateAuthenticatedClientAsync(
        string email = "alice@example.com",
        string password = "password123",
        string displayName = "Alice")
        => RegisterAndAuthenticateAsync(CreateClient(), email, password, displayName);

    /// <summary>
    /// Returns a sibling factory that swaps the stubbed <see cref="ISolver"/> back to the real
    /// <see cref="OrToolsSolver"/> while keeping the rest of the test wiring (postgres container,
    /// HTTP-client stubs for TfNSW/Google/geocoding) intact. Use this to exercise the end-to-end
    /// runner → solver → persistence path; the default factory's stub solver short-circuits the
    /// CP-SAT model entirely.
    /// </summary>
    public WebApplicationFactory<Program> WithRealSolver()
    {
        return WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISolver>();
                services.AddSingleton<ISolver>(sp => sp.GetRequiredService<OrToolsSolver>());
            });
        });
    }

    internal static async Task<(HttpClient Client, AuthTokenResponse Tokens)> RegisterAndAuthenticateAsync(
        HttpClient client,
        string email,
        string password,
        string displayName)
    {
        var register = new RegisterRequest(email, password, displayName);
        var response = await client.PostAsJsonAsync("/auth/register", register).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokenResponse>().ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(tokens);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return (client, tokens);
    }
}
