using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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

        // Mode-tagged journey segments (same path as above, but split per leg). Stored as jsonb —
        // we only ever read/write the whole document, never query into it. The value comparer
        // serialises for equality so EF's change tracker treats the immutable record list correctly.
        var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        b.Property(x => x.PathLegs)
            .HasColumnName("path_legs")
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, jsonOpts),
                v => (IReadOnlyList<PathLeg>?)JsonSerializer.Deserialize<List<PathLeg>>(v, jsonOpts))
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<PathLeg>?>(
                (a, b) => JsonSerializer.Serialize(a, jsonOpts) == JsonSerializer.Serialize(b, jsonOpts),
                v => v == null ? 0 : JsonSerializer.Serialize(v, jsonOpts).GetHashCode(),
                v => v));

        b.HasIndex(x => x.ParticipantId);
        b.HasIndex(x => x.Location).HasMethod("gist");
    }
}
