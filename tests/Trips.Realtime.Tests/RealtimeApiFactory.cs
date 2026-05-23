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

namespace Trips.Realtime.Tests;

/// <summary>
/// Hosts the full Trips.Api WebApplicationFactory against a Postgres+PostGIS Testcontainer, then
/// exposes utilities for opening anonymous SignalR connections at <c>/hubs/trip</c>. With
/// anonymous-session auth there's no JWT — each test client just needs a cookie jar so its
/// <c>trips_session</c> cookie sticks across requests.
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

            // Re-register the DbContext with detailed errors for readable diagnostics.
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
                "candidate_nodes", "participants", "trip_events", "trips"
            RESTART IDENTITY CASCADE;
            """).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns an HttpClient that keeps the anonymous <c>trips_session</c> cookie across calls,
    /// plus the parsed session GUID after the cookie is primed. Equivalent to one browser.
    /// </summary>
    public async Task<AnonymousClient> CreateClientSessionAsync()
    {
        var handler = new CookieJarHandler();
        var client = CreateDefaultClient(handler);

        // Prime the session cookie via /healthz so subsequent calls all carry the same GUID.
        var primer = await client.GetAsync("/healthz").ConfigureAwait(false);
        primer.EnsureSuccessStatusCode();

        return new AnonymousClient(client, handler, handler.ExtractSessionId());
    }
}

/// <summary>Wraps an HttpClient + its cookie jar so test code can hand the cookie to SignalR too.</summary>
public sealed class AnonymousClient
{
    internal AnonymousClient(HttpClient httpClient, CookieJarHandler cookieJar, Guid sessionId)
    {
        HttpClient = httpClient;
        CookieJar = cookieJar;
        SessionId = sessionId;
    }

    public HttpClient HttpClient { get; }
    public Guid SessionId { get; }
    internal CookieJarHandler CookieJar { get; }
}

/// <summary>
/// DelegatingHandler that keeps the anonymous-session cookie alive across requests. Test clients
/// don't get a CookieContainer by default, so we hand-roll one here.
/// </summary>
internal sealed class CookieJarHandler : DelegatingHandler
{
    public CookieContainer Container { get; } = new();

    public Guid ExtractSessionId()
    {
        foreach (Cookie cookie in Container.GetAllCookies())
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
        var uri = request.RequestUri ?? new Uri("http://localhost");
        var header = Container.GetCookieHeader(uri);
        if (!string.IsNullOrEmpty(header))
        {
            request.Headers.Add("Cookie", header);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var sc in setCookies)
            {
                Container.SetCookies(uri, sc);
            }
        }

        return response;
    }
}
