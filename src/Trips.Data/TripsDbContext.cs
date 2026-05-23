using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Trips.Core.Domain;

namespace Trips.Data;

/// <summary>
/// Application <see cref="DbContext"/>. Inherits from <see cref="IdentityDbContext{TUser}"/>
/// so ASP.NET Core Identity tables live in the same database as the domain tables.
///
/// Picks up entity configurations from the assembly via
/// <see cref="ModelBuilder.ApplyConfigurationsFromAssembly"/>; configurations live in
/// <c>Trips.Data/Configurations</c>.
/// </summary>
public class TripsDbContext : IdentityDbContext<IdentityUser>
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
