using System.Net;
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
    static TripsApiFactory()
    {
        // SignalR's AddStackExchangeRedis() runs in Program.cs *before* the factory's
        // ConfigureAppConfiguration override applies, so it eagerly captures appsettings.json's
        // "localhost:6379" and tries to connect during hub use. Env vars beat JSON in the default
        // config chain, so clearing it here forces the realtime layer onto the in-memory backplane.
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

    /// <summary>Removes every row from the domain tables.</summary>
    public async Task ResetAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TripsDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE "stops", "driver_routes", "solutions", "optimisation_runs",
                "candidate_nodes", "participants", "trip_events", "trips"
            RESTART IDENTITY CASCADE;
            """).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a fresh client that carries its own anonymous-session cookie across calls. The
    /// first request stamps a <c>trips_session</c> cookie and every subsequent call on the same
    /// client reuses it — that's our test stand-in for "a single browser making N requests".
    /// The tuple's second element is the resolved session GUID (parsed from Set-Cookie on a
    /// no-op request); callers that pattern-match on the old <c>(client, tokens)</c> shape can
    /// still ignore it.
    /// </summary>
    public Task<(HttpClient Client, SessionHandle Session)> CreateAuthenticatedClientAsync(
        string email = "alice@example.com",
        string password = "password123",
        string displayName = "Alice")
        => CreateSessionClientAsync();

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

    /// <summary>
    /// Build a client with a CookieContainer so the anonymous-session cookie persists across calls,
    /// then prime it with a single GET /healthz so the Set-Cookie lands. Returns the parsed GUID.
    /// </summary>
    private async Task<(HttpClient Client, SessionHandle Session)> CreateSessionClientAsync()
    {
        var handler = new CookieContainerHandler();
        var client = CreateDefaultClient(handler);

        // Prime the cookie. /healthz is anonymous and cheap; the response carries the Set-Cookie
        // that we then forward on every subsequent request.
        var primer = await client.GetAsync("/healthz").ConfigureAwait(false);
        primer.EnsureSuccessStatusCode();

        var sessionId = handler.ExtractSessionId();
        return (client, new SessionHandle(sessionId));
    }

    /// <summary>Wraps the anonymous-session GUID assigned by the API.</summary>
    public sealed record SessionHandle(Guid SessionId);

    /// <summary>
    /// DelegatingHandler that holds onto the test client's cookie jar so the
    /// anonymous-session cookie sticks across requests. <see cref="WebApplicationFactory{T}"/>'s
    /// own client doesn't enable a CookieContainer by default.
    /// </summary>
    private sealed class CookieContainerHandler : DelegatingHandler
    {
        private readonly CookieContainer _jar = new();

        public Guid ExtractSessionId()
        {
            foreach (Cookie cookie in _jar.GetAllCookies())
            {
                if (cookie.Name == "trips_session" && Guid.TryParseExact(cookie.Value, "N", out var g))
                {
                    return g;
                }
            }
            return Guid.Empty;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri ?? new Uri("http://localhost");
            var cookieHeader = _jar.GetCookieHeader(requestUri);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.Add("Cookie", cookieHeader);
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var header in setCookies)
                {
                    _jar.SetCookies(requestUri, header);
                }
            }

            return response;
        }
    }
}
