using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trips.Core.Domain;

namespace Trips.Data.Configurations;

internal sealed class TripConfiguration : IEntityTypeConfiguration<Trip>
{
    public void Configure(EntityTypeBuilder<Trip> b)
    {
        b.ToTable("trips");
        b.HasKey(x => x.Id);

        b.Property(x => x.Name).IsRequired().HasMaxLength(200);
        b.Property(x => x.DestinationName).IsRequired().HasMaxLength(200);

        b.Property(x => x.DestinationLocation)
            .HasColumnType("geometry(Point, 4326)")
            .IsRequired();

        b.Property(x => x.DepartAt).IsRequired();
        b.Property(x => x.ArrivalWindowEarliest).IsRequired();
        b.Property(x => x.ArrivalWindowLatest).IsRequired();
        b.Property(x => x.OwnerId).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();
        b.Property(x => x.LockedSolutionId);

        b.HasMany(x => x.Participants)
            .WithOne()
            .HasForeignKey(p => p.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.Runs)
            .WithOne()
            .HasForeignKey(r => r.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.OwnerId);
        b.HasIndex(x => x.DestinationLocation).HasMethod("gist");
    }
}
