using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trips.Core.Domain;

namespace Trips.Data.Configurations;

internal sealed class DriverRouteConfiguration : IEntityTypeConfiguration<DriverRoute>
{
    public void Configure(EntityTypeBuilder<DriverRoute> b)
    {
        b.ToTable("driver_routes");
        b.HasKey(x => x.Id);

        b.Property(x => x.SolutionId).IsRequired();
        b.Property(x => x.DriverId).IsRequired();
        b.Property(x => x.TravelMins).IsRequired();
        b.Property(x => x.OrderIndex).IsRequired();

        b.HasMany(x => x.Stops)
            .WithOne()
            .HasForeignKey(s => s.DriverRouteId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.SolutionId);
        b.HasIndex(x => new { x.SolutionId, x.OrderIndex });
    }
}
