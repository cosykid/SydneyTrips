using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trips.Realtime.Eta;
using Trips.Realtime.Gtfs;
using Trips.Realtime.Hubs;

namespace Trips.Realtime;

/// <summary>
/// DI wiring for the Trips.Realtime layer. One call from the API host —
/// <c>builder.Services.AddTripsRealtime(builder.Configuration);</c> — registers the hub, ETA
/// recompute pipeline, GTFS-Realtime background worker, and the SignalR backplane.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Wire up SignalR + ETA + GTFS-RT.
    /// <list type="bullet">
    ///   <item><c>SignalR</c> hub + <c>Redis backplane</c> when <c>ConnectionStrings:Redis</c> is set;
    ///     falls back to in-memory backplane (default SignalR behaviour) otherwise.</item>
    ///   <item><see cref="EtaService"/> + <see cref="EtaRecomputeQueue"/> + <see cref="EtaRecomputeRunner"/>
    ///     hosted service.</item>
    ///   <item><see cref="GtfsRealtimeWorker"/> hosted service (gated by <c>Realtime:Gtfs:Enabled</c>).</item>
    ///   <item><see cref="InMemoryActiveTripRegistry"/> singleton used by the worker to scope feed polling.</item>
    /// </list>
    ///
    /// The caller is expected to supply an <see cref="ITripHubAuthorizer"/> registration (the API host
    /// registers <c>Trips.Api.Auth.TripAuthorizationService</c> + a thin adapter that implements the
    /// abstraction). We don't register it here because the production authoriser lives in Trips.Api.
    /// </summary>
    public static IServiceCollection AddTripsRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<GtfsRealtimeOptions>(configuration.GetSection(GtfsRealtimeOptions.SectionName));

        services.AddSingleton<EtaRecomputeQueue>();
        services.AddScoped<EtaService>();
        services.AddHostedService<EtaRecomputeRunner>();
        services.AddSingleton<IActiveTripRegistry, InMemoryActiveTripRegistry>();
        services.AddHostedService<GtfsRealtimeWorker>();

        var signalr = services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = true;
        });

        var redis = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redis))
        {
            signalr.AddStackExchangeRedis(redis, options =>
            {
                options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("sydneytrips");
            });
        }

        return services;
    }
}
