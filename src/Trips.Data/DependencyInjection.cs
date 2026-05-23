using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trips.Core.Abstractions;
using Trips.Data.Repositories;

namespace Trips.Data;

/// <summary>
/// DI registration helpers for the data layer. Use from <c>Program.cs</c>:
/// <code>builder.Services.AddTripsData(builder.Configuration);</code>
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Wires <see cref="TripsDbContext"/> against <c>ConnectionStrings:Trips</c> with PostGIS + NetTopologySuite,
    /// and registers the repository implementations against their Core abstractions.
    /// </summary>
    public static IServiceCollection AddTripsData(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Resolve the connection string from the runtime IConfiguration so test hosts that override
        // ConnectionStrings:Trips via ConfigureAppConfiguration (which runs at Build() time, after
        // this DI registration line in Program.cs) actually take effect. Capturing it eagerly here
        // would lock in whatever appsettings.json carried.
        services.AddDbContext<TripsDbContext>((sp, options) =>
        {
            var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Trips")
                ?? throw new InvalidOperationException("ConnectionStrings:Trips is not configured.");
            options.UseNpgsql(cs, npgsql =>
            {
                npgsql.UseNetTopologySuite();
                npgsql.MigrationsAssembly(typeof(TripsDbContext).Assembly.GetName().Name);
            });
        });

        services.AddScoped<ITripRepository, TripRepository>();
        services.AddScoped<IParticipantRepository, ParticipantRepository>();
        services.AddScoped<IOptimisationRunRepository, OptimisationRunRepository>();
        services.AddScoped<ITripEventRepository, TripEventRepository>();
        services.AddScoped<ISolutionRepository, SolutionRepository>();
        services.AddScoped<ILockedContextRepository, LockedContextRepository>();

        return services;
    }
}
