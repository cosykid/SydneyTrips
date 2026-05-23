using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trips.Core.Domain;

namespace Trips.Data.Configurations;

internal sealed class StopConfiguration : IEntityTypeConfiguration<Stop>
{
    public void Configure(EntityTypeBuilder<Stop> b)
    {
        b.ToTable("stops");
        b.HasKey(x => x.Id);

        b.Property(x => x.DriverRouteId).IsRequired();
        b.Property(x => x.OrderIndex).IsRequired();

        b.Property(x => x.Location)
            .HasColumnType("geometry(Point, 4326)")
            .IsRequired();

        b.Property(x => x.CandidateNodeId).IsRequired();
        b.Property(x => x.EstimatedArrival).IsRequired();

        // Pickups stored as Postgres uuid[].
        b.Property(x => x.Pickups)
            .HasColumnType("uuid[]")
            .HasConversion(
                v => v.ToArray(),
                v => v.ToArray())
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<Guid>>(
                (a, c) => a == null && c == null || a != null && c != null && a.SequenceEqual(c),
                v => v.Aggregate(0, (h, e) => HashCode.Combine(h, e.GetHashCode())),
                v => v.ToArray()));

        b.HasIndex(x => x.DriverRouteId);
        b.HasIndex(x => new { x.DriverRouteId, x.OrderIndex });
    }
}
