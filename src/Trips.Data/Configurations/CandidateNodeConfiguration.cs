using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trips.Core.Domain;

namespace Trips.Data.Configurations;

internal sealed class CandidateNodeConfiguration : IEntityTypeConfiguration<CandidateNode>
{
    public void Configure(EntityTypeBuilder<CandidateNode> b)
    {
        b.ToTable("candidate_nodes");
        b.HasKey(x => x.Id);

        b.Property(x => x.ParticipantId).IsRequired();
        b.Property(x => x.Kind).HasConversion<int>().IsRequired();

        b.Property(x => x.Location)
            .HasColumnType("geometry(Point, 4326)")
            .IsRequired();

        b.Property(x => x.WalkMins).IsRequired();
        b.Property(x => x.PtMins).IsRequired();
        b.Property(x => x.ExternalId).HasMaxLength(64);
        b.Property(x => x.DisplayName).HasMaxLength(200);

        // Optional PT-route geometry (multi-leg path home → this hub). PostGIS LineString in
        // SRID 4326, no spatial index — we never query by it, just read/write whole-row.
        b.Property(x => x.Path)
            .HasColumnType("geometry(LineString, 4326)")
            .IsRequired(false);

        b.HasIndex(x => x.ParticipantId);
        b.HasIndex(x => x.Location).HasMethod("gist");
    }
}
