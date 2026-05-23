using Microsoft.EntityFrameworkCore;
using Trips.Core.Domain;

namespace Trips.Data;

/// <summary>
/// Application <see cref="DbContext"/>. No Identity tables — auth is via the anonymous
/// <c>trips_session</c> cookie issued by <c>AnonymousSessionMiddleware</c>, so there is no
/// per-user store. Trip ownership is just a GUID column matching the cookie value.
///
/// Picks up entity configurations from the assembly via
/// <see cref="ModelBuilder.ApplyConfigurationsFromAssembly"/>; configurations live in
/// <c>Trips.Data/Configurations</c>.
/// </summary>
public class TripsDbContext : DbContext
{
    public TripsDbContext(DbContextOptions<TripsDbContext> options) : base(options)
    {
    }

    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<CandidateNode> CandidateNodes => Set<CandidateNode>();
    public DbSet<OptimisationRun> OptimisationRuns => Set<OptimisationRun>();
    public DbSet<Solution> Solutions => Set<Solution>();
    public DbSet<DriverRoute> DriverRoutes => Set<DriverRoute>();
    public DbSet<Stop> Stops => Set<Stop>();
    public DbSet<TripEvent> TripEvents => Set<TripEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TripsDbContext).Assembly);
    }
}
