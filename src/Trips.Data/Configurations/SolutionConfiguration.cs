using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trips.Core.Domain;

namespace Trips.Data.Configurations;

internal sealed class SolutionConfiguration : IEntityTypeConfiguration<Solution>
{
    public void Configure(EntityTypeBuilder<Solution> b)
    {
        b.ToTable("solutions");
        b.HasKey(x => x.Id);

        b.Property(x => x.OptimisationRunId).IsRequired();
        b.Property(x => x.Label).IsRequired().HasMaxLength(120);
        b.Property(x => x.Objective).IsRequired();

        // ObjectiveTerms stored as a Postgres double precision array.
        b.Property(x => x.ObjectiveTerms)
            .HasColumnType("double precision[]")
            .Metadata.SetValueComparer(new ValueComparer<double[]>(
                (a, c) => a == null && c == null || a != null && c != null && a.SequenceEqual(c),
                v => v.Aggregate(0, (h, e) => HashCode.Combine(h, e.GetHashCode())),
                v => v.ToArray()));

        b.HasMany(x => x.Routes)
            .WithOne()
            .HasForeignKey(r => r.SolutionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.OptimisationRunId);
    }
}
