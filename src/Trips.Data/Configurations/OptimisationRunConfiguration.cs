using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trips.Core.Domain;

namespace Trips.Data.Configurations;

internal sealed class OptimisationRunConfiguration : IEntityTypeConfiguration<OptimisationRun>
{
    public void Configure(EntityTypeBuilder<OptimisationRun> b)
    {
        b.ToTable("optimisation_runs");
        b.HasKey(x => x.Id);

        b.Property(x => x.TripId).IsRequired();
        b.Property(x => x.Status).HasConversion<int>().IsRequired();
        b.Property(x => x.Solver).HasConversion<int>().IsRequired();

        b.Property(x => x.WeightDriveTime).IsRequired();
        b.Property(x => x.WeightStopCount).IsRequired();
        b.Property(x => x.WeightWalkAndPt).IsRequired();
        b.Property(x => x.WeightArrivalSpread).IsRequired();
        b.Property(x => x.WeightFairness).IsRequired();

        b.Property(x => x.StartedAt).IsRequired();
        b.Property(x => x.CompletedAt);
        b.Property(x => x.FailureReason).HasMaxLength(2000);

        b.Property(x => x.WallClock);
        b.Property(x => x.IterationsOrNodes);
        b.Property(x => x.BestObjective);
        b.Property(x => x.LpRelaxation);
        b.Property(x => x.BestSolutionId);

        b.HasMany(x => x.Solutions)
            .WithOne()
            .HasForeignKey(s => s.OptimisationRunId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.TripId);
        b.HasIndex(x => new { x.TripId, x.StartedAt });
    }
}
