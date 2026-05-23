using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trips.Core.Domain;

namespace Trips.Data.Configurations;

internal sealed class TripEventConfiguration : IEntityTypeConfiguration<TripEvent>
{
    public void Configure(EntityTypeBuilder<TripEvent> b)
    {
        b.ToTable("trip_events");
        b.HasKey(x => x.Id);

        b.Property(x => x.TripId).IsRequired();
        b.Property(x => x.Kind).HasConversion<int>().IsRequired();
        b.Property(x => x.ActorId);
        b.Property(x => x.Location).HasColumnType("geometry(Point, 4326)");
        b.Property(x => x.Timestamp).IsRequired();
        b.Property(x => x.PayloadJson).HasColumnType("jsonb");

        b.HasIndex(x => x.TripId);
        b.HasIndex(x => new { x.TripId, x.Timestamp });
    }
}
